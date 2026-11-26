using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Upipe.Editor.Tests
{
    // Source wrapping and result conversion. Compiling and running are covered by the end-to-end script; a test
    // that runs eval calls the handler on a worker thread and yields, so the main thread pumps its hops (as ScriptRunTests).
    public class ScriptEvalTests
    {
        const string Marker = "#line 1 \"script\"\n";

        static string Body(string code, bool wrapTrailing = true)
        {
            var source = EvalSource.Build(code, "Ns", wrapTrailing);
            // The body's own directive, after any hoisted using or type that also starts at line 1.
            var start = source.IndexOf(Marker, source.IndexOf("Task<object> Run()")) + Marker.Length;
            return source.Substring(start, source.IndexOf("\n#line default\nreturn null;", start) - start);
        }

        // The trailing expression keeps its own line and column under a wrapper line of its own.
        static string Wrapped(string before, int line, int column, string expression)
        {
            return before + "return (object)(\n#line " + line + " \"script\"\n" + new string(' ', column) + expression + "\n);";
        }

        [Test]
        public void StatementsStayAsWritten()
        {
            Assert.AreEqual("return 2+2;", Body("return 2+2;"));
            Assert.AreEqual("return \"a;b\";", Body("return \"a;b\";"));
            Assert.AreEqual("return 1; // done", Body("return 1; // done"));
            Assert.AreEqual("if (x) { } else { y(); }", Body("if (x) { } else { y(); }"));
            Assert.AreEqual("var a = new[] { 1 };", Body("var a = new[] { 1 };"));
        }

        [Test]
        public void TrailingExpressionBecomesTheResult()
        {
            Assert.AreEqual(Wrapped("", 1, 0, "2+2"), Body("2+2"));
            Assert.AreEqual(Wrapped("var x = 3; ", 1, 11, "x * 2"), Body("var x = 3; x * 2"));
            Assert.AreEqual(Wrapped("", 1, 0, "new List<int>{1,2}.Count"), Body("new List<int>{1,2}.Count"));
            Assert.AreEqual(Wrapped("", 1, 0, "x") + " // note", Body("x // note"));
            Assert.AreEqual(Wrapped("", 1, 0, "1 + /* see http://x */ 2"), Body("1 + /* see http://x */ 2"));
        }

        [Test]
        public void TrailingExpressionsEndingInABraceAreFound()
        {
            Assert.AreEqual(Wrapped("", 1, 0, "new { a = 1 }"), Body("new { a = 1 }"));
            Assert.AreEqual(Wrapped("", 1, 0, "new[] { 1, 2 }"), Body("new[] { 1, 2 }"));
            Assert.AreEqual(Wrapped("var x = 1; ", 1, 11, "x switch { 1 => \"a\", _ => \"b\" }"),
                Body("var x = 1; x switch { 1 => \"a\", _ => \"b\" }"));
        }

        [Test]
        public void StatementBlocksEndStatementsWhateverFollows()
        {
            var loop = "var s = 0; foreach (var x in new[] { 1, 2 }) { s += x; }\n";
            Assert.AreEqual(Wrapped(loop, 2, 0, "$\"sum={s}\""), Body(loop + "$\"sum={s}\""));
            // A comment after a type does not glue the next statement onto it.
            var code = "class Foo { public int X; } // helper\nvar f = new Foo();\nreturn f.X;";
            StringAssert.EndsWith("var f = new Foo();\nreturn f.X;", Body(code));
        }

        [Test]
        public void LiteralsDoNotSplitStatements()
        {
            Assert.AreEqual(Wrapped("", 1, 0, "$\"{string.Join(\";\", new[] { 1 })}\""), Body("$\"{string.Join(\";\", new[] { 1 })}\""));
            Assert.AreEqual(Wrapped("", 1, 0, "@$\"C:\\dir\\\""), Body("@$\"C:\\dir\\\""));
            Assert.AreEqual("var c = '{'; return c;", Body("var c = '{'; return c;"));
        }

        [Test]
        public void VoidTrailingCallCanStayAStatement()
        {
            Assert.AreEqual("Debug.Log(\"x\");", Body("Debug.Log(\"x\")", wrapTrailing: false));
            Assert.IsTrue(EvalSource.HasTrailingExpression("Debug.Log(\"x\")"));
            Assert.IsFalse(EvalSource.HasTrailingExpression("Debug.Log(\"x\");"));
        }

        [Test]
        public void BareReturnsInTheScriptFlowReturnNull()
        {
            Assert.AreEqual("if (true) return null; return 1;", Body("if (true) return; return 1;"));
            Assert.AreEqual("if (x) { return null; }", Body("if (x) { return; }"));
            // A local function or lambda may return nothing.
            Assert.AreEqual("void F() { return; } F();", Body("void F() { return; } F();"));
            Assert.AreEqual("Action a = () => { return; };", Body("Action a = () => { return; };"));
        }

        [Test]
        public void UsingsMoveToTheTopKeepingPositions()
        {
            var source = EvalSource.Build("using System.Text; return new StringBuilder().ToString();", "Ns");
            StringAssert.StartsWith("#line 1 \"script\"\nusing System.Text;\n#line default\n", source);
            Assert.AreEqual(new string(' ', 18) + " return new StringBuilder().ToString();",
                Body("using System.Text; return new StringBuilder().ToString();"));
            // Anywhere in the snippet, and the directive points past leading comments.
            StringAssert.StartsWith("#line 4 \"script\"\nusing Some.Ns;", EvalSource.Build("// note\n/* a\nb */\nusing Some.Ns;\nreturn 1;", "Ns"));
            StringAssert.Contains("using System.Text;", EvalSource.Build("var q = 1; using System.Text; return 1;", "Ns").Split(new[] { "namespace Ns" }, StringSplitOptions.None)[0]);
            // A using declaration or statement is code, not a directive.
            Assert.AreEqual("using var s = new System.IO.MemoryStream(); return s.Length;",
                Body("using var s = new System.IO.MemoryStream(); return s.Length;"));
        }

        [Test]
        public void MovedCodeKeepsItsPreprocessorCondition()
        {
            var code = "#if UNITY_EDITOR\nusing UnityEditor;\n#endif\nreturn 1;";
            StringAssert.StartsWith("#if UNITY_EDITOR\n#line 2 \"script\"\nusing UnityEditor;\n#endif\n#line default\n", EvalSource.Build(code, "Ns"));
            // The body keeps the directive lines, so its #if and #endif still pair up.
            Assert.AreEqual("#if UNITY_EDITOR\n" + new string(' ', 18) + "\n#endif\nreturn 1;", Body(code));
            StringAssert.Contains("#if FOO\n#else\n#line 4 \"script\"\nclass Box { }\n#endif\n#line default\n",
                EvalSource.Build("#if FOO\nint a = 1;\n#else\nclass Box { }\n#endif\nreturn 1;", "Ns"));
        }

        [Test]
        public void BracesAfterQualifiedNewCaseLabelsWhenAndDelegatesAreRead()
        {
            Assert.AreEqual(Wrapped("", 1, 0, "new UnityEngine.Rect { x = 1 }"), Body("new UnityEngine.Rect { x = 1 }"));
            Assert.AreEqual(Wrapped("", 1, 0, "new A.B(1) { C = 2 }"), Body("new A.B(1) { C = 2 }"));
            Assert.AreEqual("switch (x) { case 1: { return null; } }", Body("switch (x) { case 1: { return; } }"));
            Assert.AreEqual("try { } catch (Exception e) when (e != null) { return null; }",
                Body("try { } catch (Exception e) when (e != null) { return; }"));
            var call = "new[] { 1, 2 }.Where(delegate (int a) { return a > 1; }).Count()";
            Assert.AreEqual(Wrapped("", 1, 0, call), Body(call));
            // A new() constraint opens a local function's body, not an initializer.
            foreach (var function in new[] { "T Make<T>() where T : new() { return new T(); } ", "T Make<T>() where T : class, new() { return new T(); } " })
                Assert.AreEqual(Wrapped(function, 1, function.Length, "Make<object>()"), Body(function + "Make<object>()"));
            // A loop right after a type is a statement of its own, not the tail of a do-while.
            Assert.AreEqual(new string(' ', 12) + "\nwhile (false) { }", Body("enum E { A }\nwhile (false) { }"));
        }

        [Test]
        public void GenericLocalFunctionsAndPropertyPatternsAreClassifiedCorrectly()
        {
            const string idFn = "T Id<T>(T x) { return x; }\n";
            Assert.AreEqual(Wrapped(idFn, 2, 0, "Id(42)"), Body(idFn + "Id(42)"));

            // Non-new() generic constraints still open a local function, so bare `return;` stays `return;` instead of `return null;`.
            Assert.AreEqual("void Foo<T>(T x) where T : class { return; }\nFoo(\"a\");",
                Body("void Foo<T>(T x) where T : class { return; }\nFoo(\"a\");"));
            const string comparableFn = "void Bar<T>(T x) where T : IComparable<T> { return; }\n";
            Assert.AreEqual(Wrapped(comparableFn, 2, 0, "1"), Body(comparableFn + "1"));
            Assert.AreEqual(Wrapped("", 1, 0, "new List<int>(1) { 2 }"), Body("new List<int>(1) { 2 }"));

            Assert.AreEqual(Wrapped("var s = \"abc\"; ", 1, 15, "s is { Length: 3 }"), Body("var s = \"abc\"; s is { Length: 3 }"));
            Assert.AreEqual(Wrapped("var s = \"abc\"; ", 1, 15, "s is string { Length: 3 } && true"), Body("var s = \"abc\"; s is string { Length: 3 } && true"));
            Assert.AreEqual(Wrapped("var s = \"abc\"; ", 1, 15, "s is not { Length: 0 }"), Body("var s = \"abc\"; s is not { Length: 0 }"));
            Assert.AreEqual(Wrapped("object x = \"a\"; ", 1, 16, "x switch { string { Length: 1 } => 1, _ => 0 }"),
                Body("object x = \"a\"; x switch { string { Length: 1 } => 1, _ => 0 }"));
        }

        [Test]
        public void TypesMoveIntoTheSnippetNamespace()
        {
            var code = "class Box { public int V = 7; }\nreturn new Box().V;";
            var source = EvalSource.Build(code, "Ns");
            StringAssert.Contains("namespace Ns\n{\n#line 1 \"script\"\nclass Box { public int V = 7; }\n#line default\n", source);
            Assert.AreEqual(new string(' ', 31) + "\nreturn new Box().V;", Body(code));
            StringAssert.Contains("delegate int Cb(int x);", EvalSource.Build("delegate int Cb(int x); Cb c = v => v; return c(1);", "Ns")
                .Split(new[] { "public static class EvalEntry" }, StringSplitOptions.None)[0]);
            // Extension methods need a top-level static class, which a namespace member is.
            StringAssert.DoesNotContain("this int", Body("static class Ext { public static int Twice(this int x) => x * 2; } return 2.Twice();"));
        }

        [Test]
        public void UnityNamesWinUnlessTheScriptAliasesThem()
        {
            var source = EvalSource.Build("return 1;", "W");
            StringAssert.Contains("using Object = UnityEngine.Object;", source);
            StringAssert.Contains("using Random = UnityEngine.Random;", source);
            var own = EvalSource.Build("using Object = System.Object; return 1;", "W");
            Assert.AreEqual(1, own.Split('\n').Count(l => l.StartsWith("using Object")));
        }

        enum Shade { Dark }

        struct Loop
        {
            public Loop Next => new Loop();
        }

        [Test]
        public void ResultsConvertLikeTheOfficialPackage()
        {
            Assert.AreEqual(new Vector3(1, 2, 3).ToString(), ScriptEvalCommand.ToJson(new Vector3(1, 2, 3)));
            Assert.AreEqual("Dark", ScriptEvalCommand.ToJson(Shade.Dark));
            Assert.IsTrue(JToken.DeepEquals(JToken.Parse("{\"a\":1,\"b\":\"two\"}"), (JToken)ScriptEvalCommand.ToJson(new { a = 1, b = "two" })));
            Assert.IsTrue(JToken.DeepEquals(JToken.Parse("{\"k\":1,\"nested\":[2,3]}"),
                (JToken)ScriptEvalCommand.ToJson(new JObject { ["k"] = 1, ["nested"] = new JArray(2, 3) })));
            Assert.IsTrue(JToken.DeepEquals(JToken.Parse("[1,2]"), (JToken)ScriptEvalCommand.ToJson(new List<int> { 1, 2 })));
            Assert.IsNull(ScriptEvalCommand.ToJson(null));
            // A value whose property returns the same kind of value stops at the depth limit.
            Assert.IsInstanceOf<JObject>(ScriptEvalCommand.ToJson(new Loop()));
        }

        [Test]
        public void FrameworkObjectsAreNeverReadThroughTheirProperties()
        {
            // Reading Task.Result here would block the thread for good.
            var pending = new TaskCompletionSource<int>().Task;
            Assert.AreEqual(pending.ToString(), ScriptEvalCommand.ToJson(pending));
            Assert.AreEqual(typeof(string).ToString(), ScriptEvalCommand.ToJson(typeof(string)));
            var lazy = new Lazy<int>(() => throw new InvalidOperationException("must not run"));
            Assert.AreEqual(lazy.ToString(), ScriptEvalCommand.ToJson(lazy));
        }

        // The script may have asked for the reload that beats its result hop: a retryable 503 would make the CLI
        // resend it and reload again. FailPending stands in for the reload, while the main thread pumps nothing.
        [UnityTest]
        public IEnumerator ReloadAfterTheScriptRanIsNotRetryable()
        {
            Assume.That(MainThreadQueue.IsAttached, Is.True, "the Upipe server is disabled here, so nothing pumps the main-thread queue");
            var key = "UpipeEvalTests_" + Guid.NewGuid().ToString("N");
            var gate = new TaskCompletionSource<object>();
            AppDomain.CurrentDomain.SetData(key, gate);
            var code = "var domain = System.AppDomain.CurrentDomain; domain.SetData(\"" + key + ".ran\", true); " +
                "await ((System.Threading.Tasks.TaskCompletionSource<object>)domain.GetData(\"" + key + "\")).Task.ConfigureAwait(false); return 1;";
            var handler = CommandRegistry.Find("script", "eval").CancellableHandler;
            var task = Task.Run(() => handler(new JObject { ["code"] = code }, CancellationToken.None));
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted && DateTime.UtcNow < deadline && AppDomain.CurrentDomain.GetData(key + ".ran") == null)
                yield return null;
            // From here until the reload, this thread never yields: the script ends off the main thread and its
            // result hop waits in the queue.
            gate.SetResult(null);
            var queue = (ICollection)typeof(MainThreadQueue).GetField("s_Work", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            while (!task.IsCompleted && DateTime.UtcNow < deadline && queue.Count == 0)
                Thread.Sleep(5);
            MainThreadQueue.FailPending();
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
                yield return null;
            AppDomain.CurrentDomain.SetData(key, null);
            AppDomain.CurrentDomain.SetData(key + ".ran", null);

            var error = UpipeServer.Unwrap(task.Exception) as CommandFailure;
            Assert.IsNotNull(error, task.Exception?.ToString() ?? "eval finished before the reload");
            StringAssert.Contains("domain reload", error.Message);
        }

        // A reload that cuts off a started script must not get the retryable 503 either: the client would resend it and
        // the script would run again from the top. The flag is up only for the instant the cancelled wait takes.
        [Test]
        public void ReloadCuttingOffAStartedScriptIsNotRetryable()
        {
            var never = new TaskCompletionSource<object>().Task;
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                Assert.Catch<OperationCanceledException>(() => ScriptEvalCommand.Await(never, cancel.Token, "X"));
                var prior = MainThreadQueue.DomainReloadInProgress;
                CommandFailure failure;
                try
                {
                    MainThreadQueue.DomainReloadInProgress = true;
                    failure = Assert.Throws<CommandFailure>(() => ScriptEvalCommand.Await(never, cancel.Token, "X"));
                }
                finally
                {
                    MainThreadQueue.DomainReloadInProgress = prior;
                }
                StringAssert.Contains("domain reload cut it off", failure.Message);
            }
        }

        [UnityTest]
        public IEnumerator EvalHonorsUnityEditorDefineAndUtf8Literals()
        {
            Assume.That(MainThreadQueue.IsAttached, Is.True, "the Upipe server is disabled here, so nothing pumps the main-thread queue");
            var handler = CommandRegistry.Find("script", "eval").CancellableHandler;
            var task = Task.Run(() => handler(new JObject
            {
                ["code"] = "#if UNITY_EDITOR\nreturn \"编辑_αβ\";\n#else\nreturn \"player\";\n#endif",
            }, CancellationToken.None));
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
                yield return null;
            Assert.IsTrue(task.IsCompletedSuccessfully, task.Exception?.ToString());
            Assert.AreEqual("编辑_αβ", ((JToken)task.Result).Value<string>());
        }

        [UnityTest]
        public IEnumerator CancelledEvalSurfacesOperationCanceledException()
        {
            Assume.That(MainThreadQueue.IsAttached, Is.True, "the Upipe server is disabled here, so nothing pumps the main-thread queue");
            var key = "UpipeEvalCancel_" + Guid.NewGuid().ToString("N");
            var code = "var domain = System.AppDomain.CurrentDomain; domain.SetData(\"" + key + ".started\", true); " +
                "await System.Threading.Tasks.Task.Delay(-1).ConfigureAwait(false); return 1;";
            var handler = CommandRegistry.Find("script", "eval").CancellableHandler;
            using (var cancel = new CancellationTokenSource())
            {
                var task = Task.Run(() => handler(new JObject { ["code"] = code }, cancel.Token));
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!task.IsCompleted && DateTime.UtcNow < deadline && AppDomain.CurrentDomain.GetData(key + ".started") == null)
                    yield return null;
                Assert.AreEqual(true, AppDomain.CurrentDomain.GetData(key + ".started"));
                cancel.Cancel();
                deadline = DateTime.UtcNow.AddSeconds(10);
                while (!task.IsCompleted && DateTime.UtcNow < deadline)
                    yield return null;
                AppDomain.CurrentDomain.SetData(key + ".started", null);
                Assert.IsTrue(task.IsFaulted);
                Assert.IsInstanceOf<OperationCanceledException>(UpipeServer.Unwrap(task.Exception));
            }
        }

        [Test]
        public void CscCompilerUsesMemoryAndDiskCacheKeyedBySourceAndDefines()
        {
            CscCompiler.ClearCache(includeDisk: true);
            var tag = "UpipeDiskCache_" + Guid.NewGuid().ToString("N");
            var code = "public static class " + tag + " { public static int V() => 7; }";

            var (a1, s1, e1) = CscCompiler.Compile(tag, code, "", new[] { "A_ONE", "B_TWO" });
            Assert.IsEmpty(e1);
            Assert.IsNotNull(a1);
            Assert.IsNotNull(s1);
            Assert.AreEqual(1, CscCompiler.CacheMisses);
            Assert.AreEqual(0, CscCompiler.CacheHits);

            // Normalized defines hit the L1 memory cache immediately.
            var (a2, s2, e2) = CscCompiler.Compile(tag, code, "", new[] { "B_TWO", "A_ONE", "A_ONE" });
            Assert.IsEmpty(e2);
            Assert.AreEqual(a1.Length, a2.Length);
            Assert.AreEqual(s1.Length, s2.Length);
            Assert.AreEqual(1, CscCompiler.CacheHits);
            Assert.AreEqual(1, CscCompiler.CacheMisses);

            // Clearing only L1 memory exercises the L2 disk cache under Temp/UpipeEval/cache.
            CscCompiler.ClearCache(includeDisk: false);
            var (a3, _, e3) = CscCompiler.Compile(tag, code, "", new[] { "A_ONE", "B_TWO" });
            Assert.IsEmpty(e3);
            Assert.AreEqual(a1.Length, a3.Length);
            Assert.AreEqual(1, CscCompiler.CacheHits);
            Assert.AreEqual(0, CscCompiler.CacheMisses);

            // Missing .pdb files in L2 disk cache must not invalidate a valid cached .dll.
            CscCompiler.ClearCache(includeDisk: false);
            foreach (var pdb in Directory.EnumerateFiles(Path.Combine(CscCompiler.WorkDirectory, "cache"), "*.pdb"))
                File.Delete(pdb);
            var (aNoPdb, sNoPdb, eNoPdb) = CscCompiler.Compile(tag, code, "", new[] { "A_ONE", "B_TWO" });
            Assert.IsEmpty(eNoPdb);
            Assert.IsNotNull(aNoPdb);
            Assert.IsNull(sNoPdb);
            Assert.AreEqual(1, CscCompiler.CacheHits);
            Assert.AreEqual(0, CscCompiler.CacheMisses);

            // Changing defines misses the cache cleanly.
            var (_, _, e4) = CscCompiler.Compile(tag, code, "", new[] { "A_ONE" });
            Assert.IsEmpty(e4);
            Assert.AreEqual(1, CscCompiler.CacheMisses);
        }

        [Test]
        public void ConcurrentCompilesShareDiskCacheSafely()
        {
            CscCompiler.ClearCache(includeDisk: true);
            var tag = "UpipeConcurrent_" + Guid.NewGuid().ToString("N");
            var snippet = "public static class " + tag + " { public static int V() => 77; }";
            using (var gate = new ManualResetEventSlim(false))
            {
                var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                {
                    gate.Wait();
                    return CscCompiler.Compile(tag, snippet, "");
                })).ToArray();
                gate.Set();
                Task.WaitAll(tasks);
                foreach (var result in tasks.Select(t => t.Result))
                {
                    Assert.IsEmpty(result.errors);
                    Assert.IsNotNull(result.assembly);
                    Assert.AreEqual(tasks[0].Result.assembly.Length, result.assembly.Length);
                }
            }
            var cacheDir = Path.Combine(CscCompiler.WorkDirectory, "cache");
            Assert.AreEqual(1, Directory.GetFiles(cacheDir, "*.dll").Length);
            Assert.AreEqual(1, Directory.GetFiles(cacheDir, "*.pdb").Length);
            Assert.IsEmpty(Directory.GetFiles(cacheDir, "*.tmp"));
        }

        [Test]
        public void DiskCachePruningEvictsOldestEntriesAndOrphanSymbols()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "upipe-prune-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var now = DateTime.UtcNow;
                for (var i = 0; i < 3; i++)
                {
                    var dll = Path.Combine(tempDir, $"k{i}.dll");
                    var pdb = Path.Combine(tempDir, $"k{i}.pdb");
                    File.WriteAllBytes(dll, new byte[] { (byte)(i + 1) });
                    File.WriteAllBytes(pdb, new byte[] { (byte)(i + 10) });
                    File.SetLastWriteTimeUtc(dll, now.AddMinutes(i));
                    File.SetLastWriteTimeUtc(pdb, now.AddMinutes(i));
                }
                var orphanPdb = Path.Combine(tempDir, "orphan.pdb");
                File.WriteAllBytes(orphanPdb, new byte[] { 99 });

                CscCompiler.PruneDiskCache(tempDir, maxEntries: 2);

                Assert.IsFalse(File.Exists(Path.Combine(tempDir, "k0.dll")));
                Assert.IsFalse(File.Exists(Path.Combine(tempDir, "k0.pdb")));
                Assert.IsFalse(File.Exists(orphanPdb));
                Assert.IsTrue(File.Exists(Path.Combine(tempDir, "k1.dll")));
                Assert.IsTrue(File.Exists(Path.Combine(tempDir, "k1.pdb")));
                Assert.IsTrue(File.Exists(Path.Combine(tempDir, "k2.dll")));
                Assert.IsTrue(File.Exists(Path.Combine(tempDir, "k2.pdb")));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Test]
        public void DiskCachePruningKeepsTheSymbolsOfAnEntryItCannotDelete()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "upipe-prune-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var now = DateTime.UtcNow;
                for (var i = 0; i < 2; i++)
                {
                    File.WriteAllBytes(Path.Combine(tempDir, $"k{i}.dll"), new byte[] { (byte)(i + 1) });
                    File.WriteAllBytes(Path.Combine(tempDir, $"k{i}.pdb"), new byte[] { (byte)(i + 10) });
                    File.SetLastWriteTimeUtc(Path.Combine(tempDir, $"k{i}.dll"), now.AddMinutes(i));
                }
                var oldestDll = Path.Combine(tempDir, "k0.dll");
                // Open the way TryGetCached's File.ReadAllBytes does: on Windows that blocks the delete, elsewhere not.
                using (new FileStream(oldestDll, FileMode.Open, FileAccess.Read, FileShare.Read))
                    CscCompiler.PruneDiskCache(tempDir, maxEntries: 1);

                var dllKept = File.Exists(oldestDll);
                if (Application.platform == RuntimePlatform.WindowsEditor)
                    Assert.IsTrue(dllKept, "an open file cannot be deleted on Windows");
                Assert.AreEqual(dllKept, File.Exists(Path.Combine(tempDir, "k0.pdb")), "an entry keeps its .pdb exactly while its .dll stays");
                Assert.IsTrue(File.Exists(Path.Combine(tempDir, "k1.dll")));
                Assert.IsTrue(File.Exists(Path.Combine(tempDir, "k1.pdb")));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [UnityTest]
        public IEnumerator RepeatedEvalHitsCacheAndIsolatesStaticStateAndVoidCalls()
        {
            Assume.That(MainThreadQueue.IsAttached, Is.True, "the Upipe server is disabled here, so nothing pumps the main-thread queue");
            CscCompiler.ClearCache(includeDisk: true);
            var handler = CommandRegistry.Find("script", "eval").CancellableHandler;

            async Task<JToken> EvalAsync(string code)
            {
                return (JToken)await Task.Run(() => handler(new JObject { ["code"] = code }, CancellationToken.None));
            }

            var staticTag = Guid.NewGuid().ToString("N");
            var staticCode = "class Counter_" + staticTag + " { public static int X; }\nreturn ++Counter_" + staticTag + ".X;";
            for (var run = 0; run < 2; run++)
            {
                var task = EvalAsync(staticCode);
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!task.IsCompleted && DateTime.UtcNow < deadline)
                    yield return null;
                Assert.IsTrue(task.IsCompletedSuccessfully, task.Exception?.ToString());
                Assert.AreEqual(1, task.Result.Value<int>(), "static state must reset cleanly across repeated evals");
            }
            Assert.GreaterOrEqual(CscCompiler.CacheHits, 1);
            var missesAfterStatic = CscCompiler.CacheMisses;

            var voidCode = "UnityEngine.Debug.Log(\"upipe_void_" + staticTag + "\")";
            var firstVoid = EvalAsync(voidCode);
            var firstDeadline = DateTime.UtcNow.AddSeconds(60);
            while (!firstVoid.IsCompleted && DateTime.UtcNow < firstDeadline)
                yield return null;
            Assert.IsTrue(firstVoid.IsCompletedSuccessfully, firstVoid.Exception?.ToString());
            Assert.IsNull(firstVoid.Result);
            Assert.AreEqual(missesAfterStatic + 2, CscCompiler.CacheMisses, "first void call runs wrapTrailing=true probe then fallback");

            var hitsBeforeSecondVoid = CscCompiler.CacheHits;
            var secondVoid = EvalAsync(voidCode);
            var secondDeadline = DateTime.UtcNow.AddSeconds(60);
            while (!secondVoid.IsCompleted && DateTime.UtcNow < secondDeadline)
                yield return null;
            Assert.IsTrue(secondVoid.IsCompletedSuccessfully, secondVoid.Exception?.ToString());
            Assert.IsNull(secondVoid.Result);
            // Second void invocation must skip the CS0030 probe and hit the cache directly.
            Assert.AreEqual(missesAfterStatic + 2, CscCompiler.CacheMisses);
            Assert.GreaterOrEqual(CscCompiler.CacheHits, hitsBeforeSecondVoid + 1);

            // Simulated domain reload: clearing in-memory void hashes + L1 cache must still hit L2 directly with zero CS0030 probe.
            ScriptEvalCommand.Detach();
            CscCompiler.ClearCache(includeDisk: false);
            var afterReloadVoid = EvalAsync(voidCode);
            var reloadDeadline = DateTime.UtcNow.AddSeconds(60);
            while (!afterReloadVoid.IsCompleted && DateTime.UtcNow < reloadDeadline)
                yield return null;
            Assert.IsTrue(afterReloadVoid.IsCompletedSuccessfully, afterReloadVoid.Exception?.ToString());
            Assert.IsNull(afterReloadVoid.Result);
            Assert.AreEqual(0, CscCompiler.CacheMisses, "post-reload void eval should hit L2 disk cache directly without a CS0030 probe");
            Assert.AreEqual(1, CscCompiler.CacheHits);
        }
    }
}
