// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

#if UPIPE_TEST_FRAMEWORK
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.TestTools.TestRunner.Api;

namespace Upipe.Editor.Tests
{
    // The parts of `test run` / `test status` that need no real run (running one from inside a test would nest runs).
    public class TestCommandsTests
    {
        static JObject Finished(string status, int passed, int failed, int skipped, params JObject[] results) => new JObject
        {
            ["status"] = status,
            ["mode"] = "editor",
            ["filterType"] = "TestName",
            ["error"] = status == "completed" ? null : "cut short",
            ["summary"] = new JObject
            {
                ["total"] = passed + failed + skipped, ["passed"] = passed, ["failed"] = failed, ["skipped"] = skipped, ["inconclusive"] = 0,
            },
            ["results"] = new JArray(results),
        };

        static JObject Result(string name, string status, string message = "") =>
            new JObject { ["fullName"] = name, ["status"] = status, ["message"] = message };

        [Test]
        public void FinishedRunPassesOnlyWhenSomethingRanAndNothingFailed()
        {
            Assert.IsNull(TestCommands.FailureFor(Finished("completed", 2, 0, 1)));
            Assert.IsNull(TestCommands.FailureFor(new JObject { ["status"] = "running" }));
            Assert.IsNull(TestCommands.FailureFor(new JObject { ["status"] = "no_tests" }));

            var failed = TestCommands.FailureFor(Finished("completed", 1, 1, 0,
                Result("A.Passes", "Passed"), Result("A.Breaks", "Failed", "  Expected: 1\n  But was:  2\n")));
            StringAssert.StartsWith("TESTS_FAILED: 1 of 2 tests failed (1 passed, 0 skipped).", failed.Message);
            StringAssert.Contains("\nA.Breaks: Expected: 1 | But was:  2", failed.Message);
            StringAssert.DoesNotContain("A.Passes", failed.Message);
            Assert.IsNotNull(failed.Result, "the payload rides along for raw HTTP clients");

            var none = TestCommands.FailureFor(Finished("completed", 0, 0, 2));
            Assert.AreEqual("Parameter Validation Failed", none.Error);
            StringAssert.Contains("No test ran", none.Message);

            StringAssert.StartsWith("TEST_RUN_INTERRUPTED: cut short", TestCommands.FailureFor(Finished("interrupted", 1, 0, 0)).Message);
            StringAssert.StartsWith("TEST_RUN_ERROR: cut short", TestCommands.FailureFor(Finished("error", 0, 0, 0)).Message);
            var canceled = TestCommands.FailureFor(Finished("canceled", 2, 0, 0));
            StringAssert.StartsWith("TEST_RUN_CANCELED: cut short", canceled.Message);
            Assert.IsNotNull(canceled.Result, "a canceled run never reads as a pass, and its results ride along");
        }

        // 1.4.3 ends a run with a fixed "Multiple unexpected errors" after logging the real causes; both go into the failure.
        [Test]
        public void ARunErrorTellsThePassesAndTheErrorsLoggedBeforeIt()
        {
            var status = Finished("error", 1, 0, 0, Result("A.Passes", "Passed"));
            Assert.AreEqual("TEST_RUN_ERROR: cut short\n1 of 1 tests passed, 0 failed.", TestCommands.FailureFor(status).Message);
            var log = new JArray("One or more exceptions when executing IPostBuildCleanup.", "UnhandledLogMessageException: boom");
            status["errorLog"] = log;
            Assert.AreEqual("TEST_RUN_ERROR: cut short\n1 of 1 tests passed, 0 failed.\nLogged during the run:" +
                "\nOne or more exceptions when executing IPostBuildCleanup.\nUnhandledLogMessageException: boom", TestCommands.FailureFor(status).Message);
            status["missingTestNames"] = new JArray("Missing.Fixture.TypoTest");
            Assert.AreEqual("TEST_RUN_ERROR: cut short\n1 of 1 tests passed, 0 failed." +
                "\nMissing test name(s) (TEST_NAMES_NOT_RUN): Missing.Fixture.TypoTest." +
                "\nLogged during the run:\nOne or more exceptions when executing IPostBuildCleanup.\nUnhandledLogMessageException: boom",
                TestCommands.FailureFor(status).Message);
            Assert.AreEqual("TEST_RUN_ERROR: cut short", TestCommands.FailureFor(Finished("error", 0, 0, 0)).Message, "nothing ran: no counts");
            foreach (var state in new[] { "interrupted", "canceled" })
            {
                var other = Finished(state, 1, 0, 0);
                other["errorLog"] = log;
                StringAssert.DoesNotContain("\n", TestCommands.FailureFor(other).Message, state + " is not a run error");
            }
        }

