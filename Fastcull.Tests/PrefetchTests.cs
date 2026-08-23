using System;
using System.Collections.Generic;
using System.Linq;
using Fastcull.ViewModels;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// PRD 3.3's sliding window, direction reversal, and LRU eviction.
///
/// These run against a fake item rather than the real view-model, which lives in the WinUI
/// project and cannot be referenced from a test project. That is exactly why ICacheableItem
/// exists - without the seam, none of this logic would be verifiable at all.
/// </summary>
public class PrefetchTests
{
    private sealed class FakeItem : ICacheableItem
    {
        public FakeItem(int index) => Index = index;

        public int Index { get; }
        public bool IsPinned { get; set; }
        public bool IsResident { get; set; }
        public long ResidentBytes { get; set; } = 2_500_000;   // followup3's ~2.5 MB per photo

        public int LoadCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int EvictCalls { get; private set; }

        public void BeginLoad() { LoadCalls++; IsResident = true; }
        public void CancelLoad() => CancelCalls++;
        public void Evict() { EvictCalls++; IsResident = false; }
    }

    private static List<FakeItem> Items(int count) =>
        Enumerable.Range(0, count).Select(i => new FakeItem(i)).ToList();

    // ---- Window shape ----

    [Fact]
    public void Window_IsFiveAheadAndTwoBehind()
    {
        var w = new PrefetchWindow();
        var r = w.Advance(activeIndex: 50, itemCount: 200);

        Assert.Equal(48, r.Start);
        Assert.Equal(55, r.EndInclusive);
        Assert.Equal(8, r.Count);
    }

    [Fact]
    public void Window_ClampsAtBothEndsOfTheSequence()
    {
        var w = new PrefetchWindow();

        var atStart = w.Advance(0, 200);
        Assert.Equal(0, atStart.Start);
        Assert.Equal(5, atStart.EndInclusive);

        w.Reset();
        var atEnd = w.Advance(199, 200);
        Assert.Equal(197, atEnd.Start);
        Assert.Equal(199, atEnd.EndInclusive);
    }

    [Fact]
    public void Window_IsEmptyForAnEmptySequence()
    {
        var w = new PrefetchWindow();
        Assert.True(w.Advance(0, 0).IsEmpty);
    }

    // ---- Direction reversal ----

    [Fact]
    public void ThreeConsecutiveBackwardMoves_SwapTheDeepSideToTrailing()
    {
        var w = new PrefetchWindow();
        w.Advance(50, 200);

        w.Advance(49, 200);
        Assert.False(w.IsReversed);
        w.Advance(48, 200);
        Assert.False(w.IsReversed);

        var r = w.Advance(47, 200);
        Assert.True(w.IsReversed);

        // Deep side is now behind the cursor: five trailing, two leading.
        Assert.Equal(42, r.Start);
        Assert.Equal(49, r.EndInclusive);
    }

    [Fact]
    public void ASingleForwardStepDoesNotUndoAReversal()
    {
        // Stepping back three and forward one is still going backwards. Flipping on the first
        // forward move would make the window thrash on ordinary jitter.
        var w = new PrefetchWindow();
        w.Advance(50, 200);
        w.Advance(49, 200); w.Advance(48, 200); w.Advance(47, 200);
        Assert.True(w.IsReversed);

        w.Advance(48, 200);
        Assert.True(w.IsReversed);
        w.Advance(49, 200);
        Assert.True(w.IsReversed);

        w.Advance(50, 200);
        Assert.False(w.IsReversed);
    }

    [Fact]
    public void RepeatingTheSameIndexDoesNotCountAsAMove()
    {
        var w = new PrefetchWindow();
        w.Advance(50, 200);
        for (var i = 0; i < 10; i++) w.Advance(50, 200);

        Assert.False(w.IsReversed);
    }

    // ---- Coordinator: load and cancel ----

    [Fact]
    public void OnlyItemsInsideTheWindowAreLoaded()
    {
        var items = Items(200);
        new PrefetchCoordinator().OnCursorMoved(50, items);

        foreach (var item in items)
        {
            var expected = item.Index >= 48 && item.Index <= 55;
            Assert.Equal(expected, item.LoadCalls > 0);
        }
    }

    [Fact]
    public void ConstructingItemsLoadsNothingUntilTheCursorMoves()
    {
        // The defect Task A fixed: construction used to imply decoding, so a 2,000-photo folder
        // launched 4,000 decodes at once.
        var items = Items(2000);
        Assert.All(items, i => Assert.Equal(0, i.LoadCalls));
    }

