// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Upipe.Editor.Tests
{
    // script run through the real compiler. The handler runs on a worker thread while the test yields, so the main
    // thread keeps pumping MainThreadQueue (and the script's own continuations) in between.
    public class ScriptRunTests
    {
        string m_Directory;

        [SetUp]
        public void CreateDirectory()
        {
            // Outside the project: script run must not need the file to be under Assets/.
            m_Directory = Path.Combine(Path.GetTempPath(), "UpipeRunTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(m_Directory);
            Assume.That(MainThreadQueue.IsAttached, Is.True, "the Upipe server is disabled here, so nothing pumps the main-thread queue");
        }

        [TearDown]
        public void DeleteDirectory() => Directory.Delete(m_Directory, true);

        string Write(string source) => WriteAt(Guid.NewGuid().ToString("N") + ".cs", source);

        static Task<object> Start(JObject parameters, CancellationToken cancel = default)
        {
            var handler = CommandRegistry.Find("script", "run").CancellableHandler;
            return Task.Run(() => handler(parameters, cancel));
        }

        static bool Pending(Task task, DateTime deadline) => !task.IsCompleted && DateTime.UtcNow < deadline;

        static JToken Result(Task<object> task)
        {
            Assert.IsTrue(task.IsCompleted, "script run did not finish");
            if (task.IsFaulted)
                Assert.Fail(UpipeServer.Unwrap(task.Exception).ToString());
            return task.Result as JToken ?? JToken.FromObject(task.Result);
        }

        [UnityTest]
        public IEnumerator SyncEntryGetsItsArgumentsFromJson()
        {
            var file = Write("public static class Tool { public static int Add(int a, int b) => a + b; }");
            var task = Start(new JObject { ["file"] = file, ["entry"] = "Tool.Add", ["args"] = "[2, 3]" });
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Pending(task, deadline))
                yield return null;
            Assert.AreEqual(5, (int)Result(task));
        }

        [UnityTest]
        public IEnumerator AsyncTaskOfIntIsAwaitedWithTheInjectedToken()
        {
            var file = Write(@"
using System.Threading;
using System.Threading.Tasks;
public static class Job
{
    public static async Task<int> Run(CancellationToken token)
    {
        await Task.Delay(20, token);
        return token.CanBeCanceled ? 42 : -1;
    }
}");
            using (var cancel = new CancellationTokenSource())
            {
                var task = Start(new JObject { ["file"] = file }, cancel.Token); // default entry: the only static Run
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (Pending(task, deadline))
                    yield return null;
                Assert.AreEqual(42, (int)Result(task));
            }
        }

        // Shaped like UniTask<T>: a struct whose GetAwaiter() returns a nested struct awaiter, completed later from
        // another thread so OnCompleted really runs. No compile-time reference to it exists anywhere in the server.
        [UnityTest]
        public IEnumerator CustomAwaitableLikeUniTaskIsAwaitedByShape()
        {
            var file = Write(@"
using System;
using System.Runtime.CompilerServices;
using System.Threading;
namespace Demo
{
    public sealed class Source<T>
    {
        readonly object gate = new object();
        Action continuation;
        public bool Done;
        public T Value;
        public void Complete(T value)
        {
            Action next;
            lock (gate) { Value = value; Done = true; next = continuation; }
            next?.Invoke();
        }
        public void OnCompleted(Action next)
        {
            bool now;
            lock (gate) { now = Done; if (!now) continuation = next; }
            if (now) next();
        }
    }

    public readonly struct FakeUniTask<T>
    {
        readonly Source<T> source;
        public FakeUniTask(Source<T> source) { this.source = source; }
        public Awaiter GetAwaiter() => new Awaiter(source);

        public readonly struct Awaiter : INotifyCompletion
        {
            readonly Source<T> source;
            public Awaiter(Source<T> source) { this.source = source; }
            public bool IsCompleted => source.Done;
            public void OnCompleted(Action continuation) => source.OnCompleted(continuation);
            public T GetResult() => source.Value;
        }
    }

    public static class Fake
    {
        static Timer timer;
        public static FakeUniTask<string> Later(int milliseconds)
        {
            var source = new Source<string>();
            timer = new Timer(_ => source.Complete(""late""), null, milliseconds, Timeout.Infinite);
            return new FakeUniTask<string>(source);
        }
    }
}");
            var task = Start(new JObject { ["file"] = file, ["entry"] = "Demo.Fake.Later", ["args"] = "100" });
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Pending(task, deadline))
                yield return null;
            Assert.AreEqual("late", (string)Result(task));
        }

        [UnityTest]
        public IEnumerator CancellingStopsWaitingAndCancelsTheScriptsToken()
        {
            // A fresh type name, so an assembly from an earlier run in this domain is not mistaken for this one.
            var type = "Forever_" + Guid.NewGuid().ToString("N");
            var file = Write(@"
using System.Threading;
using System.Threading.Tasks;
public static class " + type + @"
{
    public static async Task<int> Run(CancellationToken token)
    {
        await Task.Delay(-1, token);
        return 0;
    }
}");
            using (var cancel = new CancellationTokenSource())
            {
                var task = Start(new JObject { ["file"] = file }, cancel.Token);
                var deadline = DateTime.UtcNow.AddSeconds(60);
                // Cancel once the script is running (compiling takes a moment).
                while (Pending(task, deadline) && !ScriptLoaded(type))
                    yield return null;
                cancel.Cancel();
                deadline = DateTime.UtcNow.AddSeconds(10);
                while (Pending(task, deadline))
                    yield return null;
                Assert.IsTrue(task.IsFaulted, "did not stop after cancel");
                Assert.IsInstanceOf<OperationCanceledException>(UpipeServer.Unwrap(task.Exception));
            }
        }

        [UnityTest]
        public IEnumerator SynchronousCancelDuringAwaitStillReportsOperationCanceledException()
        {
            var type = "CanceledTask_" + Guid.NewGuid().ToString("N");
            var file = Write($@"using System.Threading;
using System.Threading.Tasks;
public static class {type}
{{
    public static async Task<int> Run(CancellationToken token)
    {{
        var tcs = new TaskCompletionSource<int>();
        using (token.Register(() => tcs.TrySetCanceled(token)))
            return await tcs.Task.ConfigureAwait(false);
    }}
}}");
            using (var cancel = new CancellationTokenSource())
            {
                var task = Start(new JObject { ["file"] = file }, cancel.Token);
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (Pending(task, deadline) && !ScriptLoaded(type))
                    yield return null;
                cancel.Cancel();
                deadline = DateTime.UtcNow.AddSeconds(10);
                while (Pending(task, deadline))
                    yield return null;
                Assert.IsTrue(task.IsFaulted);
                Assert.IsInstanceOf<OperationCanceledException>(UpipeServer.Unwrap(task.Exception));
            }
        }

        // The compiled assembly shows up in the domain once the script has been loaded.
        static bool ScriptLoaded(string type)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetType(type) != null)
                    return true;
            return false;
        }

        [UnityTest]
        public IEnumerator CompileErrorsReadAsLineAndColumn()
        {
            var file = Write("public static class Broken { public static int Run() => nope; }");
            var task = Start(new JObject { ["file"] = file });
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Pending(task, deadline))
                yield return null;
            var error = UpipeServer.Unwrap(task.Exception) as CommandFailure;
            Assert.IsNotNull(error, task.Exception?.ToString());
            StringAssert.StartsWith("COMPILATION_ERROR: Compilation Failed: 1 error(s)\nline 1, col 56: error CS0103:", error.Message);
        }

        static async Task NothingAsync() => await Task.CompletedTask;

        // A request that timed out or a cancelled job does not keep a thread waiting on the compiler.
        [Test]
        public void ACancelledCompileStopsTheCompilerAndThrows()
        {
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                Assert.Catch<OperationCanceledException>(() => CscCompiler.Compile("UpipeCancelled_" + Guid.NewGuid().ToString("N"),
                    "public static class Cancelled { }", "", null, cancel.Token));
            }
        }

        [Test]
        public void AwaitUsesTheDeclaredReturnType()
        {
            // An `async Task` method's task is a Task<VoidTaskResult> at run time; its result must read as null.
            Assert.IsNull(ScriptRunCommand.Await(NothingAsync(), typeof(Task)).Result);
            Assert.AreEqual(7, ScriptRunCommand.Await(Task.FromResult(7), typeof(Task<int>)).Result);
            Assert.AreEqual(5, ScriptRunCommand.Await(5, typeof(int)).Result);
            Assert.AreEqual(9, ScriptRunCommand.Await(new ValueTask<int>(9), typeof(ValueTask<int>)).Result);
            Assert.AreEqual(3, ScriptRunCommand.Await(Task.FromResult<object>(3), typeof(object)).Result, "falls back to the run-time type");

            var faulted = Task.FromException<int>(new OperationCanceledException("user canceled"));
            var awaited = ScriptRunCommand.Await(faulted, typeof(Task<int>));
            var inner = Assert.Throws<AggregateException>(() => awaited.Wait()).InnerException;
            Assert.IsInstanceOf<OperationCanceledException>(inner);
            Assert.AreEqual("user canceled", inner.Message);
        }

        [UnityTest]
        public IEnumerator AsyncUserThrownOperationCanceledPreservesDiagnostics()
        {
            var tcs = new TaskCompletionSource<int>();
            var awaited = ScriptRunCommand.Await(tcs.Task, typeof(Task<int>));
            yield return null;
            tcs.SetException(new OperationCanceledException("user canceled async"));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Pending(awaited, deadline))
                yield return null;
            Assert.IsTrue(awaited.IsFaulted);
            var inner = UpipeServer.Unwrap(awaited.Exception);
            Assert.IsInstanceOf<OperationCanceledException>(inner);
            Assert.AreEqual("user canceled async", inner.Message);
        }

        static string Greet(string name, int times = 2, CancellationToken token = default) => name + times;
        static int Sum(int[] values) => values.Length;

        [Test]
        public void ArgumentsBindByPositionByNameOrAsOneValue()
        {
            var greet = typeof(ScriptRunTests).GetMethod(nameof(Greet), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var sum = typeof(ScriptRunTests).GetMethod(nameof(Sum), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            using (var cancel = new CancellationTokenSource())
            {
                var named = ScriptRunCommand.BindArguments(greet, JToken.Parse("{\"name\":\"a\"}"), cancel.Token);
                CollectionAssert.AreEqual(new object[] { "a", 2, cancel.Token }, named);
                var positional = ScriptRunCommand.BindArguments(greet, JToken.Parse("[\"b\", 3]"), cancel.Token);
                CollectionAssert.AreEqual(new object[] { "b", 3, cancel.Token }, positional);
                var single = ScriptRunCommand.BindArguments(sum, JToken.Parse("[1, 2, 3]"), cancel.Token);
                CollectionAssert.AreEqual(new[] { 1, 2, 3 }, (int[])single[0]);
                Assert.Throws<CommandFailure>(() => ScriptRunCommand.BindArguments(greet, JToken.Parse("{\"nope\":1}"), cancel.Token));
                Assert.Throws<CommandFailure>(() => ScriptRunCommand.BindArguments(greet, null, cancel.Token), "name has no default");
                Assert.Throws<CommandFailure>(() => ScriptRunCommand.BindArguments(greet, JToken.Parse("[\"x\", \"y\"]"), cancel.Token), "y is no int");
            }
        }

        // Normalized, as the server resolves paths (Windows turns the / in `relative` into \).
        string WriteAt(string relative, string source)
        {
            var path = Path.GetFullPath(Path.Combine(m_Directory, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, source);
            return path;
        }

        static CommandFailure Failure(Task<object> task)
        {
            if (!task.IsFaulted)
                Assert.Fail("script run did not fail: " + (task.IsCompleted ? task.Result : "still running"));
            var failure = UpipeServer.Unwrap(task.Exception) as CommandFailure;
            Assert.IsNotNull(failure, task.Exception.ToString());
            return failure;
        }

        [UnityTest]
        public IEnumerator ExtraFilesAndDirectoriesCompileWithTheMainFile()
        {
            var main = WriteAt("Main.cs", "public static class MainTool { public static int Run() => Helpers.Twice(Deep.Value); }");
            WriteAt("lib/Helpers.cs", "public static class Helpers { public static int Twice(int x) => 2 * x; }");
            WriteAt("lib/sub/Deep.cs", "public static class Deep { public const int Value = 21; }");
            // Hidden files and folders are skipped: compiling these would fail.
            WriteAt("lib/.hidden/Broken.cs", "this is not C#");
            WriteAt("lib/.Broken.cs", "this is not C#");
            WriteAt("lib/notes.txt", "not a source file");
            var lib = Path.Combine(m_Directory, "lib");
            var extra = new JArray(lib, Path.Combine(lib, "Helpers.cs") + ";" + main);
            var task = Start(new JObject { ["file"] = main, ["extra-files"] = extra });
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Pending(task, deadline))
                yield return null;
            Assert.AreEqual(42, (int)Result(task));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);

        // Windows hands out 8.3 short paths (a long user name makes %TEMP% one); Roslyn's #line expands them, so the
        // main file named one way and an extra file named the other must still be the same file.
        [Test]
        public void ShortPathsExpandToTheirLongForm()
        {
            Assume.That(UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor, "Windows only: no 8.3 names elsewhere");
            var main = UpipeServer.CanonicalizePath(WriteAt("ALongDirectoryName/ALongScriptName.cs", ""));
            var buffer = new StringBuilder(1024);
            var length = GetShortPathNameW(main, buffer, (uint)buffer.Capacity);
            var shortPath = length > 0 && length < buffer.Capacity ? buffer.ToString() : main;
            Assume.That(!string.Equals(shortPath, main, StringComparison.OrdinalIgnoreCase), "this volume makes no 8.3 names");

            Assert.AreEqual(main, UpipeServer.CanonicalizePath(shortPath));
            CollectionAssert.IsEmpty(ScriptRunCommand.ResolveExtraFiles(new[] { shortPath }, main), "the main file again, by its short name");
            Assert.AreEqual(main, ScriptEvalCommand.ResolveScriptPath(shortPath));
        }

        [Test]
        public void ExtraFilesResolveOnceInAStableOrder()
        {
            var main = WriteAt("Main.cs", "");
            var a = WriteAt("lib/A.cs", "");
            var b = WriteAt("lib/sub/B.cs", "");
            WriteAt("lib/.hidden/C.cs", "");
            var lib = Path.Combine(m_Directory, "lib");
            var sameCase = UpipeServer.PathsIgnoreCase ? a.ToUpperInvariant() : a;
            var files = ScriptRunCommand.ResolveExtraFiles(new[] { b, lib, sameCase, main, lib + "/" }, main);
            CollectionAssert.AreEqual(new[] { UpipeServer.CanonicalizePath(b), UpipeServer.CanonicalizePath(a) }, files,
                "named twice, the main file again, or in another case: compiled once");

            Assert.Throws<CommandFailure>(() => ScriptRunCommand.ResolveExtraFiles(new[] { Path.Combine(m_Directory, "Nope.cs") }, main));
            Assert.Throws<CommandFailure>(() => ScriptRunCommand.ResolveExtraFiles(new[] { WriteAt("notes.txt", "") }, main));
            Directory.CreateDirectory(Path.Combine(m_Directory, "empty"));
            Assert.Throws<CommandFailure>(() => ScriptRunCommand.ResolveExtraFiles(new[] { Path.Combine(m_Directory, "empty") }, main));
            var missingDir = Assert.Throws<CommandFailure>(() => ScriptRunCommand.ResolveExtraFiles(new[] { Path.Combine(m_Directory, "missing_dir") }, main));
            StringAssert.Contains("directory or .cs file not found", missingDir.Message);
            Assert.IsEmpty(ScriptRunCommand.ResolveExtraFiles(new[] { " ", "" }, main));
        }

        [UnityTest]
        public IEnumerator DefaultEntryComesFromTheMainFileAndEntryReachesAnyFile()
        {
            var main = WriteAt("Main.cs", "namespace App { public static class Tool { public static string Run() => \"main\"; } }");
            var helper = WriteAt("Helper.cs", "public static class Helper { public static string Run() => \"helper\"; public static string Main() => \"helper main\"; }");
            var byDefault = Start(new JObject { ["file"] = main, ["extra-files"] = helper });
            var explicitEntry = Start(new JObject { ["file"] = main, ["extra-files"] = helper, ["entry"] = "Helper.Main" });
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Pending(byDefault, deadline) || Pending(explicitEntry, deadline))
                yield return null;
            Assert.AreEqual("main", (string)Result(byDefault));
            Assert.AreEqual("helper main", (string)Result(explicitEntry));
        }

        [UnityTest]
        public IEnumerator ErrorsInAnExtraFileNameIt()
        {
            var main = WriteAt("Main.cs", "public static class Ok { public static int Run() => Helper.Value; }");
            var helper = WriteAt("Helper.cs", "public static class Helper\n{\n    public static int Value => nope;\n}");
            var task = Start(new JObject { ["file"] = main, ["extra-files"] = helper });
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Pending(task, deadline))
                yield return null;
            StringAssert.StartsWith("COMPILATION_ERROR: Compilation Failed: 1 error(s)\nHelper.cs: line 3, col 31: error CS0103:", Failure(task).Message);
        }

        [UnityTest]
        public IEnumerator RuntimeTracesNameTheRealFiles()
        {
            var main = WriteAt("Main.cs", "public static class Thrower { public static int Run() => Helper.Fail(); }");
            // Not inlined, so the helper's own frame shows up in the trace.
            var helper = WriteAt("sub dir/Helper.cs", "using System.Runtime.CompilerServices;\npublic static class Helper\n{\n" +
                "    [MethodImpl(MethodImplOptions.NoInlining)]\n    public static int Fail() => throw new System.InvalidOperationException(\"deep\");\n}");
            var task = Start(new JObject { ["file"] = main, ["extra-files"] = helper });
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Pending(task, deadline))
                yield return null;
            var message = Failure(task).Message;
            StringAssert.StartsWith("RUNTIME_EXCEPTION: Runtime Error. InvalidOperationException: deep", message);
            StringAssert.Contains(UpipeServer.CanonicalizePath(helper), message);
            StringAssert.Contains(UpipeServer.CanonicalizePath(main), message);
            StringAssert.DoesNotContain(CscCompiler.WorkDirectory, message);
        }

        [UnityTest]
        public IEnumerator LegacyInstanceEntriesRunOnANewInstance()
        {
            var key = "upipe-instance-" + Guid.NewGuid().ToString("N");
            // Shaped like a legacy hot-reload entrance: an interface and a plain class with `public void Main()`.
            var legacy = Write($@"
public interface IEntrance {{ void Main(); }}
public class LegacyTool : IEntrance
{{
    public void Main() => System.AppDomain.CurrentDomain.SetData(""{key}"", ""ran"");
}}");
            var asyncInstance = Write(@"
using System.Threading;
using System.Threading.Tasks;
public class Counter
{
    readonly int bias;
    Counter() { bias = 1; }
    async Task<int> Run(int value, CancellationToken token)
    {
        await Task.Delay(1, token);
        return value + bias;
    }
}");
            var staticWins = Write("public class A { public static string Run() => \"static\"; } public class B { public string Main() => \"instance\"; }");
            var ctorThrows = Write("public class Boom { public Boom() { throw new System.InvalidOperationException(\"ctor\"); } public void Run() { } }");
            var tasks = new[]
            {
                Start(new JObject { ["file"] = legacy }),
                Start(new JObject { ["file"] = asyncInstance, ["args"] = "41" }),
                Start(new JObject { ["file"] = staticWins }),
                Start(new JObject { ["file"] = ctorThrows }),
            };
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (Array.Exists(tasks, t => Pending(t, deadline)))
                yield return null;
            Assert.IsTrue(tasks[0].Status == TaskStatus.RanToCompletion, tasks[0].Exception?.ToString());
            Assert.IsNull(tasks[0].Result, "a void entry has no result");
            Assert.AreEqual("ran", AppDomain.CurrentDomain.GetData(key));
            Assert.AreEqual(42, (int)Result(tasks[1]));
            Assert.AreEqual("static", (string)Result(tasks[2]));
            StringAssert.StartsWith("RUNTIME_EXCEPTION: Runtime Error. InvalidOperationException: ctor", Failure(tasks[3]).Message);
        }

        [UnityTest]
        public IEnumerator InstanceEntriesThatCannotBeConstructedAreRefused()
        {
            var sources = new[]
            {
                ("public class Behaviour : UnityEngine.MonoBehaviour { public void Main() { } }", "UnityEngine.Object"),
                ("public class Asset : UnityEngine.ScriptableObject { public void Run() { } }", "UnityEngine.Object"),
                ("public abstract class Base { public void Run() { } }", "abstract"),
                ("public class NeedsArgument { public NeedsArgument(int x) { } public void Run() { } }", "no parameterless constructor"),
                ("public class Two { public void Run() { } public void Main() { } }", "More than one instance"),
            };
            var tasks = Array.ConvertAll(sources, s => Start(new JObject { ["file"] = Write(s.Item1) }));
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (Array.Exists(tasks, t => Pending(t, deadline)))
                yield return null;
            for (var i = 0; i < tasks.Length; i++)
            {
                var failure = Failure(tasks[i]);
                Assert.AreEqual("Parameter Validation Failed", failure.Error, failure.Message);
                StringAssert.Contains(sources[i].Item2, failure.Message);
            }
        }

        [UnityTest]
        public IEnumerator ProgressParametersAreNoOpsOutsideAJobAndTakeNoArgument()
        {
            var file = Write(@"
using System;
using System.Threading;
public static class Quiet
{
    public static int Run(IProgress<int> percent, int value, Action<string> log, Action<string, int> pair, IProgress<string> text,
        IProgress<(string, int)> both, CancellationToken token)
    {
        percent.Report(5); log(""hi""); pair(""a"", 1); text.Report(""b""); both.Report((""c"", 2));
        return value * 2;
    }
}");
            var task = Start(new JObject { ["file"] = file, ["args"] = "21" });
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Pending(task, deadline))
                yield return null;
            Assert.AreEqual(42, (int)Result(task), "the one bindable parameter still takes the whole --args value");
        }

        [UnityTest]
        public IEnumerator ProgressParametersReportToTheJob()
        {
            var table = new JobManager("Upipe.Jobs.ScriptRunTests." + Guid.NewGuid().ToString("N"));
            var go = Path.Combine(m_Directory, "go");
            var file = Write(@"
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
public static class Reporting
{
    public static async Task<string> Run(string go, Action<string, int> pair, IProgress<(string, int)> both, CancellationToken token)
    {
        pair(""starting"", 10);
        await Task.Run(() => both.Report((""half"", 50)), token); // from a worker thread too
        while (!File.Exists(go))
            await Task.Delay(10, token);
        return ""done"";
    }
}");
            var handler = CommandRegistry.Find("script", "run").CancellableHandler;
            var parameters = new JObject { ["file"] = file, ["args"] = Newtonsoft.Json.JsonConvert.ToString(go) };
            var id = (string)table.Submit("script run", null, (token, started, progress) => Task.Run(() =>
            {
                // What the server does on a job's own thread.
                started();
                return handler(parameters, new CommandContext(token, progress));
            }))["jobId"];
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while ((string)table.Status(id)["progress"]?["message"] != "half" && (string)table.Status(id)["state"] != "failed" && DateTime.UtcNow < deadline)
                    yield return null;
                var running = table.Status(id);
                Assert.AreEqual("running", (string)running["state"], running.ToString());
                Assert.AreEqual(50, (int)running["progress"]["percent"]);

                File.WriteAllText(go, "");
                while ((string)table.Status(id)["state"] == "running" && DateTime.UtcNow < deadline)
                    yield return null;
                var finished = table.Status(id);
                Assert.AreEqual("completed", (string)finished["state"], finished.ToString());
                Assert.AreEqual("done", (string)finished["result"]);
                Assert.IsFalse(finished.ContainsKey("progress"));
            }
            finally
            {
                table.Cancel(id);
                UnityEditor.SessionState.EraseString(table.SavedJobsKey);
            }
        }

        [Test]
        public void DeclaredTypeNamesFollowNamespacesNestingAndGenerics()
        {
            var names = ScriptRunCommand.DeclaredTypeNames(@"
using System;
// class Commented { }
namespace Outer.Inner
{
    [Serializable]
    public partial class Tool<TKey, TValue> where TKey : class where TValue : struct
    {
        const string Text = ""class InString { }"";
        char brace = '{';
        public class Nested { void M<T>() where T : class { var s = $""{{class Hole}} {Text}""; } }
        enum Kind { A, B }
    }
    readonly struct Point { }
    public record Pair(int A, int B);
    interface IThing { }
}
namespace Other { static class Tool { } }
public static class Global { }
");
            CollectionAssert.AreEquivalent(new[]
            {
                "Outer.Inner.Tool`2", "Outer.Inner.Tool`2+Nested", "Outer.Inner.Point", "Outer.Inner.Pair", "Outer.Inner.IThing", "Other.Tool", "Global",
            }, names);
        }

        [Test]
        public void ReferencesAreCachedUntilAnAssemblyLoadsFromDisk()
        {
            var first = CscCompiler.GetReferences();
            Assert.AreSame(first, CscCompiler.GetReferences(), "a second compile reuses the list");

            // Loading from memory, as eval and script run do, leaves the list alone.
            var inMemory = "UpipeCacheMemory_" + Guid.NewGuid().ToString("N");
            var (memoryBytes, _, memoryErrors) = CscCompiler.Compile(inMemory, "public static class " + inMemory + " { }", "");
            Assert.IsEmpty(memoryErrors);
            System.Reflection.Assembly.Load(memoryBytes);
            Assert.AreSame(first, CscCompiler.GetReferences());

            // Another name: loading the same identity again would hand back the in-memory assembly.
            var name = "UpipeCacheProbe_" + Guid.NewGuid().ToString("N");
            var (bytes, _, errors) = CscCompiler.Compile(name, "namespace " + name + " { public static class Probe { public static int Value => 7; } }", "");
            Assert.IsEmpty(errors);
            // Outside m_Directory: Windows keeps a loaded DLL locked, so TearDown could not delete it.
            var dll = Path.Combine(Path.GetTempPath(), "UpipeCacheProbes", name + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(dll));
            File.WriteAllBytes(dll, bytes);
            System.Reflection.Assembly.LoadFrom(dll);
            var after = CscCompiler.GetReferences();
            Assert.AreNotSame(first, after);
            // By file name: Location holds the resolved path (/private/var/... for /var/... on macOS).
            Assert.IsTrue(Array.Exists(after, path => Path.GetFileName(path) == name + ".dll"), "the new assembly is referenced");
            var (_, _, useErrors) = CscCompiler.Compile(name + "_User", "public static class User { public static int Run() => " + name + ".Probe.Value; }", "");
            Assert.IsEmpty(useErrors, "a compile right after the load sees the new assembly");
        }

        [Test]
        public void WindowsSeparatorsResolveOnEveryPlatform()
        {
            var directory = Path.Combine(UpipeServer.ProjectPath, "Temp", "UpipeRunTests");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "Tool.cs");
            File.WriteAllText(file, "");
            try
            {
                Assert.AreEqual(UpipeServer.CanonicalizePath(Path.GetFullPath(file)), ScriptEvalCommand.ResolveScriptPath("Temp\\UpipeRunTests\\Tool.cs"));
                Assert.AreEqual(UpipeServer.CanonicalizePath(Path.GetFullPath(file)), ScriptEvalCommand.ResolveScriptPath(file.Replace('/', '\\')));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [UnityTest]
        public IEnumerator RepeatedScriptRunHitsCacheAndInvalidatesOnFileOrDefineChange()
        {
            CscCompiler.ClearCache(includeDisk: true);
            var file = Write("public static class CachedTool {\n#if EXTRA_DEF\npublic static int Run() => 42;\n#else\npublic static int Run() => 11;\n#endif\n}");

            var deadline = DateTime.UtcNow.AddSeconds(60);
            var t1 = Start(new JObject { ["file"] = file });
            while (Pending(t1, deadline)) yield return null;
            Assert.AreEqual(11, (int)Result(t1));
            var misses1 = CscCompiler.CacheMisses;

            var t2 = Start(new JObject { ["file"] = file });
            while (Pending(t2, deadline)) yield return null;
            Assert.AreEqual(11, (int)Result(t2));
            Assert.AreEqual(misses1, CscCompiler.CacheMisses);
            Assert.GreaterOrEqual(CscCompiler.CacheHits, 1);

            var t3 = Start(new JObject { ["file"] = file, ["defines"] = "EXTRA_DEF" });
            while (Pending(t3, deadline)) yield return null;
            Assert.AreEqual(42, (int)Result(t3));
            Assert.Greater(CscCompiler.CacheMisses, misses1);

            var missesBeforeEdit = CscCompiler.CacheMisses;
            File.WriteAllText(file, "public static class CachedTool { public static int Run() => 99; }");
            var t4 = Start(new JObject { ["file"] = file });
            while (Pending(t4, deadline)) yield return null;
            Assert.AreEqual(99, (int)Result(t4));
            Assert.Greater(CscCompiler.CacheMisses, missesBeforeEdit);
        }
    }
}
