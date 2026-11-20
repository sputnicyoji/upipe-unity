using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Upipe.Editor
{
    internal static class BuiltInCommands
    {
        public static void Register()
        {
            CommandRegistry.Register(new CommandEntry
            {
                Group = "editor",
                Name = "status",
                Description = "Report whether the Editor is ready, compiling or in play mode, and whether the player loop is ticking.",
                Tags = new[] { "editor" },
                // Answered from the cached state so it stays servable while the main thread is busy.
                MainThreadRequired = false,
                ReadOnly = true,
                Alias = "editor_status",
                Handler = _ => EditorStatus(),
            });
            RecompileCommands.Register();
            ConsoleCommands.Register();
            PlayModeCommands.Register();
            ScriptEvalCommand.Register();
            ScriptRunCommand.Register();
#if UPIPE_TEST_FRAMEWORK
            TestCommands.Register();
#endif
            CaptureCommands.Register();
        }

        // Also served as GET /api/editor_status.
        public static JObject EditorStatus()
        {
            var status = MainThreadQueue.StateFields();
            status["projectPath"] = UpipeServer.ProjectPath;
            status["unityVersion"] = UpipeServer.UnityVersion;
            status["mode"] = UpipeServer.Mode;
            status["server"] = "Upipe " + UpipeServer.Version;
            return status;
        }
    }
}
