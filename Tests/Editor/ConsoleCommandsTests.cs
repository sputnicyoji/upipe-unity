using System;
using System.Linq;
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

        [Test]
        public void WriteLogsAtTheRequestedLevel()
        {
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
    }
}
