// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

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
            public Action<string, int?> Progress;
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
            var job = m_Jobs.Submit("test work", timeout, (token, started, progress) =>
            {
                work.Token = token;
                work.Started = started;
                work.Progress = progress;
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
            Assert.IsNull(m_Jobs.Submit("one too many", null, (token, started, progress) => Task.FromResult<object>(null)));
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
            Assert.IsTrue(running.Token.IsCancellationRequested, "reload must cancel unfinished job tokens so subprocesses exit");
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

        // A manual stop forgets the copy saved before an earlier reload: a reload while the server is off saves nothing,
        // and the old copy must not come back when the server is turned on again.
        [Test]
        public void AManualStopForgetsTheSavedTable()
        {
            var done = new FakeWork();
            var doneId = Submit(done);
            done.Started();
            done.Completion.SetResult(1);
            WaitForState(doneId, "completed");
            m_Jobs.SaveForReload();
            m_Jobs.Initialize();
            Assert.AreEqual("completed", (string)m_Jobs.Status(doneId)["state"]);
            m_Jobs.ForgetSaved();
            m_Jobs.Initialize(); // the domain after a reload the stopped server did not save
            Assert.IsNull(m_Jobs.Status(doneId));
        }

        // A manual stop: queued work never runs, running work is told to stop and reads as failed.
        [Test]
        public void CancelAllEndsQueuedJobsAsCanceledAndRunningOnesAsFailed()
        {
            var queuedId = Submit(new FakeWork());
            var running = new FakeWork();
            var runningId = Submit(running);
            running.Started();
            m_Jobs.CancelAll();
            Assert.AreEqual("canceled", (string)m_Jobs.Status(queuedId)["state"]);
            var stopped = m_Jobs.Status(runningId);
            Assert.AreEqual("failed", (string)stopped["state"]);
            Assert.AreEqual("Server Stopped", (string)stopped["error"]);
            Assert.IsTrue(running.Token.IsCancellationRequested);

            // Shutdown passes stopAccepting=true so racing HTTP threads cannot spawn zombie jobs before Resume().
            m_Jobs.CancelAll(stopAccepting: true);
            Assert.Throws<ServerStoppedException>(() => Submit(new FakeWork()));
            m_Jobs.Resume();
            Submit(new FakeWork());
        }

        // A script's own token callback may throw; the other jobs are still cancelled and nothing escapes to the caller.
        [Test]
        public void ACancellationCallbackThatThrowsStopsNothingElse()
        {
            var first = new FakeWork();
            var firstId = Submit(first);
            first.Started();
            first.Token.Register(() => throw new InvalidOperationException("from a script"));
            var second = new FakeWork();
            var secondId = Submit(second);
            second.Started();
            LogAssert.Expect(LogType.Warning, new Regex("A cancellation callback threw"));
            Assert.DoesNotThrow(() => m_Jobs.CancelAll());
            Assert.IsTrue(second.Token.IsCancellationRequested);
            Assert.AreEqual("failed", (string)m_Jobs.Status(secondId)["state"]);
            Assert.AreEqual("failed", (string)m_Jobs.Status(firstId)["state"]);
        }

        [Test]
        public void RunningJobReportsProgressUntilItFinishes()
        {
            var work = new FakeWork();
            var id = Submit(work);
            Assert.IsFalse(m_Jobs.Status(id).ContainsKey("progress"), "nothing reported yet");
            work.Started();
            work.Progress("step 2", 40);
            var progress = m_Jobs.Status(id)["progress"];
            Assert.AreEqual("step 2", (string)progress["message"]);
            Assert.AreEqual(40, (int)progress["percent"]);

            work.Progress(null, 250);
            Assert.AreEqual("step 2", (string)m_Jobs.Status(id)["progress"]["message"], "a percent alone keeps the message");
            Assert.AreEqual(100, (int)m_Jobs.Status(id)["progress"]["percent"], "clamped");
            work.Progress(null, -5);
            Assert.AreEqual(0, (int)m_Jobs.Status(id)["progress"]["percent"]);
            work.Progress("only a message", null);
            Assert.AreEqual(JTokenType.Null, m_Jobs.Status(id)["progress"]["percent"].Type);

            work.Completion.SetResult(1);
            Assert.IsFalse(WaitForState(id, "completed").ContainsKey("progress"), "a finished job shows no stale progress");
            work.Progress("late", 99);
            Assert.IsFalse(m_Jobs.Status(id).ContainsKey("progress"));
        }

        [Test]
        public void EveryWayAJobEndsDropsItsProgress()
        {
            var canceled = new FakeWork();
            var canceledId = Submit(canceled);
            canceled.Started();
            canceled.Progress("half", 50);
            Assert.IsTrue(m_Jobs.Cancel(canceledId).ContainsKey("progress"), "still running until the handler stops");
            canceled.Completion.SetException(new OperationCanceledException(canceled.Token));
            Assert.IsFalse(WaitForState(canceledId, "canceled").ContainsKey("progress"));

            // Reported while still queued, so a slow machine cannot start it after the timeout fired.
            var timedOut = new FakeWork();
            var timedOutId = Submit(timedOut, timeout: 50);
            timedOut.Progress("half", 50);
            Assert.IsFalse(WaitForState(timedOutId, "failed").ContainsKey("progress"));

            var interrupted = new FakeWork();
            var interruptedId = Submit(interrupted);
            interrupted.Started();
            interrupted.Progress("half", 50);
            m_Jobs.SaveForReload();
            m_Jobs.Initialize();
            var status = m_Jobs.Status(interruptedId);
            Assert.AreEqual("failed", (string)status["state"]);
            Assert.IsFalse(status.ContainsKey("progress"));
        }

        [Test]
        public void ConcurrentReportsLeaveOneWholeSnapshot()
        {
            var work = new FakeWork();
            var id = Submit(work);
            work.Started();
            Parallel.For(0, 200, i => work.Progress("step " + i, i % 101));
            var progress = m_Jobs.Status(id)["progress"];
            var step = int.Parse(((string)progress["message"]).Substring(5));
            Assert.AreEqual(step % 101, (int)progress["percent"], "message and percent come from the same report");
            work.Completion.SetResult(1);
            WaitForState(id, "completed");
        }

        [Test]
        public void TimeoutTimerDisposesImmediatelyWhenJobCompletes()
        {
            var work = new FakeWork();
            var id = Submit(work, timeout: 600000);
            Assert.IsTrue(m_Jobs.HasActiveTimeout(id));
            work.Started();
            work.Completion.SetResult(7);
            WaitForState(id, "completed");
            Assert.IsFalse(m_Jobs.HasActiveTimeout(id));
        }

        [Test]
        public void LargeCompletedResultSpillsToDiskAcrossReloadAndCleansUpOnPrune()
        {
            var spillDir = Path.Combine(Path.GetTempPath(), "UpipeSpillTests_" + Guid.NewGuid().ToString("N"));
            var spillKey = "Upipe.Jobs.SpillTests." + Guid.NewGuid().ToString("N");
            try
            {
                var table = new JobManager(spillKey, spillDir);
                var work = new FakeWork();
                var largeId = (string)table.Submit("big", null, (t, s, p) => { work.Token = t; work.Started = s; return work.Completion.Task; })["jobId"];
                work.Started();
                var bigText = new string('z', JobManager.MaxInlineResultChars + 4096);
                work.Completion.SetResult(new JObject { ["date"] = "2026-11-20T00:00:00Z", ["blob"] = bigText });
                SpinWait.SpinUntil(() => (string)table.Status(largeId)["state"] == JobManager.Completed, 5000);

                table.SaveForReload();
                Assert.Less(SessionState.GetString(spillKey, "").Length, 2048, "large payload must spill out of SessionState");
                Assert.IsTrue(File.Exists(Path.Combine(spillDir, largeId + ".json")));

                table.Initialize();
                var restored = table.Status(largeId);
                Assert.AreEqual(JobManager.Completed, (string)restored["state"]);
                Assert.AreEqual(JTokenType.String, restored["result"]["date"].Type);
                Assert.AreEqual("2026-11-20T00:00:00Z", (string)restored["result"]["date"]);
                Assert.AreEqual(bigText, (string)restored["result"]["blob"]);

                // Evict the large job past MaxFinishedJobs (100) and verify its spill file is removed.
                Thread.Sleep(5);
                for (var i = 0; i < 105; i++)
                {
                    var w = new FakeWork();
                    var smallId = (string)table.Submit("small", null, (t, s, p) => { w.Token = t; w.Started = s; return w.Completion.Task; })["jobId"];
                    w.Started();
                    w.Completion.SetResult(i);
                    SpinWait.SpinUntil(() => (string)table.Status(smallId)["state"] == JobManager.Completed, 5000);
                }
                Assert.IsNull(table.Status(largeId));
                Assert.IsFalse(File.Exists(Path.Combine(spillDir, largeId + ".json")), "evicted job's spill file must be deleted");
            }
            finally
            {
                SessionState.EraseString(spillKey);
                if (Directory.Exists(spillDir))
                    Directory.Delete(spillDir, true);
            }
        }

        // A spill file gone by the next domain leaves a failed job, not a completed one without its result.
        [Test]
        public void ALostSpillFileFailsTheJob()
        {
            var spillDir = Path.Combine(Path.GetTempPath(), "UpipeSpillTests_" + Guid.NewGuid().ToString("N"));
            var spillKey = "Upipe.Jobs.SpillTests." + Guid.NewGuid().ToString("N");
            try
            {
                var table = new JobManager(spillKey, spillDir);
                var work = new FakeWork();
                var id = (string)table.Submit("big", null, (t, s, p) => { work.Token = t; work.Started = s; return work.Completion.Task; })["jobId"];
                work.Started();
                work.Completion.SetResult(new string('z', JobManager.MaxInlineResultChars + 4096));
                SpinWait.SpinUntil(() => (string)table.Status(id)["state"] == JobManager.Completed, 5000);
                table.SaveForReload();
                File.Delete(Path.Combine(spillDir, id + ".json"));
                table.Initialize();
                var lost = table.Status(id);
                Assert.AreEqual("failed", (string)lost["state"]);
                Assert.AreEqual("Job Result Lost", (string)lost["error"]);
            }
            finally
            {
                SessionState.EraseString(spillKey);
                if (Directory.Exists(spillDir))
                    Directory.Delete(spillDir, true);
            }
        }
    }
}
