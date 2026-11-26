using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Upipe.Editor.Tests
{
    // Adds tagged entries to the live buffer and reads them back through the command handler.
    public class ConsoleCommandsTests
    {
        static JObject Get(object parameters) => (JObject)ConsoleCommands.Get(JObject.FromObject(parameters));

        // Unique per test, so entries other tests or the Editor log never match.
        static string NewTag() => "upipe-test-" + Guid.NewGuid().ToString("N") + ":";

        static string[] Messages(JObject reply, string tag)
        {
            return reply["entries"].Select(e => (string)e["message"]).Where(m => m.StartsWith(tag, StringComparison.Ordinal)).ToArray();
        }

        [Test]
        public void LevelFilterTailAndMapping()
        {
            var tag = NewTag();
            var since = ConsoleCommands.Position.cursor;
            ConsoleCommands.Add(LogType.Log, tag + "log", "");
            ConsoleCommands.Add(LogType.Warning, tag + "warn", "");
            ConsoleCommands.Add(LogType.Exception, tag + "exception", "trace");

            var all = Get(new { since = since.ToString() });
            CollectionAssert.AreEqual(new[] { tag + "log", tag + "warn", tag + "exception" }, Messages(all, tag));
            Assert.AreEqual("error", (string)all["entries"].Last()["level"]);
            Assert.AreEqual("Exception", (string)all["entries"].Last()["logType"]);
            Assert.AreEqual(since + 3, (long)all["cursor"]);

            CollectionAssert.AreEqual(new[] { tag + "warn", tag + "exception" }, Messages(Get(new { since = since.ToString(), level = "warn" }), tag));
            CollectionAssert.AreEqual(new[] { tag + "exception" }, Messages(Get(new { since = since.ToString(), tail = "1" }), tag));
        }

        [Test]
        public void ErrorsSinceKeepsTheLastErrorLevelMessages()
        {
            var tag = NewTag();
            var since = ConsoleCommands.Position.cursor;
            ConsoleCommands.Add(LogType.Error, tag + "error", "");
            ConsoleCommands.Add(LogType.Log, tag + "log", "");
            ConsoleCommands.Add(LogType.Warning, tag + "warn", "");
            ConsoleCommands.Add(LogType.Exception, tag + "exception", "");
            ConsoleCommands.Add(LogType.Assert, tag + "assert", "");
            string[] Ours(int max) => ConsoleCommands.ErrorsSince(since, max).Where(m => m.StartsWith(tag, StringComparison.Ordinal)).ToArray();
            CollectionAssert.AreEqual(new[] { tag + "error", tag + "exception", tag + "assert" }, Ours(10));
            CollectionAssert.AreEqual(new[] { tag + "exception", tag + "assert" }, Ours(2));
            CollectionAssert.IsEmpty(ConsoleCommands.ErrorsSince(ConsoleCommands.Position.cursor, 10));
        }

        [Test]
        public void CursorFollowsAndForeignSessionResets()
        {
            var tag = NewTag();
            var (cursor, session) = ConsoleCommands.Position;
            ConsoleCommands.Add(LogType.Log, tag + "after", "");

            var follow = (JObject)ConsoleCommands.Get(new JObject { ["since"] = cursor.ToString(), ["since-session"] = session });
            Assert.IsFalse((bool)follow["reset"]);
            Assert.AreEqual(1, follow["entries"].Count());

            var foreign = (JObject)ConsoleCommands.Get(new JObject { ["since"] = cursor.ToString(), ["since-session"] = "other" });
            Assert.IsTrue((bool)foreign["reset"]);
            Assert.AreEqual(session, (string)foreign["session"]);

            var ahead = Get(new { since = (cursor + 1000000).ToString() });
            Assert.IsTrue((bool)ahead["reset"]);
        }

        [Test]
        public void TailCuttingAFollowedRangeReportsDropped()
        {
            var tag = NewTag();
            var since = ConsoleCommands.Position.cursor;
            ConsoleCommands.Add(LogType.Log, tag + "1", "");
            ConsoleCommands.Add(LogType.Log, tag + "2", "");
            ConsoleCommands.Add(LogType.Log, tag + "3", "");

            Assert.IsTrue((bool)Get(new { since = since.ToString(), tail = "2" })["dropped"]);
            Assert.IsFalse((bool)Get(new { since = since.ToString(), tail = "3" })["dropped"]);
        }

        [Test]
        public void HugeMessagesAreClipped()
        {
            var since = ConsoleCommands.Position.cursor;
            ConsoleCommands.Add(LogType.Log, new string('x', 100000), "");
            var message = (string)Get(new { since = since.ToString() })["entries"].Last()["message"];
            Assert.Less(message.Length, 20000);
            StringAssert.EndsWith("[truncated]", message);
        }

        [Test]
        public void InvalidLevelIsRejected()
        {
            Assert.Throws<CommandFailure>(() => Get(new { level = "verbose" }));
        }

        [Test]
        public void ClearDropsEarlierEntriesAndKeepsTheCursor()
        {
            var tag = NewTag();
            var (before, session) = ConsoleCommands.Position;
            ConsoleCommands.Add(LogType.Error, tag + "old", "");
            ConsoleCommands.Clear();
            var (cleared, sameSession) = ConsoleCommands.Position;
            Assert.AreEqual(session, sameSession);
            Assert.GreaterOrEqual(cleared, before + 1, "numbering continues after a clear");
            CollectionAssert.IsEmpty(Messages(Get(new { tail = "2000" }), tag));

            ConsoleCommands.Add(LogType.Log, tag + "new", "");
            var follow = (JObject)ConsoleCommands.Get(new JObject { ["since"] = before.ToString(), ["since-session"] = session });
            CollectionAssert.AreEqual(new[] { tag + "new" }, Messages(follow, tag));
            Assert.IsFalse((bool)follow["reset"]);
            Assert.IsFalse((bool)follow["dropped"], "cleared entries were not missed");
        }

        // A reload keeps the newest entries, the clear baseline and the session; numbering restarts past what the old
        // domain may have handed out, so an old cursor still follows without skipping or re-reading anything.
        [Test]
        public void CursorAndClearBaselineSurviveAReload()
        {
            var tag = NewTag();
            var start = ConsoleCommands.Position.cursor;
            ConsoleCommands.Add(LogType.Log, tag + "cleared", "");
            ConsoleCommands.Clear();
            ConsoleCommands.Add(LogType.Log, tag + "kept", "");
            var (cursor, session) = ConsoleCommands.Position;

            ConsoleCommands.ReloadInPlace();
            Assert.Greater(ConsoleCommands.Position.cursor, cursor);
            Assert.AreEqual(session, ConsoleCommands.Position.session);
            ConsoleCommands.Add(LogType.Log, tag + "after", "");

            var follow = (JObject)ConsoleCommands.Get(new JObject { ["since"] = cursor.ToString(), ["since-session"] = session });
            Assert.IsFalse((bool)follow["reset"]);
            CollectionAssert.AreEqual(new[] { tag + "after" }, Messages(follow, tag));
            var fromStart = Get(new { since = start.ToString() });
            CollectionAssert.AreEqual(new[] { tag + "kept", tag + "after" }, Messages(fromStart, tag));
            Assert.IsFalse((bool)fromStart["dropped"], "entries a clear removed were not missed, before or after the reload");
        }

        [Test]
        public void ReloadAfterClearWithEmptyBufferDoesNotReportDroppedAndForgetSavedClearsState()
        {
            var tag = NewTag();
            ConsoleCommands.Add(LogType.Log, tag + "old", "");
            ConsoleCommands.Clear();
            var (clearedAt, session) = ConsoleCommands.Position;

            ConsoleCommands.ReloadInPlace();
            ConsoleCommands.Add(LogType.Log, tag + "fresh", "");
            var follow = (JObject)ConsoleCommands.Get(new JObject { ["since"] = clearedAt.ToString(), ["since-session"] = session });
            CollectionAssert.AreEqual(new[] { tag + "fresh" }, Messages(follow, tag));
            Assert.IsFalse((bool)follow["reset"]);
            Assert.IsFalse((bool)follow["dropped"], "reloading an empty/cleared buffer must not read the seq margin as dropped");

            ConsoleCommands.Persist();
            if (ConsoleCommands.IsAttached)
                StringAssert.Contains(tag + "fresh", UnityEditor.SessionState.GetString("Upipe.Console.Buffer", ""));
            ConsoleCommands.ForgetSaved();
            Assert.AreEqual("", UnityEditor.SessionState.GetString("Upipe.Console.Buffer", ""));
        }

        [Test]
        public void EntriesEvictedPastCapacityReadAsDropped()
        {
            var tag = NewTag();
            var since = ConsoleCommands.Position.cursor;
            ConsoleCommands.Add(LogType.Log, tag + "first", "");
            for (var i = 0; i < 2005; i++) // capacity is 2000
                ConsoleCommands.Add(LogType.Log, tag + i, "");
            var reply = Get(new { since = since.ToString(), tail = "5000" });
            Assert.IsTrue((bool)reply["dropped"]);
            CollectionAssert.DoesNotContain(Messages(reply, tag), tag + "first");
            Assert.AreEqual(tag + "2004", Messages(reply, tag).Last());
        }

        [Test]
        public void WriteLogsAtTheRequestedLevel()
        {
            Assume.That(ConsoleCommands.IsAttached, Is.True, "the Upipe server is disabled here, so Unity's log messages are not captured");
            var tag = NewTag();
            var since = ConsoleCommands.Position.cursor;
            ConsoleCommands.Write(new JObject { ["message"] = tag + "plain" });
            ConsoleCommands.Write(new JObject { ["message"] = tag + "warn", ["level"] = "warn" });
            LogAssert.Expect(LogType.Error, tag + "error");
            ConsoleCommands.Write(new JObject { ["message"] = tag + "error", ["level"] = "error" });

            var entries = Get(new { since = since.ToString() })["entries"].Where(e => ((string)e["message"]).StartsWith(tag, StringComparison.Ordinal)).ToArray();
            CollectionAssert.AreEqual(new[] { "log", "warn", "error" }, entries.Select(e => (string)e["level"]));
            CollectionAssert.AreEqual(new[] { "Log", "Warning", "Error" }, entries.Select(e => (string)e["logType"]));

            Assert.Throws<CommandFailure>(() => ConsoleCommands.Write(new JObject { ["message"] = tag + "bad", ["level"] = "info" }));
            CollectionAssert.DoesNotContain(Messages(Get(new { since = since.ToString() }), tag), tag + "bad");
        }

        static void ThrowForClearTest() => throw new InvalidOperationException("simulated LogEntries failure");

        [Test]
        public void ClearEditorConsoleLogsWarningInsteadOfThrowingOnReflectionFailure()
        {
            LogAssert.Expect(LogType.Warning, new Regex("LogEntries\\.Clear was not found"));
            ConsoleCommands.ClearEditorConsole(() => null);

            LogAssert.Expect(LogType.Warning, new Regex("LogEntries\\.Clear failed"));
            ConsoleCommands.ClearEditorConsole(() => typeof(ConsoleCommandsTests).GetMethod(nameof(ThrowForClearTest), BindingFlags.Static | BindingFlags.NonPublic));
        }

        [Test]
        public void StatusReportsNonNegativeGroundTruthAgeMs()
        {
            var status = ConsoleCommands.Status();
            Assert.GreaterOrEqual((long)status["groundTruth"]["ageMs"], 0);
        }
    }
}
