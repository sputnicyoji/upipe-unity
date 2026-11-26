// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Upipe.Editor
{
    // `editor quit`: answers first, then quits from a later EditorApplication.update (which keeps running in an
    // unfocused Editor, unlike delayCall) once the reply has gone out. An interactive Editor refuses while Play Mode is on
    // or a scene has unsaved changes, unless force or confirm is given; a batchmode Editor always quits.
    internal static class EditorQuitCommand
    {
        // How long the exit waits for in-flight replies (this one included) before quitting anyway.
        const double ReplyGraceSeconds = 0.25;
        // The exit code of a quit that was answered but has not happened yet, so a domain reload in between cannot drop it.
        const string PendingExitCodeKey = "Upipe.PendingQuitExitCode";

        static int s_ExitCode;
        static double s_ScheduledAt;

        public static void Register()
        {
            CommandRegistry.Register(new CommandEntry
            {
                Group = "editor",
                Name = "quit",
                Alias = "editor_quit",
                Description = "Quit the Editor after this reply is sent. Refused while Play Mode is on or open scenes have unsaved " +
                    "changes unless force (or confirm) is set; a batchmode Editor quits regardless.",
                Tags = new[] { "editor" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "force", IsBool = true, Description = "Quit even in Play Mode or with unsaved scenes, losing their changes (default false)." },
                    new CommandParameter { Name = "confirm", IsBool = true, Description = "Same as force." },
                    new CommandParameter { Name = "exit-code", IsInteger = true, Description = "The Editor process's exit code (default 0)." },
                },
                Handler = Quit,
            });
        }

        // Main thread.
        static object Quit(JObject parameters)
        {
            var force = CommandRegistry.ReadBool(parameters, "force", false) | CommandRegistry.ReadBool(parameters, "confirm", false);
            var exitCode = CommandRegistry.ReadInteger(parameters, "exit-code", 0, int.MinValue, int.MaxValue);
            var refusal = Refusal(Application.isBatchMode, force, EditorApplication.isPlayingOrWillChangePlaymode, BuiltInCommands.DirtySceneNames());
            if (refusal != null)
                throw refusal;
            Schedule((int)exitCode);
            return new JObject { ["quitting"] = true, ["exitCode"] = (int)exitCode };
        }

        // Why an Editor must not quit yet, or null.
        internal static CommandFailure Refusal(bool batchMode, bool force, bool inPlayMode, IReadOnlyList<string> dirtyScenes)
        {
            if (batchMode || force)
                return null;
            if (inPlayMode)
                return new CommandFailure("Command Execution Failed",
                    "PLAY_MODE_ACTIVE: stop Play Mode first, or pass --force to quit anyway.");
            if (dirtyScenes.Count > 0)
                return new CommandFailure("Command Execution Failed", "UNSAVED_SCENES: save or revert " +
                    string.Join(", ", dirtyScenes) + " first, or pass --force to quit and lose the changes.");
            return null;
        }

        // Main thread, when the server starts: a quit answered right before a domain reload happens in the new domain.
        public static void Attach()
        {
            if (int.TryParse(SessionState.GetString(PendingExitCodeKey, ""), out var exitCode))
                Schedule(exitCode);
        }

        static void Schedule(int exitCode)
        {
            SessionState.SetString(PendingExitCodeKey, exitCode.ToString());
            s_ExitCode = exitCode;
            s_ScheduledAt = EditorApplication.timeSinceStartup;
            EditorApplication.update -= ExitOnceAnswered; // a second quit replaces the first
            EditorApplication.update += ExitOnceAnswered;
        }

        // Main thread. The quit request counts as in flight until its reply is written and closed.
        static void ExitOnceAnswered()
        {
            if (UpipeServer.RequestsInFlight > 0 && EditorApplication.timeSinceStartup - s_ScheduledAt < ReplyGraceSeconds)
                return;
            EditorApplication.update -= ExitOnceAnswered;
            SessionState.EraseString(PendingExitCodeKey);
            // Exit raises quitting (measured on 2022.3.62), whose handler stops the server, removes the descriptor and puts
            // Run In Background back. Not stopping here first keeps the server up if something keeps the Editor open.
            EditorApplication.Exit(s_ExitCode);
        }
    }
}
