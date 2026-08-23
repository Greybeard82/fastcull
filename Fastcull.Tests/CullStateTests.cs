using System;
using Fastcull.Models;
using Xunit;

namespace Fastcull.Tests;

/// <summary>Covers the full eight-state ladder and every invariant from PRD 1.6.</summary>
public class CullStateTests
{
    // ---- The eight states map to the right (Flag, Stars) pair and back ----

    [Theory]
    [InlineData(0, Flag.Rejected, 0)]
    [InlineData(1, Flag.Unflagged, 0)]
    [InlineData(2, Flag.Picked, 0)]
    [InlineData(3, Flag.Picked, 1)]
    [InlineData(4, Flag.Picked, 2)]
    [InlineData(5, Flag.Picked, 3)]
    [InlineData(6, Flag.Picked, 4)]
    [InlineData(7, Flag.Picked, 5)]
    public void LadderIndex_RoundTrips(int index, Flag expectedFlag, int expectedStars)
    {
        var state = CullState.FromLadderIndex(index);
        Assert.Equal(expectedFlag, state.Flag);
        Assert.Equal(expectedStars, state.Stars);
        Assert.Equal(index, state.LadderIndex);

        // and the other direction
        Assert.Equal(index, new CullState(expectedFlag, expectedStars).LadderIndex);
    }

    [Fact]
    public void Default_IsUnrated() => Assert.Equal(1, CullState.Default.LadderIndex);

    // ---- Up / Down from every rung, including the clamps ----

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 5)]
    [InlineData(5, 6)]
    [InlineData(6, 7)]
    [InlineData(7, 7)]   // clamped
    public void Up_StepsOneRung_AndClampsAtSeven(int from, int expected)
        => Assert.Equal(expected, CullState.FromLadderIndex(from).Up().LadderIndex);

    [Theory]
    [InlineData(7, 6)]
    [InlineData(6, 5)]
    [InlineData(5, 4)]
    [InlineData(4, 3)]
    [InlineData(3, 2)]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    [InlineData(0, 0)]   // clamped
    public void Down_StepsOneRung_AndClampsAtZero(int from, int expected)
        => Assert.Equal(expected, CullState.FromLadderIndex(from).Down().LadderIndex);

    [Fact]
    public void WalkingUpEightTimesFromRejected_LandsOnFiveStarsAndStays()
    {
        var state = CullState.FromLadderIndex(0);
        for (var i = 0; i < 8; i++) state = state.Up();

        Assert.Equal(7, state.LadderIndex);
        Assert.Equal(Flag.Picked, state.Flag);
        Assert.Equal(5, state.Stars);

        Assert.Equal(7, state.Up().LadderIndex);   // still stuck at the top
    }

    [Fact]
    public void WalkingDownEightTimesFromFiveStars_LandsOnRejectedAndStays()
    {
        var state = CullState.FromLadderIndex(7);
        for (var i = 0; i < 8; i++) state = state.Down();

        Assert.Equal(0, state.LadderIndex);
        Assert.Equal(Flag.Rejected, state.Flag);
        Assert.Equal(0, state.Stars);

        Assert.Equal(0, state.Down().LadderIndex);  // still stuck at the bottom
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(99)]
    public void FromLadderIndex_ClampsRatherThanThrowing(int index)
    {
        var state = CullState.FromLadderIndex(index);
        Assert.InRange(state.LadderIndex, 0, 7);
    }

    // ---- Invariants: invalid pairs must throw, not be normalised ----

    [Theory]
    [InlineData(Flag.Rejected, 1)]
    [InlineData(Flag.Rejected, 5)]
    [InlineData(Flag.Unflagged, 1)]
    [InlineData(Flag.Unflagged, 3)]
    public void InvalidPairs_Throw(Flag flag, int stars)
        => Assert.ThrowsAny<ArgumentException>(() => new CullState(flag, stars));

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(100)]
    public void OutOfRangeStars_Throw(int stars)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new CullState(Flag.Picked, stars));

    [Fact]
    public void EveryLadderState_SatisfiesTheInvariants()
    {
        for (var i = 0; i <= 7; i++)
        {
            var s = CullState.FromLadderIndex(i);
            if (s.Flag == Flag.Rejected) Assert.Equal(0, s.Stars);
            if (s.Flag == Flag.Unflagged) Assert.Equal(0, s.Stars);
            if (s.Stars >= 1) Assert.Equal(Flag.Picked, s.Flag);
        }
    }

    // ---- WithStars / AsPicked / AsRejected / AsUnflagged from every starting state ----

    [Fact]
    public void WithStars_FromEveryState_ImpliesPickedWhenNonZero()
    {
        for (var i = 0; i <= 7; i++)
        {
            var start = CullState.FromLadderIndex(i);
            for (var stars = 1; stars <= 5; stars++)
            {
                var result = start.WithStars(stars);
                Assert.Equal(Flag.Picked, result.Flag);
                Assert.Equal(stars, result.Stars);
            }
        }
    }

    [Fact]
    public void SetStarsZero_KeepsCurrentFlag()
    {
        // PRD 2.1: "0 / NumPad0 -> Clear stars, keep the current flag."
        var fromPicked = CullState.FromLadderIndex(5).WithStars(0);   // picked, 3 stars
        Assert.Equal(Flag.Picked, fromPicked.Flag);
        Assert.Equal(0, fromPicked.Stars);

        var fromRejected = CullState.FromLadderIndex(0).WithStars(0);
        Assert.Equal(Flag.Rejected, fromRejected.Flag);
        Assert.Equal(0, fromRejected.Stars);

        var fromUnflagged = CullState.FromLadderIndex(1).WithStars(0);
        Assert.Equal(Flag.Unflagged, fromUnflagged.Flag);
        Assert.Equal(0, fromUnflagged.Stars);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void WithStars_OutOfRange_Throws(int stars)
        => Assert.Throws<ArgumentOutOfRangeException>(() => CullState.Default.WithStars(stars));

    [Theory]
    [InlineData(0, 2)]   // rejected  -> picked
    [InlineData(1, 2)]   // unrated   -> picked
    [InlineData(2, 2)]   // picked    -> unchanged
    [InlineData(3, 3)]   // 1 star    -> no-op, keeps stars
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    [InlineData(7, 7)]   // 5 stars   -> no-op
    public void AsPicked_FromEveryState(int from, int expected)
        => Assert.Equal(expected, CullState.FromLadderIndex(from).AsPicked().LadderIndex);

    [Fact]
    public void AsRejected_FromEveryState_ClearsStars()
    {
        for (var i = 0; i <= 7; i++)
        {
            var result = CullState.FromLadderIndex(i).AsRejected();
            Assert.Equal(0, result.LadderIndex);
            Assert.Equal(Flag.Rejected, result.Flag);
            Assert.Equal(0, result.Stars);
        }
    }

    [Fact]
    public void AsUnflagged_FromEveryState_ClearsStars()
    {
        for (var i = 0; i <= 7; i++)
        {
            var result = CullState.FromLadderIndex(i).AsUnflagged();
            Assert.Equal(1, result.LadderIndex);
            Assert.Equal(Flag.Unflagged, result.Flag);
            Assert.Equal(0, result.Stars);
        }
    }
}
