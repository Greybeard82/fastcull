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
    /// <summary>
    /// What a decode is for, which decides whether it may be made to wait.
    /// </summary>
    public enum DecodePriority
    {
        /// <summary>
        /// A photo somebody is looking at or about to look at - the display tier and the zoom tier.
        /// These are what the app is judged on.
        /// </summary>
        Interactive,

        /// <summary>
        /// Filmstrip thumbnails. There are as many of these as there are photos in the folder, and
        /// nobody is waiting on any particular one, so they yield to Interactive work.
        /// </summary>
        Background,
    }

    public static class DecodeGate
    {
        /// <summary>
        /// Total decodes in flight, app-wide.
        ///
        /// **Retuned once decodes actually began running in parallel.** The old value was
        /// <c>min(6, coreCount - 2)</c>, chosen when every decode went through a
        /// FileRandomAccessStream and WIC serialised them process-wide - measured, throughput was
        /// flat at ~13/sec from 1 thread to 12 with cores-busy pinned at 1.00, so the cap was a
        /// queue in front of a single worker and its value could not matter. It does now: with the
        /// decode reading from memory, throughput scales to roughly 7-8 cores.
        ///
        /// Still "- 2": the UI thread and the scan's parallel metadata pass both need room, and
        /// PRD 0's prime directive is that the app feels instant, not that it finishes a backlog
        /// half a second sooner.
        ///
        /// **The ceiling of 8 is measured, not picked.** Past it the workload oversubscribes:
        /// across 48 thumbnail decodes the throughput gain from 6 to 12 threads was 5%, while
        /// cores-busy FELL from 3.67 to 2.46 - more threads contending for the same work, finishing
        /// no faster. Eight also keeps the cap sane on a high-core machine, where <c>cores - 2</c>
        /// alone would put 14 decodes in flight on a CPU with 8 physical cores.
        /// </summary>
        public static int MaxConcurrency { get; } =
            Math.Clamp(Environment.ProcessorCount - 2, 2, 8);

        /// <summary>
        /// The most slots background work may hold at once.
        ///
        /// This is the whole of the priority mechanism, and it is a cap rather than a queue on
        /// purpose. A background decode takes the background permit BEFORE the shared one, so at
        /// most this many of them are ever queued on the shared gate - which bounds how long an
        /// Interactive decode can be stuck behind them, no matter how many thousand thumbnails the
        /// filmstrip has asked for. Measured before this existed: scrolling hard put the display
        /// tier's p95 wait at 2.8 s, entirely behind thumbnails.
        ///
        /// It is not zero-sum with throughput: when no Interactive work exists, background still
        /// gets this many slots running, and when background is idle Interactive gets all of them.
        /// </summary>
        public static int MaxBackgroundConcurrency { get; } =
            Math.Max(1, MaxConcurrency / 2);

        private static readonly SemaphoreSlim Gate = new(MaxConcurrency, MaxConcurrency);

        /// <summary>Taken before <see cref="Gate"/>, and only by background work.</summary>
        private static readonly SemaphoreSlim BackgroundGate =
            new(MaxBackgroundConcurrency, MaxBackgroundConcurrency);

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
            => RunAsync(decode, cancellationToken, DecodePriority.Interactive, label: null);

        /// <param name="priority">
        /// Background work takes a second, smaller permit first, so it can never occupy more than
        /// <see cref="MaxBackgroundConcurrency"/> of the shared slots and can never queue more than
        /// that many requests ahead of an Interactive one.
        /// </param>
        /// <param name="label">
        /// Only used by <see cref="Diagnostics.PerfTrace"/>, and only when it is switched on.
        /// </param>
        public static async Task<T?> RunAsync<T>(Func<Task<T?>> decode, CancellationToken cancellationToken,
                                                 DecodePriority priority, string? label)
        {
            Interlocked.Increment(ref _waiting);

            var queuedAt = Diagnostics.PerfTrace.Enabled ? Stopwatch.GetTimestamp() : 0L;
            var admitted = false;
            var background = priority == DecodePriority.Background;
            var holdsBackground = false;

            try
            {
                // Order is load-bearing: the background permit is taken BEFORE the shared one, so
                // surplus background work waits here instead of piling up in front of interactive
                // work on the shared gate. Reversing these two lines would silently undo the
                // priority while leaving every count and every test looking identical.
                if (background)
                {
                    await BackgroundGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    holdsBackground = true;
                }

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
                if (holdsBackground) BackgroundGate.Release();
            }
        }
    }
}
