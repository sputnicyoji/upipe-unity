using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace Upipe.Editor
{
    // `script recompile` and `script recompile_status`. State lives in SessionState because a successful
    // compile reloads the domain between the request and the poll that reads the outcome.
    //
    // status: idle -> triggered (requested) -> compiling -> completed | up_to_date.
    // A compile that ends without errors stays "compiling" until the domain reload that usually follows
    // has finished: only then is scriptCompilationFailed accurate (it is still set right after removing
    // the last broken script), and a client that sees the outcome can send its next command at once.
    [InitializeOnLoad]
    internal static class RecompileCommands
    {
        const string StateKey = "Upipe.Recompile.State";
        const string LastErrorsKey = "Upipe.Recompile.LastErrors";
        const string RequestedAtKey = "Upipe.Recompile.RequestedAt";
        const string FinishedAtKey = "Upipe.Recompile.FinishedAt";
        const string CompiledAssembliesKey = "Upipe.Recompile.CompiledAssemblies";
        // shortcut: a request whose compile has not started this long after it was sent is treated as
        // "nothing to compile"; raise it if a large project takes longer to begin compiling.
        const double StartGraceSeconds = 5.0;
        // A compile that ended without errors and has not reloaded the domain within this window is decided anyway.
        const double ReloadGraceSeconds = 5.0;

        static readonly Regex s_LocatedError = new Regex(@"^.+\(\d+,\d+\): ", RegexOptions.Compiled);

        // What recompile_status returns, readable from HTTP threads without touching Unity APIs.
        static volatile string s_Snapshot;
        // Main thread only.
        static string s_Status;
        static readonly List<string> s_Errors = new List<string>();
        static int s_CompiledAssemblies;
        // When the Editor last became idle (neither compiling nor importing); -1 while busy.
        static double s_IdleSince = -1;

        static RecompileCommands()
        {
            var saved = SessionState.GetString(StateKey, "");
            var state = saved.Length > 0 ? JObject.Parse(saved) : NewState("idle");
            // Still "compiling" after a reload: the compile finished without errors and the reload has completed.
            // "triggered" is left to the watchdog, since other reloads (play mode, package import) look the same.
            if ((string)state["status"] == "compiling")
            {
                state = Outcome(afterReload: true);
                SessionState.SetFloat(FinishedAtKey, 0);
            }
            Save(state);

            CompilationPipeline.compilationStarted += _ => OnCompilationStarted();
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            CompilationPipeline.compilationFinished += _ => OnCompilationFinished();
            EditorApplication.update += Watchdog;
        }

        public static void Register()
        {
            CommandRegistry.Register(new CommandEntry
            {
                Group = "script",
                Name = "recompile",
                Alias = "recompile",
                Description = "Refresh assets and recompile scripts. Poll script recompile_status until it is completed or up_to_date.",
                Tags = new[] { "scripts" },
                Parameters = new[]
                {
                    // shortcut: accepted because the unity CLI always sends it; compiling does not need focus,
                    // so it is ignored. Implement it if a workflow depends on the Editor coming to the front.
                    new CommandParameter { Name = "focus", IsBool = true, Description = "Accepted for compatibility; ignored." },
                },
                MainThreadRequired = true,
                Handler = _ => Recompile(),
            });
            CommandRegistry.Register(new CommandEntry
            {
                Group = "script",
                Name = "recompile_status",
                Alias = "recompile_status",
                Description = "Status of the last recompile: idle | triggered | compiling | completed | up_to_date, with its errors.",
                Tags = new[] { "scripts" },
                MainThreadRequired = false,
                ReadOnly = true,
                Handler = _ => JObject.Parse(s_Snapshot),
            });
        }

        static object Recompile()
        {
            // Already compiling, or finished and waiting for the reload: join it rather than restart the state.
            if (EditorApplication.isCompiling || s_Status == "compiling")
            {
                // A compile is under way but its start event has not arrived yet: hide the previous outcome.
                if (s_Status != "compiling")
                {
                    ResetProgress();
                    Save(NewState("compiling"));
                }
                return new JObject { ["status"] = "compiling" };
            }
            SessionState.SetFloat(FinishedAtKey, 0);
            Save(NewState("triggered"));
            AssetDatabase.Refresh(); // pick up scripts edited or added on disk since the last import
            CompilationPipeline.RequestScriptCompilation();
            // Counted from here so a slow Refresh does not eat the grace period.
            SessionState.SetFloat(RequestedAtKey, (float)EditorApplication.timeSinceStartup);
            return new JObject { ["status"] = "compiling" };
        }

        internal static void OnCompilationStarted()
        {
            ResetProgress();
            SessionState.SetFloat(FinishedAtKey, 0);
            SessionState.SetInt(CompiledAssembliesKey, 0);
            Save(NewState("compiling"));
        }

        internal static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
        {
            s_CompiledAssemblies++;
            s_Errors.AddRange(messages.Where(m => m.type == CompilerMessageType.Error).Select(FormatError));
        }

        internal static void OnCompilationFinished()
        {
            if (s_Errors.Count > 0)
            {
                // A failed compile never reloads the domain: the outcome is final now.
                LastErrors = s_Errors.ToArray();
                Save(NewState("completed", s_Errors));
                return;
            }
            if (s_CompiledAssemblies > 0)
                LastErrors = new string[0];
            // Stay "compiling"; the next domain (or the watchdog, if no reload comes) decides via Outcome().
            SessionState.SetInt(CompiledAssembliesKey, s_CompiledAssemblies);
            SessionState.SetFloat(FinishedAtKey, (float)EditorApplication.timeSinceStartup);
        }

        static void Watchdog()
        {
            var now = EditorApplication.timeSinceStartup;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                s_IdleSince = -1;
                return;
            }
            if (s_IdleSince < 0)
                s_IdleSince = now;
            if (s_Status == "triggered" && now - SessionState.GetFloat(RequestedAtKey, 0) >= StartGraceSeconds)
            {
                SessionState.SetInt(CompiledAssembliesKey, 0);
                Save(EditorApplication.isPlaying ? PlayModeDeferred() : Outcome(afterReload: false));
                return;
            }
            // Finished without errors and no reload yet: decide only after the Editor has stayed idle this long.
            var finishedAt = SessionState.GetFloat(FinishedAtKey, 0);
            if (s_Status == "compiling" && finishedAt > 0 && now - Math.Max(finishedAt, s_IdleSince) >= ReloadGraceSeconds)
            {
                SessionState.SetFloat(FinishedAtKey, 0);
                Save(Outcome(afterReload: false));
            }
        }

        // The result of a compile that ended without errors. Scripts can still be broken from an earlier compile
        // when nothing was recompiled: report that compile's errors again ("completed" + errors), which the CLI
        // treats as a failure, as with the official package. scriptCompilationFailed is only trustworthy after
        // the reload; before it, a compile that built assemblies without errors counts as a success.
        internal static JObject Outcome(bool afterReload)
        {
            var compiled = SessionState.GetInt(CompiledAssembliesKey, 0);
            if (!afterReload && compiled > 0)
                return NewState("completed");
            if (EditorUtility.scriptCompilationFailed)
            {
                var errors = LastErrors;
                return NewState("completed",
                    errors.Length > 0 ? errors : new[] { "Script compilation failed; read the errors with: console get --level error" }, true);
            }
            return compiled == 0 ? NewState("up_to_date") : NewState("completed");
        }

        // Unity can postpone compiling until Play Mode ends (Preferences > General > Script Changes While Playing).
        // Reporting "up_to_date" would claim the edits compiled, so report a failure that says why.
        static JObject PlayModeDeferred()
        {
            return NewState("completed", new[]
            {
                "Play Mode: no compile started within " + StartGraceSeconds + " s of the request; Unity may defer it until Play Mode ends.",
            });
        }

        // The CLI parses "file(line,col): error CSxxxx: text" lines; keep every error on one such line.
        internal static string FormatError(CompilerMessage message)
        {
            var text = (message.message ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
            if (!s_LocatedError.IsMatch(text) && !string.IsNullOrEmpty(message.file))
                text = $"{message.file}({message.line},{message.column}): {text}";
            return text;
        }

        internal static JObject NewState(string status, IEnumerable<string> errors = null, bool forceFailed = false)
        {
            var list = new JArray((errors ?? Enumerable.Empty<string>()).ToArray());
            var failed = forceFailed || list.Count > 0;
            return new JObject
            {
                ["status"] = status,
                ["failed"] = failed,
                ["errors"] = list,
                ["compilationFailed"] = failed,
            };
        }

        internal static JObject Current => JObject.Parse(s_Snapshot);

        // Errors of the last compile that produced any, kept apart from the snapshot that each request overwrites.
        internal static string[] LastErrors
        {
            get => JArray.Parse(SessionState.GetString(LastErrorsKey, "[]")).Select(e => (string)e).ToArray();
            set => SessionState.SetString(LastErrorsKey, new JArray(value).ToString(Formatting.None));
        }

        internal static void ResetProgress()
        {
            s_Errors.Clear();
            s_CompiledAssemblies = 0;
        }

        internal static void Save(JObject state)
        {
            s_Status = (string)state["status"];
            var json = state.ToString(Formatting.None);
            SessionState.SetString(StateKey, json);
            s_Snapshot = json;
        }
    }
}
