// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Yoji

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Upipe.Editor
{
    // Raised to callers whose queued work was dropped because the domain is about to reload.
    internal sealed class EditorReloadingException : Exception
    {
        public EditorReloadingException() : base("The Editor is reloading scripts; retry the request.") { }
    }

    // Raised when the server stops or detaches while a request was waiting (not a domain reload).
    internal sealed class ServerStoppedException : Exception
    {
        public ServerStoppedException() : base("The Upipe server was stopped before the request could run.") { }
    }

    // Runs work on the Editor main thread and caches Editor state so HTTP threads can read it
    // (Unity APIs are main-thread only). The cache keeps /api/status answerable while the main thread is busy.
    internal static class MainThreadQueue
    {
        // Records whether this Editor session completed its initial cold-start 1.0s settle; subsequent
        // domains in the same session start unsettled until their own main loop runs an idle tick.
        const string ColdSettledKey = "Upipe.ColdSettled";
        // The Editor must stay idle this long before it counts as settled, so a gap between
        // compile and import during a cold start is not mistaken for readiness.
        const double SettleSeconds = 1.0;
        // The player loop counts as ticking while Time.frameCount has moved within this window.
        const double TickingWindowSeconds = 1.0;
        // Values of UnityEditor.AssetPipelineAutoRefreshMode, as the Preferences window stores them.
        const string AutoRefreshModeKey = "kAutoRefreshMode";
        const int AutoRefreshEnabled = 1;
        const int AutoRefreshOutsidePlayMode = 2;

        sealed class WorkItem
        {
            public Func<object> Work;
            public TaskCompletionSource<object> Completion;
            public CancellationToken Cancellation;
        }

        internal struct TestState
        {
            public bool Attached;
            public bool Rejecting;
            public bool DomainReloadInProgress;
            public bool Settled;
            public bool ColdSettled;
            public bool SessionColdSettled;
            public long LastTickUtcTicks;
            public double IdleSince;
            public bool TransitioningPlayMode;
        }

        static readonly ConcurrentQueue<WorkItem> s_Work = new ConcurrentQueue<WorkItem>();
        static double s_IdleSince = -1;

        public static volatile bool IsCompiling;
        public static volatile bool IsUpdating;
        public static volatile bool IsPlaying;
        public static volatile bool IsPaused;
        // Reset on every domain load until this domain's main loop ticks while idle.
        public static volatile bool Settled;
        // Set right before the domain unloads; a new domain starts with it false again.
        public static volatile bool DomainReloadInProgress;
        public static volatile bool AutoRefresh;
        public static volatile bool RunInBackground;
        public static volatile int FrameCount;
        // DateTime.UtcNow.Ticks when FrameCount last changed; read with Volatile (long cannot be volatile).
        static long s_FrameAdvancedAt;
        static long s_LastTickUtcTicks;
        static int s_AutoRefreshMode = AutoRefreshEnabled;
        static double s_AutoRefreshReadAt = double.NegativeInfinity;
        static bool s_ColdSettled;
        static volatile bool s_Attached;
        static volatile bool s_Rejecting = true;
        static bool s_TransitioningPlayMode;

        internal static bool IsAttached => s_Attached;
        internal static int PendingCount => s_Work.Count;

        public static void Attach()
        {
            if (s_Attached)
                return;
            s_Attached = true;
            DomainReloadInProgress = false;
            Settled = false;
            s_IdleSince = -1;
            s_TransitioningPlayMode = false;
            s_ColdSettled = SessionState.GetBool(ColdSettledKey, false);
            Volatile.Write(ref s_LastTickUtcTicks, 0L);
            s_AutoRefreshReadAt = double.NegativeInfinity;
            RefreshState();
            s_Rejecting = false;
            EditorApplication.update += Pump;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static void MarkDomainReloading()
        {
            DomainReloadInProgress = true;
            s_Rejecting = true;
            Settled = false;
            s_IdleSince = -1;
        }

        public static void Detach(bool forReload)
        {
            s_Rejecting = true;
            Settled = false;
            s_IdleSince = -1;
            if (forReload)
                DomainReloadInProgress = true;
            if (!s_Attached)
                return;
            s_Attached = false;
            EditorApplication.update -= Pump;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
            {
                s_TransitioningPlayMode = true;
                Settled = false;
                s_IdleSince = -1;
            }
            else if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                s_TransitioningPlayMode = false;
            }
        }

        public static long? LastTickAgeMs()
        {
            var ticks = Volatile.Read(ref s_LastTickUtcTicks);
            if (ticks <= 0)
                return null;
            return Math.Max(0L, (DateTime.UtcNow.Ticks - ticks) / TimeSpan.TicksPerMillisecond);
        }

        // A playing, unpaused game whose frame counter stopped moving is frozen (often: Editor unfocused
        // without runInBackground).
        public static bool PlayerLoopTicking => IsPlaying && !IsPaused &&
            DateTime.UtcNow.Ticks - Volatile.Read(ref s_FrameAdvancedAt) < TimeSpan.FromSeconds(TickingWindowSeconds).Ticks;

        // Cached Editor state that any thread may read; the base of `editor status`, /api/status and /api/editor_status.
        // frameCount and playerLoopTicking appear only in play mode, as the unity CLI skill docs describe them.
        public static JObject StateFields()
        {
            var lastTickAgeMs = LastTickAgeMs();
            var fields = new JObject
            {
                ["status"] = Settled ? "ready" : "settling",
                ["compiling"] = IsCompiling,
                ["updating"] = IsUpdating,
                ["domainReloadInProgress"] = DomainReloadInProgress,
                ["playMode"] = !IsPlaying ? "stopped" : IsPaused ? "paused" : "playing",
                ["autoRefresh"] = AutoRefresh,
                ["runInBackground"] = RunInBackground,
                ["lastTickAgeMs"] = lastTickAgeMs.HasValue ? new JValue(lastTickAgeMs.Value) : JValue.CreateNull(),
            };
            if (IsPlaying)
            {
                fields["frameCount"] = FrameCount;
                fields["playerLoopTicking"] = PlayerLoopTicking;
            }
            return fields;
        }

        // shortcut: work waits for the next Editor tick, about 10 per second while the Editor is unfocused (macOS,
        // 2022.3.62). Auto tick posts through UnitySynchronizationContext, which runs on that same tick, so it does
        // not shorten the wait; find a native wake-up if background latency becomes a problem.
        public static Task<object> Run(Func<object> work, CancellationToken cancellation)
        {
            if (s_Rejecting || DomainReloadInProgress || !s_Attached)
                return Task.FromException<object>(DomainReloadInProgress ? (Exception)new EditorReloadingException() : new ServerStoppedException());
            if (cancellation.IsCancellationRequested)
                return Task.FromCanceled<object>(cancellation);
            var item = new WorkItem
            {
                Work = work,
                Completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously),
                Cancellation = cancellation,
            };
            s_Work.Enqueue(item);
            // Re-check after enqueue so a concurrent Stop/Reload can never leave an item stranded.
            if (s_Rejecting || DomainReloadInProgress || !s_Attached)
                FailPending(DomainReloadInProgress);
            return item.Completion.Task;
        }

        // Run, then block this (non-main) thread for the result. Reload, stop, cancel and CommandFailure pass through as
        // they are, so the server answers them as such; anything else fails as "<failure>: <message>".
        public static object RunAndWait(Func<object> work, CancellationToken cancellation, string failure)
        {
            var task = Run(work, cancellation);
            try
            {
                task.Wait(cancellation);
                return task.Result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AggregateException e)
            {
                var error = UpipeServer.Unwrap(e);
                if (error is EditorReloadingException || error is ServerStoppedException || error is OperationCanceledException ||
                    error is CommandFailure)
                    throw error;
                throw new CommandFailure("Command Execution Failed", $"{failure}: {error.Message}");
            }
        }

        // Fails everything still queued when the domain unloads or the server stops manually.
        public static void FailPending(bool forReload = true)
        {
            while (s_Work.TryDequeue(out var item))
                item.Completion.TrySetException(forReload ? (Exception)new EditorReloadingException() : new ServerStoppedException());
        }

        // Main thread: every Editor update, and each auto tick.
        internal static void Pump()
        {
            Volatile.Write(ref s_LastTickUtcTicks, DateTime.UtcNow.Ticks);
            RefreshState();
            AdvanceSettle(
                EditorApplication.timeSinceStartup,
                IsCompiling,
                IsUpdating,
                s_TransitioningPlayMode || (EditorApplication.isPlayingOrWillChangePlaymode && !IsPlaying));
            while (s_Work.TryDequeue(out var item))
            {
                if (s_Rejecting || DomainReloadInProgress)
                {
                    item.Completion.TrySetException(DomainReloadInProgress ? (Exception)new EditorReloadingException() : new ServerStoppedException());
                    continue;
                }
                // A request that timed out while queued never runs.
                if (item.Cancellation.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled();
                    continue;
                }
                try
                {
                    item.Completion.TrySetResult(item.Work());
                }
                catch (Exception e)
                {
                    item.Completion.TrySetException(e);
                }
            }
        }

        static void RefreshState()
        {
            IsCompiling = EditorApplication.isCompiling;
            IsUpdating = EditorApplication.isUpdating;
            IsPlaying = EditorApplication.isPlaying;
            IsPaused = EditorApplication.isPaused;
            RunInBackground = Application.runInBackground;
            var now = EditorApplication.timeSinceStartup;
            // The preference changes only in the Preferences window, so reading it once a second is enough.
            if (now - s_AutoRefreshReadAt >= 1)
            {
                s_AutoRefreshMode = EditorPrefs.GetInt(AutoRefreshModeKey, AutoRefreshEnabled);
                s_AutoRefreshReadAt = now;
            }
            AutoRefresh = s_AutoRefreshMode == AutoRefreshEnabled || (s_AutoRefreshMode == AutoRefreshOutsidePlayMode && !IsPlaying);
            var frame = Time.frameCount;
            if (frame != FrameCount)
            {
                FrameCount = frame;
                Volatile.Write(ref s_FrameAdvancedAt, DateTime.UtcNow.Ticks);
            }
        }

        internal static void AdvanceSettle(double now, bool isCompiling, bool isUpdating, bool transitioningPlayMode)
        {
            if (Settled)
                return;
            if (isCompiling || isUpdating || transitioningPlayMode)
            {
                s_IdleSince = -1;
                return;
            }
            if (s_ColdSettled)
            {
                Settled = true;
                return;
            }
            if (s_IdleSince < 0)
                s_IdleSince = now;
            else if (now - s_IdleSince >= SettleSeconds)
            {
                s_ColdSettled = true;
                SessionState.SetBool(ColdSettledKey, true);
                Settled = true;
            }
        }

        internal static TestState CaptureTestState()
        {
            return new TestState
            {
                Attached = s_Attached,
                Rejecting = s_Rejecting,
                DomainReloadInProgress = DomainReloadInProgress,
                Settled = Settled,
                ColdSettled = s_ColdSettled,
                SessionColdSettled = SessionState.GetBool(ColdSettledKey, false),
                LastTickUtcTicks = Volatile.Read(ref s_LastTickUtcTicks),
                IdleSince = s_IdleSince,
                TransitioningPlayMode = s_TransitioningPlayMode,
            };
        }

        internal static void ResetForTests(bool settled, bool coldSettled, long lastTickUtcTicks)
        {
            Settled = settled;
            s_ColdSettled = coldSettled;
            s_IdleSince = -1;
            Volatile.Write(ref s_LastTickUtcTicks, lastTickUtcTicks);
        }

        internal static void RestoreTestState(TestState state)
        {
            s_ColdSettled = state.ColdSettled;
            SessionState.SetBool(ColdSettledKey, state.SessionColdSettled);
            Settled = state.Settled;
            s_IdleSince = state.IdleSince;
            Volatile.Write(ref s_LastTickUtcTicks, state.LastTickUtcTicks);
            DomainReloadInProgress = state.DomainReloadInProgress;
            s_Rejecting = state.Rejecting;
            s_TransitioningPlayMode = state.TransitioningPlayMode;
            if (state.Attached && !s_Attached)
            {
                s_Attached = true;
                EditorApplication.update += Pump;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            }
            else if (!state.Attached && s_Attached)
            {
                s_Attached = false;
                EditorApplication.update -= Pump;
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            }
        }
    }
}
