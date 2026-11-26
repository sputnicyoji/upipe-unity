// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

using System;
using System.Collections.Generic;
using System.IO;
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
    internal static class RecompileCommands
    {
        const string StateKey = "Upipe.Recompile.State";
        const string LastErrorsKey = "Upipe.Recompile.LastErrors";
        const string RequestedAtKey = "Upipe.Recompile.RequestedAt";
        const string FinishedAtKey = "Upipe.Recompile.FinishedAt";
        const string CompiledAssembliesKey = "Upipe.Recompile.CompiledAssemblies";
        const string FailedBeforeStartKey = "Upipe.Recompile.FailedBeforeStart";
        // shortcut: a request whose compile has not started this long after it was sent is treated as
        // "nothing to compile"; raise it if a large project takes longer to begin compiling.
        const double StartGraceSeconds = 5.0;
        // A compile that ended without errors and has not reloaded the domain within this window is decided anyway.
        const double ReloadGraceSeconds = 5.0;

        static readonly Regex s_LocatedError = new Regex(@"^.+\(\d+,\d+\): ", RegexOptions.Compiled);

        // What recompile_status returns, readable from HTTP threads without touching Unity APIs.
        static volatile string s_Snapshot = NewState("idle").ToString(Formatting.None);
        // Main thread only.
        static string s_Status = "idle";
        static readonly List<string> s_Errors = new List<string>();
        static int s_CompiledAssemblies;
        // When the Editor last became idle (neither compiling nor importing); -1 while busy.
        static double s_IdleSince = -1;
        static bool s_Attached;
        static bool s_Restored;
        static bool s_DirtySinceLastCompile;
        static bool s_BaselineCleared;
        static bool s_PendingReloadOutcome;

        internal static bool IsAttached => s_Attached;
        internal static bool DirtySinceLastCompile => s_DirtySinceLastCompile;

        public static void Attach()
        {
            if (s_Attached)
                return;
            s_Attached = true;
            s_DirtySinceLastCompile = false;
            s_BaselineCleared = false;
            if (!s_Restored)
            {
                s_Restored = true;
                var saved = SessionState.GetString(StateKey, "");
                var state = saved.Length > 0 ? JObject.Parse(saved) : NewState("idle");
                // Still "compiling" after a reload: hold "compiling" until MainThreadQueue.Settled is true on the first
                // idle tick so a client that sees "completed" can send its next command immediately without 503 settling.
                if ((string)state["status"] == "compiling")
                {
                    if (!EditorApplication.isCompiling && MainThreadQueue.Settled)
                    {
                        state = Outcome(afterReload: true);
                        SessionState.SetFloat(FinishedAtKey, 0);
                    }
                    else
                    {
                        s_PendingReloadOutcome = true;
                    }
                }
                Save(state);
            }

            CompilationPipeline.compilationStarted += OnCompilationStartedEvent;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            CompilationPipeline.compilationFinished += OnCompilationFinishedEvent;
            EditorApplication.update += Watchdog;
        }

        public static void Detach()
        {
            if (!s_Attached)
                return;
            s_Attached = false;
            CompilationPipeline.compilationStarted -= OnCompilationStartedEvent;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            CompilationPipeline.compilationFinished -= OnCompilationFinishedEvent;
            EditorApplication.update -= Watchdog;
        }

        static void OnCompilationStartedEvent(object _) => OnCompilationStarted();

        static void OnCompilationFinishedEvent(object _) => OnCompilationFinished();

        public static void Register()
        {
            CommandRegistry.Register(new CommandEntry
            {
                Group = "script",
                Name = "recompile",
                Alias = "recompile",
                Description = "Refresh assets and compile scripts when script or assembly assets changed (or with --force). Poll script recompile_status until completed or up_to_date.",
                Tags = new[] { "scripts" },
                Parameters = new[]
                {
                    // shortcut: accepted because the unity CLI always sends it; compiling does not need focus,
                    // so it is ignored. Implement it if a workflow depends on the Editor coming to the front.
                    new CommandParameter { Name = "focus", IsBool = true, Description = "Accepted for compatibility; ignored." },
                    new CommandParameter { Name = "force", IsBool = true, Description = "Request script compilation even when no script or assembly assets changed (default false)." },
                },
                MainThreadRequired = true,
                Handler = Recompile,
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

        internal static bool IsCompilationAsset(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            var normalized = path.Replace('\\', '/');
            if (string.Equals(normalized, "Packages/manifest.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Packages/packages-lock.json", StringComparison.OrdinalIgnoreCase))
                return true;
            var extension = Path.GetExtension(normalized);
            return string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".asmdef", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".asmref", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".rsp", StringComparison.OrdinalIgnoreCase);
        }

        internal static void OnAssetsPostprocessed(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (!s_Attached)
                return;
            if (AnyCompilationAsset(importedAssets) ||
                AnyCompilationAsset(deletedAssets) ||
                AnyCompilationAsset(movedAssets) ||
                AnyCompilationAsset(movedFromAssetPaths))
                s_DirtySinceLastCompile = true;
        }

        static bool AnyCompilationAsset(string[] paths) => paths != null && paths.Any(IsCompilationAsset);

        internal static bool NeedsCompilation(bool force, bool dirtySinceLastCompile, bool scriptCompilationFailed, bool isCompiling, bool isUpdating)
        {
            return force || dirtySinceLastCompile || scriptCompilationFailed || isCompiling || isUpdating;
        }

        static object Recompile(JObject parameters)
        {
            var force = CommandRegistry.ReadBool(parameters, "force", false);
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
            var dirtyBeforeRefresh = s_DirtySinceLastCompile;
            AssetDatabase.Refresh(); // pick up scripts edited or added on disk since the last import
            if (EditorApplication.isCompiling || s_Status == "compiling")
            {
                if (s_Status != "compiling")
                {
                    ResetProgress();
                    Save(NewState("compiling"));
                }
                return new JObject { ["status"] = "compiling" };
            }
            var failedBefore = EditorUtility.scriptCompilationFailed;
            var dirty = dirtyBeforeRefresh || s_DirtySinceLastCompile;
            if (!NeedsCompilation(force, dirty, failedBefore, EditorApplication.isCompiling, EditorApplication.isUpdating))
            {
                s_DirtySinceLastCompile = false;
                SessionState.SetInt(CompiledAssembliesKey, 0);
                SessionState.SetBool(FailedBeforeStartKey, false);
                var upToDate = NewState("up_to_date");
                Save(upToDate);
                return upToDate;
            }
            SessionState.SetBool(FailedBeforeStartKey, failedBefore);
            Save(NewState("triggered"));
            CompilationPipeline.RequestScriptCompilation();
            // Counted from here so a slow Refresh does not eat the grace period.
            SessionState.SetFloat(RequestedAtKey, (float)EditorApplication.timeSinceStartup);
            return new JObject { ["status"] = "compiling" };
        }

        internal static void OnCompilationStarted()
        {
            s_PendingReloadOutcome = false;
            s_DirtySinceLastCompile = false;
            ResetProgress();
            if (s_Status != "triggered")
                SessionState.SetBool(FailedBeforeStartKey, EditorUtility.scriptCompilationFailed);
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
            AdvanceWatchdog(EditorApplication.timeSinceStartup, EditorApplication.isCompiling, EditorApplication.isUpdating, EditorApplication.isPlaying, MainThreadQueue.Settled);
        }

        internal static void AdvanceWatchdog(double now, bool isCompiling, bool isUpdating, bool isPlaying, bool settled = true)
        {
            if (isCompiling || isUpdating)
            {
                s_IdleSince = -1;
                return;
            }
            if (!s_BaselineCleared)
            {
                // Initial asset/domain refreshes postprocess already-compiled scripts after [InitializeOnLoad];
                // clear that startup noise once the Editor reaches its first idle update in this domain.
                s_BaselineCleared = true;
                s_DirtySinceLastCompile = false;
            }
            if (s_PendingReloadOutcome)
            {
                if (!settled)
                    return;
                s_PendingReloadOutcome = false;
                SessionState.SetFloat(FinishedAtKey, 0);
                Save(Outcome(afterReload: true));
                return;
            }
            if (s_IdleSince < 0)
                s_IdleSince = now;
            if (s_Status == "triggered" && now - Math.Max(SessionState.GetFloat(RequestedAtKey, 0), s_IdleSince) >= StartGraceSeconds)
            {
                SessionState.SetInt(CompiledAssembliesKey, 0);
                Save(isPlaying ? PlayModeDeferred() : Outcome(afterReload: false));
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
        // when nothing was recompiled: report that compile's errors again ("completed" + errors).
        // When compiled > 0 without reload, scriptCompilationFailed is trusted if scripts were healthy before the
        // compile started (catching post-csc Bee failures such as CopyFiles).
        internal static JObject Outcome(bool afterReload) => Outcome(afterReload, EditorUtility.scriptCompilationFailed);

        internal static JObject Outcome(bool afterReload, bool scriptCompilationFailed)
        {
            var compiled = SessionState.GetInt(CompiledAssembliesKey, 0);
            var failedBefore = SessionState.GetBool(FailedBeforeStartKey, false);
            if (!afterReload && compiled > 0 && (failedBefore || !scriptCompilationFailed))
                return NewState("completed");
            if (scriptCompilationFailed)
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

        internal static void ResetTestState(bool baselineCleared = true)
        {
            ResetProgress();
            s_DirtySinceLastCompile = false;
            s_BaselineCleared = baselineCleared;
            s_PendingReloadOutcome = false;
            s_IdleSince = -1;
            SessionState.EraseBool(FailedBeforeStartKey);
            SessionState.SetFloat(RequestedAtKey, 0);
            SessionState.SetFloat(FinishedAtKey, 0);
            SessionState.SetInt(CompiledAssembliesKey, 0);
        }

        internal static void SetPendingReloadOutcomeForTests(int compiledAssemblies = 1)
        {
            s_PendingReloadOutcome = true;
            SessionState.SetInt(CompiledAssembliesKey, compiledAssemblies);
            SessionState.SetBool(FailedBeforeStartKey, false);
            Save(NewState("compiling"));
        }

        internal static void SetWatchdogTestState(string status, float requestedAt, float finishedAt, double idleSince, int compiledAssemblies = 0, bool failedBeforeStart = false)
        {
            s_Status = status;
            s_IdleSince = idleSince;
            SessionState.SetFloat(RequestedAtKey, requestedAt);
            SessionState.SetFloat(FinishedAtKey, finishedAt);
            SessionState.SetInt(CompiledAssembliesKey, compiledAssemblies);
            SessionState.SetBool(FailedBeforeStartKey, failedBeforeStart);
            var snapshot = NewState(status);
            var json = snapshot.ToString(Formatting.None);
            SessionState.SetString(StateKey, json);
            s_Snapshot = json;
        }

        internal static void Save(JObject state)
        {
            s_Status = (string)state["status"];
            var json = state.ToString(Formatting.None);
            SessionState.SetString(StateKey, json);
            s_Snapshot = json;
        }
    }

    internal sealed class ScriptChangeDetector : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            RecompileCommands.OnAssetsPostprocessed(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
        }
    }
}
