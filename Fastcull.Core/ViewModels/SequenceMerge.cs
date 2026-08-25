using System;
using System.Collections.Generic;

namespace Fastcull.ViewModels
{
    /// <summary>
    /// Where a streamed photo belongs in an already-sorted sequence (PRD 1.2 / 1.3).
    ///
    /// The scan yields in filesystem and parallel-completion order; PRD 1.3 requires capture order.
    /// Appending as photos arrive and sorting once at the end would reshuffle the filmstrip under
    /// a cursor the user is already culling with, which is a worse experience than waiting - so
    /// each arrival goes to its correct position immediately and the sequence is never wrong.
    ///
    /// Free of WinUI so the ordering can be tested headlessly, which matters because this decides
    /// what order somebody's photographs are shown in.
    /// </summary>
    public static class SequenceMerge
    {
        /// <summary>
        /// The index a photo with this key belongs at, given an already-sorted sequence.
        ///
        /// Ties resolve to the position AFTER equal entries, so a batch of identically-timed
        /// photos keeps its own relative order rather than reversing.
        /// </summary>
        public static int FindInsertionPoint<T>(IReadOnlyList<T> sorted, T candidate, IComparer<T> comparer)
        {
            ArgumentNullException.ThrowIfNull(sorted);
            ArgumentNullException.ThrowIfNull(comparer);

            // Nearly every arrival belongs at the end: files are normally enumerated in name order
            // and name order normally tracks capture order. Checking that first turns the common
            // case into one comparison instead of log n, and - more importantly - means a folder
            // that is already in order costs no reindexing at all.
            if (sorted.Count == 0) return 0;
            if (comparer.Compare(candidate, sorted[sorted.Count - 1]) >= 0) return sorted.Count;

            var low = 0;
            var high = sorted.Count;

            while (low < high)
            {
                var mid = low + ((high - low) / 2);

                if (comparer.Compare(candidate, sorted[mid]) >= 0) low = mid + 1;
                else high = mid;
            }

            return low;
        }
    }
}
