using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Fastcull.Diagnostics
{
    /// <summary>
    /// A switchable trace of the zoom-tier decode, from request to pixels on screen.
    ///
    /// This exists because the same two symptoms - no loading indicator, and the sharp image not
    /// swapping in until zoom is exited and re-entered - have now been reported and "fixed" three
    /// times. Every previous round was diagnosed by reading code and reasoning about it, and every
    /// previous round was wrong in a way that "it builds and throws no exception" could not catch.
    ///
    /// What it records is chosen to separate the candidate mechanisms from each other rather than
    /// to describe the happy path:
    ///
    ///   - the thread each stage resumes on, because the original defect was a UI-thread-affinity
    ///     violation that surfaced as a swallowed COMException;
    ///   - whether the property setters actually run and actually change value, because a
    ///     no-op setter raises no notification and looks identical to a missing one;
    ///   - whether the BINDINGS re-read afterwards, logged from the getters, because a property
    ///     that changes and notifies but is never re-read is a third, different failure;
    ///   - which code path made each decision in RefreshZoomImage, because two paths were added
    ///     after the last fix (scale zoom and standalone fullscreen) and either could re-enter it.
    ///
    /// Off unless FASTCULL_ZOOMTRACE=1, so it costs a single static bool test in normal use and
    /// can be switched on in a shipped build the next time this resurfaces.
    /// </summary>
    internal static class ZoomTrace
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("FASTCULL_ZOOMTRACE") == "1";

        private static readonly object Gate = new();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>
        /// Milliseconds to stall the zoom decode by, from FASTCULL_ZOOMDELAYMS.
        ///
        /// The zoom decode is ~200 ms on real files, which is too short to photograph reliably and
        /// far too short to tell "the loading indicator never renders" apart from "the loading
        /// indicator renders for a sixth of a second". Stalling it turns that into an observation
        /// instead of an argument. Honoured only when tracing is on, so it cannot affect a normal
        /// run even if the variable is left set.
        /// </summary>
        public static readonly int ForcedDelayMs =
            Enabled && int.TryParse(Environment.GetEnvironmentVariable("FASTCULL_ZOOMDELAYMS"), out var ms)
                ? Math.Clamp(ms, 0, 30_000)
                : 0;

        /// <summary>
        /// Deliberately has no ConfigureAwait(false): it sits inside LoadZoomImageAsync's
        /// UI-thread-affine region, and resuming off-thread here would inject the very defect this
        /// is meant to investigate.
        /// </summary>
        public static async System.Threading.Tasks.Task StallAsync(CancellationToken cancellationToken)
        {
            if (ForcedDelayMs <= 0) return;

            Log("ARTIFICIAL STALL", $"{ForcedDelayMs} ms (FASTCULL_ZOOMDELAYMS)");
            await System.Threading.Tasks.Task.Delay(ForcedDelayMs, cancellationToken);
        }

        public static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastCull", "logs", "zoom-trace.log");

        /// <summary>
        /// Set once the UI dispatcher is known, so every line can say whether it is on the UI
        /// thread rather than leaving thread ids to be correlated by hand.
        /// </summary>
        private static Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

        public static void Bind(Microsoft.UI.Dispatching.DispatcherQueue queue) => _uiQueue = queue;

        public static void Log(string stage, string detail = "")
        {
            if (!Enabled) return;

            try
            {
                var ui = _uiQueue?.HasThreadAccess;
                var where = ui switch
                {
                    true => "UI ",
                    false => "BG ",
                    _ => "?? ",
                };

                var line = $"{Clock.ElapsedMilliseconds,8} ms  {where}t{Thread.CurrentThread.ManagedThreadId,-3} {stage,-28} {detail}";

                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Tracing must never be the thing that breaks the run it is investigating.
            }
        }

        /// <summary>Starts a fresh file, so one reproduction is not read through the last five.</summary>
        public static void Reset(string reason)
        {
            if (!Enabled) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.WriteAllText(LogPath, $"=== zoom trace: {reason} @ {DateTime.Now:HH:mm:ss.fff} ==={Environment.NewLine}");
            }
            catch
            {
            }
        }
    }
}
