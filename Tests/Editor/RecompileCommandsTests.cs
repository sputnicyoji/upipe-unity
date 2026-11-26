using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.Compilation;

namespace Upipe.Editor.Tests
{
    // Drives the compile event handlers directly; the session's real recompile state is restored after each test.
    public class RecompileCommandsTests
    {
        JObject m_SavedState;
        string[] m_SavedLastErrors;

        [SetUp]
        public void SaveState()
        {
            m_SavedState = RecompileCommands.Current;
            m_SavedLastErrors = RecompileCommands.LastErrors;
        }

        [TearDown]
        public void RestoreState()
        {
            RecompileCommands.ResetTestState();
            RecompileCommands.LastErrors = m_SavedLastErrors;
            RecompileCommands.Save(m_SavedState);
        }

        static CompilerMessage Message(CompilerMessageType type, string text, string file = null)
        {
            return new CompilerMessage { type = type, message = text, file = file, line = 3, column = 7 };
        }

        [Test]
        public void ErrorsMakeTheRecompileFailed()
        {
            RecompileCommands.OnCompilationStarted();
            Assert.AreEqual("compiling", (string)RecompileCommands.Current["status"]);
            RecompileCommands.OnAssemblyCompiled("A.dll", new[]
            {
                Message(CompilerMessageType.Warning, "Assets/A.cs(1,1): warning CS0219: unused"),
                Message(CompilerMessageType.Error, "Assets/A.cs(4,22): error CS0029: bad"),
            });
            RecompileCommands.OnCompilationFinished();

            var state = RecompileCommands.Current;
            Assert.AreEqual("completed", (string)state["status"]);
            Assert.IsTrue((bool)state["failed"]);
            Assert.IsTrue((bool)state["compilationFailed"]);
            Assert.AreEqual(new JArray("Assets/A.cs(4,22): error CS0029: bad"), state["errors"]);
            CollectionAssert.AreEqual(new[] { "Assets/A.cs(4,22): error CS0029: bad" }, RecompileCommands.LastErrors);
        }

        [Test]
        public void SuccessStaysCompilingUntilTheReload()
        {
            RecompileCommands.OnCompilationStarted();
            RecompileCommands.OnAssemblyCompiled("A.dll", new[] { Message(CompilerMessageType.Warning, "w") });
            RecompileCommands.OnCompilationFinished();

            var state = RecompileCommands.Current;
            Assert.AreEqual("compiling", (string)state["status"]);
            Assert.IsFalse((bool)state["failed"]);
            CollectionAssert.IsEmpty(RecompileCommands.LastErrors);
        }

        [Test]
        public void NothingCompiledIsDecidedAfterTheEditorSettles()
        {
            RecompileCommands.OnCompilationStarted();
            RecompileCommands.OnCompilationFinished();
            // Removing the last broken script also compiles nothing, so the outcome waits for a fresh compile flag.
            Assert.AreEqual("compiling", (string)RecompileCommands.Current["status"]);
            Assert.AreEqual("up_to_date", (string)RecompileCommands.Outcome(afterReload: true)["status"]);
        }

        [Test]
        public void BuiltAssembliesWithoutErrorsCountAsSuccessBeforeTheReload()
        {
            RecompileCommands.OnCompilationStarted();
            RecompileCommands.OnAssemblyCompiled("A.dll", new CompilerMessage[0]);
            RecompileCommands.OnCompilationFinished();
            var outcome = RecompileCommands.Outcome(afterReload: false);
            Assert.AreEqual("completed", (string)outcome["status"]);
            Assert.IsFalse((bool)outcome["failed"]);
        }

        [Test]
        public void NewStartClearsPreviousErrors()
        {
            RecompileCommands.OnCompilationStarted();
            RecompileCommands.OnAssemblyCompiled("A.dll", new[] { Message(CompilerMessageType.Error, "e", "Assets/A.cs") });
            RecompileCommands.OnCompilationFinished();
            RecompileCommands.OnCompilationStarted();

            var state = RecompileCommands.Current;
            Assert.AreEqual("compiling", (string)state["status"]);
            Assert.IsFalse((bool)state["failed"]);
        }

