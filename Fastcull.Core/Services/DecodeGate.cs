using System;
using System.Diagnostics;
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

        private static int _waiting;

        /// <summary>
        /// Requests admitted but not yet holding a slot. This is the number the "scrolling leaves
        /// photos unloaded" report turns on: if it is still large once the user has stopped
        /// scrolling, the gate is working through a backlog for photos nobody is looking at, and
        /// the visible ones are queued behind it.
        /// </summary>
        public static int Waiting => Volatile.Read(ref _waiting);

        /// <summary>
        /// Runs one decode under the cap. Cancellation is honoured while WAITING for a slot - a
        /// photo that leaves the prefetch window before its turn comes up never decodes at all,
        /// which is the whole point of cancelling on window exit.
        /// </summary>
        public static Task<T?> RunAsync<T>(Func<Task<T?>> decode, CancellationToken cancellationToken)
            => RunAsync(decode, cancellationToken, label: null);

        /// <param name="label">
        /// Only used by <see cref="Diagnostics.PerfTrace"/>, and only when it is switched on.
        /// </param>
        public static async Task<T?> RunAsync<T>(Func<Task<T?>> decode, CancellationToken cancellationToken,
                                                 string? label)
        {
            Interlocked.Increment(ref _waiting);

            var queuedAt = Diagnostics.PerfTrace.Enabled ? Stopwatch.GetTimestamp() : 0L;
            var admitted = false;

            try
            {
                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                admitted = true;
                Interlocked.Decrement(ref _waiting);

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!Diagnostics.PerfTrace.Enabled) return await decode().ConfigureAwait(false);

                    var waitMs = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
                    var startedAt = Stopwatch.GetTimestamp();

                    var result = await decode().ConfigureAwait(false);

                    var decodeMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                    Diagnostics.PerfTrace.Log("decode done",
                        $"{label} wait={waitMs:F0}ms decode={decodeMs:F0}ms waiting={Waiting} inflight={InFlight}");
                    Diagnostics.PerfTrace.Count("decodes completed");

                    return result;
                }
                finally
                {
                    Gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                Diagnostics.PerfTrace.Count("decodes cancelled at gate");
                throw;
            }
            finally
            {
                // Only if the wait itself threw - the admitted path already decremented, and
                // decrementing twice would make Waiting drift negative over a session.
                if (!admitted) Interlocked.Decrement(ref _waiting);
            }
        }
    }
}
