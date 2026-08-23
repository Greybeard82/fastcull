using System;

namespace Fastcull.ViewModels
{
    /// <summary>An inclusive range of item indices. Empty when <see cref="EndInclusive"/> precedes <see cref="Start"/>.</summary>
    public readonly record struct PrefetchRange(int Start, int EndInclusive)
    {
        public static readonly PrefetchRange Empty = new(0, -1);

        public bool IsEmpty => EndInclusive < Start;
        public int Count => IsEmpty ? 0 : EndInclusive - Start + 1;
        public bool Contains(int index) => index >= Start && index <= EndInclusive;
    }

    /// <summary>
    /// PRD 3.3's sliding prefetch window: five photos ahead of the cursor, two behind, reversing
    /// when the user is consistently paging backwards.
    ///
    /// The asymmetry is the point - culling runs forward far more often than backwards, so the
    /// deeper side should be whichever way the user is actually going. "Predict, do not react"
    /// (PRD 0) only works if the prediction follows the user.
    /// </summary>
    public sealed class PrefetchWindow
    {
        /// <summary>Photos kept decoded on the leading side of the cursor.</summary>
        public const int Lookahead = 5;

        /// <summary>Photos kept decoded on the trailing side.</summary>
        public const int Lookbehind = 2;

        /// <summary>Consecutive moves in one direction before the window flips to favour it.</summary>
        public const int ReversalThreshold = 3;

        private int _lastIndex = -1;
        private int _backwardRun;
        private int _forwardRun;

        /// <summary>True when the deeper lookahead has been swapped to the trailing side.</summary>
        public bool IsReversed { get; private set; }

        /// <summary>The most recently computed range.</summary>
        public PrefetchRange Current { get; private set; } = PrefetchRange.Empty;

        /// <summary>
        /// Records a cursor move and returns the range that should be resident.
        ///
        /// Reversal is symmetric: three consecutive moves in either direction set the orientation.
        /// PRD 3.3 only specifies the backwards case, but making a single forward step flip the
        /// window straight back would make it thrash on any jitter - a user stepping back three
        /// and forward one is still going backwards. The symmetry is a deliberate reading of the
        /// rule rather than a literal one; see the run report.
        /// </summary>
        public PrefetchRange Advance(int activeIndex, int itemCount)
        {
            if (itemCount <= 0 || activeIndex < 0)
            {
                Reset();
                return Current;
            }

            activeIndex = Math.Clamp(activeIndex, 0, itemCount - 1);

            if (_lastIndex >= 0 && activeIndex != _lastIndex)
            {
                if (activeIndex < _lastIndex)
                {
                    _backwardRun++;
                    _forwardRun = 0;
                    if (_backwardRun >= ReversalThreshold) IsReversed = true;
                }
                else
                {
                    _forwardRun++;
                    _backwardRun = 0;
                    if (_forwardRun >= ReversalThreshold) IsReversed = false;
                }
            }

            _lastIndex = activeIndex;

            var ahead = IsReversed ? Lookbehind : Lookahead;
            var behind = IsReversed ? Lookahead : Lookbehind;

            Current = new PrefetchRange(
                Math.Max(0, activeIndex - behind),
                Math.Min(itemCount - 1, activeIndex + ahead));

            return Current;
        }

        /// <summary>Forgets direction history - used when the folder changes.</summary>
        public void Reset()
        {
            _lastIndex = -1;
            _backwardRun = 0;
            _forwardRun = 0;
            IsReversed = false;
            Current = PrefetchRange.Empty;
        }
    }
}
