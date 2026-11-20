using System;
using System.Collections;
using System.IO;
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
        }

        [TearDown]
        public void DeleteDirectory() => Directory.Delete(m_Directory, true);

        string Write(string source)
        {
            var path = Path.Combine(m_Directory, Guid.NewGuid().ToString("N") + ".cs");
            File.WriteAllText(path, source);
            return path;
        }

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

        [Test]
        public void AwaitUsesTheDeclaredReturnType()
        {
            // An `async Task` method's task is a Task<VoidTaskResult> at run time; its result must read as null.
            Assert.IsNull(ScriptRunCommand.Await(NothingAsync(), typeof(Task)).Result);
            Assert.AreEqual(7, ScriptRunCommand.Await(Task.FromResult(7), typeof(Task<int>)).Result);
            Assert.AreEqual(5, ScriptRunCommand.Await(5, typeof(int)).Result);
            Assert.AreEqual(9, ScriptRunCommand.Await(new ValueTask<int>(9), typeof(ValueTask<int>)).Result);
            Assert.AreEqual(3, ScriptRunCommand.Await(Task.FromResult<object>(3), typeof(object)).Result, "falls back to the run-time type");
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

        [Test]
        public void WindowsSeparatorsResolveOnEveryPlatform()
        {
            var directory = Path.Combine(UpipeServer.ProjectPath, "Temp", "UpipeRunTests");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "Tool.cs");
            File.WriteAllText(file, "");
            try
            {
                Assert.AreEqual(Path.GetFullPath(file), ScriptEvalCommand.ResolveScriptPath("Temp\\UpipeRunTests\\Tool.cs"));
                Assert.AreEqual(Path.GetFullPath(file), ScriptEvalCommand.ResolveScriptPath(file.Replace('/', '\\')));
            }
            finally
            {
                File.Delete(file);
            }
        }
    }
}
