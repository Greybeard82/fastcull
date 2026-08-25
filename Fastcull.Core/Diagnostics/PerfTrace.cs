using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Fastcull.Diagnostics
{
    /// <summary>
    /// A switchable trace of the decode pipeline, built for the "fast filmstrip scrolling leaves
    /// photos unloaded even after scrolling stops" report on slower hardware.
    ///
    /// The question that report raises is specifically about *settled* state: whether a request
    /// that was cancelled mid-scroll is ever re-issued for the position the user actually stopped
    /// at, and whether the bounded gate is still working through a backlog of requests for photos
    /// nobody is looking at any more. Neither is visible from a stopwatch on a single decode - it
    /// needs the whole request lifecycle recorded per item, plus a snapshot of what is outstanding
    /// once everything has gone quiet.
    ///
    /// In Core rather than beside InputTrace because the gate and the decoders live here and the
    /// WinUI project cannot be referenced from them.
    ///
    /// Off unless FASTCULL_PERFTRACE=1, so it costs one static bool test in normal use.
    /// </summary>
    public static class PerfTrace
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("FASTCULL_PERFTRACE") == "1";

        private static readonly object Gate = new();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>
        /// Counters that survive the log being read, so a snapshot can state totals rather than
        /// making the reader tally thousands of lines.
        /// </summary>
        private static readonly ConcurrentDictionary<string, int> Counters = new();

        public static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastCull", "logs", "perf-trace.log");

        public static void Count(string name) =>
            Counters.AddOrUpdate(name, 1, static (_, v) => v + 1);

        public static int Get(string name) => Counters.TryGetValue(name, out var v) ? v : 0;

        public static void Log(string stage, string detail = "")
        {
            if (!Enabled) return;

            try
            {
                var line = $"{Clock.Elapsed.TotalSeconds,9:F3}  t{Thread.CurrentThread.ManagedThreadId,-3} {stage,-24} {detail}";

                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Tracing must never break the run it is investigating.
            }
        }

        /// <summary>Writes the accumulated counters under a heading.</summary>
        public static void Snapshot(string reason, string extra = "")
        {
            if (!Enabled) return;

            var sb = new StringBuilder();
            sb.AppendLine($"--- SNAPSHOT: {reason} @ {Clock.Elapsed.TotalSeconds:F3}s ---");
            if (extra.Length > 0) sb.AppendLine("    " + extra);

            foreach (var key in new System.Collections.Generic.SortedSet<string>(Counters.Keys))
                sb.AppendLine($"    {key,-34} {Counters[key],8}");

            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    File.AppendAllText(LogPath, sb.ToString());
                }
            }
            catch
            {
            }
        }

        public static void Reset(string reason)
        {
            if (!Enabled) return;

            Counters.Clear();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.WriteAllText(LogPath,
                    $"=== perf trace: {reason} @ {DateTime.Now:HH:mm:ss.fff} ==={Environment.NewLine}"
                    + $"    ProcessorCount={Environment.ProcessorCount}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }
}
