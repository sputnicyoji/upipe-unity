using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UpipeFixtures
{
    // A test that passes, then a cleanup that logs an error: test-framework 1.4+ turns that into a run error
    // ("Multiple unexpected errors happened while running tests."), 1.1.33 does not. Named only, and the cleanup logs
    // only after the test ran, so a run that merely includes the fixture is not affected.
    [PostBuildCleanup(typeof(LogsAnErrorOnCleanup))]
    public class CleanupErrorFixture
    {
        [Test, Explicit("named only: its cleanup logs an error")]
        public void PassesBeforeACleanupError()
        {
            LogsAnErrorOnCleanup.Armed = true;
            Assert.Pass();
        }
    }

    public class LogsAnErrorOnCleanup : IPostBuildCleanup
    {
        internal static bool Armed;

        public void Cleanup()
        {
            if (!Armed)
                return;
            Armed = false;
            Debug.LogError("UpipeFixtures: cleanup error by design");
        }
    }
}
