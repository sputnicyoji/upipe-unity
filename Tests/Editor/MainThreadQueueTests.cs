using System.Collections;
using System.Threading;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Upipe.Editor.Tests
{
    public class MainThreadQueueTests
    {
        [UnityTest]
        public IEnumerator QueuedWorkRunsOnTheMainThread()
        {
            Assume.That(MainThreadQueue.IsAttached, Is.True, "the Upipe server is disabled here, so nothing pumps the main-thread queue");
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var ranOn = -1;
            var task = MainThreadQueue.Run(() =>
            {
                ranOn = Thread.CurrentThread.ManagedThreadId;
                return 42;
            }, CancellationToken.None);

            for (var frame = 0; frame < 100 && !task.IsCompleted; frame++)
                yield return null;

            Assert.IsTrue(task.IsCompleted, "work was not pumped");
            Assert.AreEqual(42, task.Result);
            Assert.AreEqual(mainThread, ranOn);
        }

        [UnityTest]
        public IEnumerator WorkCancelledWhileQueuedNeverRuns()
        {
            Assume.That(MainThreadQueue.IsAttached, Is.True, "the Upipe server is disabled here, so nothing pumps the main-thread queue");
            var ran = false;
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var task = MainThreadQueue.Run(() =>
                {
                    ran = true;
                    return null;
                }, cancellation.Token);

                for (var frame = 0; frame < 100 && !task.IsCompleted; frame++)
                    yield return null;

                Assert.IsTrue(task.IsCanceled);
                Assert.IsFalse(ran);
            }
        }

        [Test]
        public void NewDomainRequiresColdSettleFirstThenFirstIdleTickAfterReload()
        {
            var saved = MainThreadQueue.CaptureTestState();
            try
            {
                MainThreadQueue.ResetForTests(settled: false, coldSettled: false, lastTickUtcTicks: 0);
                MainThreadQueue.AdvanceSettle(10.0, isCompiling: false, isUpdating: false, transitioningPlayMode: false);
                Assert.IsFalse(MainThreadQueue.Settled, "cold start needs 1.0s continuous idle");

                MainThreadQueue.AdvanceSettle(10.6, isCompiling: true, isUpdating: false, transitioningPlayMode: false);
                Assert.IsFalse(MainThreadQueue.Settled, "compiling resets cold idle timer");

                MainThreadQueue.AdvanceSettle(11.1, isCompiling: false, isUpdating: false, transitioningPlayMode: false);
                MainThreadQueue.AdvanceSettle(11.8, isCompiling: false, isUpdating: false, transitioningPlayMode: false);
                Assert.IsFalse(MainThreadQueue.Settled, "elapsed idle since compile ended is only 0.7s");

                MainThreadQueue.AdvanceSettle(12.2, isCompiling: false, isUpdating: false, transitioningPlayMode: false);
                Assert.IsTrue(MainThreadQueue.Settled, "1.1s continuous idle completes cold start settle");

                // Simulate a subsequent domain in the same Editor session after reload/Play entry.
                MainThreadQueue.ResetForTests(settled: false, coldSettled: true, lastTickUtcTicks: 0);
                Assert.IsFalse(MainThreadQueue.Settled, "a fresh domain never starts ready before ticking");

                MainThreadQueue.AdvanceSettle(20.0, isCompiling: true, isUpdating: false, transitioningPlayMode: false);
                Assert.IsFalse(MainThreadQueue.Settled, "busy compiling cannot settle");

                MainThreadQueue.AdvanceSettle(20.1, isCompiling: false, isUpdating: false, transitioningPlayMode: true);
                Assert.IsFalse(MainThreadQueue.Settled, "Play Mode transition cannot settle");

                MainThreadQueue.AdvanceSettle(20.2, isCompiling: false, isUpdating: false, transitioningPlayMode: false);
                Assert.IsTrue(MainThreadQueue.Settled, "first idle tick after warm reload settles immediately");
            }
            finally
            {
                MainThreadQueue.RestoreTestState(saved);
            }
        }

        [Test]
        public void LastTickAgeMsIsNullBeforePumpAndNonNegativeAfterPump()
        {
            var saved = MainThreadQueue.CaptureTestState();
            try
            {
                MainThreadQueue.ResetForTests(settled: true, coldSettled: true, lastTickUtcTicks: 0);
                Assert.IsNull(MainThreadQueue.LastTickAgeMs());
                Assert.AreEqual(Newtonsoft.Json.Linq.JTokenType.Null, MainThreadQueue.StateFields()["lastTickAgeMs"].Type);

                MainThreadQueue.Pump();
                var age = MainThreadQueue.LastTickAgeMs();
                Assert.IsTrue(age.HasValue && age.Value >= 0);
                Assert.AreEqual(age.Value, (long)MainThreadQueue.StateFields()["lastTickAgeMs"]);
            }
            finally
            {
                MainThreadQueue.RestoreTestState(saved);
            }
        }

        [Test]
        public void RunRejectsImmediatelyWhenDetachedOrReloading()
        {
            var saved = MainThreadQueue.CaptureTestState();
            try
            {
                MainThreadQueue.Detach(forReload: false);
                var stopped = MainThreadQueue.Run(() => 1, CancellationToken.None);
                Assert.IsTrue(stopped.IsFaulted);
                Assert.IsInstanceOf<ServerStoppedException>(stopped.Exception.InnerException);

                MainThreadQueue.RestoreTestState(saved);
                MainThreadQueue.ResetForTests(settled: true, coldSettled: true, lastTickUtcTicks: 1);
                MainThreadQueue.MarkDomainReloading();
                Assert.IsFalse(MainThreadQueue.Settled, "domain reload drain must stop reporting settled");
                var reloading = MainThreadQueue.Run(() => 1, CancellationToken.None);
                Assert.IsTrue(reloading.IsFaulted);
                Assert.IsInstanceOf<EditorReloadingException>(reloading.Exception.InnerException);

                MainThreadQueue.RestoreTestState(saved);
                MainThreadQueue.ResetForTests(settled: true, coldSettled: true, lastTickUtcTicks: 1);
                MainThreadQueue.Detach(forReload: false);
                Assert.IsFalse(MainThreadQueue.Settled, "detach must clear settled");
            }
            finally
            {
                MainThreadQueue.RestoreTestState(saved);
            }
        }

        [Test]
        public void RunAndWaitStopsBlockingImmediatelyWhenCancelledWhileQueued()
        {
            Assume.That(MainThreadQueue.IsAttached, Is.True, "the Upipe server is disabled here, so nothing pumps the main-thread queue");
            var saved = MainThreadQueue.CaptureTestState();
            try
            {
                // This [Test] runs synchronously on the main thread, so nothing pumps MainThreadQueue while Wait runs.
                using (var cancel = new CancellationTokenSource())
                {
                    var task = System.Threading.Tasks.Task.Run(() => MainThreadQueue.RunAndWait(() => 1, cancel.Token, "FAIL"));
                    Thread.Sleep(30);
                    cancel.Cancel();
                    Assert.IsTrue(task.Wait(1000), "RunAndWait stayed blocked on an unpumped queue after cancel");
                }
            }
            catch (System.AggregateException e)
            {
                Assert.IsInstanceOf<System.OperationCanceledException>(UpipeServer.Unwrap(e));
                return;
            }
            finally
            {
                MainThreadQueue.FailPending(forReload: false);
                MainThreadQueue.RestoreTestState(saved);
            }
            Assert.Fail("expected OperationCanceledException");
        }
    }
}
