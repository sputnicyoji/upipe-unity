using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Upipe.Editor
{
    // `console get|status|clear|write`: a sequence-numbered buffer of log messages with a cursor
    // clients can follow. Entries come from Application.logMessageReceivedThreaded (compiler messages
    // included) and survive domain reloads through SessionState; a new Editor session starts a new
    // buffer and a new session id, so an old cursor is refused with reset=true.
    internal static class ConsoleCommands
    {
        const string BufferKey = "Upipe.Console.Buffer";
        const string SessionKey = "Upipe.Console.Session";
        const int Capacity = 2000;
        // Kept across a domain reload; SessionState is rewritten in full on every reload.
        const int PersistedEntries = 500;
        const int MaxFieldChars = 16 * 1024;
        // Messages logged after the buffer is saved for a reload are lost with the old domain. Restarting the
        // numbering past them keeps a cursor handed out in that window from skipping new entries.
        const long RestoreSeqMargin = 100;
        const int DefaultTail = 100;
        const double SampleIntervalSeconds = 0.25;
        static readonly string[] s_Levels = { "log", "warn", "error" };

        // Immutable once added, so HTTP threads may read entries outside the lock.
        sealed class Entry
        {
            public long Seq;
            public string TimestampUtc;
            public string Level;
            public string LogType;
            public string Message;
            public string StackTrace;
        }

        // Immutable pair so HTTP threads never observe a torn timestamp/payload pair across updates.
        internal sealed class GroundTruthSnapshot
        {
            public readonly JObject Json;
            public readonly DateTime SampledAt;

            public GroundTruthSnapshot(JObject json, DateTime sampledAt)
            {
                Json = json;
                SampledAt = sampledAt;
            }
        }

        static readonly object s_Lock = new object();
        static readonly LinkedList<Entry> s_Entries = new LinkedList<Entry>();
        static long s_LastSeq;
        // Seq of the last entry `console clear` removed; those entries were not missed, so they never count as dropped.
        static long s_ClearedThrough;
        static string s_Session;
        static bool s_Restored;
        static bool s_Attached;

        // Editor state sampled on the main thread and replaced whole, read by HTTP threads.
        static volatile GroundTruthSnapshot s_GroundTruth;
        static double s_NextSample;
        static MethodInfo s_GetCountsByType;
        static bool s_LookedUpCounts;

        static string EnsureSession()
        {
            lock (s_Lock)
            {
                if (!string.IsNullOrEmpty(s_Session))
                    return s_Session;
                s_Session = SessionState.GetString(SessionKey, "");
                if (s_Session.Length == 0)
                {
                    s_Session = Guid.NewGuid().ToString("N");
                    SessionState.SetString(SessionKey, s_Session);
                }
                return s_Session;
            }
        }

        public static void Attach()
        {
            if (s_Attached)
                return;
            s_Attached = true;
            EnsureSession();
            if (!s_Restored)
            {
                s_Restored = true;
                Restore(SessionState.GetString(BufferKey, ""));
            }
            s_NextSample = 0;
            SampleGroundTruth();
            Application.logMessageReceivedThreaded += OnLog;
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static void Detach()
        {
            if (!s_Attached)
                return;
            s_Attached = false;
            Application.logMessageReceivedThreaded -= OnLog;
            EditorApplication.update -= OnUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        // Leaving Play Mode clears the native flag the event's add accessor sets, so OnLog, still subscribed, gets
        // nothing until a new subscription (measured on 2022.3.62). Entry is covered for Play Mode without a reload.
        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if ((state != PlayModeStateChange.EnteredEditMode && state != PlayModeStateChange.EnteredPlayMode) || !s_Attached)
                return;
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
        }

        static void OnUpdate()
        {
            if (EditorApplication.timeSinceStartup >= s_NextSample)
                SampleGroundTruth();
        }

        public static void Register()
        {
            CommandRegistry.Register(new CommandEntry
            {
                Group = "console",
                Name = "get",
                // `unity recompile --strict` sends "console" for the entries after its cursor.
                Alias = "console",
                Description = "Captured console output; supports tail, minimum level, and following via cursor and session.",
                Tags = new[] { "console" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "tail", IsInteger = true, Description = "Maximum number of most-recent entries to return (default 100)." },
                    new CommandParameter { Name = "level", Description = "Minimum severity: log | warn | error (default log)." },
                    new CommandParameter { Name = "since", IsInteger = true, Description = "Cursor: only entries newer than this seq (default -1)." },
                    new CommandParameter { Name = "since-session", Description = "Session the since cursor came from." },
                },
                MainThreadRequired = false,
                ReadOnly = true,
                Handler = Get,
            });
            CommandRegistry.Register(new CommandEntry
            {
                Group = "console",
                Name = "status",
                Alias = "console_status",
                Description = "Cursor, retained counts and Editor ground truth without entries.",
                Tags = new[] { "console" },
                MainThreadRequired = false,
                ReadOnly = true,
                Handler = _ => Status(),
            });
            CommandRegistry.Register(new CommandEntry
            {
                Group = "console",
                Name = "clear",
                Alias = "clear_console",
                Description = "Clear the Editor console and the captured entries; cursors stay valid and numbering continues.",
                Tags = new[] { "console" },
                Handler = _ => Clear(),
            });
            CommandRegistry.Register(new CommandEntry
            {
                Group = "console",
                Name = "write",
                Alias = "console_write",
                Description = "Write a message to the Editor console.",
                Tags = new[] { "console" },
                Parameters = new[]
                {
                    new CommandParameter { Name = "message", Required = true, Description = "Text to log." },
                    new CommandParameter { Name = "level", Description = "log | warn | error (default log)." },
                },
                Handler = Write,
            });
        }

        // Main thread (LogEntries is main-thread only).
        internal static object Clear()
        {
            lock (s_Lock)
            {
                s_Entries.Clear();
                s_ClearedThrough = s_LastSeq;
            }
            ClearEditorConsole();
            SampleGroundTruth();
            return null;
        }

        internal static object Write(JObject parameters)
        {
            var message = (string)parameters["message"];
            switch ((string)parameters["level"] ?? "log")
            {
                case "log":
                    Debug.Log(message);
                    break;
                case "warn":
                    Debug.LogWarning(message);
                    break;
                case "error":
                    Debug.LogError(message);
                    break;
                default:
                    throw CommandFailure.InvalidUsage("level must be one of: log, warn, error.");
            }
            return null;
        }

        internal static object Get(JObject parameters)
        {
            var tail = CommandRegistry.ReadInteger(parameters, "tail", DefaultTail);
            var since = CommandRegistry.ReadInteger(parameters, "since", -1);
            var level = (string)parameters["level"] ?? "log";
            var sinceSession = (string)parameters["since-session"];
            if (tail < 0)
                throw CommandFailure.InvalidUsage("tail must be zero or greater.");
            var minimum = Array.IndexOf(s_Levels, level);
            if (minimum < 0)
                throw CommandFailure.InvalidUsage("level must be one of: log, warn, error.");

            List<Entry> page;
            bool reset, dropped;
            long cursor;
            int[] counts;
            var session = EnsureSession();
            lock (s_Lock)
            {
                // A cursor from another session, or one ahead of this buffer, names different entries.
                reset = since >= 0 && ((sinceSession != null && sinceSession != session) || since > s_LastSeq);
                var from = since >= 0 && !reset ? since : -1;
                var matching = s_Entries.Where(e => e.Seq > from && Array.IndexOf(s_Levels, e.Level) >= minimum).ToList();
                page = matching.Skip(Math.Max(0, matching.Count - (int)Math.Min(tail, int.MaxValue))).ToList();
                // Following a cursor, entries can be missed two ways: evicted before this read, or cut by tail.
                dropped = from >= 0 && ((s_Entries.Count > 0 && s_Entries.First.Value.Seq > Math.Max(from, s_ClearedThrough) + 1) || matching.Count > page.Count);
                cursor = s_LastSeq;
                counts = CountByLevel();
            }
            return Build(page, cursor, session, counts, reset, dropped);
        }

        internal static JObject Status()
        {
            long cursor;
            int[] counts;
            var session = EnsureSession();
            lock (s_Lock)
            {
                cursor = s_LastSeq;
                counts = CountByLevel();
            }
            return Build(new List<Entry>(), cursor, session, counts, false, false);
        }

        // Called under s_Lock. Order matches s_Levels.
        static int[] CountByLevel()
        {
            var counts = new int[s_Levels.Length];
            foreach (var e in s_Entries)
                counts[Array.IndexOf(s_Levels, e.Level)]++;
            return counts;
        }

        static JObject Build(List<Entry> entries, long cursor, string session, int[] counts, bool reset, bool dropped)
        {
            var snapshot = s_GroundTruth ?? SampleGroundTruth();
            var truth = (JObject)snapshot.Json.DeepClone();
            truth["ageMs"] = Math.Max(0L, (long)(DateTime.UtcNow - snapshot.SampledAt).TotalMilliseconds);
            return new JObject
            {
                ["entries"] = new JArray(entries.Select(ToJson)),
                ["cursor"] = cursor,
                ["session"] = session,
                ["returned"] = entries.Count,
                ["dropped"] = dropped,
                ["reset"] = reset,
                ["counts"] = new JObject { ["error"] = counts[2], ["warn"] = counts[1], ["log"] = counts[0] },
                ["groundTruth"] = truth,
            };
        }

        static JObject ToJson(Entry e)
        {
            return new JObject
            {
                ["seq"] = e.Seq,
                ["timestampUtc"] = e.TimestampUtc,
                ["level"] = e.Level,
                ["logType"] = e.LogType,
                ["message"] = e.Message,
                ["stackTrace"] = e.StackTrace,
                ["seeded"] = false,
            };
        }

        // Runs on whatever thread logged; Unity raises it for compiler messages too.
        internal static void OnLog(string message, string stackTrace, LogType type)
        {
            Add(type, message, stackTrace);
        }

        internal static void Add(LogType type, string message, string stackTrace)
        {
            var entry = new Entry
            {
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Level = type == LogType.Warning ? "warn" : type == LogType.Log ? "log" : "error",
                LogType = type.ToString(),
                Message = Clip(message),
                StackTrace = Clip(stackTrace),
            };
            lock (s_Lock)
            {
                entry.Seq = ++s_LastSeq;
                s_Entries.AddLast(entry);
                while (s_Entries.Count > Capacity)
                    s_Entries.RemoveFirst();
            }
        }

        static string Clip(string text)
        {
            text = text ?? "";
            return text.Length <= MaxFieldChars ? text : text.Substring(0, MaxFieldChars) + " [truncated]";
        }

        static GroundTruthSnapshot SampleGroundTruth()
        {
            s_NextSample = EditorApplication.timeSinceStartup + SampleIntervalSeconds;
            var sampledAt = DateTime.UtcNow;
            var truth = new JObject
            {
                ["sampledUtc"] = sampledAt.ToString("o"),
                ["compilationFailed"] = EditorUtility.scriptCompilationFailed,
                ["compiling"] = EditorApplication.isCompiling,
                ["consoleErrors"] = null,
                ["consoleWarnings"] = null,
                ["consoleLogs"] = null,
                ["seeded"] = false,
            };
            var counts = ReadEditorConsoleCounts();
            if (counts != null)
            {
                truth["consoleErrors"] = counts[0];
                truth["consoleWarnings"] = counts[1];
                truth["consoleLogs"] = counts[2];
            }
            var snapshot = new GroundTruthSnapshot(truth, sampledAt);
            s_GroundTruth = snapshot;
            return snapshot;
        }

        // shortcut: the Editor console is read and cleared through the internal UnityEditor.LogEntries API. If a Unity
        // version renames or changes it, its counts stay null and clear empties only the captured entries, with a warning.
        static MethodInfo LogEntriesMethod(string name, params Type[] parameters)
        {
            return typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries")?.GetMethod(
                name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null);
        }

        static int[] ReadEditorConsoleCounts()
        {
            try
            {
                if (!s_LookedUpCounts)
                {
                    s_LookedUpCounts = true;
                    var byRef = typeof(int).MakeByRefType();
                    s_GetCountsByType = LogEntriesMethod("GetCountsByType", byRef, byRef, byRef);
                }
                if (s_GetCountsByType == null)
                    return null;
                var args = new object[] { 0, 0, 0 };
                s_GetCountsByType.Invoke(null, args);
                return new[] { (int)args[0], (int)args[1], (int)args[2] };
            }
            catch (Exception)
            {
                s_GetCountsByType = null; // stop trying for this domain
                return null;
            }
        }

        internal static void ClearEditorConsole(Func<MethodInfo> resolveClear = null)
        {
            try
            {
                var clear = resolveClear != null ? resolveClear() : LogEntriesMethod("Clear");
                if (clear == null)
                    Debug.LogWarning("[Upipe] console clear: UnityEditor.LogEntries.Clear was not found; only the captured entries were cleared.");
                else
                    clear.Invoke(null, null);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Upipe] console clear: UnityEditor.LogEntries.Clear failed ({e.Message}); only the captured entries were cleared.");
            }
        }

        public static void Persist()
        {
            if (s_Attached)
                SessionState.SetString(BufferKey, Save());
        }

        // On a manual stop: a reload while the server is off persists nothing, so an earlier domain's buffer must not
        // come back later and rewind seq below what clients saw before the stop.
        public static void ForgetSaved() => SessionState.EraseString(BufferKey);

        static string Save()
        {
            lock (s_Lock)
            {
                return new JObject
                {
                    ["lastSeq"] = s_LastSeq,
                    ["clearedThrough"] = s_ClearedThrough,
                    ["entries"] = new JArray(s_Entries.Skip(Math.Max(0, s_Entries.Count - PersistedEntries)).Select(ToJson)),
                }.ToString(Formatting.None);
            }
        }

        // Test hook: what a domain reload does to the buffer, saved and read back in place.
        internal static void ReloadInPlace()
        {
            var saved = Save();
            lock (s_Lock)
            {
                s_Entries.Clear();
                s_LastSeq = 0;
                s_ClearedThrough = 0;
            }
            Restore(saved);
        }

        static void Restore(string json)
        {
            if (json.Length == 0)
                return;
            try
            {
                var state = JObject.Parse(json);
                lock (s_Lock)
                {
                    foreach (var e in state["entries"])
                    {
                        s_Entries.AddLast(new Entry
                        {
                            Seq = (long)e["seq"],
                            TimestampUtc = (string)e["timestampUtc"],
                            Level = (string)e["level"],
                            LogType = (string)e["logType"],
                            Message = (string)e["message"],
                            StackTrace = (string)e["stackTrace"],
                        });
                    }
                    s_LastSeq = (long)state["lastSeq"] + RestoreSeqMargin;
                    s_ClearedThrough = (long?)state["clearedThrough"] ?? 0;
                    // No surviving entry (fresh session or right after `console clear`): advance cleared baseline past
                    // the reload seq margin so the next log after reload does not read as dropped entries.
                    if (s_Entries.Count == 0)
                        s_ClearedThrough = s_LastSeq;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Upipe] Could not restore the console buffer after reload: {e.Message}");
            }
        }

        // The last `max` error-level messages (Error, Exception, Assert) logged after `seq`, oldest first. Any thread.
        internal static List<string> ErrorsSince(long seq, int max)
        {
            List<string> messages;
            lock (s_Lock)
                messages = s_Entries.Where(e => e.Seq > seq && e.Level == "error").Select(e => e.Message).ToList();
            return messages.Skip(Math.Max(0, messages.Count - max)).ToList();
        }

        // Test hook: whether Unity's log messages reach the buffer (only while the server runs).
        internal static bool IsAttached => s_Attached;

        // Test hook: the cursor and session a client would see.
        internal static (long cursor, string session) Position
        {
            get
            {
                var session = EnsureSession();
                lock (s_Lock)
                    return (s_LastSeq, session);
            }
        }
    }
}
