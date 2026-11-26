// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Upipe.Editor.Tests
{
    public class UpipeSettingsTests
    {
        [Test]
        public void MissingFileOrKeyMeansEnabled()
        {
            Assert.IsTrue(UpipeSettings.Decide(null, null));
            Assert.IsTrue(UpipeSettings.Decide("{}", null));
        }

        [Test]
        public void FileSwitchesTheServer()
        {
            Assert.IsFalse(UpipeSettings.Decide("{\"enabled\": false}", null));
            Assert.IsTrue(UpipeSettings.Decide("{\"enabled\": true}", null));
        }

        [Test]
        public void EnvironmentOverridesTheFile()
        {
            Assert.IsTrue(UpipeSettings.Decide("{\"enabled\": false}", "1"));
            Assert.IsTrue(UpipeSettings.Decide("{\"enabled\": false}", "TRUE"));
            Assert.IsFalse(UpipeSettings.Decide(null, "0"));
            Assert.IsFalse(UpipeSettings.Decide("{\"enabled\": true}", " false "));
        }

        [Test]
        public void UnrecognizedEnvironmentValueFallsBackToTheFile()
        {
            Assert.IsFalse(UpipeSettings.Decide("{\"enabled\": false}", "yes"));
            Assert.IsFalse(UpipeSettings.Decide("{\"enabled\": false}", ""));
        }

        [Test]
        public void UnreadableFileKeepsTheDefault()
        {
            Assert.IsTrue(UpipeSettings.Decide("{\"enabled\": \"maybe\"}", null));
            LogAssert.Expect(LogType.Warning, new Regex("Ignoring unreadable"));
            Assert.IsTrue(UpipeSettings.Decide("{not json", null));
        }
    }
}
