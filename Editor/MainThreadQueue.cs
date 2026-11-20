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

    // Runs work on the Editor main thread and caches Editor state so HTTP threads can read it
    // (Unity APIs are main-thread only). The cache keeps /api/status answerable while the main thread is busy.
    [InitializeOnLoad]
    internal static class MainThreadQueue
    {
        const string SettledKey = "Upipe.Settled";
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

        static readonly ConcurrentQueue<WorkItem> s_Work = new ConcurrentQueue<WorkItem>();
        static double s_IdleSince = -1;

        public static volatile bool IsCompiling;
        public static volatile bool IsUpdating;
        public static volatile bool IsPlaying;
        public static volatile bool IsPaused;
        // One-way per Editor session: true once the Editor has been seen idle after startup.
        public static volatile bool Settled;
        // Set right before the domain unloads; a new domain starts with it false again.
        public static volatile bool DomainReloadInProgress;
        public static volatile bool AutoRefresh;
        public static volatile bool RunInBackground;
        public static volatile int FrameCount;
        // DateTime.UtcNow.Ticks when FrameCount last changed; read with Volatile (long cannot be volatile).
        static long s_FrameAdvancedAt;
        static int s_AutoRefreshMode;
        static double s_AutoRefreshReadAt = double.NegativeInfinity;

        static MainThreadQueue()
        {
            Settled = SessionState.GetBool(SettledKey, false);
            RefreshState();
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += () => DomainReloadInProgress = true;
        }

        // A playing, unpaused game whose frame counter stopped moving is frozen (often: Editor unfocused
        // without runInBackground).
        public static bool PlayerLoopTicking => IsPlaying && !IsPaused &&
            DateTime.UtcNow.Ticks - Volatile.Read(ref s_FrameAdvancedAt) < TimeSpan.FromSeconds(TickingWindowSeconds).Ticks;

        // Cached Editor state that any thread may read; the base of `editor status`, /api/status and /api/editor_status.
        // frameCount and playerLoopTicking appear only in play mode, as the unity CLI skill docs describe them.
        public static JObject StateFields()
        {
            var fields = new JObject
            {
                ["status"] = Settled ? "ready" : "settling",
                ["compiling"] = IsCompiling,
                ["updating"] = IsUpdating,
                ["domainReloadInProgress"] = DomainReloadInProgress,
                ["playMode"] = !IsPlaying ? "stopped" : IsPaused ? "paused" : "playing",
                ["autoRefresh"] = AutoRefresh,
                ["runInBackground"] = RunInBackground,
            };
            if (IsPlaying)
            {
                fields["frameCount"] = FrameCount;
                fields["playerLoopTicking"] = PlayerLoopTicking;
            }
            return fields;
        }

        // Called from the main thread before the listener starts, so the static constructor above
        // never runs first on an HTTP thread (its Unity API calls would throw there).
        public static void Initialize() { }

        // shortcut: work waits for the next Editor tick, about 10 per second while the Editor is unfocused (macOS,
        // 2022.3.62). Auto tick posts through UnitySynchronizationContext, which runs on that same tick, so it does
        // not shorten the wait; find a native wake-up if background latency becomes a problem.
        public static Task<object> Run(Func<object> work, CancellationToken cancellation)
        {
            var item = new WorkItem
            {
                Work = work,
                Completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously),
                Cancellation = cancellation,
            };
            s_Work.Enqueue(item);
            return item.Completion.Task;
        }

        // Fails everything still queued; used right before a domain reload drops the queue.
        public static void FailPending()
        {
            while (s_Work.TryDequeue(out var item))
                item.Completion.TrySetException(new EditorReloadingException());
        }

        // Main thread: every Editor update, and each auto tick.
        internal static void Pump()
        {
            RefreshState();
            while (s_Work.TryDequeue(out var item))
            {
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
            if (Settled)
                return;
            if (IsCompiling || IsUpdating)
                s_IdleSince = -1;
            else if (s_IdleSince < 0)
                s_IdleSince = now;
            else if (now - s_IdleSince >= SettleSeconds)
            {
                Settled = true;
                SessionState.SetBool(SettledKey, true);
            }
        }
    }
}
