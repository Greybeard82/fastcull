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
}
