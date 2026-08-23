using System;
using System.Collections.Generic;

namespace Fastcull.Models
{
    /// <summary>
    /// A count of where a sequence of photos sits on the cull ladder: how many picked, rejected
    /// and still untouched, plus the star histogram.
    ///
    /// Pure and WinUI-free so the arithmetic can be tested headlessly - the sidebar that displays
    /// it lives in the WinUI project, which no test project can reference.
    ///
    /// Recomputed wholesale rather than updated incrementally. A 2,000-item sweep costs about
    /// 10 microseconds against PRD 3.5's 16 ms keypress budget, which buys the guarantee that the
    /// displayed tally can never drift out of step with the sequence it describes. Incremental
    /// deltas would be faster in a way nobody could perceive and wrong in ways they eventually
    /// would.
    /// </summary>
    public sealed class CullTally
    {
        /// <summary>Stars run 1-5; index 0 is unused so the array reads as its own label.</summary>
        private readonly int[] _stars = new int[6];

        public static readonly CullTally Empty = new();

        private CullTally() { }

        /// <summary>Every photo in the sequence, rated or not.</summary>
        public int Total { get; private set; }

        /// <summary>Flag.Picked, at any star count including zero.</summary>
        public int Picked { get; private set; }

        public int Rejected { get; private set; }

        /// <summary>Still untouched - what is left to decide.</summary>
        public int Unflagged { get; private set; }

        /// <summary>How many photos carry exactly this many stars. Out-of-range returns 0.</summary>
        public int StarCount(int stars) => stars is >= 1 and <= 5 ? _stars[stars] : 0;

        /// <summary>
        /// The largest bar in the histogram, for scaling it. Zero when nothing is starred, which
        /// callers must treat as "draw no bars" rather than dividing by it.
        /// </summary>
        public int MaxStarCount
        {
            get
            {
                var max = 0;
                for (var i = 1; i <= 5; i++) if (_stars[i] > max) max = _stars[i];
                return max;
            }
        }

        /// <summary>Picked plus rejected: how far through the folder the cull has actually got.</summary>
        public int Decided => Picked + Rejected;

        public static CullTally From(IEnumerable<CullState> states)
        {
            var tally = new CullTally();
            if (states is null) return tally;

            foreach (var state in states)
            {
                tally.Total++;

                switch (state.Flag)
                {
                    case Flag.Picked:
                        tally.Picked++;
                        // A picked photo with no stars is still picked; only 1-5 land in the
                        // histogram, so the two counts deliberately do not sum to each other.
                        if (state.Stars is >= 1 and <= 5) tally._stars[state.Stars]++;
                        break;

                    case Flag.Rejected:
                        tally.Rejected++;
                        break;

                    default:
                        tally.Unflagged++;
                        break;
                }
            }

            return tally;
        }
    }
}
