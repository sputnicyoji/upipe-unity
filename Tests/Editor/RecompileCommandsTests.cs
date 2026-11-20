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
            RecompileCommands.ResetProgress();
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
    }
}