        [Test]
        public void MissingTestNamesAreComputedOnCompletedAndTerminalErrorRunsOnly()
        {
            Assert.IsTrue(TestCommands.ShouldComputeMissingTestNames("completed", lastPhaseFinished: false));
            Assert.IsTrue(TestCommands.ShouldComputeMissingTestNames("completed", lastPhaseFinished: true));
            // A post-build cleanup error (1.4.3): after the last RunFinished.
            Assert.IsTrue(TestCommands.ShouldComputeMissingTestNames("error", lastPhaseFinished: true));
            // An IPrebuildSetup error: no RunFinished.
            Assert.IsFalse(TestCommands.ShouldComputeMissingTestNames("error", lastPhaseFinished: false));
            Assert.IsFalse(TestCommands.ShouldComputeMissingTestNames("canceled", lastPhaseFinished: true));
            Assert.IsFalse(TestCommands.ShouldComputeMissingTestNames("interrupted", lastPhaseFinished: true));
        }

        [Test]
        public void CancelAsksTheFrameworkOnlyForAJobOfOursThatIsStillRunning()
        {
            JObject Run(string status, bool pending = false, bool finishing = false, bool cancelRequested = false) => new JObject
            {
                ["status"] = status, ["pending"] = pending, ["finishing"] = finishing, ["cancelRequested"] = cancelRequested,
            };
            Assert.AreEqual(TestCommands.CancelStep.None, TestCommands.CancelStepFor(null));
            Assert.AreEqual(TestCommands.CancelStep.None, TestCommands.CancelStepFor(Run("completed")));
            Assert.AreEqual(TestCommands.CancelStep.None, TestCommands.CancelStepFor(Run("canceled", cancelRequested: true)));
            Assert.AreEqual(TestCommands.CancelStep.Framework, TestCommands.CancelStepFor(Run("running")));
            Assert.AreEqual(TestCommands.CancelStep.Framework, TestCommands.CancelStepFor(new JObject { ["status"] = "running", ["pending"] = false }),
                "a run saved by an older Upipe has no cancelRequested field");
            Assert.AreEqual(TestCommands.CancelStep.Defer, TestCommands.CancelStepFor(Run("running", pending: true)), "between the halves of all");
            Assert.AreEqual(TestCommands.CancelStep.Wait, TestCommands.CancelStepFor(Run("running", finishing: true)), "every result is in");
            Assert.AreEqual(TestCommands.CancelStep.Wait, TestCommands.CancelStepFor(Run("running", cancelRequested: true)), "asked already");
            Assert.AreEqual(TestCommands.CancelStep.Wait, TestCommands.CancelStepFor(Run("running", pending: true, cancelRequested: true)));
        }

        [Test]
        public void ParametersAcceptTheDocumentedSpellings()
        {
            CollectionAssert.AreEqual(new[] { TestMode.EditMode }, TestCommands.ParseModes(null, "editor"));
            CollectionAssert.AreEqual(new[] { TestMode.EditMode }, TestCommands.ParseModes("EditMode", "all"));
            CollectionAssert.AreEqual(new[] { TestMode.EditMode, TestMode.PlayMode }, TestCommands.ParseModes(null, "all"));
            Assert.Throws<CommandFailure>(() => TestCommands.ParseModes("player", "editor"));
            Assert.AreEqual("Category", TestCommands.ParseFilterType("category"));
            Assert.AreEqual("TestName", TestCommands.ParseFilterType(null));
            Assert.Throws<CommandFailure>(() => TestCommands.ParseFilterType("regex"));
            Assert.AreEqual("summary", TestCommands.ParseDetail("Summary"));
            Assert.Throws<CommandFailure>(() => TestCommands.ParseDetail("brief"));
        }

        [Test]
        public void NameFilterIsAPartialCaseInsensitiveMatchOfTheLiteralText()
        {
            var filter = TestCommands.BuildFilter(TestMode.PlayMode, "jobs.Cancel(", "TestName");
            Assert.AreEqual(TestMode.PlayMode, filter.testMode);
            var pattern = filter.groupNames[0];
            Assert.IsTrue(Regex.IsMatch("Upipe.Editor.Tests.JobS.cancel(1)", pattern));
            Assert.IsFalse(Regex.IsMatch("Upipe.Editor.Tests.JobsXCancel(1)", pattern), "the dot and paren are literal");
            Assert.IsTrue(Regex.IsMatch("smoke", TestCommands.BuildFilter(TestMode.EditMode, "Smoke", "Category").categoryNames[0]));
            Assert.IsFalse(Regex.IsMatch("smoke-slow", TestCommands.BuildFilter(TestMode.EditMode, "Smoke", "Category").categoryNames[0]));
            Assert.AreEqual("Sample", TestCommands.BuildFilter(TestMode.EditMode, "Sample", "Assembly").assemblyNames[0]);
            Assert.IsNull(TestCommands.BuildFilter(TestMode.EditMode, null, "TestName").groupNames);
        }

