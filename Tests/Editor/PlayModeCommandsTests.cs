using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Upipe.Editor.Tests
{
    // Play Mode itself cannot be entered from an EditMode test; these cover the parts that stay in Edit Mode.
    public class PlayModeCommandsTests
    {
        int m_PriorInterval;
        int m_PriorPersisted;
        bool m_PriorRunInBackground;
        bool m_Remembered;

        [SetUp]
        public void Remember()
        {
            m_Remembered = false;
            Assume.That(PlayModeCommands.IsRunInBackgroundOverridden, Is.False, "an override from outside the test is active");
            m_PriorInterval = AutoTick.IntervalMs;
            m_PriorPersisted = SessionState.GetInt(AutoTick.IntervalKey, 0);
            m_PriorRunInBackground = Application.runInBackground;
            m_Remembered = true;
        }

        [TearDown]
        public void PutBack()
        {
            // A skipped test leaves an outside override as it found it; restoring it here would leave an unrecorded value.
            if (!m_Remembered)
                return;
            AutoTick.Set(m_PriorInterval, m_PriorPersisted > 0);
            PlayModeCommands.RestoreRunInBackground();
            Application.runInBackground = m_PriorRunInBackground;
        }

        [Test]
        public void AutoTickRejectsBadParameters()
        {
            Assert.Throws<CommandFailure>(() => PlayModeCommands.SetAutoTick(new JObject { ["interval-ms"] = "0" }));
            Assert.Throws<CommandFailure>(() => PlayModeCommands.SetAutoTick(new JObject { ["interval-ms"] = "60001" }));
            Assert.Throws<CommandFailure>(() => PlayModeCommands.SetAutoTick(new JObject { ["interval-ms"] = "fast" }));
            Assert.Throws<CommandFailure>(() => PlayModeCommands.SetAutoTick(new JObject { ["enable"] = "maybe" }));
            Assert.Throws<CommandFailure>(() => PlayModeCommands.SetAutoTick(new JObject { ["persist"] = 1 }));
        }

        [Test]
        public void AutoTickDefaultsAndSessionStatePersistence()
        {
            var reply = (JObject)PlayModeCommands.SetAutoTick(new JObject());
            Assert.AreEqual(true, (bool)reply["enabled"]);
            Assert.AreEqual(16, (int)reply["intervalMs"]);
            Assert.AreEqual(true, (bool)reply["persist"]);
            Assert.AreEqual(16, AutoTick.IntervalMs);
            Assert.AreEqual(16, SessionState.GetInt(AutoTick.IntervalKey, 0));

            // Structured requests send JSON booleans and numbers; argv binds booleans and strings.
            PlayModeCommands.SetAutoTick(new JObject { ["interval-ms"] = 40, ["persist"] = false });
            Assert.AreEqual(40, AutoTick.IntervalMs);
            Assert.AreEqual(0, SessionState.GetInt(AutoTick.IntervalKey, -1), "a one-off setting must not come back after a reload");

            PlayModeCommands.SetAutoTick(new JObject { ["interval-ms"] = "25", ["persist"] = "true" });
            Assert.AreEqual(25, SessionState.GetInt(AutoTick.IntervalKey, 0));

            PlayModeCommands.SetAutoTick(new JObject { ["enable"] = false });
            Assert.AreEqual(0, AutoTick.IntervalMs);
            Assert.AreEqual(0, SessionState.GetInt(AutoTick.IntervalKey, -1));
        }

        [Test]
        public void OverrideRecordsTheProjectValueOnce()
        {
            var project = Application.runInBackground;
            PlayModeCommands.OverrideRunInBackground(!project);
            Assert.AreEqual(!project, Application.runInBackground);
            Assert.IsTrue(PlayModeCommands.IsRunInBackgroundOverridden);
            // A second override must not record the overridden value as the project's.
            PlayModeCommands.OverrideRunInBackground(!project);
            PlayModeCommands.RestoreRunInBackground();
            Assert.AreEqual(project, Application.runInBackground);
            Assert.IsFalse(PlayModeCommands.IsRunInBackgroundOverridden);
            // Restoring without an override changes nothing.
            PlayModeCommands.RestoreRunInBackground();
            Assert.AreEqual(project, Application.runInBackground);
        }

        [Test]
        public void SetRunInBackgroundFalsePutsTheProjectValueBack([Values(false, true)] bool project)
        {
            Application.runInBackground = project;

            var untouched = (JObject)PlayModeCommands.SetRunInBackground(new JObject { ["enable"] = false });
            Assert.AreEqual(project, (bool)untouched["runInBackground"], "enable=false restores; it does not force false");
            Assert.AreEqual(false, (bool)untouched["overridden"]);

            var on = (JObject)PlayModeCommands.SetRunInBackground(new JObject());
            Assert.AreEqual(true, (bool)on["runInBackground"]);
            Assert.AreEqual(true, (bool)on["overridden"]);

            var back = (JObject)PlayModeCommands.SetRunInBackground(new JObject { ["enable"] = "false" });
            Assert.AreEqual(project, (bool)back["runInBackground"]);
            Assert.AreEqual(false, (bool)back["overridden"]);
            Assert.AreEqual(project, Application.runInBackground);
        }

        [Test]
        public void SetRunInBackgroundRejectsANonBoolean()
        {
            Assert.Throws<CommandFailure>(() => PlayModeCommands.SetRunInBackground(new JObject { ["enable"] = "yes" }));
            Assert.IsFalse(PlayModeCommands.IsRunInBackgroundOverridden);
        }

        [Test]
        public void BuildGuardTemporarilyRestoresRunInBackgroundAroundBuilds()
        {
            var project = Application.runInBackground;
            PlayModeCommands.OverrideRunInBackground(!project);
            Assert.AreEqual(!project, Application.runInBackground);

            var guard = new RunInBackgroundBuildGuard();
            guard.OnPreprocessBuild(null);
            Assert.AreEqual(project, Application.runInBackground, "player builds must see the saved project setting");
            Assert.IsTrue(PlayModeCommands.IsRunInBackgroundOverridden);

            guard.OnPostprocessBuild(null);
            Assert.AreEqual(!project, Application.runInBackground, "the session override returns after the build");
        }
    }
}