    [Fact]
    public void ItemsLeavingTheWindowAreCancelled()
    {
        var items = Items(200);
        var c = new PrefetchCoordinator();

        c.OnCursorMoved(50, items);
        c.OnCursorMoved(120, items);

        Assert.True(items[50].CancelCalls > 0, "a photo far behind the cursor should have been cancelled");
        Assert.True(items[120].LoadCalls > 0);
    }

    [Fact]
    public void PinnedItemsAreLoadedAndNeverCancelled_EvenFarOutsideTheWindow()
    {
        // A nine-photo stage spans +/-4, wider than the -2 lookbehind, so the stage and the
        // window genuinely disagree. Pinned has to win or navigation would cancel loads it had
        // just started.
        var items = Items(200);
        items[10].IsPinned = true;

        var c = new PrefetchCoordinator();
        c.OnCursorMoved(100, items);

        Assert.True(items[10].LoadCalls > 0);
        Assert.Equal(0, items[10].CancelCalls);
    }

    // ---- Coordinator: eviction ----

    [Fact]
    public void EvictionDropsFurthestFromTheCursorFirst()
    {
        var items = Items(500);
        foreach (var i in items) i.IsResident = true;

        // 500 x 2.5 MB = 1.25 GB; a 100 MB ceiling forces heavy eviction.
        var c = new PrefetchCoordinator(ceilingBytes: 100L * 1024 * 1024);
        c.OnCursorMoved(250, items);

        var survivors = items.Where(i => i.IsResident).Select(i => i.Index).ToList();
        var evicted = items.Where(i => !i.IsResident).Select(i => i.Index).ToList();

        Assert.NotEmpty(evicted);

        // Everything still resident must be closer to the cursor than anything evicted.
        var furthestSurvivor = survivors.Max(i => Math.Abs(i - 250));
        var nearestEvicted = evicted.Min(i => Math.Abs(i - 250));
        Assert.True(nearestEvicted >= furthestSurvivor,
            $"evicted an item at distance {nearestEvicted} while keeping one at {furthestSurvivor}");
    }

    [Fact]
    public void EvictionNeverTouchesPinnedOrWindowedItems()
    {
        var items = Items(500);
        foreach (var i in items) i.IsResident = true;
        items[0].IsPinned = true;      // pinned and about as far from the cursor as possible
        items[499].IsPinned = true;

        var c = new PrefetchCoordinator(ceilingBytes: 10L * 1024 * 1024);
        c.OnCursorMoved(250, items);

        Assert.True(items[0].IsResident, "a pinned item was evicted");
        Assert.True(items[499].IsResident, "a pinned item was evicted");
        Assert.Equal(0, items[0].EvictCalls);

        for (var i = 248; i <= 255; i++)
            Assert.True(items[i].IsResident, $"windowed item {i} was evicted");
    }

    [Fact]
    public void EvictionStopsAsSoonAsItIsUnderTheCeiling()
    {
        var items = Items(500);
        foreach (var i in items) i.IsResident = true;

        // Room for 200 items; expect roughly 300 evicted, not all 500.
        var c = new PrefetchCoordinator(ceilingBytes: 200L * 2_500_000);
        c.OnCursorMoved(250, items);

        Assert.True(c.ResidentBytes <= 200L * 2_500_000);
        Assert.InRange(items.Count(i => i.IsResident), 190, 210);
    }

    [Fact]
    public void NothingIsEvictedWhileUnderTheCeiling()
    {
        var items = Items(20);
        foreach (var i in items) i.IsResident = true;

        var c = new PrefetchCoordinator();   // 3 GB against 20 x 2.5 MB
        c.OnCursorMoved(10, items);

        Assert.Equal(0, c.LastEvictionCount);
        Assert.All(items, i => Assert.True(i.IsResident));
    }

    [Fact]
    public void ResidentBytesTracksWhatIsActuallyHeld()
    {
        var items = Items(10);
        foreach (var i in items) i.IsResident = true;

        var c = new PrefetchCoordinator();
        c.OnCursorMoved(5, items);

        Assert.Equal(10 * 2_500_000L, c.ResidentBytes);
    }

    [Fact]
    public void CoordinatorHandlesAnEmptySequenceWithoutThrowing()
    {
        var c = new PrefetchCoordinator();
        c.OnCursorMoved(0, new List<ICacheableItem>());
        Assert.Equal(0, c.ResidentBytes);
    }
}