        [Test]
        public void TestNamesSplitOutsideParenthesesAndQuotes()
        {
            CollectionAssert.AreEqual(new[] { "Ns.Calc.Add(1,2)", "Ns.Calc.Quote(\"a,b\")", "A", "B", "C", "D" },
                TestCommands.SplitTestNames("Ns.Calc.Add(1,2), Ns.Calc.Quote(\"a,b\");A\nB,C ;; D"));
            CollectionAssert.AreEqual(new[] { "Ns.Q(\"x\\\",y\")", "Ns.C(',')" }, TestCommands.SplitTestNames("Ns.Q(\"x\\\",y\"),Ns.C(',')"));
            CollectionAssert.IsEmpty(TestCommands.SplitTestNames(" ; , "));
            CollectionAssert.AreEqual(new[] { "Ns.Fx<Int32,String>.Test", "Ns.Fx.Doesn't crash", "Ns.Fx.Other" },
                TestCommands.SplitTestNames("Ns.Fx<Int32,String>.Test,Ns.Fx.Doesn't crash;Ns.Fx.Other"), "generic arguments and apostrophes");
            CollectionAssert.AreEqual(new[] { "Ns.Fix.Test(1 > 0)", "Ns.Fix.Test(1 < 2, 3 > 1)", "Ns.Box<Int32, String>.Test(1 > 0)", "Ns.Fix.Next" },
                TestCommands.SplitTestNames("Ns.Fix.Test(1 > 0), Ns.Fix.Test(1 < 2, 3 > 1), Ns.Box<Int32, String>.Test(1 > 0), Ns.Fix.Next"),
                "angle operators inside parens do not break angle or paren balancing");

            CollectionAssert.AreEqual(new[] { "A", "B(1,2)", "C" },
                TestCommands.ParseTestNames(new JObject { ["test-names"] = new JArray("A;B(1,2)", "A", "[\"C\"]") }), "in order, once each, JSON arrays expanded");
            CollectionAssert.AreEqual(new[] { "Ns.Fx.Odd;Name" }, TestCommands.ParseTestNames(new JObject { ["test-names"] = "[\"Ns.Fx.Odd;Name\"]" }),
                "a JSON array element is taken whole");
            Assert.IsNull(TestCommands.ParseTestNames(new JObject()));
            Assert.Throws<CommandFailure>(() => TestCommands.ParseTestNames(new JObject { ["test-names"] = " ; " }));
        }

        [Test]
        public void TestNamesAndFilterAreRefusedTogetherBeforeAnyRun()
        {
            var run = CommandRegistry.Find("test", "run").CancellableHandler;
            var failure = Assert.Throws<CommandFailure>(() => run(new JObject { ["filter"] = "x", ["test-names"] = "A.B" }, default));
            Assert.AreEqual("Parameter Validation Failed", failure.Error);
            StringAssert.Contains("not both", failure.Message);
        }

        [Test]
        public void TestNamesBecomeAnExactNameFilter()
        {
            var filter = TestCommands.BuildFilter(TestMode.EditMode, null, "TestName", new[] { "Ns.A.One", "Ns.B" });
            CollectionAssert.AreEqual(new[] { "Ns.A.One", "Ns.B" }, filter.testNames);
            Assert.IsNull(filter.groupNames);
        }

        [Test]
        public void EveryRequestedNameMustHitAResult()
        {
            var results = new JArray(Result("Ns.Calc.Add(1,2)", "Passed"), Result("Ns.Calc.Add(3,4)", "Passed"),
                Result("Ns.Calc.Quote(\"a,b\")", "Passed"), Result("Ns.Other.Lone", "Passed"));
            var requested = new JArray("Ns.Calc.Add", "Ns.Calc.Ad", "Ns.Calc.Quote(\"a,b\")", "Ns.Other", "Ns.Calc.Add(1,2)", "Ns.Missing.Test");
            CollectionAssert.AreEqual(new[] { "Ns.Calc.Ad", "Ns.Missing.Test" }, TestCommands.MissingTestNames(requested, results).Select(n => (string)n));

            var run = Finished("completed", 4, 0, 0, results.Select(r => (JObject)r).ToArray());
            run["testNames"] = requested;
            run["missingTestNames"] = TestCommands.MissingTestNames(requested, results);
            var failure = TestCommands.FailureFor(run);
            Assert.AreEqual("Parameter Validation Failed", failure.Error, "everything that ran passed, yet a misspelled name fails the run");
            StringAssert.Contains("TEST_NAMES_NOT_RUN: 2 of 6 requested test name(s) matched no test in mode editor: Ns.Calc.Ad, Ns.Missing.Test", failure.Message);
            Assert.AreSame(run, failure.Result);

            run["missingTestNames"] = new JArray();
            Assert.IsNull(TestCommands.FailureFor(run));
        }

