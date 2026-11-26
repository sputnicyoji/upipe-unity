// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Upipe.Editor
{
    // Detached commands (`unity command --detach`): POST /api/exec with "job": true answers with a job id at once,
    // GET /api/job?id= reports the job and POST /api/job/cancel asks it to stop. The states are the ones `unity job wait`
    // reads: queued -> running -> completed | failed | canceled.
    // The table survives domain reloads through SessionState; a job the reload cut short reads as failed, not unknown.
    // One table per instance: the server uses Live; tests build their own so they never touch the jobs a client is
    // waiting on (the package's tests can run inside the Editor they test, through `test run`).
    internal sealed class JobManager
    {
        // Each unfinished off-main-thread job holds a thread of its own.
        public const int MaxUnfinishedJobs = 16;
        const int MaxFinishedJobs = 100;
        internal const int MaxInlineResultChars = 32 * 1024;
        static readonly TimeSpan FinishedLifetime = TimeSpan.FromHours(1);
        static readonly Encoding s_Utf8NoBom = new UTF8Encoding(false);
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
            public CancellationTokenSource TimeoutCancellation;
            // Replaced whole on each report and dropped when the job finishes: never a finished job's leftover.
            public JObject Progress;
        }

        readonly object m_Lock = new object();
        readonly Dictionary<string, Job> m_Jobs = new Dictionary<string, Job>();
        // SessionState key the table is saved under across domain reloads.
        internal readonly string SavedJobsKey;
        readonly string m_SpillDirectory;
        // Set once the table is saved for a reload: a job taken after that would never be saved and read as unknown.
        bool m_Saved;
        // Set on manual stop so submissions racing with shutdown fail instead of spawning zombie worker threads.
        bool m_Stopped;
        bool m_Initialized;

        public JobManager(string savedJobsKey, string spillDirectory = null)
        {
            SavedJobsKey = savedJobsKey;
            m_SpillDirectory = spillDirectory;
        }

        string SpillDirectory => m_SpillDirectory ?? Path.Combine(UpipeServer.ProjectPath, "Temp", "UpipeJobs");

        static bool IsFinished(Job job) => job.State == Completed || job.State == Failed || job.State == Canceled;

        internal bool HasActiveTimeout(string id)
        {
            lock (m_Lock)
                return m_Jobs.TryGetValue(id, out var job) && job.TimeoutCancellation != null;
        }

        // Starts a job and returns its status, or null when MaxUnfinishedJobs are queued or running; throws
        // EditorReloadingException once the table has been saved for a domain reload. `start` gets the
        // job's token (cancelled by a cancel request or the timeout), a callback to run right before the handler, and
        // the job's progress reporter (any thread). The callback throws OperationCanceledException when the job was
        // cancelled first, so the handler never runs.
        public JObject Submit(string command, int? timeoutMilliseconds, Func<CancellationToken, Action, Action<string, int?>, Task<object>> start)
        {
            var job = new Job { Id = Guid.NewGuid().ToString("N"), Command = command };
            lock (m_Lock)
            {
                if (m_Saved)
                    throw new EditorReloadingException();
                if (m_Stopped)
                    throw new ServerStoppedException();
                Prune();
                if (m_Jobs.Values.Count(j => !IsFinished(j)) >= MaxUnfinishedJobs)
                    return null;
                if (timeoutMilliseconds.HasValue)
                {
                    var timeoutCts = new CancellationTokenSource();
                    job.TimeoutCancellation = timeoutCts;
                    Task.Delay(timeoutMilliseconds.Value, timeoutCts.Token)
                        .ContinueWith(t =>
                        {
                            if (!t.IsCanceled)
                                Expire(job, timeoutMilliseconds.Value);
                        }, TaskScheduler.Default);
                }
                m_Jobs[job.Id] = job;
            }
            Task<object> task;
            try
            {
                task = start(job.Cancellation.Token, () => MarkRunning(job), (message, percent) => ReportProgress(job, message, percent));
            }
            catch (Exception e)
            {
                task = Task.FromException<object>(e);
            }
            task.ContinueWith(t => Complete(job, t), TaskScheduler.Default);
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
            UpipeServer.CancelQuietly(job.Cancellation);
            lock (m_Lock)
                return Describe(job);
        }

        // Cancels every queued or running job when the server is stopped manually or the Editor quits.
        public void CancelAll(string error = "Server Stopped", string errorDetails = "The Upipe server was stopped before the job finished.",
            bool stopAccepting = false)
        {
            List<Job> active;
            lock (m_Lock)
            {
                if (stopAccepting)
                    m_Stopped = true;
                active = m_Jobs.Values.Where(j => !IsFinished(j)).ToList();
                foreach (var job in active)
                {
                    job.CancellationRequested = true;
                    if (job.State == Queued)
                        Finish(job, Canceled);
                    else
                        Finish(job, Failed, error, errorDetails);
                }
            }
            foreach (var job in active)
                UpipeServer.CancelQuietly(job.Cancellation);
        }

        // Percent is clamped to 0..100. A finished job ignores late reports.
        void ReportProgress(Job job, string message, int? percent)
        {
            lock (m_Lock)
            {
                if (IsFinished(job))
                    return;
                job.Progress = new JObject
                {
                    ["message"] = message ?? (string)job.Progress?["message"],
                    ["percent"] = percent.HasValue ? new JValue(Math.Max(0, Math.Min(100, percent.Value))) : JValue.CreateNull(),
                };
            }
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
                else if (error is ServerStoppedException)
                    Finish(job, Failed, "Server Stopped", error.Message);
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
            UpipeServer.CancelQuietly(job.Cancellation);
        }

        static void Finish(Job job, string state, string error = null, string details = null, JToken result = null)
        {
            job.State = state;
            job.Error = error;
            job.ErrorDetails = details;
            job.Result = result;
            job.Progress = null;
            job.FinishedAt = DateTime.UtcNow;
            var timeoutCts = job.TimeoutCancellation;
            job.TimeoutCancellation = null;
            if (timeoutCts != null)
            {
                try { timeoutCts.Cancel(); } catch (ObjectDisposedException) { }
                try { timeoutCts.Dispose(); } catch (ObjectDisposedException) { }
            }
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
            // `unity job status` shows it as given. Finish drops it.
            if (job.Progress != null)
                reply["progress"] = job.Progress.DeepClone();
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
            var evicted = finished.Skip(MaxFinishedJobs).Concat(finished.Where(j => now - j.FinishedAt > FinishedLifetime)).ToList();
            if (evicted.Count == 0)
                return;
            var spillDir = SpillDirectory;
            foreach (var job in evicted)
            {
                m_Jobs.Remove(job.Id);
                TryDeleteFile(Path.Combine(spillDir, job.Id + ".json"));
            }
        }

        // Main thread, before the domain unloads (SessionState is main-thread only). Work still queued or running
        // dies with the domain, so it is saved as failed; finished jobs keep their result (large payloads spill to Temp).
        public void SaveForReload()
        {
            List<Job> snapshot;
            List<Job> interrupted;
            lock (m_Lock)
            {
                m_Saved = true;
                interrupted = m_Jobs.Values.Where(j => !IsFinished(j)).ToList();
                foreach (var job in interrupted)
                    Finish(job, Failed, InterruptedError, InterruptedDetails);
                Prune();
                snapshot = m_Jobs.Values.ToList();
            }

            var spillDir = SpillDirectory;
            var saved = new JArray();
            foreach (var job in snapshot)
            {
                JObject entry;
                lock (m_Lock)
                {
                    entry = Describe(job);
                    entry["finishedAt"] = job.FinishedAt.Ticks;
                }
                if (job.State == Completed)
                {
                    var resultToken = entry["result"];
                    var serialized = resultToken != null ? resultToken.ToString(Formatting.None) : "null";
                    if (serialized.Length > MaxInlineResultChars && TryWriteSpillFile(spillDir, job.Id, serialized, out var fileName))
                    {
                        entry.Remove("result");
                        entry["resultFile"] = fileName;
                    }
                }
                saved.Add(entry);
            }
            SessionState.SetString(SavedJobsKey, saved.ToString(Formatting.None));
            // Outside m_Lock: cancelling lets off-main-thread work (such as dotnet csc.dll) exit promptly before unload.
            foreach (var job in interrupted)
                UpipeServer.CancelQuietly(job.Cancellation);
        }

        // On a manual stop: a reload while the server is off saves nothing, so the table saved before an earlier reload must
        // not come back when the server is turned on again.
        public void ForgetSaved() => SessionState.EraseString(SavedJobsKey);

        // Main thread, on Start: ensures initialization ran once per domain and clears m_Saved/m_Stopped so same-domain restarts keep jobs.
        public void Resume()
        {
            bool needInit;
            lock (m_Lock)
            {
                m_Saved = false;
                m_Stopped = false;
                needInit = !m_Initialized;
            }
            if (needInit)
                Initialize();
        }

        // Main thread, on a new domain load: restores the saved job table from SessionState and any spilled result files.
        public void Initialize()
        {
            var spillDir = SpillDirectory;
            var text = SessionState.GetString(SavedJobsKey, "");
            lock (m_Lock)
            {
                m_Saved = false;
                m_Stopped = false;
                m_Initialized = true;
                m_Jobs.Clear();
                if (text.Length > 0)
                {
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
                        var resultFile = (string)entry["resultFile"];
                        if (!string.IsNullOrEmpty(resultFile) && job.State == Completed)
                        {
                            if (TryReadSpillFile(Path.Combine(spillDir, resultFile), out var restored, out var readError))
                                job.Result = restored;
                            else
                            {
                                job.State = Failed;
                                job.Error = "Job Result Lost";
                                job.ErrorDetails = "Could not load the spilled job result after domain reload: " + readError;
                            }
                        }
                        if (job.Id != null)
                            m_Jobs[job.Id] = job;
                    }
                    Prune();
                }
                CleanupOrphanedSpillFiles(spillDir);
            }
        }

        static bool TryWriteSpillFile(string spillDir, string jobId, string json, out string fileName)
        {
            fileName = jobId + ".json";
            try
            {
                Directory.CreateDirectory(spillDir);
                File.WriteAllText(Path.Combine(spillDir, fileName), json, s_Utf8NoBom);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Upipe] Failed to write spilled job result for {jobId}: {e.Message}");
                return false;
            }
        }

        static bool TryReadSpillFile(string path, out JToken result, out string error)
        {
            result = null;
            error = null;
            try
            {
                var raw = File.ReadAllText(path, Encoding.UTF8);
                var reader = new JsonTextReader(new StringReader(raw)) { DateParseHandling = DateParseHandling.None };
                result = JToken.Load(reader);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        void CleanupOrphanedSpillFiles(string spillDir)
        {
            if (!Directory.Exists(spillDir))
                return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(spillDir, "*.json"))
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (!m_Jobs.ContainsKey(id))
                        TryDeleteFile(file);
                }
            }
            catch (Exception)
            {
            }
        }

        static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
            }
        }
    }
}
