// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Upipe.Editor.Tests
{
    // Only the parts that never quit: the guards and the parameter checks, which all fail before an exit is scheduled.
    public class EditorQuitCommandTests
    {
        static CommandEntry Command => CommandRegistry.Find("editor", "quit");

        [Test]
        public void InteractiveEditorRefusesPlayModeAndUnsavedScenesUnlessForced()
        {
            var none = Array.Empty<string>();
            var dirty = new[] { "Assets/Main.unity", "the untitled scene" };
            Assert.IsNull(EditorQuitCommand.Refusal(batchMode: false, force: false, inPlayMode: false, dirtyScenes: none));

            var playing = EditorQuitCommand.Refusal(batchMode: false, force: false, inPlayMode: true, dirtyScenes: dirty);
            Assert.AreEqual("Command Execution Failed", playing.Error);
            StringAssert.StartsWith("PLAY_MODE_ACTIVE:", playing.Message);

            var unsaved = EditorQuitCommand.Refusal(batchMode: false, force: false, inPlayMode: false, dirtyScenes: dirty);
            Assert.AreEqual("Command Execution Failed", unsaved.Error);
            StringAssert.StartsWith("UNSAVED_SCENES: save or revert Assets/Main.unity, the untitled scene first", unsaved.Message);

            Assert.IsNull(EditorQuitCommand.Refusal(batchMode: false, force: true, inPlayMode: true, dirtyScenes: dirty), "force");
            Assert.IsNull(EditorQuitCommand.Refusal(batchMode: true, force: false, inPlayMode: true, dirtyScenes: dirty), "batchmode");
        }

        [Test]
        public void BadParametersAreRefusedBeforeAnExitIsScheduled()
        {
            foreach (var parameters in new[]
            {
                new JObject { ["exit-code"] = "one" },
                new JObject { ["exit-code"] = "99999999999" },
                new JObject { ["force"] = "maybe" },
                new JObject { ["force"] = true, ["confirm"] = "yes" },
            })
                Assert.AreEqual("Parameter Validation Failed", Assert.Throws<CommandFailure>(() => Command.Handler(parameters)).Error, parameters.ToString());
        }

        [Test]
        public void ArgvAndCatalogShape()
        {
            var bound = CommandRegistry.BindArgv(Command, new[] { "--confirm", "--exit-code", "3" });
            Assert.IsTrue(bound.Ok, bound.Problems.ToString());
            Assert.AreEqual(true, (bool)bound.Values["confirm"]);
            Assert.AreEqual("3", (string)bound.Values["exit-code"]);
            Assert.AreEqual("typeMismatch", (string)CommandRegistry.BindArgv(Command, new[] { "--exit-code", "x" }).Problems[0]["kind"]);

            Assert.AreEqual("editor_quit", Command.CatalogName);
            // The catalog's query=ready search must keep matching only the status commands.
            StringAssert.DoesNotContain("ready", Command.Description.ToLowerInvariant());
            Assert.IsTrue(Command.MainThreadRequired);
        }
    }
}
