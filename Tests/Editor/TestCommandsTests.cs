#if UPIPE_TEST_FRAMEWORK
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
    }
}
#endif
