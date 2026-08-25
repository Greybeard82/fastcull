using System;
using System.Collections.Generic;
using System.Linq;
using Fastcull.ViewModels;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// PRD 3.3's window and ceiling under PRD 1.2's streaming scan.
///
/// Streaming changed an assumption the prefetch system was written against: the sequence used to
/// be built once and then only shrink (a delete). Now it grows while the user is navigating, and
/// every insert before an item shifts that item's <see cref="ICacheableItem.Index"/> - which is
/// the value <c>PrefetchRange.Contains</c> and the furthest-from-cursor eviction sort both key on.
///
/// These tests drive that interaction directly, because it cannot be reached from the app's own
/// tests: FilmstripItemViewModel lives in the WinUI project, which a test project cannot
/// reference. That is what ICacheableItem is for.
/// </summary>
public class StreamingPrefetchTests
{
    /// <summary>As PrefetchTests' fake, but with a settable Index - streaming reindexes.</summary>
    private sealed class StreamedItem : ICacheableItem
    {
        public StreamedItem(int sortKey) => SortKey = sortKey;

        private bool _displayLoaded;

        /// <summary>Stable identity, unlike Index. Capture order stands in for capture time.</summary>
        public int SortKey { get; }

        public int Index { get; set; }
        public bool IsPinned { get; set; }

        public bool IsResident
        {
            get => _displayLoaded;
            set => _displayLoaded = value;
        }

        public long ResidentBytes => _displayLoaded ? 2_500_000 : 0;
        public long EvictableBytes => IsPinned || !_displayLoaded ? 0 : 2_500_000;

        public bool Wanted { get; private set; }

        public void BeginLoad() { Wanted = true; _displayLoaded = true; }
        public void CancelLoad() { Wanted = false; }
        public void Evict() { if (!IsPinned) _displayLoaded = false; }
    }

    /// <summary>Inserts in sort order and reindexes, exactly as MainViewModel.MergeBatch does.</summary>
    private static int Insert(List<StreamedItem> items, StreamedItem item)
    {
        var at = SequenceMerge.FindInsertionPoint(
            items, item, Comparer<StreamedItem>.Create((a, b) => a.SortKey.CompareTo(b.SortKey)));

        items.Insert(at, item);
        for (var i = at; i < items.Count; i++) items[i].Index = i;
        return at;
    }

    [Fact]
    public void TheKeepSetStaysCorrectWhileTheSequenceGrows()
    {
        // The invariant PRD 3.3 rests on: after any cursor pass, everything inside the window is
        // loaded and everything outside it - unpinned - is not wanted. Streaming must not be able
        // to leave an item inside the window unloaded, which is the "photo never appears" bug.
        var rng = new Random(4242);
        var coordinator = new PrefetchCoordinator();
        var items = new List<StreamedItem>();

        // Arrivals out of capture order, as a real scan delivers them.
        foreach (var key in Enumerable.Range(0, 400).OrderBy(_ => rng.Next()))
        {
            Insert(items, new StreamedItem(key));

            var cursor = Math.Min(items.Count - 1, rng.Next(items.Count));
            coordinator.OnCursorMoved(cursor, items);

            var range = coordinator.CurrentRange;

            foreach (var item in items)
            {
                if (range.Contains(item.Index))
                    Assert.True(item.Wanted, $"index {item.Index} is inside {range.Start}-{range.EndInclusive} but not loaded");
                else if (!item.IsPinned)
                    Assert.False(item.Wanted, $"index {item.Index} is outside {range.Start}-{range.EndInclusive} but still wanted");
            }
        }
    }

    [Fact]
    public void IndicesStayContiguousAndUniqueAcrossStreamedInserts()
    {
        // Eviction sorts by distance from the cursor, and the window is a range test. Both are
        // nonsense if two items ever claim the same index, or if a gap appears.
        var rng = new Random(99);
        var items = new List<StreamedItem>();

        foreach (var key in Enumerable.Range(0, 500).OrderBy(_ => rng.Next()))
        {
            Insert(items, new StreamedItem(key));

            for (var i = 0; i < items.Count; i++)
                Assert.Equal(i, items[i].Index);
        }
    }

    [Fact]
    public void TheCeilingStillHoldsWhenTheSequenceGrowsUnderTheCursor()
    {
        // A small ceiling so eviction is forced repeatedly: 20 items' worth against 600 arrivals.
        const long ceiling = 20 * 2_500_000L;

        var rng = new Random(2026);
        var coordinator = new PrefetchCoordinator(ceiling);
        var items = new List<StreamedItem>();

        foreach (var key in Enumerable.Range(0, 600).OrderBy(_ => rng.Next()))
        {
            Insert(items, new StreamedItem(key));
            coordinator.OnCursorMoved(rng.Next(items.Count), items);

            // The window and the pinned set are exempt by design (PRD 3.3), so the bound is the
            // ceiling plus what those can legitimately hold - not the ceiling alone.
            var exempt = coordinator.CurrentRange.Count * 2_500_000L;
            Assert.True(coordinator.ResidentBytes <= ceiling + exempt,
                $"resident {coordinator.ResidentBytes} exceeded {ceiling} + {exempt} at {items.Count} items");
        }
    }

    [Fact]
    public void NothingIsLeftWantedButUnloadedAfterAStreamedBurst()
    {
        // Cancellation safety, which is what "rapid navigation while the folder is still loading"
        // actually stresses: a burst of inserts interleaved with cursor moves, then a settle. The
        // stranded condition is an item the window wants that nothing is loading.
        var rng = new Random(31337);
        var coordinator = new PrefetchCoordinator();
        var items = new List<StreamedItem>();

        for (var batch = 0; batch < 40; batch++)
        {
            foreach (var key in Enumerable.Range(batch * 25, 25).OrderBy(_ => rng.Next()))
                Insert(items, new StreamedItem(key));

            // Several cursor moves per batch, as a user culling during the load would produce.
            for (var move = 0; move < 5; move++)
                coordinator.OnCursorMoved(rng.Next(items.Count), items);
        }

        var settled = items.Count / 2;
        coordinator.OnCursorMoved(settled, items);

        var range = coordinator.CurrentRange;
        var stranded = items.Count(i => range.Contains(i.Index) && !i.Wanted);

        Assert.Equal(0, stranded);
    }
}
