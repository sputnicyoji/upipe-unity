using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace Upipe.Editor.Tests
{
    // A job table of the test's own (never JobManager.Live, which may hold the job running these tests): each job's
    // work is a TaskCompletionSource the test settles by hand.
    public class JobManagerTests
    {
        sealed class FakeWork
        {
            public readonly TaskCompletionSource<object> Completion = new TaskCompletionSource<object>();
            public CancellationToken Token;
            public Action Started;
        }

        readonly List<string> m_Submitted = new List<string>();
        JobManager m_Jobs;

        [SetUp]
        public void NewTable() => m_Jobs = new JobManager("Upipe.Jobs.Tests");

        [TearDown]
        public void CancelLeftovers()
        {
            foreach (var id in m_Submitted)
                m_Jobs.Cancel(id);
            m_Submitted.Clear();
            SessionState.EraseString(m_Jobs.SavedJobsKey);
        }

        string Submit(FakeWork work, int? timeout = null)
        {
            var job = m_Jobs.Submit("test work", timeout, (token, started) =>
            {
                work.Token = token;
                work.Started = started;
                return work.Completion.Task;
            });
            Assert.IsNotNull(job, "refused");
            var id = (string)job["jobId"];
            m_Submitted.Add(id);
            return id;
        }

        // Completion is reported from a thread-pool continuation.
        JObject WaitForState(string id, string state)
        {
            JObject status = null;
            SpinWait.SpinUntil(() => (string)(status = m_Jobs.Status(id))["state"] == state, 5000);
            Assert.AreEqual(state, (string)status["state"], status.ToString());
            return status;
        }

        [Test]
        public void JobGoesFromQueuedToRunningToCompleted()
        {
            var work = new FakeWork();
            var id = Submit(work);
            var status = m_Jobs.Status(id);
            Assert.AreEqual("queued", (string)status["state"]);
            Assert.AreEqual("test work", (string)status["command"]);
            Assert.IsFalse((bool)status["cancellationRequested"]);
            Assert.IsFalse(status.ContainsKey("result"));

            work.Started();
            Assert.AreEqual("running", (string)m_Jobs.Status(id)["state"]);
            work.Completion.SetResult(new { answer = 42 });
            status = WaitForState(id, "completed");
            Assert.AreEqual(42, (int)status["result"]["answer"]);
            Assert.IsFalse(status.ContainsKey("error"));
        }

        [Test]
        public void FailureKeepsTheCommandsErrorAndDetails()
        {
            var work = new FakeWork();
            var id = Submit(work);
            work.Started();
            work.Completion.SetException(new CommandFailure("Command Execution Failed", "RUNTIME_EXCEPTION: boom"));
            var status = WaitForState(id, "failed");
            Assert.AreEqual("Command Execution Failed", (string)status["error"]);
            Assert.AreEqual("RUNTIME_EXCEPTION: boom", (string)status["errorDetails"]);
            Assert.IsFalse(status.ContainsKey("result"));
        }

        [Test]
        public void CancelledWhileQueuedNeverRuns()
        {
            var work = new FakeWork();
            var id = Submit(work);
            var status = m_Jobs.Cancel(id);
            Assert.AreEqual("canceled", (string)status["state"]);
            Assert.IsTrue((bool)status["cancellationRequested"]);
            Assert.IsTrue(work.Token.IsCancellationRequested);
            Assert.Throws<OperationCanceledException>(() => work.Started(), "the handler must not start");
        }

        [Test]
        public void CancelledWhileRunningReportsCanceledOnceTheHandlerGivesUp()
        {
            var work = new FakeWork();
            var id = Submit(work);
            work.Started();
            var status = m_Jobs.Cancel(id);
            Assert.AreEqual("running", (string)status["state"], "still running until the handler stops");
            Assert.IsTrue((bool)status["cancellationRequested"]);
            Assert.IsTrue(work.Token.IsCancellationRequested);
            work.Completion.SetException(new OperationCanceledException(work.Token));
            WaitForState(id, "canceled");
        }

        [Test]
        public void CancelAfterFinishingChangesNothing()
        {
            var work = new FakeWork();
            var id = Submit(work);
            work.Started();
            work.Completion.SetResult(1);
            WaitForState(id, "completed");
            var status = m_Jobs.Cancel(id);
            Assert.AreEqual("completed", (string)status["state"]);
            Assert.IsFalse((bool)status["cancellationRequested"]);
        }

        [Test]
        public void TimeoutFailsTheJobAndCancelsItsToken()
        {
            var work = new FakeWork();
            var id = Submit(work, timeout: 50);
            work.Started();
            var status = WaitForState(id, "failed");
            Assert.AreEqual("Request Timed Out", (string)status["error"]);
            Assert.IsTrue(work.Token.IsCancellationRequested);
            work.Completion.SetResult(1); // a late result is dropped
            Thread.Sleep(50);
            Assert.AreEqual("failed", (string)m_Jobs.Status(id)["state"]);
        }

        [Test]
        public void SubmissionsPastTheCapAreRefused()
        {
            for (var i = 0; i < JobManager.MaxUnfinishedJobs; i++)
                Submit(new FakeWork());
            Assert.IsNull(m_Jobs.Submit("one too many", null, (token, started) => Task.FromResult<object>(null)));
            m_Jobs.Cancel(m_Submitted[0]);
            Submit(new FakeWork()); // room again
        }

        [Test]
        public void UnknownJobIsNull()
        {
            Assert.IsNull(m_Jobs.Status("nope"));
            Assert.IsNull(m_Jobs.Cancel("nope"));
        }

        [Test]
        public void DomainReloadTurnsUnfinishedJobsIntoFailuresAndKeepsFinishedOnes()
        {
            var queued = Submit(new FakeWork());
            var running = new FakeWork();
            var runningId = Submit(running);
            running.Started();
            var done = new FakeWork();
            var doneId = Submit(done);
            done.Started();
            done.Completion.SetResult("2026-11-19T12:00:00Z"); // an ISO date string must come back as a string
            WaitForState(doneId, "completed");

            m_Jobs.SaveForReload();
            // Between saving and the reload no job is taken: it would never be saved.
            Assert.Throws<EditorReloadingException>(() => Submit(new FakeWork()));
            m_Jobs.Initialize(); // what the next domain does

            foreach (var id in new[] { queued, runningId })
            {
                var status = m_Jobs.Status(id);
                Assert.IsNotNull(status, "a job cut short by the reload must not read as unknown");
                Assert.AreEqual("failed", (string)status["state"]);
                Assert.AreEqual(JobManager.InterruptedError, (string)status["error"]);
                StringAssert.Contains("domain reload", (string)status["errorDetails"]);
            }
            var finished = m_Jobs.Status(doneId);
            Assert.AreEqual("completed", (string)finished["state"]);
            Assert.AreEqual(JTokenType.String, finished["result"].Type);
            Assert.AreEqual("2026-11-19T12:00:00Z", (string)finished["result"]);

            // A second reload keeps them too.
            m_Jobs.SaveForReload();
            m_Jobs.Initialize();
            Assert.AreEqual("failed", (string)m_Jobs.Status(queued)["state"]);
            Assert.AreEqual("completed", (string)m_Jobs.Status(doneId)["state"]);
        }
    }
}
