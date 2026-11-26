#if UPIPE_TEST_FRAMEWORK
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Upipe.Editor
{
    // `test list|run|status|cancel` on com.unity.test-framework's TestRunnerApi (2022.3 ships 1.1.33). The run Upipe
    // started lives in memory, is saved to SessionState before every domain reload and is read back, with the callbacks
    // registered again, by the next domain: entering Play Mode reloads, so a PlayMode run keeps reporting through
    // `test status`. Runs started elsewhere (Test Runner window, -runTests) are not recorded. With test-framework 1.4.0+
    // (UPIPE_TEST_CANCEL) `test cancel` stops the recorded run through TestRunnerApi.CancelTestRun and the run ends canceled.
    internal static class TestCommands
    {
        const string SavedRunKey = "Upipe.TestRun";
        const string Running = "running";
        const string Completed = "completed";
        const string Interrupted = "interrupted";
        const string Errored = "error";
        const string Canceled = "canceled";
        const string CanceledError = "Canceled by test cancel; tests that had not finished are not in the results.";
        internal static readonly string[] Details = { "full", "failures", "summary" };
        const int MaxListedFailures = 20;
        // Error logs kept for a run error: 1.4.3 logs the real causes just before its generic "Multiple unexpected errors".
        const int MaxErrorLog = 3;

        const double StaleRunInterruptSeconds = 0.5;
        const double UnknownFrameworkIdleInterruptSeconds = 5.0;
        const double UnknownFrameworkPendingWaitSeconds = 0.2;
        // How long a finished run waits for the Test Framework to let go of its job (see FinishOnceFrameworkIdle).
        const double FrameworkWindDownSeconds = 10.0;
        // How long test cancel waits for the canceled run to stop before answering it as still running.
        const double CancelWaitSeconds = 10.0;
        // How long a canceled run's framework job may outlive the cancel in Edit Mode before it is canceled again (see EnforceCancel).
        // shortcut: a fixed grace; a framework wind-down slower than this (a minimized Editor) loses its undo and cleanup
        // checks to the second cancel (scenes are still restored), tie it to the job's task progress if that shows up.
        const double CancelGraceSeconds = 2.0;
#if UPIPE_TEST_CANCEL
        static double s_CancelRequestedAt = -1;
#endif

        static readonly object s_Lock = new object();
        static readonly Recorder s_Recorder = new Recorder();
        // The current or last run; null when none was started in this Editor session. Fields: status, mode, async,
        // filter, filterType, testNames, phase (the TestMode running now), next (the TestMode after it), pending (phase
        // not started yet), finishing (last RunFinished seen, framework not idle yet), guid (what TestRunnerApi.Execute
        // returned for the phase running now), cancelRequested (test cancel asked; the run ends canceled), scenes (the
        // scene setup at the start, restored after a cancel or a run error the framework reports), startedAt, finishedAt,
        // error, results, missingTestNames.
        static JObject s_Run;
        // Set when the run ends; only a synchronous `test run` in this domain waits on it.
        static TaskCompletionSource<JObject> s_Ended;
        static TestRunnerApi s_Api;
        static bool s_Attached;
        static bool s_Restored;
        static double s_LastActivityAt = -1;
        static double s_ReconcileRequestedAt = -1;
        // The job reporter of a detached synchronous run (in memory: such a run never outlives the domain), and its counts.
        static Action<string, int?> s_Progress;
        static int s_ProgressTotal;
        static int s_ProgressDone;
        // The scene setup a canceled or framework-errored run started from, for OnUpdate to reopen (set under s_Lock).
        static JArray s_ScenesToReopen;

        internal static bool HasApi => s_Api != null;

        static TestRunnerApi EnsureApi()
        {
            if (s_Api == null)
            {
                s_Api = ScriptableObject.CreateInstance<TestRunnerApi>();
                s_Api.hideFlags = HideFlags.HideAndDontSave;
                // Callbacks live in memory only, so every domain registers its own.
                s_Api.RegisterCallbacks(s_Recorder);
            }
            return s_Api;
        }

        static void DestroyApi()
        {
            if (s_Api == null)
                return;
            try
            {
                s_Api.UnregisterCallbacks(s_Recorder);
            }
            catch (Exception)
            {
            }
            UnityEngine.Object.DestroyImmediate(s_Api);
            s_Api = null;
        }

        public static void Attach()
        {
            if (s_Attached)
                return;
            s_Attached = true;
            if (!s_Restored)
            {
                s_Restored = true;
                var saved = SessionState.GetString(SavedRunKey, "");
                if (saved.Length > 0)
                    s_Run = JObject.Load(new JsonTextReader(new StringReader(saved)) { DateParseHandling = DateParseHandling.None });
            }
            EnsureApi();
            var now = EditorApplication.timeSinceStartup;
            s_LastActivityAt = now;
            s_ReconcileRequestedAt = IsRunning ? now : -1;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnUpdate;
        }

        // A reload saves the run first. A manual stop ends it instead: its callbacks go with the API, so the results that
        // arrive while the server is off would be missing from a run that later reads as completed.
        public static void Detach(bool serverStopped = false)
        {
            if (!s_Attached)
                return;
            s_Attached = false;
            if (serverStopped)
                lock (s_Lock)
                {
                    if ((bool?)s_Run?["finishing"] == true)
                        EndUnlessCanceled(Completed, null); // every result is in, as SaveForReload treats it
                    else
                        EndUnlessCanceled(Interrupted, "The Upipe server was stopped during the run.");
                    s_ScenesToReopen = null; // OnUpdate is gone; a later start must not reopen them
                }
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= OnUpdate;
            DestroyApi();
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            var now = EditorApplication.timeSinceStartup;
            s_LastActivityAt = now;
            if (state == PlayModeStateChange.EnteredEditMode)
                s_ReconcileRequestedAt = now;
        }

        static void OnUpdate()
        {
            if (!s_Attached)
                return;
            ReopenStartingScenes();
            bool running, pending, finishing, cancelRequested;
            lock (s_Lock)
            {
                running = (string)s_Run?["status"] == Running;
                pending = running && (bool)s_Run["pending"];
                finishing = running && (bool?)s_Run["finishing"] == true;
                cancelRequested = running && (bool?)s_Run["cancelRequested"] == true;
            }
            if (!running)
                return;
#if UPIPE_TEST_CANCEL
            // Once RunFinished is in (pending, finishing) only the framework's own wind-down is left; never cut that.
            if (cancelRequested && !pending && !finishing)
                EnforceCancel();
#endif
            if (finishing)
                FinishOnceFrameworkIdle();
            else if (pending)
                StartPending();
            else
                Reconcile();
        }

        public static void Register()
        {
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
                    "entering Play Mode reloads the domain, so poll test status instead. Fails unless at least one test ran and none failed; " +
                    "a run stopped by test cancel fails with TEST_RUN_CANCELED.",
                Tags = new[] { "test" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "mode", Description = "editor (default; also editmode), playmode or all (EditMode, then PlayMode)." },
                    new CommandParameter { Name = "filter", Description = "Which tests: a case-insensitive part of the full name, or an assembly or category name (see filter-type)." },
                    new CommandParameter { Name = "filter-type", Description = "TestName (default), Assembly or Category." },
                    new CommandParameter { Name = "async-tests", IsBool = true, Description = "Answer once the run starts (default false) and report through test status." },
                    new CommandParameter { Name = "timeout", IsInteger = true, Description = "Seconds to wait for a synchronous run (default 600); the unity CLI's own --timeout takes precedence." },
                    // Last, so positional binding of the parameters above stays as it was.
                    new CommandParameter { Name = "test-names", Repeatable = true, Description = "Exact full names of the tests to run, " +
                        "separated by ; (or by commas outside parentheses and quotes, so Ns.Fixture.Add(1,2) stays whole), or a JSON array. " +
                        "A fixture or parameterized method name runs all of it. Not with filter. Fails if any name ran no test." },
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
                Description = "Report the last test run started here: status (running, completed, canceled, interrupted, error, or no_tests), " +
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
#if UPIPE_TEST_CANCEL
                Description = "Cancel the test run started here and answer the state it stops in: canceled, or completed when " +
                    "the run had reported its end before the cancel. A waiting test run and test status then fail with TEST_RUN_CANCELED.",
#else
                Description = "Cancel the test run started here; needs com.unity.test-framework 1.4.0 or newer, so it answers " +
                    "NOT_SUPPORTED while a run is going with this version.",
#endif
                Tags = new[] { "test" },
                // Asks the framework on the main thread, then waits here for the run to stop.
                MainThreadRequired = false,
                CancellableHandler = Cancel,
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
                foreach (var key in new[] { "status", "mode", "async", "filter", "filterType", "testNames", "missingTestNames", "cancelRequested", "startedAt", "finishedAt", "error", "errorLog" })
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
                    (state == Interrupted ? "TEST_RUN_INTERRUPTED: " : state == Canceled ? "TEST_RUN_CANCELED: " : "TEST_RUN_ERROR: ") + status["error"] +
                    (state == Errored ? RunErrorDetails(status) : "")) { Result = status };
            var summary = (JObject)status["summary"];
            int Field(string name) => (int)summary[name];
            // A misspelled name runs nothing and the framework says nothing, so the rest passing must not read as a pass.
            if (status["missingTestNames"] is JArray missing && missing.Count > 0)
            {
                var requested = (status["testNames"] as JArray)?.Count ?? missing.Count;
                var failure = CommandFailure.InvalidUsage($"TEST_NAMES_NOT_RUN: {missing.Count} of {requested} requested test name(s) " +
                    $"matched no test in mode {status["mode"]}: {string.Join(", ", missing.Select(n => (string)n))}" +
                    (Field("failed") > 0 ? $". Also {Field("failed")} of {Field("total")} tests that ran failed." : "."));
                failure.Result = status;
                return failure;
            }
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

        // What the framework's run error hides: how many tests had passed, unrun requested names, and the errors logged just before it.
        static string RunErrorDetails(JObject status)
        {
            var text = status["summary"] is JObject summary && (int)summary["total"] > 0
                ? $"\n{summary["passed"]} of {summary["total"]} tests passed, {summary["failed"]} failed." : "";
            if (status["missingTestNames"] is JArray missing && missing.Count > 0)
                text += $"\nMissing test name(s) (TEST_NAMES_NOT_RUN): {string.Join(", ", missing.Select(n => (string)n))}.";
            if (status["errorLog"] is JArray log && log.Count > 0)
                text += "\nLogged during the run:" + string.Concat(log.Select(line => "\n" + line));
            return text;
        }

        // The last error-level logs of this run, one line each, without the framework's own copy of `error`.
        static JArray ErrorLog(long consoleSeq, string error) => new JArray(ConsoleCommands.ErrorsSince(consoleSeq, MaxErrorLog + 1)
            .Where(message => message.Trim() != (error ?? "").Trim()).Select(OneLine).Reverse().Take(MaxErrorLog).Reverse());

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

        internal enum CancelStep { None, Wait, Defer, Framework }

        // What test cancel does with the run: nothing to cancel; only wait (every result is in, or the framework already has
        // the request); defer (between the halves of `all` no framework job of ours runs, so StartPending ends the run
        // instead of starting the PlayMode half); or ask the framework.
        internal static CancelStep CancelStepFor(JObject run)
        {
            if ((string)run?["status"] != Running)
                return CancelStep.None;
            if ((bool?)run["finishing"] == true || (bool?)run["cancelRequested"] == true)
                return CancelStep.Wait;
            return (bool?)run["pending"] == true ? CancelStep.Defer : CancelStep.Framework;
        }

        static object Cancel(JObject parameters, CommandContext context)
        {
#if UPIPE_TEST_CANCEL
            if (!(bool)MainThreadQueue.RunAndWait(RequestCancel, context.Cancel, "INTERNAL_ERROR: could not cancel the test run"))
                return new JObject { ["status"] = "no_tests" };
            // The framework drops the job within an Editor update or two (a PlayMode run also leaves Play Mode); OnUpdate then
            // ends the run. Answer with the state it ends in, or as still running with cancelRequested if it takes longer.
            // shortcut: waits on any run, so a run another client starts within the 50 ms poll is waited on and answered
            // instead; compare the run's startedAt if concurrent clients turn up.
            var until = DateTime.UtcNow.AddSeconds(CancelWaitSeconds);
            while (IsRunning && DateTime.UtcNow < until && !context.Cancel.IsCancellationRequested)
                Thread.Sleep(50);
            return Status("summary");
#else
            if (!IsRunning)
                return new JObject { ["status"] = "no_tests" };
            throw new CommandFailure("Command Execution Failed", "NOT_SUPPORTED: canceling a test run needs com.unity.test-framework " +
                "1.4.0 or newer; poll test status until this run ends.");
#endif
        }

#if UPIPE_TEST_CANCEL
        // Main thread: false when no run of ours is going.
        static object RequestCancel()
        {
            string guid;
            bool playModePhase;
            lock (s_Lock)
            {
                var step = CancelStepFor(s_Run);
                if (step == CancelStep.None)
                    return false;
                if (step == CancelStep.Defer)
                    s_Run["cancelRequested"] = true;
                if (step != CancelStep.Framework)
                    return true;
                guid = (string)s_Run["guid"];
                playModePhase = (string)s_Run["phase"] == nameof(TestMode.PlayMode);
            }
            if (playModePhase && EditorApplication.isPlaying)
            {
                // In Play Mode, 1.4.3's CancelTestRun drops the job but the tests go on to the end. Leaving Play Mode stops
                // them, and the framework winds the run down (scenes reopened) as for any Play Mode stop.
                EditorApplication.isPlaying = false;
            }
            // 1.4.3 then runs only the tasks marked RunOnCancel (unlock reloads, delete the bootstrap scene, clean up the
            // PlayMode test controller): no RunFinished, no TestFinished for the test that was going, and no scene restore,
            // undo, IPostBuildCleanup or teardown of that test. False when the framework has no running job of that guid.
            else
                CancelFrameworkJob(guid);
            s_LastActivityAt = s_CancelRequestedAt = EditorApplication.timeSinceStartup;
            lock (s_Lock)
                s_Run["cancelRequested"] = true;
            return true;
        }

        // Main thread: cancels the framework's job of this guid and releases its EditMode runner (see ReleaseEditModeRunner).
        internal static void CancelFrameworkJob(string guid)
        {
            var runner = EditModeRunnerOf(guid);
            if (guid == null || !TestRunnerApi.CancelTestRun(guid))
                throw new CommandFailure("Command Execution Failed", "TEST_CANCEL_FAILED: the Test Framework has no running job " +
                    "for this run; it may be ending on its own, poll test status.");
            if (runner != null && !ReleaseEditModeRunner(runner))
                Debug.LogWarning(RunnerNotReleased);
        }

        // Main thread, every update until a canceled run ends. Measured on 1.4.3: a PlayMode run canceled on its way into
        // Play Mode can still get there with no job left to stop its tests, and a PlayMode job whose Play Mode was cut short
        // waits for it forever. So a canceled run never stays in Play Mode, and its job is canceled again when it is still
        // alive CancelGraceSeconds after the cancel or the return to Edit Mode (a normal wind-down takes a few updates).
        static void EnforceCancel()
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                return;
            }
            var since = Math.Max(s_CancelRequestedAt, s_ReconcileRequestedAt);
            if (EditorApplication.isPlayingOrWillChangePlaymode || since < 0 || EditorApplication.timeSinceStartup - since < CancelGraceSeconds ||
                FrameworkHasLiveRun() != true)
                return;
            string guid;
            lock (s_Lock)
                guid = (string)s_Run?["guid"];
            if (guid != null)
                TestRunnerApi.CancelTestRun(guid);
        }