        [Test]
        public void ErrorsWithoutLocationGetOneAndStayOnOneLine()
        {
            Assert.AreEqual("Assets/A.cs(3,7): error CS0006: missing",
                RecompileCommands.FormatError(Message(CompilerMessageType.Error, "error CS0006: missing", "Assets/A.cs")));
            Assert.AreEqual("error CS5001: no entry point",
                RecompileCommands.FormatError(Message(CompilerMessageType.Error, "error CS5001: no entry point")));
            Assert.AreEqual("Assets/A.cs(1,2): error X: line one line two",
                RecompileCommands.FormatError(Message(CompilerMessageType.Error, "Assets/A.cs(1,2): error X: line one\nline two", "Assets/A.cs")));
        }

        [Test]
        public void NeedsCompilationSkipsCleanNoOpUnlessDirtyBrokenOrForced()
        {
            Assert.IsFalse(RecompileCommands.NeedsCompilation(force: false, dirtySinceLastCompile: false, scriptCompilationFailed: false, isCompiling: false, isUpdating: false));
            Assert.IsTrue(RecompileCommands.NeedsCompilation(force: true, dirtySinceLastCompile: false, scriptCompilationFailed: false, isCompiling: false, isUpdating: false));
            Assert.IsTrue(RecompileCommands.NeedsCompilation(force: false, dirtySinceLastCompile: true, scriptCompilationFailed: false, isCompiling: false, isUpdating: false));
            Assert.IsTrue(RecompileCommands.NeedsCompilation(force: false, dirtySinceLastCompile: false, scriptCompilationFailed: true, isCompiling: false, isUpdating: false));

            Assert.IsTrue(RecompileCommands.IsCompilationAsset("Assets/Scripts/Foo.CS"));
            Assert.IsTrue(RecompileCommands.IsCompilationAsset("Assets/Game.asmdef"));
            Assert.IsTrue(RecompileCommands.IsCompilationAsset("Assets/Extra.asmref"));
            Assert.IsTrue(RecompileCommands.IsCompilationAsset("Assets/Plugins/Lib.dll"));
            Assert.IsTrue(RecompileCommands.IsCompilationAsset("Assets/csc.rsp"));
            Assert.IsTrue(RecompileCommands.IsCompilationAsset("Packages\\manifest.json"));
            Assert.IsFalse(RecompileCommands.IsCompilationAsset("Assets/Art/Texture.png"));
        }

        [Test]
        public void WatchdogHonorsIdleBaselineWhenImportsKeepEditorBusy()
        {
            RecompileCommands.SetWatchdogTestState("triggered", requestedAt: 10f, finishedAt: 0f, idleSince: -1);
            // Importing keeps s_IdleSince reset even far past requestedAt + StartGraceSeconds.
            RecompileCommands.AdvanceWatchdog(20.0, isCompiling: false, isUpdating: true, isPlaying: false);
            Assert.AreEqual("triggered", (string)RecompileCommands.Current["status"]);

            // First idle sample sets s_IdleSince = 21.0; must wait full 5s from max(requestedAt, idleSince).
            RecompileCommands.AdvanceWatchdog(21.0, isCompiling: false, isUpdating: false, isPlaying: false);
            Assert.AreEqual("triggered", (string)RecompileCommands.Current["status"]);

            RecompileCommands.AdvanceWatchdog(25.9, isCompiling: false, isUpdating: false, isPlaying: false);
            Assert.AreEqual("triggered", (string)RecompileCommands.Current["status"]);

            RecompileCommands.AdvanceWatchdog(26.1, isCompiling: false, isUpdating: false, isPlaying: false);
            Assert.AreEqual("up_to_date", (string)RecompileCommands.Current["status"]);
        }

