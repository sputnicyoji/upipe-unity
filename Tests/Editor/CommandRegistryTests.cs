// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Upipe.Editor.Tests
{
    public class CommandRegistryTests
    {
        static CommandEntry Sample()
        {
            return new CommandEntry
            {
                Group = "sample",
                Name = "run",
                Parameters = new[]
                {
                    new CommandParameter { Name = "label" },
                    new CommandParameter { Name = "path", Required = true },
                    new CommandParameter { Name = "force", IsBool = true },
                },
                Handler = _ => null,
            };
        }

        static string Kinds(BindResult result)
        {
            return string.Join(",", result.Problems.Select(p => (string)p["kind"]));
        }

        [Test]
        public void PositionalsFillRequiredBeforeOptional()
        {
            var result = CommandRegistry.BindArgv(Sample(), new[] { "a.cs", "first" });
            Assert.IsTrue(result.Ok, Kinds(result));
            Assert.AreEqual("a.cs", (string)result.Values["path"]);
            Assert.AreEqual("first", (string)result.Values["label"]);
        }

        [Test]
        public void FlagFormsBind()
        {
            var result = CommandRegistry.BindArgv(Sample(), new[] { "--path=a.cs", "--label", "x", "--force" });
            Assert.IsTrue(result.Ok, Kinds(result));
            Assert.AreEqual("a.cs", (string)result.Values["path"]);
            Assert.AreEqual("x", (string)result.Values["label"]);
            Assert.AreEqual(true, (bool)result.Values["force"]);
        }

        [Test]
        public void FollowerStartingWithDoubleDashIsNotAValue()
        {
            var result = CommandRegistry.BindArgv(Sample(), new[] { "--label", "--path", "a.cs" });
            Assert.AreEqual("emptyValue", Kinds(result));
        }

        [Test]
        public void SingleDashFollowerIsAValue()
        {
            var result = CommandRegistry.BindArgv(Sample(), new[] { "--path", "-foo" });
            Assert.IsTrue(result.Ok, Kinds(result));
            Assert.AreEqual("-foo", (string)result.Values["path"]);
        }

        [Test]
        public void UnknownFlagConsumesItsValue()
        {
            var result = CommandRegistry.BindArgv(Sample(), new[] { "--bogus", "v", "a.cs" });
            Assert.AreEqual("unknownName", Kinds(result));
        }

        [Test]
        public void ExcessPositionalIsReported()
        {
            // Three slots (path, label, force); the fourth positional has nowhere to go.
            var result = CommandRegistry.BindArgv(Sample(), new[] { "a.cs", "x", "true", "extra" });
            Assert.AreEqual("excessPositional", Kinds(result));
            Assert.AreEqual("extra", (string)result.Problems[0]["token"]);
        }

        [Test]
        public void DoubleDashEndsFlags()
        {
            var result = CommandRegistry.BindArgv(Sample(), new[] { "--", "--weird" });
            Assert.IsTrue(result.Ok, Kinds(result));
            Assert.AreEqual("--weird", (string)result.Values["path"]);
        }

        [Test]
        public void DuplicateAndMissingRequiredAreReported()
        {
            Assert.AreEqual("duplicate", Kinds(CommandRegistry.BindArgv(Sample(), new[] { "--path", "a", "--path", "b" })));
            Assert.AreEqual("missingRequired", Kinds(CommandRegistry.BindArgv(Sample(), new string[0])));
        }

        [Test]
        public void EmptyNameIsReportedBeforeEmptyValue()
        {
            Assert.AreEqual("emptyName", Kinds(CommandRegistry.BindArgv(Sample(), new[] { "a.cs", "--=" })));
            Assert.AreEqual("emptyValue", Kinds(CommandRegistry.BindArgv(Sample(), new[] { "a.cs", "--label=" })));
        }

        [Test]
        public void NotFoundEchoesWhatWasTyped()
        {
            Assert.AreEqual("nosuch", CommandRegistry.ResolveTokens(new[] { "nosuch", "--x" }).Typed);
            Assert.AreEqual("editor nope", CommandRegistry.ResolveTokens(new[] { "editor", "nope" }).Typed);
        }

        [Test]
        public void BoolRejectsNonBoolValue()
        {
            Assert.AreEqual("typeMismatch", Kinds(CommandRegistry.BindArgv(Sample(), new[] { "a.cs", "--force", "maybe" })));
        }

        [Test]
        public void StructuredDropsTokenAndRejectsUnknownKeys()
        {
            var ok = CommandRegistry.BindStructured(Sample(), new JObject { ["path"] = "a.cs", ["token"] = "t" });
            Assert.IsTrue(ok.Ok, Kinds(ok));
            Assert.IsFalse(ok.Values.ContainsKey("token"));
            var bad = CommandRegistry.BindStructured(Sample(), new JObject { ["path"] = "a.cs", ["Path"] = "b" });
            Assert.AreEqual("unknownName", Kinds(bad));
        }

        [Test]
        public void BuiltInEditorStatusResolvesInEveryForm()
        {
            var expected = CommandRegistry.Find("editor", "status");
            Assert.IsNotNull(expected);
            Assert.AreSame(expected, CommandRegistry.ResolveTokens(new[] { "editor", "status" }).Command);
            Assert.AreSame(expected, CommandRegistry.ResolveStructured("editor status", out _));
            Assert.AreSame(expected, CommandRegistry.ResolveStructured("editor.status", out _));
            Assert.AreEqual("editor", CommandRegistry.ResolveTokens(new[] { "editor" }).ListedGroup);
            Assert.IsNull(CommandRegistry.ResolveTokens(new[] { "editor", "nope" }).Command);
        }

        [Test]
        public void SharedBareNameIsAmbiguous()
        {
            // "editor status" and "console status" both answer to "status".
            Assert.IsNull(CommandRegistry.ResolveStructured("status", out var details));
            StringAssert.Contains("ambiguous", details);
            Assert.AreSame(CommandRegistry.Find("script", "recompile_status"), CommandRegistry.ResolveStructured("recompile_status", out _));
        }

        [Test]
        public void UnderscoreFormResolvesAfterDocumentedForms()
        {
            Assert.AreSame(CommandRegistry.Find("console", "status"), CommandRegistry.ResolveStructured("console_status", out _));
            Assert.IsNull(CommandRegistry.ResolveStructured("console_nope", out _));
            // Since #3, argv takes the same single-token forms as structured requests.
            Assert.AreSame(CommandRegistry.Find("console", "status"), CommandRegistry.ResolveTokens(new[] { "console_status" }).Command);
            Assert.AreSame(CommandRegistry.Find("console", "get"), CommandRegistry.ResolveStructured("console", out _));
            Assert.AreEqual("console", CommandRegistry.ResolveTokens(new[] { "console" }).ListedGroup);
        }

        [Test]
        public void StructuredUnderscoreAndHyphenSpellingsAreOneParameter()
        {
            var command = CommandRegistry.Find("console", "get");
            var one = CommandRegistry.BindStructured(command, new JObject { ["since_session"] = "a" });
            Assert.IsTrue(one.Ok, Kinds(one));
            Assert.AreEqual("a", (string)one.Values["since-session"]);
            var both = CommandRegistry.BindStructured(command, new JObject { ["since-session"] = "a", ["since_session"] = "b" });
            Assert.AreEqual("duplicate", Kinds(both));
        }

        [Test]
        public void CliAliasesResolveInArgvAndStructured()
        {
            var cases = new[]
            {
                ("editor_status", "editor", "status"),
                ("console_status", "console", "status"),
                ("eval", "script", "eval"),
                ("recompile", "script", "recompile"),
                ("recompile_status", "script", "recompile_status"),
                ("script_eval", "script", "eval"),
                ("editor_play", "editor.playmode", "start"),
                ("editor_stop", "editor.playmode", "stop"),
                ("editor_pause", "editor.playmode", "pause"),
                ("set_autotick", "editor", "set_auto_tick"),
                ("editor_set_run_in_background", "editor.playmode", "set_run_in_background"),
                ("clear_console", "console", "clear"),
                ("console_write", "console", "write"),
            };
            foreach (var (alias, group, name) in cases)
            {
                var expected = CommandRegistry.Find(group, name);
                Assert.IsNotNull(expected, alias);
                Assert.AreSame(expected, CommandRegistry.ResolveTokens(new[] { alias }).Command, alias);
                Assert.AreSame(expected, CommandRegistry.ResolveStructured(alias, out _), alias);
            }
            // Unique bare short names still work in CLI/structured invocations without exposing bare verbs in catalog.
            Assert.AreSame(CommandRegistry.Find("console", "write"), CommandRegistry.ResolveTokens(new[] { "write" }).Command);
            Assert.AreSame(CommandRegistry.Find("editor.playmode", "pause"), CommandRegistry.ResolveStructured("pause", out _));
            Assert.AreEqual("console_write", (string)CommandRegistry.Find("console", "write").ToCatalogEntry()["name"]);
            Assert.AreEqual("editor_pause", (string)CommandRegistry.Find("editor.playmode", "pause").ToCatalogEntry()["name"]);
        }

        [Test]
        public void ConsoleAliasTakesFlagsWhileTheBareGroupStillLists()
        {
            var get = CommandRegistry.Find("console", "get");
            var withFlags = CommandRegistry.ResolveTokens(new[] { "console", "--tail", "2" });
            Assert.AreSame(get, withFlags.Command);
            CollectionAssert.AreEqual(new[] { "--tail", "2" }, withFlags.RemainingTokens);
            Assert.AreSame(get, CommandRegistry.ResolveTokens(new[] { "console", "get" }).Command);
            Assert.AreEqual("console", CommandRegistry.ResolveTokens(new[] { "console" }).ListedGroup);
            Assert.IsNull(CommandRegistry.ResolveTokens(new[] { "console", "nope" }).Command);
        }

        [Test]
        public void CatalogNamesAreUniqueBecauseUnityMcpUsesThemAsToolNames()
        {
            CollectionAssert.AllItemsAreUnique(CommandRegistry.Commands.Select(c => (string)c.ToCatalogEntry()["name"]));
            Assert.AreEqual("editor_status", (string)CommandRegistry.Find("editor", "status").ToCatalogEntry()["name"]);
            Assert.AreEqual("editor status", (string)CommandRegistry.Find("editor", "status").ToCatalogEntry()["command"]);
        }

        [Test]
        public void ArgvUnderscoreSpellingBindsTheHyphenatedParameter()
        {
            var eval = CommandRegistry.Find("script", "eval");
            var result = CommandRegistry.BindArgv(eval, new[] { "--code", "1", "--timeout_ms", "1500" });
            Assert.IsTrue(result.Ok, Kinds(result));
            Assert.AreEqual("1500", (string)result.Values["timeout-ms"]);
            Assert.AreEqual("duplicate", Kinds(CommandRegistry.BindArgv(eval, new[] { "--timeout-ms", "1", "--timeout_ms", "2" })));
        }

        [Test]
        public void IntegerParameterRejectsNonInteger()
        {
            var command = new CommandEntry
            {
                Group = "sample",
                Name = "tail",
                Parameters = new[] { new CommandParameter { Name = "tail", IsInteger = true } },
                Handler = _ => null,
            };
            var ok = CommandRegistry.BindArgv(command, new[] { "--tail", "3" });
            Assert.IsTrue(ok.Ok, Kinds(ok));
            Assert.AreEqual("3", (string)ok.Values["tail"]);
            Assert.AreEqual("typeMismatch", Kinds(CommandRegistry.BindArgv(command, new[] { "--tail", "x" })));
        }

        // Structured requests follow argv's rules: a dry run sent as "1" must not bind as false and run the code.
        [Test]
        public void StructuredValuesMustParseAsTheirTypeAndNullIsNotGiven()
        {
            var command = new CommandEntry
            {
                Group = "sample",
                Name = "typed",
                Parameters = new[]
                {
                    new CommandParameter { Name = "path", Required = true },
                    new CommandParameter { Name = "force", IsBool = true },
                    new CommandParameter { Name = "tail", IsInteger = true },
                },
                Handler = _ => null,
            };
            var ok = CommandRegistry.BindStructured(command, new JObject { ["path"] = "a", ["force"] = "true", ["tail"] = 3 });
            Assert.IsTrue(ok.Ok, Kinds(ok));
            Assert.IsTrue(CommandRegistry.BindStructured(command, new JObject { ["path"] = "a", ["force"] = false, ["tail"] = "4" }).Ok);
            Assert.AreEqual("typeMismatch", Kinds(CommandRegistry.BindStructured(command, new JObject { ["path"] = "a", ["force"] = "1" })));
            Assert.AreEqual("typeMismatch", Kinds(CommandRegistry.BindStructured(command, new JObject { ["path"] = "a", ["force"] = 1 })));
            Assert.AreEqual("typeMismatch", Kinds(CommandRegistry.BindStructured(command, new JObject { ["path"] = "a", ["tail"] = "x" })));
            Assert.AreEqual("typeMismatch", Kinds(CommandRegistry.BindStructured(command, new JObject { ["path"] = "a", ["tail"] = 1.5 })));
            Assert.AreEqual("missingRequired", Kinds(CommandRegistry.BindStructured(command, new JObject { ["path"] = null })));
            var unset = CommandRegistry.BindStructured(command, new JObject { ["path"] = "a", ["force"] = null });
            Assert.IsTrue(unset.Ok, Kinds(unset));
            Assert.IsFalse(unset.Values.ContainsKey("force"));
        }

        static CommandEntry WithList() => new CommandEntry
        {
            Group = "sample",
            Name = "list",
            Parameters = new[]
            {
                new CommandParameter { Name = "file", Required = true },
                new CommandParameter { Name = "extra-files", Repeatable = true },
            },
            Handler = _ => null,
        };

        [Test]
        public void RepeatableFlagCollectsEveryValueInOrder()
        {
            var repeated = CommandRegistry.BindArgv(WithList(), new[] { "a.cs", "--extra-files", "b.cs", "--extra_files=c.cs", "--extra-files", "d;e" });
            Assert.IsTrue(repeated.Ok, Kinds(repeated));
            CollectionAssert.AreEqual(new[] { "b.cs", "c.cs", "d;e" }, repeated.Values["extra-files"].Select(t => (string)t));
            CollectionAssert.AreEqual(new[] { "b.cs", "c.cs", "d;e" }, CommandRegistry.ReadStrings(repeated.Values, "extra-files"));

            var once = CommandRegistry.BindArgv(WithList(), new[] { "a.cs", "--extra-files", "[\"x.cs\",\"y.cs\"]" });
            CollectionAssert.AreEqual(new[] { "x.cs", "y.cs" }, CommandRegistry.ReadStrings(once.Values, "extra-files"), "a JSON array from argv is expanded");
            // A positional past the other slots lands in the repeatable one, like any optional parameter.
            CollectionAssert.AreEqual(new[] { "z.cs" }, CommandRegistry.ReadStrings(CommandRegistry.BindArgv(WithList(), new[] { "a.cs", "z.cs" }).Values, "extra-files"));
            Assert.AreEqual("duplicate", Kinds(CommandRegistry.BindArgv(WithList(), new[] { "--file", "a.cs", "--file", "b.cs" })), "only Repeatable parameters repeat");
            Assert.IsEmpty(CommandRegistry.ReadStrings(CommandRegistry.BindArgv(WithList(), new[] { "a.cs" }).Values, "extra-files"));
        }

        [Test]
        public void RepeatableStructuredValueIsAStringOrAnArrayOfStrings()
        {
            var array = CommandRegistry.BindStructured(WithList(), JObject.Parse("{\"file\":\"a.cs\",\"extra_files\":[\"b.cs\",\"c.cs\"]}"));
            Assert.IsTrue(array.Ok, Kinds(array));
            CollectionAssert.AreEqual(new[] { "b.cs", "c.cs" }, CommandRegistry.ReadStrings(array.Values, "extra-files"));
            var text = CommandRegistry.BindStructured(WithList(), JObject.Parse("{\"file\":\"a.cs\",\"extra-files\":\"b.cs;c.cs\"}"));
            CollectionAssert.AreEqual(new[] { "b.cs;c.cs" }, CommandRegistry.ReadStrings(text.Values, "extra-files"));
            Assert.AreEqual("typeMismatch", Kinds(CommandRegistry.BindStructured(WithList(), JObject.Parse("{\"file\":\"a.cs\",\"extra-files\":[1]}"))));
            System.Func<string, System.Collections.Generic.IEnumerable<string>> split = v => v.Split(';');
            CollectionAssert.AreEqual(new[] { "b.cs", "c.cs", "d;e.cs" },
                CommandRegistry.ReadStrings(new JObject { ["x"] = new JArray("b.cs;c.cs", "[\"d;e.cs\"]") }, "x", split), "JSON array elements are not split");
            Assert.AreEqual("typeMismatch", Kinds(CommandRegistry.BindStructured(WithList(), JObject.Parse("{\"file\":\"a.cs\",\"extra-files\":{\"a\":1}}"))));
        }
    }
}
