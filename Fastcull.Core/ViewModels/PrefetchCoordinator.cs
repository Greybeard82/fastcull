using System;
using System.Collections.Generic;

namespace Fastcull.ViewModels
{
    /// <summary>
    /// Decides which photos are decoded and which are dropped, per PRD 3.3.
    ///
    /// Three rules, in order of authority:
    ///
    ///   1. **Pinned wins.** A photo currently on stage is never cancelled and never evicted.
    ///      Dropping one blanks a photo the user is looking at - a visible bug, not a saving.
    ///      This matters more than it did when PRD 3.3 was written: the stage now shows a
    ///      variable 3-9 photos (followup6), so at nine it spans +/-4 and is actually WIDER than
    ///      the -2 lookbehind. The keep-set is therefore the window UNION the pinned set, not the
    ///      window alone, or navigating with a wide stage would cancel loads it had just started.
    ///   2. **Inside the window loads.** Everything in the sliding range gets BeginLoad.
    ///   3. **Everything else is fair game.** Outside both, in-flight loads are cancelled, and
    ///      residents are evicted furthest-from-cursor-first once the ceiling is crossed.
    ///
    /// Deliberately free of WinUI so it can be tested against fakes - FilmstripItemViewModel's
    /// project cannot be referenced from a test project.
    /// </summary>
    public sealed class PrefetchCoordinator
    {
        /// <summary>PRD 3.3's hard ceiling on resident decoded pixels.</summary>
        public const long DefaultCeilingBytes = 3L * 1024 * 1024 * 1024;

        private readonly PrefetchWindow _window = new();
        private readonly long _ceilingBytes;

        public PrefetchCoordinator(long ceilingBytes = DefaultCeilingBytes)
            => _ceilingBytes = ceilingBytes > 0 ? ceilingBytes : DefaultCeilingBytes;

        public PrefetchRange CurrentRange => _window.Current;
        public bool IsReversed => _window.IsReversed;

        /// <summary>Bytes held by resident items at the last <see cref="OnCursorMoved"/>.</summary>
        public long ResidentBytes { get; private set; }

        /// <summary>How many items the last pass evicted. Exposed for tests and the harness.</summary>
        public int LastEvictionCount { get; private set; }

        /// <summary>Forgets direction history and the window. Call on folder change.</summary>
        public void Reset() => _window.Reset();

        /// <summary>
        /// Applies all three rules for a new cursor position. Cheap enough to call on every
        /// navigation - it is a scan of the item list, not a decode.
        /// </summary>
        public void OnCursorMoved(int activeIndex, IReadOnlyList<ICacheableItem> items)
        {
            if (items is null || items.Count == 0)
            {
                ResidentBytes = 0;
                LastEvictionCount = 0;
                return;
            }

            var range = _window.Advance(activeIndex, items.Count);

            foreach (var item in items)
            {
                var keep = range.Contains(item.Index) || item.IsPinned;

                if (keep) item.BeginLoad();
                else item.CancelLoad();
            }

            EvictToCeiling(activeIndex, items);
        }

        /// <summary>
        /// Drops residents furthest from the cursor until the total is back under the ceiling.
        ///
        /// Pinned and in-window items are never candidates, so the ceiling can in principle be
        /// exceeded by them alone - that is correct behaviour rather than a bug: those photos are
        /// on screen or about to be, and the alternative is blanking them. In practice the window
        /// plus a nine-photo stage tops out around 17 items, well inside the ceiling.
        /// </summary>
        public int EvictToCeiling(int activeIndex, IReadOnlyList<ICacheableItem> items)
        {
            LastEvictionCount = 0;

            var total = 0L;
            foreach (var item in items)
                if (item.IsResident) total += item.ResidentBytes;

            ResidentBytes = total;

            // Deliberately two passes. This runs on every navigation against PRD 3.5's 16 ms
            // keypress budget, and the overwhelming majority of steps are nowhere near the
            // ceiling - allocating and sorting a candidate list for all of them was measured at
            // 11.4 ms per step. Under the ceiling, the cheap first pass is the whole cost.
            if (total <= _ceilingBytes) return 0;

            List<ICacheableItem>? candidates = null;
            foreach (var item in items)
            {
                if (!item.IsResident || item.IsPinned) continue;
                if (item.EvictableBytes <= 0) continue;          // nothing to give back
                if (_window.Current.Contains(item.Index)) continue;

                (candidates ??= new List<ICacheableItem>()).Add(item);
            }

            if (candidates is null) return 0;

            // Furthest from the cursor goes first: it is the least likely to be needed next.
            candidates.Sort((a, b) =>
            {
                var byDistance = Math.Abs(b.Index - activeIndex).CompareTo(Math.Abs(a.Index - activeIndex));
                return byDistance != 0 ? byDistance : a.Index.CompareTo(b.Index);
            });

            foreach (var victim in candidates)
            {
                if (total <= _ceilingBytes) break;

                // Credit only what the item actually gave back, measured rather than assumed.
                // EvictableBytes already filtered the no-ops out of the candidate list; this is
                // the guard against an implementation whose estimate and whose Evict disagree.
                var before = victim.ResidentBytes;
                victim.Evict();
                var freed = before - victim.ResidentBytes;
                if (freed <= 0) continue;

                total -= freed;
                LastEvictionCount++;
            }

            ResidentBytes = total;
            return LastEvictionCount;
        }
    }
}
