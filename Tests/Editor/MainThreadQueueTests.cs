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
    }
}
