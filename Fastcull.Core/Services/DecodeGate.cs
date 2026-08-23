using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fastcull.Services
{
    /// <summary>
    /// The bounded worker pool PRD 3.3 specifies: at most <c>min(6, coreCount - 2)</c> decodes in
    /// flight at once, app-wide.
    ///
    /// Before this existed, every scanned file started two decodes the moment its view-model was
    /// constructed, so opening a folder of 2,000 photos launched 4,000 decode jobs at once with
    /// nothing bounding them. The cap is what stops a folder open from becoming a thundering herd;
    /// the sliding window (PrefetchWindow) is what stops it from being asked for in the first
    /// place. Both are needed - the window decides WHAT to decode, this decides HOW MANY at once.
    ///
    /// Deliberately a plain SemaphoreSlim rather than a custom scheduler. The work is already
    /// async and already cancellable; a queue with priorities would be a second scheduler fighting
    /// the thread pool for no measured benefit.
    /// </summary>
    public static class DecodeGate
    {
        /// <summary>
        /// PRD 3.3's <c>min(6, coreCount - 2)</c>, floored at 1 so a dual-core machine still runs.
        /// The "- 2" leaves headroom for the UI thread and the scan's own parallel metadata pass.
        /// </summary>
        public static int MaxConcurrency { get; } =
            Math.Max(1, Math.Min(6, Environment.ProcessorCount - 2));

        private static readonly SemaphoreSlim Gate = new(MaxConcurrency, MaxConcurrency);

        /// <summary>Decodes currently holding a slot. Exposed for tests and the perf harness.</summary>
        public static int InFlight => MaxConcurrency - Gate.CurrentCount;

        /// <summary>
        /// Runs one decode under the cap. Cancellation is honoured while WAITING for a slot - a
        /// photo that leaves the prefetch window before its turn comes up never decodes at all,
        /// which is the whole point of cancelling on window exit.
        /// </summary>
        public static async Task<T?> RunAsync<T>(Func<Task<T?>> decode, CancellationToken cancellationToken)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await decode().ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
