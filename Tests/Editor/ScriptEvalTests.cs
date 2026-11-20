using System;
using System.Collections;
using System.Collections.Generic;
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
    }
}
