using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace Fastcull.Diagnostics
{
    /// <summary>
    /// Detects the UI thread being blocked, which is the one thing a log written FROM the UI thread
    /// can never tell you about.
    ///
    /// Built for the "loading 5,000 photos from an external drive hangs hard enough to need Task
    /// Manager" report. A hang of that kind means the message loop has stopped pumping, so the
    /// evidence has to come from somewhere else: a background thread posts a ping to the dispatcher
    /// on a fixed interval and measures how long the ping takes to run. That delay IS the stall -
    /// if the UI thread is busy or blocked, the ping cannot execute, and the gap is exactly how
    /// long a user's click would have gone unanswered.
    ///
    /// Off unless FASTCULL_PERFTRACE=1.
    /// </summary>
    internal static class UiWatchdog
    {
        /// <summary>Gaps below this are ordinary scheduling noise and not worth a line.</summary>
        private const int ReportThresholdMs = 250;

        private static Thread? _thread;

        public static void Start(DispatcherQueue queue)
        {
            if (!PerfTrace.Enabled || _thread is not null) return;

            _thread = new Thread(() => Run(queue))
            {
                IsBackground = true,
                Name = "FastCull UI watchdog",
                // Above normal so the watchdog itself is never the thing starved of CPU while the
                // decode pool has every core busy - otherwise a late ping would be indistinguishable
                // from a blocked UI thread, which is the exact distinction this exists to make.
                Priority = ThreadPriority.AboveNormal,
            };

            _thread.Start();
            PerfTrace.Log("watchdog", $"started, reporting stalls over {ReportThresholdMs} ms");
        }

        private static void Run(DispatcherQueue queue)
        {
            var worst = 0L;

            while (true)
            {
                Thread.Sleep(100);

                var posted = Stopwatch.GetTimestamp();
                var ran = new ManualResetEventSlim(false);

                if (!queue.TryEnqueue(DispatcherQueuePriority.Low, () => ran.Set()))
                {
                    PerfTrace.Log("watchdog", "TryEnqueue refused - the dispatcher is shutting down");
                    return;
                }

                // Waited on with a generous ceiling rather than indefinitely, so a truly wedged UI
                // thread still produces a line every few seconds instead of silence.
                while (!ran.Wait(2_000))
                {
                    var stalledMs = (long)Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                    PerfTrace.Log("UI STALLED", $"{stalledMs} ms and still not pumping");
                    PerfTrace.Count("ui stalls over 2s");
                }

                var waitedMs = (long)Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                ran.Dispose();

                if (waitedMs >= ReportThresholdMs)
                {
                    PerfTrace.Log("ui stall", $"{waitedMs} ms");
                    PerfTrace.Count("ui stalls over 250ms");

                    if (waitedMs > worst)
                    {
                        worst = waitedMs;
                        PerfTrace.Log("ui stall WORST", $"{waitedMs} ms");
                    }
                }
            }
        }
    }
}
