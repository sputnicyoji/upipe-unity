using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Upipe.Editor
{
    // `editor.playmode start|stop|pause|set_run_in_background` and `editor set_auto_tick`.
    // Run In Background is a Player Setting that ships in builds, so an override first records the project's value in
    // SessionState and puts it back when the Editor returns to Edit Mode or quits: ProjectSettings.asset never keeps it.
    [InitializeOnLoad]
    internal static class PlayModeCommands
    {
        // The project's runInBackground before the first override in this Editor session: 0 or 1, -1 when not overridden.
        const string OriginalRunInBackgroundKey = "Upipe.RunInBackground.Original";
        const int DefaultAutoTickIntervalMs = 16;
        const int MaxAutoTickIntervalMs = 60000;

        static PlayModeCommands()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredEditMode)
                    RestoreRunInBackground();
            };
            EditorApplication.quitting += RestoreRunInBackground;
        }

        public static void Register()
        {
            CommandRegistry.Register(new CommandEntry
            {
                Group = "editor",
                Name = "set_auto_tick",
                Alias = "set_autotick",
                Description = "While the Editor is unfocused, tick the main thread every interval-ms: drain queued commands and request a player-loop update.",
                Tags = new[] { "editor", "playmode" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "enable", IsBool = true, Description = "Turn auto tick on (default true) or off." },
                    new CommandParameter { Name = "interval-ms", IsInteger = true, Description = "Tick interval in milliseconds (default 16)." },
                    new CommandParameter { Name = "persist", IsBool = true, Description = "Keep the setting across domain reloads in this Editor session (default true)." },
                },
                Handler = SetAutoTick,
            });
            CommandRegistry.Register(new CommandEntry
            {
                Group = "editor.playmode",
                Name = "start",
                Alias = "editor_play",
                Description = "Enter Play Mode. Poll unity status --until-ready afterwards: entering reloads the domain.",
                Tags = new[] { "playmode" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "run-in-background", IsBool = true, Description = "Turn Run In Background on until Play Mode ends (default true), so an unfocused Editor keeps playing." },
                },
                Handler = Start,
            });
            CommandRegistry.Register(new CommandEntry
            {
                Group = "editor.playmode",
                Name = "stop",
                Alias = "editor_stop",
                Description = "Exit Play Mode; an overridden Run In Background goes back to the project's value.",
                Tags = new[] { "playmode" },
                Handler = _ => Stop(),
            });
            CommandRegistry.Register(new CommandEntry
            {
                Group = "editor.playmode",
                Name = "pause",
                Description = "Pause Play Mode, or resume it with paused=false.",
                Tags = new[] { "playmode" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "paused", IsBool = true, Description = "true pauses (default), false resumes." },
                },
                Handler = Pause,
            });
            CommandRegistry.Register(new CommandEntry
            {
                Group = "editor.playmode",
                Name = "set_run_in_background",
                Alias = "editor_set_run_in_background",
                Description = "Turn Run In Background on for this Editor session without changing the saved project settings; enable=false puts the project's value back.",
                Tags = new[] { "playmode" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "enable", IsBool = true, Description = "true overrides to on (default); false restores the project's value." },
                },
                Handler = SetRunInBackground,
            });
        }

        internal static object SetAutoTick(JObject parameters)
        {
            var enable = CommandRegistry.ReadBool(parameters, "enable", true);
            var interval = CommandRegistry.ReadInteger(parameters, "interval-ms", DefaultAutoTickIntervalMs);
            var persist = CommandRegistry.ReadBool(parameters, "persist", true);
            if (interval < 1 || interval > MaxAutoTickIntervalMs)
                throw CommandFailure.InvalidUsage($"interval-ms must be between 1 and {MaxAutoTickIntervalMs}.");
            AutoTick.Set(enable ? (int)interval : 0, persist);
            return new JObject { ["enabled"] = enable, ["intervalMs"] = (int)interval, ["persist"] = persist };
        }

        static object Start(JObject parameters)
        {
            if (CommandRegistry.ReadBool(parameters, "run-in-background", true))
                OverrideRunInBackground(true);
            EditorApplication.isPlaying = true;
            return null;
        }

        static object Stop()
        {
            EditorApplication.isPlaying = false;
            return null;
        }

        static object Pause(JObject parameters)
        {
            EditorApplication.isPaused = CommandRegistry.ReadBool(parameters, "paused", true);
            return null;
        }

        internal static object SetRunInBackground(JObject parameters)
        {
            if (CommandRegistry.ReadBool(parameters, "enable", true))
                OverrideRunInBackground(true);
            else
                RestoreRunInBackground();
            return new JObject { ["runInBackground"] = Application.runInBackground, ["overridden"] = IsRunInBackgroundOverridden };
        }

        internal static bool IsRunInBackgroundOverridden => SessionState.GetInt(OriginalRunInBackgroundKey, -1) >= 0;

        // Main thread only. Only the first override records the original, so overriding twice still restores the project's value.
        // shortcut: a player build made while the override is on ships with it; put the project's value back around
        // builds (IPreprocessBuildWithReport) if builds run from an Editor that a CLI session is driving.
        internal static void OverrideRunInBackground(bool value)
        {
            if (!IsRunInBackgroundOverridden)
                SessionState.SetInt(OriginalRunInBackgroundKey, Application.runInBackground ? 1 : 0);
            if (Application.runInBackground != value)
                Application.runInBackground = value;
        }

        internal static void RestoreRunInBackground()
        {
            var original = SessionState.GetInt(OriginalRunInBackgroundKey, -1);
            if (original < 0)
                return;
            SessionState.EraseInt(OriginalRunInBackgroundKey);
            if (Application.runInBackground != (original == 1))
                Application.runInBackground = original == 1;
        }
    }
}
