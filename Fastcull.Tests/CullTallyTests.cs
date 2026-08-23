using System.Collections.Generic;
using System.Linq;
using Fastcull.Models;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// The sidebar's live tallies. The counts are the whole point of the panel, so they get the same
/// scrutiny as the ladder itself - a sidebar that miscounts is worse than no sidebar, because the
/// photographer uses it to decide when the cull is finished.
/// </summary>
public class CullTallyTests
{
    private static CullState Picked(int stars = 0) => new(Flag.Picked, stars);
    private static CullState Rejected() => new(Flag.Rejected, 0);
    private static CullState Unflagged() => CullState.Default;

    [Fact]
    public void AnEmptySequenceCountsNothing()
    {
        var t = CullTally.From(new List<CullState>());

        Assert.Equal(0, t.Total);
        Assert.Equal(0, t.Picked);
        Assert.Equal(0, t.Rejected);
        Assert.Equal(0, t.Unflagged);
        Assert.Equal(0, t.MaxStarCount);
        Assert.Equal(0, t.Decided);
    }

    [Fact]
    public void NullIsTreatedAsEmptyRatherThanThrowing()
        => Assert.Equal(0, CullTally.From(null!).Total);

    [Fact]
    public void TheThreeFlagCountsPartitionTheSequence()
    {
        var states = new[]
        {
            Picked(), Picked(3), Picked(5),
            Rejected(), Rejected(),
            Unflagged(), Unflagged(), Unflagged(), Unflagged(),
        };

        var t = CullTally.From(states);

        Assert.Equal(9, t.Total);
        Assert.Equal(3, t.Picked);
        Assert.Equal(2, t.Rejected);
        Assert.Equal(4, t.Unflagged);

        // The invariant that matters: nothing is double-counted and nothing is lost.
        Assert.Equal(t.Total, t.Picked + t.Rejected + t.Unflagged);
    }

    [Fact]
    public void StarsAreCountedAtEachLevel()
    {
        var states = new[]
        {
            Picked(1),
            Picked(2), Picked(2),
            Picked(3), Picked(3), Picked(3),
            Picked(5),
        };

        var t = CullTally.From(states);

        Assert.Equal(1, t.StarCount(1));
        Assert.Equal(2, t.StarCount(2));
        Assert.Equal(3, t.StarCount(3));
        Assert.Equal(0, t.StarCount(4));
        Assert.Equal(1, t.StarCount(5));
        Assert.Equal(3, t.MaxStarCount);
    }

    [Fact]
    public void APickedPhotoWithNoStarsCountsAsPickedButNotInTheHistogram()
    {
        var t = CullTally.From(new[] { Picked(), Picked(), Picked(4) });

        Assert.Equal(3, t.Picked);
        Assert.Equal(1, t.StarCount(4));

        // Deliberately does not sum: the histogram describes stars, not picks.
        var histogram = Enumerable.Range(1, 5).Sum(t.StarCount);
        Assert.Equal(1, histogram);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void AnOutOfRangeStarQueryReturnsZeroRatherThanThrowing(int stars)
        => Assert.Equal(0, CullTally.From(new[] { Picked(3) }).StarCount(stars));

    [Fact]
    public void DecidedIsPickedPlusRejectedAndExcludesUnflagged()
    {
        var t = CullTally.From(new[] { Picked(2), Rejected(), Unflagged(), Unflagged() });

        Assert.Equal(2, t.Decided);
        Assert.Equal(t.Total - t.Unflagged, t.Decided);
    }

    [Fact]
    public void TheTallyMatchesTheLadderAcrossItsWholeRange()
    {
        // One photo at every rung of PRD 1.6's eight-state ladder.
        var states = Enumerable.Range(0, 8).Select(CullState.FromLadderIndex).ToList();
        var t = CullTally.From(states);

        Assert.Equal(8, t.Total);
        Assert.Equal(1, t.Rejected);    // rung 0
        Assert.Equal(1, t.Unflagged);   // rung 1
        Assert.Equal(6, t.Picked);      // rungs 2-7: picked with 0-5 stars

        for (var star = 1; star <= 5; star++)
            Assert.Equal(1, t.StarCount(star));
    }

    [Fact]
    public void RecountingAfterAChangeReflectsTheChange()
    {
        var states = new List<CullState> { Unflagged(), Unflagged(), Unflagged() };
        Assert.Equal(3, CullTally.From(states).Unflagged);

        states[0] = Picked(4);
        states[1] = Rejected();

        var t = CullTally.From(states);
        Assert.Equal(1, t.Picked);
        Assert.Equal(1, t.Rejected);
        Assert.Equal(1, t.Unflagged);
        Assert.Equal(1, t.StarCount(4));
    }
}
