using System.Linq;
using Fastcull.ViewModels;
using Xunit;

namespace Fastcull.Tests;

/// <summary>Every row of the work order's Task E.2 table, plus the degenerate counts.</summary>
public class FilmstripWindowTests
{
    [Theory]
    // count, activeIndex, expectedWindowStart, expectedActiveSlot
    [InlineData(10, 0, 0, 0)]   // first photo   -> marker on the LEFT slot
    [InlineData(10, 1, 0, 1)]   // centre
    [InlineData(10, 5, 4, 1)]   // centre
    [InlineData(10, 8, 7, 1)]   // centre
    [InlineData(10, 9, 7, 2)]   // last photo    -> marker on the RIGHT slot
    [InlineData(3, 0, 0, 0)]
    [InlineData(3, 2, 0, 2)]
    [InlineData(2, 0, 0, 0)]
    [InlineData(2, 1, 0, 1)]
    [InlineData(1, 0, 0, 0)]
    public void Compute_MatchesSpecTable(int count, int activeIndex, int expectedStart, int expectedSlot)
    {
        var result = FilmstripWindow.Compute(activeIndex, count);
        Assert.Equal(expectedStart, result.WindowStart);
        Assert.Equal(expectedSlot, result.ActiveSlot);
    }

    [Fact]
    public void Compute_EmptySequence_ReturnsNoActiveSlot()
    {
        var result = FilmstripWindow.Compute(0, 0);
        Assert.Equal(0, result.WindowStart);
        Assert.Equal(-1, result.ActiveSlot);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-99)]
    public void Compute_ActiveIndexBelowRange_ClampsToFirst(int activeIndex)
    {
        var result = FilmstripWindow.Compute(activeIndex, 10);
        Assert.Equal(0, result.WindowStart);
        Assert.Equal(0, result.ActiveSlot);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(999)]
    public void Compute_ActiveIndexAboveRange_ClampsToLast(int activeIndex)
    {
        var result = FilmstripWindow.Compute(activeIndex, 10);
        Assert.Equal(7, result.WindowStart);
        Assert.Equal(2, result.ActiveSlot);
    }

    [Fact]
    public void Compute_ActiveSlotIsAlwaysWithinTheThreeSlots()
    {
        for (var count = 1; count <= 20; count++)
        {
            for (var active = 0; active < count; active++)
            {
                var result = FilmstripWindow.Compute(active, count);
                Assert.InRange(result.ActiveSlot, 0, 2);

                // The window never scrolls past either end.
                Assert.True(result.WindowStart >= 0);
                Assert.True(result.WindowStart <= System.Math.Max(0, count - 3));

                // The active slot always points back at the active photo.
                Assert.Equal(active, result.WindowStart + result.ActiveSlot);
            }
        }
    }

    [Fact]
    public void Compute_NeverThrows_ForAnyInput()
    {
        foreach (var count in new[] { -5, 0, 1, 2, 3, 57 })
        {
            foreach (var active in new[] { -100, -1, 0, 1, 56, 1000 })
            {
                var result = FilmstripWindow.Compute(active, count);
                Assert.InRange(result.ActiveSlot, -1, 2);
            }
        }
    }

    // ---- Variable slot count (PRD 1.5: as many photos as actually fit) ----

    [Theory]
    // slots, count, activeIndex, expectedStart, expectedActiveSlot
    [InlineData(5, 20, 10, 8, 2)]    // centred: two either side
    [InlineData(5, 20, 0, 0, 0)]     // first photo - window cannot extend behind it
    [InlineData(5, 20, 1, 0, 1)]
    [InlineData(5, 20, 19, 15, 4)]   // last photo - window cannot extend ahead
    [InlineData(7, 20, 10, 7, 3)]
    [InlineData(9, 20, 10, 6, 4)]
    [InlineData(1, 20, 10, 10, 0)]
    public void Compute_CentresTheActivePhoto_AtAnyOddSlotCount(
        int slots, int count, int activeIndex, int expectedStart, int expectedSlot)
    {
        var result = FilmstripWindow.Compute(activeIndex, count, slots);
        Assert.Equal(expectedStart, result.WindowStart);
        Assert.Equal(expectedSlot, result.ActiveSlot);
        Assert.Equal(slots, result.SlotCount);
    }

    [Fact]
    public void Compute_AtAnySlotCount_KeepsEveryInvariant()
    {
        foreach (var slots in new[] { 1, 3, 5, 7, 9 })
        {
            for (var count = 1; count <= 25; count++)
            {
                for (var active = 0; active < count; active++)
                {
                    var result = FilmstripWindow.Compute(active, count, slots);
                    var effective = System.Math.Min(slots, count);

                    Assert.Equal(effective, result.SlotCount);
                    Assert.InRange(result.ActiveSlot, 0, effective - 1);

                    // The window never scrolls past either end...
                    Assert.True(result.WindowStart >= 0);
                    Assert.True(result.WindowStart + result.SlotCount <= count);

                    // ...and always still points at the active photo.
                    Assert.Equal(active, result.WindowStart + result.ActiveSlot);
                }
            }
        }
    }

    [Fact]
    public void Compute_SlotCountIsCappedAndNeverExceedsTheSequence()
    {
        Assert.Equal(FilmstripWindow.MaxSlots, FilmstripWindow.Compute(50, 100, 99).SlotCount);
        Assert.Equal(4, FilmstripWindow.Compute(1, 4, 9).SlotCount);
        Assert.Equal(1, FilmstripWindow.Compute(0, 1, 9).SlotCount);
    }

    [Fact]
    public void ChooseSlotCount_StaysAtThreeWhenTheStageIsAlreadyFull()
    {
        // Three landscape photos across a normal window: width binds immediately, so there is
        // no slack to spend and the count must not grow.
        var chosen = StageLayout.ChooseSlotCount(
            availableWidth: 1400, availableHeight: 400, gapWidth: 5, itemCount: 100,
            aspectsForCount: n => Enumerable.Repeat(1.5, n).ToList());

        Assert.Equal(3, chosen);
    }

    [Fact]
    public void ChooseSlotCount_GrowsWhenNarrowPhotosLeaveTheWidthUnused()
    {
        // Portrait photos on a wide stage are height-bound: three of them span far less than the
        // available width, and that slack is what extra photos are for.
        var chosen = StageLayout.ChooseSlotCount(
            availableWidth: 2400, availableHeight: 300, gapWidth: 5, itemCount: 100,
            aspectsForCount: n => Enumerable.Repeat(2.0 / 3.0, n).ToList());

        Assert.True(chosen > 3, $"expected the stage to grow past three, got {chosen}");
        Assert.True(chosen % 2 == 1, "counts stay odd so the active photo has a real centre slot");
    }

    [Fact]
    public void ChooseSlotCount_NeverExceedsTheCapOrTheSequenceLength()
    {
        // An absurdly wide stage full of very narrow photos must still stop at the ceiling.
        var capped = StageLayout.ChooseSlotCount(
            availableWidth: 100_000, availableHeight: 200, gapWidth: 5, itemCount: 500,
            aspectsForCount: n => Enumerable.Repeat(0.1, n).ToList());

        Assert.True(capped <= FilmstripWindow.MaxSlots, $"cap breached: {capped}");

        // And a short sequence cannot stage more photos than exist.
        var shortSequence = StageLayout.ChooseSlotCount(
            availableWidth: 100_000, availableHeight: 200, gapWidth: 5, itemCount: 2,
            aspectsForCount: n => Enumerable.Repeat(0.1, n).ToList());

        Assert.True(shortSequence <= 2, $"staged more photos than exist: {shortSequence}");
    }

    [Fact]
    public void ChooseSlotCount_ChosenSetAlwaysFitsTheAvailableWidth()
    {
        const double width = 2400;
        const double height = 300;
        const double gap = 5;

        foreach (var aspect in new[] { 0.5, 2.0 / 3.0, 1.0, 1.5, 2.4 })
        {
            var chosen = StageLayout.ChooseSlotCount(
                width, height, gap, itemCount: 100, aspectsForCount: n => Enumerable.Repeat(aspect, n).ToList());

            var aspects = Enumerable.Repeat(aspect, chosen).ToList();
            var gaps = StageLayout.ComputeTotalGapWidth(chosen, gap);
            var shared = StageLayout.ComputeSharedHeight(width, height, gaps, aspects);

            Assert.True(StageLayout.ComputeSetWidth(shared, gaps, aspects) <= width + 1e-6,
                $"aspect {aspect} at {chosen} slots overflowed the stage");
        }
    }

    [Fact]
    public void ChooseSlotCount_ReturnsZeroWhenThereIsNothingOrNoRoom()
    {
        Assert.Equal(0, StageLayout.ChooseSlotCount(1400, 400, 5, 0, n => new double[0]));
        Assert.Equal(0, StageLayout.ChooseSlotCount(0, 400, 5, 10, n => new double[0]));
        Assert.Equal(0, StageLayout.ChooseSlotCount(1400, 0, 5, 10, n => new double[0]));
    }
}