        [Test]
        public void OutcomeDetectsPostCscFailureWhileKeepingBrokenRecoveryContract()
        {
            // Compiled > 0 from a clean start, no reload, but Unity reports scriptCompilationFailed (Bee failure).
            RecompileCommands.SetWatchdogTestState("compiling", requestedAt: 0f, finishedAt: 1f, idleSince: 1f, compiledAssemblies: 1, failedBeforeStart: false);
            var beeFailed = RecompileCommands.Outcome(afterReload: false, scriptCompilationFailed: true);
            Assert.IsTrue((bool)beeFailed["failed"]);
            Assert.IsTrue((bool)beeFailed["compilationFailed"]);

            // Compiled > 0 while recovering from an earlier broken compile: trust csc before the reload clears the stale flag.
            RecompileCommands.SetWatchdogTestState("compiling", requestedAt: 0f, finishedAt: 1f, idleSince: 1f, compiledAssemblies: 1, failedBeforeStart: true);
            var recovering = RecompileCommands.Outcome(afterReload: false, scriptCompilationFailed: true);
            Assert.IsFalse((bool)recovering["failed"]);
        }

        [Test]
        public void ScriptRecompileCommandWithoutChangesReturnsUpToDateImmediately()
        {
            Assume.That(!UnityEditor.EditorUtility.scriptCompilationFailed && !UnityEditor.EditorApplication.isCompiling && !UnityEditor.EditorApplication.isUpdating);
            RecompileCommands.ResetTestState();
            var result = (JObject)CommandRegistry.Find("script", "recompile").Handler(new JObject());
            Assert.AreEqual("up_to_date", (string)result["status"]);
            Assert.AreEqual("up_to_date", (string)RecompileCommands.Current["status"]);
        }

        [Test]
        public void InitialStartupPostprocessIsClearedOnceWhileLaterEditsStayDirty()
        {
            Assume.That(RecompileCommands.IsAttached, "RecompileCommands must be attached to observe AssetPostprocessor events");
            RecompileCommands.ResetTestState(baselineCleared: false);
            RecompileCommands.OnAssetsPostprocessed(new[] { "Assets/Startup.cs" }, null, null, null);
            Assert.IsTrue(RecompileCommands.DirtySinceLastCompile);

            RecompileCommands.AdvanceWatchdog(10.0, isCompiling: false, isUpdating: false, isPlaying: false);
            Assert.IsFalse(RecompileCommands.DirtySinceLastCompile, "startup postprocess must clear on first idle tick");

            RecompileCommands.OnAssetsPostprocessed(new[] { "Assets/EditedLater.cs" }, null, null, null);
            Assert.IsTrue(RecompileCommands.DirtySinceLastCompile);
            RecompileCommands.AdvanceWatchdog(11.0, isCompiling: false, isUpdating: false, isPlaying: false);
            Assert.IsTrue(RecompileCommands.DirtySinceLastCompile, "edits after baseline must remain dirty until compiled");
        }

        [Test]
        public void PendingReloadOutcomeWaitsUntilSettledBeforeReportingCompleted()
        {
            RecompileCommands.SetPendingReloadOutcomeForTests(compiledAssemblies: 1);
            Assert.AreEqual("compiling", (string)RecompileCommands.Current["status"]);

            RecompileCommands.AdvanceWatchdog(10.0, isCompiling: false, isUpdating: false, isPlaying: false, settled: false);
            Assert.AreEqual("compiling", (string)RecompileCommands.Current["status"]);

            RecompileCommands.AdvanceWatchdog(10.1, isCompiling: false, isUpdating: false, isPlaying: false, settled: true);
            var completed = RecompileCommands.Current;
            Assert.AreEqual("completed", (string)completed["status"]);
            Assert.IsFalse((bool)completed["failed"]);
        }
    }
}