        [Test]
        public void MatchesTestNameSupportsGenericAndNestedPrefixes()
        {
            Assert.IsTrue(TestCommands.MatchesTestName("Ns.Fix.Echo", "Ns.Fix.Echo<Int32>(1)"));
            Assert.IsTrue(TestCommands.MatchesTestName("Ns.Box", "Ns.Box<Int32>.Test"));
            Assert.IsTrue(TestCommands.MatchesTestName("Ns.Outer", "Ns.Outer+Inner.Test"));
            Assert.IsFalse(TestCommands.MatchesTestName("Ns.Box", "Ns.Boxed.Test"));
        }

        [Test]
        public void StaleRunInterruptionHandlesBothLiveReflectionAndUnknownFallback()
        {
            Assert.IsFalse(TestCommands.ShouldInterruptStaleRun(frameworkHasLiveRun: true, inOrEnteringPlayMode: false, busy: false, idleSeconds: 10.0, sinceReconcileRequest: 10.0));
            Assert.IsFalse(TestCommands.ShouldInterruptStaleRun(frameworkHasLiveRun: false, inOrEnteringPlayMode: true, busy: false, idleSeconds: 10.0, sinceReconcileRequest: 10.0));
            Assert.IsFalse(TestCommands.ShouldInterruptStaleRun(frameworkHasLiveRun: false, inOrEnteringPlayMode: false, busy: true, idleSeconds: 10.0, sinceReconcileRequest: 10.0));

            Assert.IsFalse(TestCommands.ShouldInterruptStaleRun(frameworkHasLiveRun: false, inOrEnteringPlayMode: false, busy: false, idleSeconds: 1.0, sinceReconcileRequest: 0.2));
            Assert.IsTrue(TestCommands.ShouldInterruptStaleRun(frameworkHasLiveRun: false, inOrEnteringPlayMode: false, busy: false, idleSeconds: 0.6, sinceReconcileRequest: 0.6));
            Assert.IsTrue(TestCommands.ShouldInterruptStaleRun(frameworkHasLiveRun: false, inOrEnteringPlayMode: false, busy: false, idleSeconds: 0.6, sinceReconcileRequest: -1));

            Assert.IsFalse(TestCommands.ShouldInterruptStaleRun(frameworkHasLiveRun: null, inOrEnteringPlayMode: false, busy: false, idleSeconds: 4.9, sinceReconcileRequest: 4.9));
            Assert.IsTrue(TestCommands.ShouldInterruptStaleRun(frameworkHasLiveRun: null, inOrEnteringPlayMode: false, busy: false, idleSeconds: 5.0, sinceReconcileRequest: 0.1));
        }
#if UPIPE_TEST_CANCEL

        // CancelTestRun only looks the guid up, so an unknown one cancels nothing, even inside the run executing this test.
        [Test]
        public void CancelingAJobTheFrameworkDoesNotKnowFails()
        {
            foreach (var guid in new[] { null, System.Guid.NewGuid().ToString("N") })
                StringAssert.StartsWith("TEST_CANCEL_FAILED:", Assert.Throws<CommandFailure>(() => TestCommands.CancelFrameworkJob(guid)).Message);
        }

        // A runner made here is never run, so it subscribes to nothing; the static list is put back for the run executing this test.
        [Test]
        public void ReleasingACanceledEditModeRunnerStopsItCountingAsRunning()
        {
            var runnerType = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.EditModeRunner");
            var running = runnerType?.GetField("RunningTests", BindingFlags.NonPublic | BindingFlags.Instance);
            var executed = System.Type.GetType("UnityEngine.TestRunner.NUnitExtensions.Runner.UnityWorkItemDataHolder, UnityEngine.TestRunner")
                ?.GetField("alreadyExecutedTests", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(running, "EditModeRunner.RunningTests moved: re-check TestCommands.ReleaseEditModeRunner");
            Assert.IsNotNull(executed, "UnityWorkItemDataHolder.alreadyExecutedTests moved: re-check TestCommands.ReleaseEditModeRunner");
            var runner = UnityEngine.ScriptableObject.CreateInstance(runnerType);
            var saved = executed.GetValue(null);
            try
            {
                running.SetValue(runner, true);
                executed.SetValue(null, new List<string> { "Not.A.Test" });
                Assert.IsTrue(TestCommands.ReleaseEditModeRunner(runner));
                Assert.IsFalse((bool)running.GetValue(runner));
                Assert.IsNull(executed.GetValue(null));
                Assert.IsFalse(TestCommands.ReleaseEditModeRunner(null));
            }
            finally
            {
                executed.SetValue(null, saved);
                UnityEngine.Object.DestroyImmediate(runner);
            }
        }
#endif
    }
}
#endif
