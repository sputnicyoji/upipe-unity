using System.Threading;
using UnityEditor;
using UnityEditorInternal;

namespace Upipe.Editor
{
    // `editor set_auto_tick`: while the Editor is unfocused, a timer posts a tick to the main thread every interval;
    // the tick drains MainThreadQueue and asks for a player-loop update. The timer is paused while the Editor has focus.
    // Measured on macOS with 2022.3.62, the posts run on the Editor's own unfocused tick, so this neither shortens
    // queue waits nor advances a game that Run In Background=false holds at frame 1; it is here because the CLI's
    // play-mode recipe calls it.
    [InitializeOnLoad]
    internal static class AutoTick
    {
        // Interval in ms kept across domain reloads; 0 when off or not persisted.
        internal const string IntervalKey = "Upipe.AutoTick.IntervalMs";

        static readonly SynchronizationContext s_MainContext;
        static Timer s_Timer;
        static bool s_Focused;
        // 1 while a posted tick has not run yet; the Editor drains posts in batches, so more would only pile up.
        static int s_Pending;

        static AutoTick()
        {
            s_MainContext = SynchronizationContext.Current;
            s_Focused = InternalEditorUtility.isApplicationActive;
            EditorApplication.update += WatchFocus;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            var persisted = SessionState.GetInt(IntervalKey, 0);
            if (persisted > 0)
                Set(persisted, true);
        }

        public static int IntervalMs { get; private set; }

        // Main thread only. 0 turns it off. A persisted setting comes back after a domain reload; otherwise the next
        // reload turns it off.
        public static void Set(int intervalMs, bool persist)
        {
            Stop();
            IntervalMs = intervalMs;
            SessionState.SetInt(IntervalKey, persist ? intervalMs : 0);
            if (intervalMs > 0)
                s_Timer = new Timer(_ => Post(), null, s_Focused ? Timeout.Infinite : intervalMs, intervalMs);
        }

        static void Stop()
        {
            s_Timer?.Dispose();
            s_Timer = null;
        }

        static void WatchFocus()
        {
            var focused = InternalEditorUtility.isApplicationActive;
            if (focused == s_Focused)
                return;
            s_Focused = focused;
            s_Timer?.Change(focused ? Timeout.Infinite : IntervalMs, IntervalMs);
        }

        // Timer thread: no Unity APIs here.
        static void Post()
        {
            if (s_MainContext == null || Interlocked.Exchange(ref s_Pending, 1) == 1)
                return;
            s_MainContext.Post(_ =>
            {
                Volatile.Write(ref s_Pending, 0);
                MainThreadQueue.Pump();
                EditorApplication.QueuePlayerLoopUpdate();
            }, null);
        }
    }
}
