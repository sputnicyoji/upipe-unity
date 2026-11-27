// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Upipe.Editor
{
    // Local on/off switch. It lives in UserSettings/, which projects do not commit, so turning the server off
    // never shows up in version control. UPIPE_ENABLE (1/true or 0/false) overrides the file for one Editor
    // process and is never written back.
    internal static class UpipeSettings
    {
        const string MenuPath = "Tools/Upipe/Server Enabled";
        const string EnvironmentVariable = "UPIPE_ENABLE";

        static string FilePath => Path.Combine(UpipeServer.ProjectPath, "UserSettings", "UpipeSettings.json");

        static string EnvironmentValue => Environment.GetEnvironmentVariable(EnvironmentVariable);

        public static bool IsEnabled() => !UpipeServer.IsSecondaryProcess && Decide(ReadFile(), EnvironmentValue);

        // Pure, so tests need neither the process environment nor a UserSettings folder.
        internal static bool Decide(string fileText, string environmentValue)
        {
            return ParseSwitch(environmentValue) ?? ReadEnabled(fileText);
        }

        internal static bool? ParseSwitch(string value)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                    return true;
                case "0":
                case "false":
                    return false;
                default:
                    return null;
            }
        }

        // A missing file, a missing key or a value that is not a switch keeps the default: enabled.
        internal static bool ReadEnabled(string fileText)
        {
            if (string.IsNullOrWhiteSpace(fileText))
                return true;
            try
            {
                return ParseSwitch(JObject.Parse(fileText)["enabled"]?.ToString()) ?? true;
            }
            catch (JsonException e)
            {
                Debug.LogWarning($"[Upipe] Ignoring unreadable {FilePath}: {e.Message}");
                return true;
            }
        }

        static string ReadFile()
        {
            try
            {
                return File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
            }
            // A file the Editor may not read keeps the default too (UnauthorizedAccessException is not an IOException).
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Upipe] Could not read {FilePath}: {e.Message}");
                return null;
            }
        }

        [MenuItem(MenuPath)]
        static void Toggle()
        {
            if (UpipeServer.IsSecondaryProcess)
                return;
            if (ParseSwitch(EnvironmentValue).HasValue)
            {
                Debug.LogWarning($"[Upipe] {EnvironmentVariable} decides whether the server runs; unset it to use this menu.");
                return;
            }
            var enabled = !IsEnabled();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, new JObject { ["enabled"] = enabled }.ToString(Formatting.Indented) + "\n");
            UpipeServer.ApplySettings();
        }

        // Shows the effective state; greyed out while UPIPE_ENABLE decides it.
        [MenuItem(MenuPath, true)]
        static bool CanToggle()
        {
            if (UpipeServer.IsSecondaryProcess)
            {
                Menu.SetChecked(MenuPath, false);
                return false;
            }
            Menu.SetChecked(MenuPath, IsEnabled());
            return !ParseSwitch(EnvironmentValue).HasValue;
        }
    }
}
