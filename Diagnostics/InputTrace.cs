using System;
using System.IO;
using System.Threading;

namespace Fastcull.Diagnostics
{
    /// <summary>
    /// A switchable trace of the keyboard path, from keystroke to command to outcome.
    ///
    /// Built for the "Delete does nothing" report. The interesting question there is *where* the
    /// keystroke stops, and there are four candidates that look identical from the outside: the
    /// key never reaching the handler, the focus guard swallowing it, the modal guard swallowing
    /// it, or the command running and the file operation failing silently. Only a log that records
    /// each stage separately can tell them apart.
    ///
    /// Off unless FASTCULL_INPUTTRACE=1, so it costs one static bool test in normal use.
    /// </summary>
    internal static class InputTrace
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("FASTCULL_INPUTTRACE") == "1";

        private static readonly object Gate = new();

        public static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastCull", "logs", "input-trace.log");

        public static void Log(string stage, string detail = "")
        {
            if (!Enabled) return;

            try
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff}  t{Thread.CurrentThread.ManagedThreadId,-3} {stage,-26} {detail}";

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

        public static void Reset(string reason)
        {
            if (!Enabled) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.WriteAllText(LogPath, $"=== input trace: {reason} @ {DateTime.Now:HH:mm:ss.fff} ==={Environment.NewLine}");
            }
            catch
            {
            }
        }
    }
}
