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
    internal static class AutoTick
    {
        // Interval in ms kept across domain reloads; 0 when off or not persisted.
        internal const string IntervalKey = "Upipe.AutoTick.IntervalMs";

        static SynchronizationContext s_MainContext;
        static Timer s_Timer;
        static bool s_Focused;
        static bool s_Attached;
        // 1 while a posted tick has not run yet; the Editor drains posts in batches, so more would only pile up.
        static int s_Pending;

        public static void Attach()
        {
            if (s_Attached)
                return;
            s_Attached = true;
            Volatile.Write(ref s_Pending, 0);
            s_MainContext = SynchronizationContext.Current;
            s_Focused = InternalEditorUtility.isApplicationActive;
            EditorApplication.update += WatchFocus;
            var persisted = SessionState.GetInt(IntervalKey, 0);
            if (persisted > 0)
                Set(persisted, true);
        }

        public static void Detach()
        {
            if (!s_Attached)
                return;
            s_Attached = false;
            EditorApplication.update -= WatchFocus;
            StopTimer();
            // A persisted interval stays in SessionState, so a server turned off and on again restarts it.
            IntervalMs = 0;
        }

        public static int IntervalMs { get; private set; }

        // Main thread only. 0 turns it off. A persisted setting comes back after a domain reload; otherwise the next
        // reload turns it off.
        public static void Set(int intervalMs, bool persist)
        {
            StopTimer();
            IntervalMs = intervalMs;
            SessionState.SetInt(IntervalKey, persist ? intervalMs : 0);
            if (intervalMs > 0 && s_Attached)
                s_Timer = new Timer(_ => Post(), null, s_Focused ? Timeout.Infinite : intervalMs, intervalMs);
        }

        static void StopTimer()
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
            var context = s_MainContext;
            if (!s_Attached || context == null || Interlocked.Exchange(ref s_Pending, 1) == 1)
                return;
            try
            {
                context.Post(_ =>
                {
                    Volatile.Write(ref s_Pending, 0);
                    if (!s_Attached)
                        return;
                    MainThreadQueue.Pump();
                    EditorApplication.QueuePlayerLoopUpdate();
                }, null);
            }
            catch
            {
                Volatile.Write(ref s_Pending, 0);
            }
        }
    }
}
