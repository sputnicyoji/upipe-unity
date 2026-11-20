using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Upipe.Editor
{
    // Detached commands (`unity command --detach`): POST /api/exec with "job": true answers with a job id at once,
    // GET /api/job?id= reports the job and POST /api/job/cancel asks it to stop. The states are the ones `unity job wait`
    // reads: queued -> running -> completed | failed | canceled (evidence/unity-pipeline-2022/issue5-cli-job-contract.log).
    // The table survives domain reloads through SessionState; a job the reload cut short reads as failed, not unknown.
    // One table per instance: the server uses Live; tests build their own so they never touch the jobs a client is
    // waiting on (the package's tests can run inside the Editor they test, through `test run`).
    internal sealed class JobManager
    {
        // Each unfinished off-main-thread job holds a thread of its own.
        public const int MaxUnfinishedJobs = 16;
        const int MaxFinishedJobs = 100;
        static readonly TimeSpan FinishedLifetime = TimeSpan.FromHours(1);
        public static readonly JobManager Live = new JobManager("Upipe.Jobs");

        public const string Queued = "queued";
        public const string Running = "running";
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Canceled = "canceled";

        internal const string InterruptedError = "Job Interrupted";
        const string InterruptedDetails = "A domain reload stopped the job before it finished; run the command again.";

        sealed class Job
        {
            public string Id;
            public string Command;
            public string State = Queued;
            public bool CancellationRequested;
            public JToken Result;
            public string Error;
            public string ErrorDetails;
            public DateTime FinishedAt;
            public CancellationTokenSource Cancellation = new CancellationTokenSource();
        }

        readonly object m_Lock = new object();
        readonly Dictionary<string, Job> m_Jobs = new Dictionary<string, Job>();
        // SessionState key the table is saved under across domain reloads.
        internal readonly string SavedJobsKey;
        // Set once the table is saved for a reload: a job taken after that would never be saved and read as unknown.
        bool m_Saved;

        public JobManager(string savedJobsKey)
        {
            SavedJobsKey = savedJobsKey;
        }

        static bool IsFinished(Job job) => job.State == Completed || job.State == Failed || job.State == Canceled;

        // Starts a job and returns its status, or null when MaxUnfinishedJobs are queued or running; throws
        // EditorReloadingException once the table has been saved for a domain reload. `start` gets the
        // job's token (cancelled by a cancel request or the timeout) and a callback to run right before the handler;
        // the callback throws OperationCanceledException when the job was cancelled first, so the handler never runs.
        public JObject Submit(string command, int? timeoutMilliseconds, Func<CancellationToken, Action, Task<object>> start)
        {
            var job = new Job { Id = Guid.NewGuid().ToString("N"), Command = command };
            lock (m_Lock)
            {
                if (m_Saved)
                    throw new EditorReloadingException();
                Prune();
                if (m_Jobs.Values.Count(j => !IsFinished(j)) >= MaxUnfinishedJobs)
                    return null;
                m_Jobs[job.Id] = job;
            }
            Task<object> task;
            try
            {
                task = start(job.Cancellation.Token, () => MarkRunning(job));
            }
            catch (Exception e)
            {
                task = Task.FromException<object>(e);
            }
            task.ContinueWith(t => Complete(job, t), TaskScheduler.Default);
            if (timeoutMilliseconds.HasValue)
                Task.Delay(timeoutMilliseconds.Value).ContinueWith(_ => Expire(job, timeoutMilliseconds.Value), TaskScheduler.Default);
            lock (m_Lock)
                return Describe(job);
        }

        // Null when no such job is known (never submitted, or finished and dropped).
        public JObject Status(string id)
        {
            lock (m_Lock)
            {
                Prune();
                return m_Jobs.TryGetValue(id, out var job) ? Describe(job) : null;
            }
        }

        // A queued job is cancelled at once and never runs. A running one gets its token cancelled and keeps
        // reporting running (cancellationRequested true) until its handler stops, as canceled if it gave up.
        public JObject Cancel(string id)
        {
            Job job;
            lock (m_Lock)
            {
                if (!m_Jobs.TryGetValue(id, out job))
                    return null;
                if (IsFinished(job))
                    return Describe(job);
                job.CancellationRequested = true;
                if (job.State == Queued)
                    Finish(job, Canceled);
            }
            // Outside the lock: cancelling runs the handler's own token callbacks on this thread.
            job.Cancellation.Cancel();
            lock (m_Lock)
                return Describe(job);
        }

        void MarkRunning(Job job)
        {
            lock (m_Lock)
            {
                if (job.State != Queued)
                    throw new OperationCanceledException();
                job.State = Running;
            }
        }

        void Complete(Job job, Task<object> task)
        {
            JToken result = null;
            Exception error = null;
            if (task.Status == TaskStatus.RanToCompletion)
            {
                try
                {
                    result = task.Result == null ? JValue.CreateNull() : JToken.FromObject(task.Result);
                }
                catch (Exception e)
                {
                    error = new CommandFailure("Command Execution Failed", "Could not convert the result: " + e.Message);
                }
            }
            else
                error = task.IsCanceled ? new OperationCanceledException() : UpipeServer.Unwrap(task.Exception);

            lock (m_Lock)
            {
                if (IsFinished(job))
                    return; // cancelled while queued, timed out or interrupted first
                if (error == null)
                    Finish(job, Completed, result: result);
                else if (error is OperationCanceledException && job.CancellationRequested)
                    Finish(job, Canceled);
                else if (error is EditorReloadingException)
                    Finish(job, Failed, InterruptedError, InterruptedDetails);
                else if (error is CommandFailure failure)
                    Finish(job, Failed, failure.Error, failure.Message);
                else
                    Finish(job, Failed, "Command Execution Failed", error.Message);
            }
        }

        // As a synchronous request would answer at its deadline: the job fails now, its token is cancelled, and a
        // handler that already started may still finish (its result is dropped).
        void Expire(Job job, int timeoutMilliseconds)
        {
            lock (m_Lock)
            {
                if (IsFinished(job))
                    return;
                Finish(job, Failed, "Request Timed Out",
                    $"No result within {timeoutMilliseconds} ms. If the command had already started, it may still finish.");
            }
            job.Cancellation.Cancel();
        }

        static void Finish(Job job, string state, string error = null, string details = null, JToken result = null)
        {
            job.State = state;
            job.Error = error;
            job.ErrorDetails = details;
            job.Result = result;
            job.FinishedAt = DateTime.UtcNow;
        }

        // The flat shape `unity job status` reads (a wrapped one is "unrecognized").
        static JObject Describe(Job job)
        {
            var reply = new JObject
            {
                ["jobId"] = job.Id,
                ["command"] = job.Command,
                ["state"] = job.State,
                ["cancellationRequested"] = job.CancellationRequested,
            };
            if (job.State == Completed)
                reply["result"] = job.Result?.DeepClone() ?? JValue.CreateNull();
            if (job.Error != null)
            {
                reply["error"] = job.Error;
                reply["errorDetails"] = job.ErrorDetails;
            }
            return reply;
        }

        // Caller holds the lock. Keeps the newest MaxFinishedJobs finished jobs, none older than FinishedLifetime.
        void Prune()
        {
            var now = DateTime.UtcNow;
            var finished = m_Jobs.Values.Where(IsFinished).OrderByDescending(j => j.FinishedAt).ToList();
            foreach (var job in finished.Skip(MaxFinishedJobs).Concat(finished.Where(j => now - j.FinishedAt > FinishedLifetime)).ToList())
                m_Jobs.Remove(job.Id);
        }

        // Main thread, before the domain unloads (SessionState is main-thread only). Work still queued or running
        // dies with the domain, so it is saved as failed; finished jobs keep their result.
        // shortcut: up to 100 results are re-serialized at every reload; cap the saved result size if that gets slow.
        public void SaveForReload()
        {
            JArray saved;
            lock (m_Lock)
            {
                m_Saved = true;
                foreach (var job in m_Jobs.Values.Where(j => !IsFinished(j)))
                    Finish(job, Failed, InterruptedError, InterruptedDetails);
                Prune();
                saved = new JArray(m_Jobs.Values.Select(job =>
                {
                    var entry = Describe(job);
                    entry["finishedAt"] = job.FinishedAt.Ticks;
                    return entry;
                }));
            }
            SessionState.SetString(SavedJobsKey, saved.ToString(Formatting.None));
        }

        // Main thread, from UpipeServer's static constructor: takes back the table the previous domain saved.
        public void Initialize()
        {
            var text = SessionState.GetString(SavedJobsKey, "");
            lock (m_Lock)
            {
                m_Saved = false;
                m_Jobs.Clear();
                if (text.Length == 0)
                    return;
                // Strings stay strings: a result holding an ISO date must not come back as a DateTime.
                var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
                foreach (var entry in JArray.Load(reader).OfType<JObject>())
                {
                    var job = new Job
                    {
                        Id = (string)entry["jobId"],
                        Command = (string)entry["command"],
                        State = (string)entry["state"],
                        CancellationRequested = (bool?)entry["cancellationRequested"] ?? false,
                        Result = entry["result"],
                        Error = (string)entry["error"],
                        ErrorDetails = (string)entry["errorDetails"],
                        FinishedAt = new DateTime((long?)entry["finishedAt"] ?? 0, DateTimeKind.Utc),
                    };
                    if (job.Id != null)
                        m_Jobs[job.Id] = job;
                }
                Prune();
            }
        }
    }
}