#endif

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

        // A requested name hits a result that is that test, or a test under it: a parameterized method's cases
        // (Name(1,2)), generic cases/fixtures (Name<Int32>(1), Box<Int32>.Test), nested types (Outer+Inner.Test), or a
        // fixture's or namespace's tests (Name.Test).
        internal static bool MatchesTestName(string requested, string fullName)
        {
            if (fullName == null || !fullName.StartsWith(requested, StringComparison.Ordinal))
                return false;
            if (fullName.Length == requested.Length)
                return true;
            var next = fullName[requested.Length];
            return next == '(' || next == '.' || next == '<' || next == '+';
        }

        // The requested names no result hits, in request order.
        internal static JArray MissingTestNames(JArray requested, JArray results)
        {
            var ran = results.Select(r => (string)r["fullName"]).ToArray();
            return new JArray(requested.Select(n => (string)n).Where(name => !ran.Any(fullName => MatchesTestName(name, fullName))));
        }

        // --test-names as given, in order and without repeats; null when the parameter is absent.
        internal static string[] ParseTestNames(JObject parameters)
        {
            var token = parameters["test-names"];
            if (token == null || token.Type == JTokenType.Null)
                return null;
            var names = CommandRegistry.ReadStrings(parameters, "test-names", SplitTestNames)
                .Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToArray();
            if (names.Length == 0)
                throw CommandFailure.InvalidUsage("test-names names no test.");
            return names;
        }

        // Splits on ; and line breaks, and on commas outside parentheses, angle brackets and quotes (with \ escapes inside
        // quotes), so Ns.Calc.Add(1,2), Ns.Calc.Quote("a,b") and Ns.Fixture<Int32,String>.Test stay whole. A ' opens a
        // quote only inside parentheses (a char argument), so an apostrophe in a custom test name is plain text.
        internal static IEnumerable<string> SplitTestNames(string text)
        {
            var names = new List<string>();
            var current = new StringBuilder();
            var parentheses = 0;
            var angles = 0;
            var quote = '\0';
            void Flush()
            {
                var name = current.ToString().Trim();
                if (name.Length > 0)
                    names.Add(name);
                current.Clear();
            }
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    current.Append(c);
                    if (c == '\\' && i + 1 < text.Length)
                        current.Append(text[++i]);
                    else if (c == quote)
                        quote = '\0';
                    continue;
                }
                if (c == ';' || c == '\n' || c == '\r' || (c == ',' && parentheses == 0 && angles == 0))
                {
                    Flush();
                    continue;
                }
                if (c == '"' || (c == '\'' && parentheses > 0))
                    quote = c;
                else if (c == '(')
                    parentheses++;
                else if (c == ')' && parentheses > 0)
                    parentheses--;
                else if (c == '<' && parentheses == 0)
                    angles++;
                else if (c == '>' && parentheses == 0 && angles > 0)
                    angles--;
                current.Append(c);
            }
            Flush();
            return names;
        }

        // 1.1.33 matches testNames and assemblyNames exactly (assemblies ignoring case) and groupNames and categoryNames
        // as regular expressions, so a partial, case-insensitive name match is an escaped group pattern. testNames also
        // selects everything under a fixture or parameterized method of that full name.
        internal static Filter BuildFilter(TestMode mode, string filter, string filterType, string[] testNames = null)
        {
            var built = new Filter { testMode = mode };
            if (testNames != null)
            {
                built.testNames = testNames;
                return built;
            }
            if (string.IsNullOrEmpty(filter))
                return built;
            if (filterType == "Assembly")
                built.assemblyNames = new[] { filter };
            else if (filterType == "Category")
                built.categoryNames = new[] { CategoryPattern(filter) };
            else
                built.groupNames = new[] { NamePattern(filter) };
            return built;
        }

        // The patterns BuildFilter hands the framework, shared with Selector so the progress total counts the same tests.
        static string NamePattern(string filter) => "(?i)" + Regex.Escape(filter);
        static string CategoryPattern(string filter) => "(?i)^" + Regex.Escape(filter) + "$";

        static object List(JObject parameters, CommandContext context)
        {
            var cancel = context.Cancel;
            var tests = new JArray();
            foreach (var mode in ParseModes((string)parameters["mode"], "all"))
            {
                var listed = new TaskCompletionSource<JArray>(TaskCreationOptions.RunContinuationsAsynchronously);
                MainThreadQueue.RunAndWait(() =>
                {
                    EnsureApi().RetrieveTestList(mode, root =>
                    {
                        try
                        {
                            listed.TrySetResult(root != null ? Flatten(root, mode, new JArray()) : new JArray());
                        }
                        catch (Exception e)
                        {
                            listed.TrySetException(e);
                        }
                    });
                    return null;
                }, cancel, "INTERNAL_ERROR: could not list the tests");
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

        static object Run(JObject parameters, CommandContext context)
        {
            var cancel = context.Cancel;
            var modes = ParseModes((string)parameters["mode"], "editor");
            var filterType = ParseFilterType((string)parameters["filter-type"]);
            var filter = (string)parameters["filter"];
            var testNames = ParseTestNames(parameters);
            if (testNames != null && !string.IsNullOrEmpty(filter))
                throw CommandFailure.InvalidUsage("Give filter or test-names, not both.");
            var async = CommandRegistry.ReadBool(parameters, "async-tests", false);
            if (!async && modes.Contains(TestMode.PlayMode))
                throw CommandFailure.InvalidUsage("playmode and all enter Play Mode, which reloads the domain and would cut this " +
                    "request off: pass --async-tests true and poll test status.");

            // A detached synchronous run reports its tests as the job's progress.
            var progress = async ? null : context.Progress;
            var (ended, initialAsync) = ((Task<JObject>, JObject))MainThreadQueue.RunAndWait(() => Start(modes, filter, filterType, testNames, async, progress),
                cancel, "INTERNAL_ERROR: could not start the test run");
            if (async)
                return initialAsync;
            // On the request timeout this stops waiting; the run goes on and test status still reports it.
            // On reload or manual stop, SaveForReload/Detach completes s_Ended right before shutdown.Cancel() fires:
            // read the completed run directly rather than letting OperationCanceledException turn into a retryable 503.
            try
            {
                ended.Wait(cancel);
            }
            catch (OperationCanceledException) when (ended.Status == TaskStatus.RanToCompletion)
            {
            }
            catch (OperationCanceledException) when (MainThreadQueue.DomainReloadInProgress)
            {
                throw new CommandFailure("Command Execution Failed",
                    "TEST_RUN_INTERRUPTED: A domain reload cut off the request waiting for this run; use --async-tests true for tests that reload.");
            }
            var status = ended.Result;
            var failure = FailureFor(status);
            if (failure != null)
                throw failure;
            return status;
        }

        // Main thread. Returns the task that completes when the run ends and (for async runs) the starting status snapshot.
        static (Task<JObject> ended, JObject initialAsync) Start(TestMode[] modes, string filter, string filterType, string[] testNames, bool async, Action<string, int?> progress)
        {
            if (IsRunning || FrameworkHasLiveRun() == true)
                throw new CommandFailure("Command Execution Failed", "TEST_RUN_IN_PROGRESS: a test run is still going; " +
#if UPIPE_TEST_CANCEL
                    (IsRunning ? "stop it with test cancel or " : "") +
#endif
                    "poll test status until it ends.");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new CommandFailure("Command Execution Failed", "PLAY_MODE_ACTIVE: tests start from Edit Mode; stop Play Mode first.");
            // A run started right after a cancel or a run error starts from the reopened scenes.
            ReopenStartingScenes();
            var unsaved = UnsavedScenes();
            if (unsaved != null)
                throw new CommandFailure("Command Execution Failed", unsaved);

            var ended = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            JObject initialAsync = null;
            var consoleSeq = ConsoleCommands.Position.cursor;
            lock (s_Lock)
            {
                s_Run = new JObject
                {
                    ["status"] = Running,
                    ["mode"] = modes.Length > 1 ? "all" : modes[0] == TestMode.EditMode ? "editor" : "playmode",
                    ["async"] = async,
                    ["filter"] = filter,
                    ["filterType"] = filterType,
                    ["testNames"] = testNames != null ? new JArray(testNames) : null,
                    ["phase"] = modes[0].ToString(),
                    ["next"] = modes.Length > 1 ? modes[1].ToString() : null,
                    ["pending"] = false,
                    ["finishing"] = false,
                    ["startedAt"] = DateTime.UtcNow.ToString("o"),
                    ["consoleSeq"] = consoleSeq,
                    ["results"] = new JArray(),
                    ["scenes"] = SceneSetupToJson(),
                };
                s_Ended = ended;
                s_Progress = progress;
                s_ProgressTotal = 0;
                s_ProgressDone = 0;
                if (async)
                {
                    // Without counts: the unity CLI prints run_tests summaries as "0/0 passed (all)", which reads as a result.
                    initialAsync = Status("summary");
                    initialAsync.Remove("summary");
                }
            }
            Execute(modes[0]);
            return (ended.Task, initialAsync);
        }

        // Main thread. The runner asks whether to save modified scenes in a modal dialog, which would hold the Editor
        // until someone answers; null when nothing would ask.
        static string UnsavedScenes()
        {
            var dirty = BuiltInCommands.DirtySceneNames();
            if (dirty.Count == 0 || Application.isBatchMode)
                return null;
            return "UNSAVED_SCENES: save or revert " + string.Join(", ", dirty) + " first; the test runner would stop at a save dialog.";
        }

        // Main thread.
        static void Execute(TestMode mode)
        {
            string filter, filterType;
            string[] testNames;
            lock (s_Lock)
                (filter, filterType, testNames) = RunSelection();
            try
            {
                s_LastActivityAt = EditorApplication.timeSinceStartup;
                s_ReconcileRequestedAt = -1;
                var guid = EnsureApi().Execute(new ExecutionSettings(BuildFilter(mode, filter, filterType, testNames)));
                lock (s_Lock)
                    s_Run["guid"] = guid;
            }
            catch (Exception e)
            {
                lock (s_Lock)
                    End(Errored, "The Test Framework refused the run: " + e.Message);
                throw new CommandFailure("Command Execution Failed", "TEST_RUN_ERROR: " + e.Message);
            }
        }

        // Main thread: the second half of `all`, once the EditMode run has wound down (pumped from EditorApplication.update).
        static void StartPending()
        {
            lock (s_Lock)
            {
                if ((string)s_Run?["status"] != Running || !(bool)s_Run["pending"])
                    return;
            }
            var now = EditorApplication.timeSinceStartup;
            var live = FrameworkHasLiveRun();
            if (live == true)
            {
                s_LastActivityAt = now;
                return;
            }
            if (live == null && s_LastActivityAt >= 0 && now - s_LastActivityAt < UnknownFrameworkPendingWaitSeconds)
                return;
            // The EditMode half may have left a scene modified; the PlayMode half would then stop at the save dialog.
            var unsaved = UnsavedScenes();
            TestMode phase;
            lock (s_Lock)
            {
                if ((string)s_Run?["status"] != Running || !(bool)s_Run["pending"])
                    return;
                if ((bool?)s_Run["cancelRequested"] == true)
                {
                    End(Canceled, CanceledError);
                    return;
                }
                if (unsaved != null)
                {
                    End(Errored, unsaved);
                    return;
                }
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

        // Main thread. The Test Framework keeps its job marked running for a few Editor updates after the last RunFinished
        // (measured in an interactive 2022.3.62 Editor; a batchmode one clears it at once). The run reads as completed
        // only once the framework lets go, so a run started right after it is not refused as TEST_RUN_IN_PROGRESS.
        static void FinishOnceFrameworkIdle()
        {
            if (FrameworkHasLiveRun() == true && EditorApplication.timeSinceStartup - s_LastActivityAt < FrameworkWindDownSeconds)
                return;
            lock (s_Lock)
                EndUnlessCanceled(Completed, null);
        }

        // Main thread: pure state rule deciding when a running test without live framework activity should be
        // marked interrupted. If Test Framework internals cannot be reflected (null), fall back to an idle grace window.
        internal static bool ShouldInterruptStaleRun(bool? frameworkHasLiveRun, bool inOrEnteringPlayMode, bool busy, double idleSeconds, double sinceReconcileRequest)
        {
            if (inOrEnteringPlayMode || busy || frameworkHasLiveRun == true)
                return false;
            if (frameworkHasLiveRun == false)
            {
                var settledSeconds = sinceReconcileRequest >= 0 ? Math.Min(idleSeconds, sinceReconcileRequest) : idleSeconds;
                return settledSeconds >= StaleRunInterruptSeconds;
            }
            return idleSeconds >= UnknownFrameworkIdleInterruptSeconds;
        }

        // Main thread: a run that is neither in the framework's job table nor in Play Mode (or went silent past
        // the fallback window when internals changed) stopped without reporting RunFinished.
        static void Reconcile()
        {
            lock (s_Lock)
            {
                if ((string)s_Run?["status"] != Running)
                    return;
            }
            var now = EditorApplication.timeSinceStartup;
            var live = FrameworkHasLiveRun();
            var inPlay = EditorApplication.isPlayingOrWillChangePlaymode;
            var busy = EditorApplication.isCompiling || EditorApplication.isUpdating;
            if (live == true || inPlay || busy)
            {
                s_LastActivityAt = now;
                return;
            }
            if (s_LastActivityAt < 0)
                s_LastActivityAt = now;
            var idleSeconds = now - s_LastActivityAt;
            var sinceReconcile = s_ReconcileRequestedAt >= 0 ? now - s_ReconcileRequestedAt : -1;
            if (!ShouldInterruptStaleRun(live, inPlay, busy, idleSeconds, sinceReconcile))
                return;
            lock (s_Lock)
                EndUnlessCanceled(Interrupted, "The run stopped without reporting its end (a domain reload, Play Mode being stopped or an Editor error cut it short).");
        }

        // Main thread, before the domain unloads (called from UpipeServer.OnBeforeAssemblyReload before Detach).
        public static void SaveForReload()
        {
            string text;
            lock (s_Lock)
            {
                if (s_Run == null)
                    return;
                if ((string)s_Run["status"] == Running && (bool?)s_Run["finishing"] == true)
                    EndUnlessCanceled(Completed, null); // every result is in; only the framework's wind-down was left
                else if ((string)s_Run["status"] == Running && !(bool)s_Run["async"])
                    EndUnlessCanceled(Interrupted, "A domain reload cut off the request waiting for this run; use --async-tests true for tests that reload.");
                text = s_Run.ToString(Formatting.None);
            }
            SessionState.SetString(SavedRunKey, text);
        }

        // On a manual stop: a reload while the server is off saves nothing, so the copy saved before an earlier reload must
        // not come back when the server is turned on again.
        public static void ForgetSaved() => SessionState.EraseString(SavedRunKey);

        // An error before the last RunFinished has not run every test, so its unrun names must not read as typos.
        internal static bool ShouldComputeMissingTestNames(string status, bool lastPhaseFinished) =>
            status == Completed || (status == Errored && lastPhaseFinished);

        // Caller holds s_Lock. reopenScenes: the framework ended the run without its scene restore (a run error it reports).
        static void End(string status, string error, bool reopenScenes = false)
        {
            if ((string)s_Run?["status"] != Running)
                return;
            // A cancel also sets finishing at RunFinished (an `all` run then skips its PlayMode half), so it does not count.
            var lastPhaseFinished = (bool?)s_Run["finishing"] == true && (bool?)s_Run["cancelRequested"] != true;
            s_Run["status"] = status;
            s_Run["finishedAt"] = DateTime.UtcNow.ToString("o");
            s_Run["next"] = null;
            s_Run.Remove("finishing");
            if (error != null)
                s_Run["error"] = error;
            if (status == Errored && s_Run["consoleSeq"] != null)
                s_Run["errorLog"] = ErrorLog((long)s_Run["consoleSeq"], error);
            // Checked once the whole run (both halves of `all`) is over: a PlayMode-only name is not missing after EditMode.
            if (ShouldComputeMissingTestNames(status, lastPhaseFinished) && s_Run["testNames"] is JArray requested)
                s_Run["missingTestNames"] = MissingTestNames(requested, (JArray)s_Run["results"]);
            s_Progress = null;
            // shortcut: a static, so lost when a domain reload (a canceled run ended by SaveForReload, or any reload before
            // the next Edit Mode update) or a server stop comes between here and OnUpdate; keep it in SessionState if
            // canceled or errored runs cut off that way turn up.
            if (status == Canceled || reopenScenes)
                s_ScenesToReopen = s_Run["scenes"] as JArray;
            // The waiting request answers with this run, even if another run has replaced it by the time it wakes.
            s_Ended?.TrySetResult(Status("full"));
        }

        // Main thread. The open scenes, as the framework's StoreSceneSetupTask takes them (an untitled scene is left out).
        static JArray SceneSetupToJson() => new JArray(EditorSceneManager.GetSceneManagerSetup().Select(scene => new JObject
        {
            ["path"] = scene.path, ["isLoaded"] = scene.isLoaded, ["isActive"] = scene.isActive, ["isSubScene"] = scene.isSubScene,
        }));

        // Main thread.
        static void ReopenStartingScenes()
        {
            var scenes = s_ScenesToReopen;
            // Scenes are reopened from Edit Mode only; OnUpdate tries again once Play Mode is over.
            if (scenes == null || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            s_ScenesToReopen = null;
            ReopenScenes(scenes);
        }

        // Main thread. A canceled run skips the framework's RestoreSceneSetupTask, which reopens the scenes the run started
        // from, or opens a new scene when it started from an untitled one; so does a run the framework ends with an error
        // (1.4.3 runs the task neither on a cancel nor in its error mode). So it is done here, unless the scenes are back
        // already (a Play Mode stop runs the framework's restore). Measured on 1.4.3: an EditMode cancel or run error leaves
        // an Untitled scene in place of the user's, and a PlayMode run canceled on its way into Play Mode leaves the deleted
        // bootstrap scene open and dirty, which makes the framework cancel every later run at its first task.
        static void ReopenScenes(JArray scenes)
        {
            var setup = scenes.Select(scene => new SceneSetup
            {
                path = (string)scene["path"], isLoaded = (bool)scene["isLoaded"], isActive = (bool)scene["isActive"], isSubScene = (bool)scene["isSubScene"],
            }).ToArray();
            // Back already: the framework restored them (a Play Mode stop, or an error raised after its restore task).
            // shortcut: only path and active scene are compared, so a changed isLoaded is not repaired.
            var open = EditorSceneManager.GetSceneManagerSetup();
            if (open.Select(scene => (scene.path, scene.isActive)).SequenceEqual(setup.Select(scene => (scene.path, scene.isActive))))
                return;
            // shortcut: every scene must be a file under the project, so a scene in an immutable package, or a setup one of
            // whose scenes was deleted during the run, is not reopened. Resolve package paths and reopen the rest if users hit it.
            var reopen = setup.Length > 0 && setup.All(scene => File.Exists(scene.path));
            // Started untitled: only a scene with a path (the bootstrap scene) needs replacing.
            if (!reopen && EditorSceneManager.GetActiveScene().path.Length == 0)
                return;
            try
            {
                if (reopen)
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Upipe] Could not restore the scenes the test run started from: " + e.Message);
            }
        }

        // Caller holds s_Lock. A run test cancel asked to stop ends canceled however it stops; the rest end as given.
        static void EndUnlessCanceled(string status, string error)
        {
            if ((bool?)s_Run?["cancelRequested"] == true)
                End(Canceled, CanceledError);
            else
                End(status, error);
        }

        // Caller holds s_Lock. What the current run selects, as BuildFilter takes it.
        static (string filter, string filterType, string[] testNames) RunSelection() =>
            ((string)s_Run["filter"], (string)s_Run["filterType"], (s_Run["testNames"] as JArray)?.Select(n => (string)n).ToArray());

        // Reports the run as its job's progress, when a detached `test run` waits on it. Outside s_Lock: the reporter
        // takes the job table's lock.
        static void ReportProgress(string phase, int done, int total)
        {
            Action<string, int?> report;
            lock (s_Lock)
                report = s_Progress;
            report?.Invoke($"{done}/{total} tests finished ({phase})", total > 0 ? done * 100 / total : (int?)null);
        }

        // Which test cases the run's selection picks, for the progress total: RunStarted hands over every test of the mode
        // whatever the filter (measured on 1.1.33), so BuildFilter's selection is applied again, with its patterns. A
        // filter matches a case through the case or any suite above it, as the framework's filters do.
        // shortcut: [Explicit] cases count only when test-names name them, as NUnit runs them; a filter that selects one
        // explicitly reads a little low. Only the percent depends on it.
        static Func<ITestAdaptor, bool> Selector(string filter, string filterType, string[] testNames)
        {
            if (testNames != null)
                return test => testNames.Any(name => MatchesTestName(name, test.FullName));
            if (string.IsNullOrEmpty(filter))
                return test => test.RunState != RunState.Explicit;
            Func<ITestAdaptor, bool> matches;
            if (filterType == "Assembly")
                matches = node => node.IsTestAssembly && string.Equals(Path.GetFileNameWithoutExtension(node.Name), filter, StringComparison.OrdinalIgnoreCase);
            else if (filterType == "Category")
            {
                var category = new Regex(CategoryPattern(filter));
                matches = node => (node.Categories ?? Array.Empty<string>()).Any(category.IsMatch);
            }
            else
            {
                var name = new Regex(NamePattern(filter));
                matches = node => name.IsMatch(node.FullName ?? "");
            }
            return test =>
            {
                if (test.RunState == RunState.Explicit)
                    return false;
                for (var node = test; node != null; node = node.Parent)
                    if (matches(node))
                        return true;
                return false;
            };
        }

        static int CountSelected(ITestAdaptor test, Func<ITestAdaptor, bool> selected) =>
            test.IsSuite ? test.Children.Sum(child => CountSelected(child, selected)) : selected(test) ? 1 : 0;

        static FieldInfo s_TestRunsField;
        static FieldInfo s_IsRunningField;
        static Type s_JobType;
        static object s_Holder;

        // Whether the Test Framework still has a test job going; null when its internals are not where 1.1.33 and 1.4.3
        // have them.
        // shortcut: reads TestJobDataHolder by reflection; if a test-framework upgrade moves it, a run cut short by a
        // reload stays "running" and runs started elsewhere are not refused. Re-check the names when upgrading.
        static bool? FrameworkHasLiveRun() => FrameworkJobs()?.Cast<object>().Any(run => (bool)s_IsRunningField.GetValue(run));

        // The Test Framework's TestJobData list; null when its internals are not where 1.1.33 and 1.4.3 have them.
        static IEnumerable FrameworkJobs()
        {
            if (s_Holder == null)
            {
                var holder = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder");
                s_TestRunsField = holder?.GetField("TestRuns");
                s_JobType = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobData");
                s_IsRunningField = s_JobType?.GetField("isRunning");
                s_Holder = holder?.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
            }
            return s_Holder == null || s_IsRunningField == null ? null : s_TestRunsField?.GetValue(s_Holder) as IEnumerable;
        }

#if UPIPE_TEST_CANCEL
        const string RunnerNotReleased = "[Upipe] Could not release the canceled run's EditMode runner (test-framework internals " +
            "moved); the next domain reload may log \"Unexpected assembly reload happened while running tests\".";

        // Main thread. The EditMode runner of the framework job with this guid; null for no such job, a PlayMode job or a runner
        // not made yet. Warns and gives null when the job's fields are not where 1.4.3 has them.
        static UnityEngine.Object EditModeRunnerOf(string guid)
        {
            var jobs = FrameworkJobs();
            var guidField = s_JobType?.GetField("guid");
            var runnerField = s_JobType?.GetField("editModeRunner");
            if (jobs == null || guidField == null || runnerField == null)
            {
                Debug.LogWarning(RunnerNotReleased);
                return null;
            }
            var job = jobs.Cast<object>().FirstOrDefault(run => (string)guidField.GetValue(run) == guid);
            return job == null ? null : runnerField.GetValue(job) as UnityEngine.Object;
        }

        // Main thread, right after CancelTestRun returns true (the framework then never steps the runner again). 1.4.3's
        // cancel skips the EditMode runner's own end (the cancel branch of EditModeRunTask and the Dispose in
        // RunFinishedInvocationEvent), so the runner still counts as running: its beforeAssemblyReload handler logs
        // "Unexpected assembly reload happened while running tests" at the next domain reload and marks whatever test is
        // current Cancelled, and a run that had resumed after a reload leaves the static list of executed tests set, which a
        // later run in the same domain skips. Reset those two as OnRunCancel and Dispose do. Not the rest of Dispose: not
        // UnlockReloadAssemblies, because the canceled job's UnlockReloadAssembliesTask still runs and releases the lock, and
        // the runner is not destroyed, which leaves one inert object per cancel until the Editor quits.
        // shortcut: private 1.4.3 names by reflection; if a test-framework upgrade moves them nothing is reset, a warning says
        // so and the smoke check fails, re-check the names when upgrading.
        internal static bool ReleaseEditModeRunner(UnityEngine.Object runner)
        {
            var running = runner == null ? null : runner.GetType().GetField("RunningTests", BindingFlags.NonPublic | BindingFlags.Instance);
            var executed = Type.GetType("UnityEngine.TestRunner.NUnitExtensions.Runner.UnityWorkItemDataHolder, UnityEngine.TestRunner")
                ?.GetField("alreadyExecutedTests", BindingFlags.Public | BindingFlags.Static);
            if (running == null || running.FieldType != typeof(bool) || executed == null)
                return false;
            running.SetValue(runner, false);
            executed.SetValue(null, null);
            return true;
        }
#endif

        // Main thread (the framework calls back from Editor updates and from Play Mode).
        sealed class Recorder : ICallbacks, IErrorCallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                s_LastActivityAt = EditorApplication.timeSinceStartup;
                string[] testNames;
                string phase, filter, filterType;
                lock (s_Lock)
                {
                    if (s_Progress == null || (string)s_Run?["status"] != Running)
                        return;
                    (filter, filterType, testNames) = RunSelection();
                    phase = (string)s_Run["phase"];
                }
                var total = CountSelected(testsToRun, Selector(filter, filterType, testNames));
                lock (s_Lock)
                {
                    s_ProgressTotal = total;
                    s_ProgressDone = 0;
                }
                ReportProgress(phase, 0, total);
            }

            public void TestStarted(ITestAdaptor test)
            {
                s_LastActivityAt = EditorApplication.timeSinceStartup;
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                s_LastActivityAt = EditorApplication.timeSinceStartup;
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
                int done = 0, total = 0;
                string phase = null;
                lock (s_Lock)
                {
                    if ((string)s_Run?["status"] == Running && !(bool)s_Run["pending"])
                    {
                        ((JArray)s_Run["results"]).Add(record);
                        if (s_Progress != null && !result.Test.IsSuite)
                        {
                            done = ++s_ProgressDone;
                            total = Math.Max(s_ProgressTotal, done);
                            phase = (string)s_Run["phase"];
                        }
                    }
                }
                if (phase != null)
                    ReportProgress(phase, done, total);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                s_LastActivityAt = EditorApplication.timeSinceStartup;
                lock (s_Lock)
                {
                    if ((string)s_Run?["status"] != Running || (bool)s_Run["pending"])
                        return;
                    var next = (string)s_Run["next"];
                    // A canceled `all` does not go on to its PlayMode half.
                    if (next == null || (bool?)s_Run["cancelRequested"] == true)
                    {
                        // Completed from OnUpdate once the framework lets go of the job (FinishOnceFrameworkIdle).
                        s_Run["finishing"] = true;
                        return;
                    }
                    s_Run["phase"] = next;
                    s_Run["next"] = null;
                    s_Run["pending"] = true;
                }
            }

            public void OnError(string message)
            {
                // The framework's error mode skips its scene restore, as a cancel does. Upipe's own error endings
                // (Execute refused, UNSAVED_SCENES before the PlayMode half) leave the scenes as they are.
                lock (s_Lock)
                    End(Errored, message, reopenScenes: true);
            }
        }
    }
}
#endif
