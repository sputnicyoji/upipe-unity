#if UPIPE_TEST_FRAMEWORK
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Upipe.Editor
{
    // `test list|run|status|cancel` on com.unity.test-framework's TestRunnerApi (2022.3 ships 1.1.33). The run Upipe
    // started lives in memory, is saved to SessionState before every domain reload and is read back, with the callbacks
    // registered again, by the next domain: entering Play Mode reloads, so a PlayMode run keeps reporting through
    // `test status`. Runs started elsewhere (Test Runner window, -runTests) are not recorded.
    internal static class TestCommands
    {
        const string SavedRunKey = "Upipe.TestRun";
        const string Running = "running";
        const string Completed = "completed";
        const string Interrupted = "interrupted";
        const string Errored = "error";
        internal static readonly string[] Details = { "full", "failures", "summary" };
        const int MaxListedFailures = 20;

        static readonly object s_Lock = new object();
        // The current or last run; null when none was started in this Editor session. Fields: status, mode, async,
        // filter, filterType, phase (the TestMode running now), next (the TestMode after it), pending (phase not
        // started yet), startedAt, finishedAt, error, results.
        static JObject s_Run;
        // Set when the run ends; only a synchronous `test run` in this domain waits on it.
        static TaskCompletionSource<bool> s_Ended;
        static TestRunnerApi s_Api;

        public static void Register()
        {
            var saved = SessionState.GetString(SavedRunKey, "");
            if (saved.Length > 0)
                s_Run = JObject.Load(new JsonTextReader(new StringReader(saved)) { DateParseHandling = DateParseHandling.None });
            s_Api = ScriptableObject.CreateInstance<TestRunnerApi>();
            s_Api.hideFlags = HideFlags.HideAndDontSave;
            // Callbacks live in memory only, so every domain registers its own.
            s_Api.RegisterCallbacks(new Recorder());
            // Called from UpipeServer's static constructor, so this runs before the server stops for the reload.
            AssemblyReloadEvents.beforeAssemblyReload += SaveForReload;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredEditMode)
                    EditorApplication.delayCall += Reconcile;
            };
            if (IsRunning)
                EditorApplication.delayCall += Reconcile;

            CommandRegistry.Register(new CommandEntry
            {
                Group = "test",
                Name = "list",
                Alias = "test_list",
                Description = "List the project's tests without running them: FullName, Mode, Assembly, Categories, Explicit.",
                Tags = new[] { "test" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "mode", Description = "all (default), editor (editmode) or playmode." },
                },
                // Starts on the main thread and waits here: the list arrives on a later Editor update.
                MainThreadRequired = false,
                WaitsForSettle = true,
                ReadOnly = true,
                CancellableHandler = List,
            });
            CommandRegistry.Register(new CommandEntry
            {
                Group = "test",
                Name = "run",
                Alias = "run_tests",
                Description = "Run tests in this Editor and wait for the results. playmode and all need async-tests=true: " +
                    "entering Play Mode reloads the domain, so poll test status instead. Fails unless at least one test ran and none failed.",
                Tags = new[] { "test" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "mode", Description = "editor (default; also editmode), playmode or all (EditMode, then PlayMode)." },
                    new CommandParameter { Name = "filter", Description = "Which tests: a case-insensitive part of the full name, or an assembly or category name (see filter-type)." },
                    new CommandParameter { Name = "filter-type", Description = "TestName (default), Assembly or Category." },
                    new CommandParameter { Name = "async-tests", IsBool = true, Description = "Answer once the run starts (default false) and report through test status." },
                    new CommandParameter { Name = "timeout", IsInteger = true, Description = "Seconds to wait for a synchronous run (default 600); the unity CLI's own --timeout takes precedence." },
                },
                MainThreadRequired = false,
                WaitsForSettle = true,
                DefaultTimeoutSeconds = 600,
                CancellableHandler = Run,
            });
            CommandRegistry.Register(new CommandEntry
            {
                Group = "test",
                Name = "status",
                Alias = "test_status",
                Description = "Report the last test run started here: status (running, completed, interrupted, error, or no_tests), " +
                    "summary and results. A finished run that failed answers as a failure, like test run.",
                Tags = new[] { "test" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "detail", Description = "full (default), failures (failed results only) or summary (counts only)." },
                },
                MainThreadRequired = false,
                ReadOnly = true,
                Handler = StatusCommand,
            });
            CommandRegistry.Register(new CommandEntry
            {
                Group = "test",
                Name = "cancel",
                Alias = "test_cancel",
                Description = "Not supported with com.unity.test-framework 1.1.33, which has no API to stop a run.",
                Tags = new[] { "test" },
                MainThreadRequired = false,
                Handler = _ => Cancel(),
            });
        }

        internal static bool IsRunning
        {
            get
            {
                lock (s_Lock)
                    return (string)s_Run?["status"] == Running;
            }
        }

        // Also served as GET /api/test-status. Any thread.
        public static JObject Status(string detail)
        {
            lock (s_Lock)
            {
                if (s_Run == null)
                    return new JObject { ["status"] = "no_tests" };
                var reply = new JObject();
                foreach (var key in new[] { "status", "mode", "async", "filter", "filterType", "startedAt", "finishedAt", "error" })
                    if (s_Run[key] != null && s_Run[key].Type != JTokenType.Null)
                        reply[key] = s_Run[key].DeepClone();
                if ((string)s_Run["status"] == Running)
                    reply["phase"] = s_Run["phase"].DeepClone();
                var results = (JArray)s_Run["results"];
                reply["summary"] = Summary(results);
                if (detail == "full")
                    reply["results"] = results.DeepClone();
                else if (detail == "failures")
                    reply["results"] = new JArray(results.Where(IsFailure).Select(r => r.DeepClone()));
                return reply;
            }
        }

        static bool IsFailure(JToken record) => (string)record["status"] == nameof(TestStatus.Failed);

        static JObject Summary(JArray results)
        {
            int Count(TestStatus status) => results.Count(r => (string)r["status"] == status.ToString());
            return new JObject
            {
                ["total"] = results.Count,
                ["passed"] = Count(TestStatus.Passed),
                ["failed"] = Count(TestStatus.Failed),
                ["skipped"] = Count(TestStatus.Skipped),
                ["inconclusive"] = Count(TestStatus.Inconclusive),
                ["duration"] = Math.Round(results.Sum(r => (double?)r["duration"] ?? 0), 3),
            };
        }

        // A finished run passes when at least one test ran and none failed; skipped and inconclusive tests do not fail it.
        // The unity CLI drops `result` from a failure and prints errorDetails, so the failing tests are listed there.
        internal static CommandFailure FailureFor(JObject status)
        {
            var state = (string)status["status"];
            if (state == Running || state == "no_tests")
                return null;
            if (state != Completed)
                return new CommandFailure("Command Execution Failed",
                    (state == Interrupted ? "TEST_RUN_INTERRUPTED: " : "TEST_RUN_ERROR: ") + status["error"]) { Result = status };
            var summary = (JObject)status["summary"];
            int Field(string name) => (int)summary[name];
            if (Field("failed") > 0)
            {
                var text = $"TESTS_FAILED: {Field("failed")} of {Field("total")} tests failed ({Field("passed")} passed, {Field("skipped")} skipped).";
                if (status["results"] is JArray results)
                {
                    var failed = results.Where(IsFailure).ToList();
                    text += string.Concat(failed.Take(MaxListedFailures).Select(r => $"\n{r["fullName"]}: {OneLine((string)r["message"])}"));
                    if (failed.Count > MaxListedFailures)
                        text += $"\n... and {failed.Count - MaxListedFailures} more; see test status --detail failures.";
                }
                return new CommandFailure("Command Execution Failed", text) { Result = status };
            }
            if (Field("passed") + Field("inconclusive") == 0)
            {
                var failure = CommandFailure.InvalidUsage($"No test ran: nothing in mode {status["mode"]} matched " +
                    (status["filter"] != null ? $"{status["filterType"]} '{status["filter"]}'" : "the run") +
                    (Field("skipped") > 0 ? $" except {Field("skipped")} skipped test(s)." : "."));
                failure.Result = status;
                return failure;
            }
            return null;
        }

        static string OneLine(string text)
        {
            text = Regex.Replace((text ?? "").Trim(), @"\s*\n\s*", " | ");
            return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
        }

        static object StatusCommand(JObject parameters)
        {
            var status = Status(ParseDetail((string)parameters["detail"]));
            var failure = FailureFor(status);
            if (failure != null)
                throw failure;
            return status;
        }

        // The detail level in canonical form (full when none is given), or null when it is not one of Details.
        internal static string NormalizeDetail(string detail)
        {
            detail = (detail ?? "full").ToLowerInvariant();
            return Details.Contains(detail) ? detail : null;
        }

        internal static string ParseDetail(string detail)
        {
            return NormalizeDetail(detail) ?? throw CommandFailure.InvalidUsage("detail must be full, failures or summary.");
        }

        static object Cancel()
        {
            if (!IsRunning)
                return new JObject { ["status"] = "no_tests" };
            throw new CommandFailure("Command Execution Failed", "NOT_SUPPORTED: com.unity.test-framework 1.1.33 has no API to " +
                "cancel a test run (TestRunnerApi.CancelTestRun comes with a newer test-framework); poll test status until it ends.");
        }

        // editor/editmode -> EditMode, playmode -> PlayMode, all -> both, in run order.
        internal static TestMode[] ParseModes(string mode, string fallback)
        {
            switch ((mode ?? fallback).ToLowerInvariant())
            {
                case "editor":
                case "editmode":
                    return new[] { TestMode.EditMode };
                case "playmode":
                    return new[] { TestMode.PlayMode };
                case "all":
                    return new[] { TestMode.EditMode, TestMode.PlayMode };
                default:
                    throw CommandFailure.InvalidUsage("mode must be editor (editmode), playmode or all.");
            }
        }

        internal static string ParseFilterType(string type)
        {
            switch ((type ?? "TestName").ToLowerInvariant())
            {
                case "testname":
                    return "TestName";
                case "assembly":
                    return "Assembly";
                case "category":
                    return "Category";
                default:
                    throw CommandFailure.InvalidUsage("filter-type must be TestName, Assembly or Category.");
            }
        }

        // 1.1.33 matches testNames and assemblyNames exactly (assemblies ignoring case) and groupNames and categoryNames
        // as regular expressions, so a partial, case-insensitive name match is an escaped group pattern.
        internal static Filter BuildFilter(TestMode mode, string filter, string filterType)
        {
            var built = new Filter { testMode = mode };
            if (string.IsNullOrEmpty(filter))
                return built;
            var escaped = Regex.Escape(filter);
            if (filterType == "Assembly")
                built.assemblyNames = new[] { filter };
            else if (filterType == "Category")
                built.categoryNames = new[] { "(?i)^" + escaped + "$" };
            else
                built.groupNames = new[] { "(?i)" + escaped };
            return built;
        }

        static object List(JObject parameters, CancellationToken cancel)
        {
            var tests = new JArray();
            foreach (var mode in ParseModes((string)parameters["mode"], "all"))
            {
                var listed = new TaskCompletionSource<JArray>(TaskCreationOptions.RunContinuationsAsynchronously);
                ScriptRunCommand.OnMainThread(() =>
                {
                    s_Api.RetrieveTestList(mode, root => listed.TrySetResult(Flatten(root, mode, new JArray())));
                    return null;
                }, cancel, "could not list the tests");
                listed.Task.Wait(cancel);
                foreach (var test in listed.Task.Result)
                    tests.Add(test);
            }
            return tests;
        }

        // Test cases only. Their TestMode reads 0 in 1.1.33 (only assembly nodes carry the platform), hence `mode`.
        static JArray Flatten(ITestAdaptor test, TestMode mode, JArray into)
        {
            if (!test.IsSuite)
            {
                var assembly = test;
                while (assembly != null && !assembly.IsTestAssembly)
                    assembly = assembly.Parent;
                into.Add(new JObject
                {
                    ["FullName"] = test.FullName,
                    ["Mode"] = mode.ToString(),
                    ["Assembly"] = assembly != null ? Path.GetFileNameWithoutExtension(assembly.Name) : null,
                    ["Categories"] = new JArray(test.Categories ?? Array.Empty<string>()),
                    ["Explicit"] = test.RunState == RunState.Explicit,
                });
            }
            foreach (var child in test.Children)
                Flatten(child, mode, into);
            return into;
        }

        static object Run(JObject parameters, CancellationToken cancel)
        {
            var modes = ParseModes((string)parameters["mode"], "editor");
            var filterType = ParseFilterType((string)parameters["filter-type"]);
            var filter = (string)parameters["filter"];
            var async = CommandRegistry.ReadBool(parameters, "async-tests", false);
            if (!async && modes.Contains(TestMode.PlayMode))
                throw CommandFailure.InvalidUsage("playmode and all enter Play Mode, which reloads the domain and would cut this " +
                    "request off: pass --async-tests true and poll test status.");

            var ended = (Task)ScriptRunCommand.OnMainThread(() => Start(modes, filter, filterType, async),
                cancel, "could not start the test run");
            if (async)
            {
                // Without counts: the unity CLI prints run_tests summaries as "0/0 passed (all)", which reads as a result.
                var started = Status("summary");
                started.Remove("summary");
                return started;
            }
            // On the request timeout this stops waiting; the run goes on and test status still reports it.
            ended.Wait(cancel);
            var status = Status("full");
            var failure = FailureFor(status);
            if (failure != null)
                throw failure;
            return status;
        }

        // Main thread. Returns the task that completes when the run ends.
        static Task Start(TestMode[] modes, string filter, string filterType, bool async)
        {
            if (IsRunning || FrameworkHasLiveRun() == true)
                throw new CommandFailure("Command Execution Failed", "TEST_RUN_IN_PROGRESS: a test run is still going " +
                    "(com.unity.test-framework 1.1.33 cannot cancel it); poll test status until it ends.");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new CommandFailure("Command Execution Failed", "PLAY_MODE_ACTIVE: tests start from Edit Mode; stop Play Mode first.");
            // The runner asks whether to save modified scenes in a modal dialog, which would hold the Editor until someone answers.
            var dirty = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(s => s.isDirty).ToList();
            if (dirty.Count > 0 && !Application.isBatchMode)
                throw new CommandFailure("Command Execution Failed", "UNSAVED_SCENES: save or revert " +
                    string.Join(", ", dirty.Select(s => s.path.Length > 0 ? s.path : "the untitled scene")) +
                    " first; the test runner would stop at a save dialog.");

            var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (s_Lock)
            {
                s_Run = new JObject
                {
                    ["status"] = Running,
                    ["mode"] = modes.Length > 1 ? "all" : modes[0] == TestMode.EditMode ? "editor" : "playmode",
                    ["async"] = async,
                    ["filter"] = filter,
                    ["filterType"] = filterType,
                    ["phase"] = modes[0].ToString(),
                    ["next"] = modes.Length > 1 ? modes[1].ToString() : null,
                    ["pending"] = false,
                    ["startedAt"] = DateTime.UtcNow.ToString("o"),
                    ["results"] = new JArray(),
                };
                s_Ended = ended;
            }
            Execute(modes[0]);
            return ended.Task;
        }

        // Main thread.
        static void Execute(TestMode mode)
        {
            string filter, filterType;
            lock (s_Lock)
            {
                filter = (string)s_Run["filter"];
                filterType = (string)s_Run["filterType"];
            }
            try
            {
                s_Api.Execute(new ExecutionSettings(BuildFilter(mode, filter, filterType)));
            }
            catch (Exception e)
            {
                lock (s_Lock)
                    End(Errored, "The Test Framework refused the run: " + e.Message);
                throw new CommandFailure("Command Execution Failed", "TEST_RUN_ERROR: " + e.Message);
            }
        }

        // Main thread: the second half of `all`, once the EditMode run has wound down (and after a reload lost the delayCall).
        static void StartPending()
        {
            lock (s_Lock)
            {
                if ((string)s_Run?["status"] != Running || !(bool)s_Run["pending"])
                    return;
            }
            if (FrameworkHasLiveRun() == true)
            {
                EditorApplication.delayCall += StartPending;
                return;
            }
            TestMode phase;
            lock (s_Lock)
            {
                s_Run["pending"] = false;
                phase = (TestMode)Enum.Parse(typeof(TestMode), (string)s_Run["phase"]);
            }
            try
            {
                Execute(phase);
            }
            catch (CommandFailure)
            {
                // recorded as an error run
            }
        }

        // Main thread, after a domain loads and when Play Mode ends: a run that is neither in the framework's job table nor
        // in Play Mode (where PlayMode tests run after their job has ended) stopped without a RunFinished.
        static void Reconcile()
        {
            bool pending;
            lock (s_Lock)
            {
                if ((string)s_Run?["status"] != Running)
                    return;
                pending = (bool)s_Run["pending"];
            }
            if (pending)
            {
                StartPending();
                return;
            }
            if (FrameworkHasLiveRun() != false || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            lock (s_Lock)
                End(Interrupted, "The run stopped without reporting its end (a domain reload, Play Mode being stopped or an Editor error cut it short).");
        }

        // Main thread, before the domain unloads. A synchronous run's waiting request dies with the domain, so that run
        // is reported as interrupted (the framework may resume it, but nobody is left to answer).
        static void SaveForReload()
        {
            string text;
            lock (s_Lock)
            {
                if (s_Run == null)
                    return;
                if ((string)s_Run["status"] == Running && !(bool)s_Run["async"])
                    End(Interrupted, "A domain reload cut off the request waiting for this run; use --async-tests true for tests that reload.");
                text = s_Run.ToString(Formatting.None);
            }
            SessionState.SetString(SavedRunKey, text);
        }

        // Caller holds s_Lock.
        static void End(string status, string error)
        {
            if ((string)s_Run?["status"] != Running)
                return;
            s_Run["status"] = status;
            s_Run["finishedAt"] = DateTime.UtcNow.ToString("o");
            s_Run["next"] = null;
            if (error != null)
                s_Run["error"] = error;
            s_Ended?.TrySetResult(true);
        }

        static FieldInfo s_TestRunsField;
        static FieldInfo s_IsRunningField;
        static object s_Holder;

        // Whether the Test Framework still has a test job going; null when its internals are not where 1.1.33 has them.
        // shortcut: reads TestJobDataHolder by reflection; if a test-framework upgrade moves it, a run cut short by a
        // reload stays "running" and runs started elsewhere are not refused. Re-check the names when upgrading.
        static bool? FrameworkHasLiveRun()
        {
            if (s_Holder == null)
            {
                var holder = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder");
                s_TestRunsField = holder?.GetField("TestRuns");
                s_IsRunningField = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobData")?.GetField("isRunning");
                s_Holder = holder?.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
            }
            if (s_Holder == null || s_IsRunningField == null || !(s_TestRunsField?.GetValue(s_Holder) is IEnumerable runs))
                return null;
            return runs.Cast<object>().Any(run => (bool)s_IsRunningField.GetValue(run));
        }

        // Main thread (the framework calls back from Editor updates and from Play Mode).
        sealed class Recorder : ICallbacks, IErrorCallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                // Test cases, plus a suite that failed on its own (a OneTimeTearDown, say) rather than through a child.
                if (result.Test.IsSuite && !(result.TestStatus == TestStatus.Failed && result.FailCount == 0))
                    return;
                var record = new JObject
                {
                    ["fullName"] = result.FullName,
                    ["status"] = result.TestStatus.ToString(),
                    ["duration"] = Math.Round(result.Duration, 3),
                    ["message"] = result.Message ?? "",
                    ["stackTrace"] = result.StackTrace ?? "",
                };
                lock (s_Lock)
                {
                    if ((string)s_Run?["status"] == Running && !(bool)s_Run["pending"])
                        ((JArray)s_Run["results"]).Add(record);
                }
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                lock (s_Lock)
                {
                    if ((string)s_Run?["status"] != Running || (bool)s_Run["pending"])
                        return;
                    var next = (string)s_Run["next"];
                    if (next == null)
                    {
                        End(Completed, null);
                        return;
                    }
                    s_Run["phase"] = next;
                    s_Run["next"] = null;
                    s_Run["pending"] = true;
                }
                EditorApplication.delayCall += StartPending;
            }

            public void OnError(string message)
            {
                lock (s_Lock)
                    End(Errored, message);
            }
        }
    }
}
#endif
