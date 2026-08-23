using Fastcull.Models;
using Xunit;

namespace Fastcull.Tests;

/// <summary>Quarter-turn model for PRD 1.11.</summary>
public class RotationTests
{
    [Fact]
    public void FourRightTurnsReturnToTheOriginal()
    {
        var r = Rotation.None;
        for (var i = 0; i < 4; i++) r = r.RotateRight();
        Assert.Equal(Rotation.None, r);
        Assert.Equal(0, r.QuarterTurns);
    }

    [Fact]
    public void FourLeftTurnsReturnToTheOriginal()
    {
        var r = Rotation.None;
        for (var i = 0; i < 4; i++) r = r.RotateLeft();
        Assert.Equal(Rotation.None, r);
    }

    [Fact]
    public void LeftFromZeroWrapsToThree_NotMinusOne()
    {
        // C#'s % keeps the dividend's sign, so a naive implementation yields -1 here and then
        // renders at -90 degrees with a negative-sized layout box.
        var r = Rotation.None.RotateLeft();
        Assert.Equal(3, r.QuarterTurns);
        Assert.Equal(270, r.Degrees);
    }

    [Fact]
    public void RightAndLeftAreInverses()
    {
        for (var start = 0; start < 4; start++)
        {
            var r = Rotation.FromQuarterTurns(start);
            Assert.Equal(r, r.RotateRight().RotateLeft());
            Assert.Equal(r, r.RotateLeft().RotateRight());
        }
    }

    [Theory]
    [InlineData(-9, 3)]
    [InlineData(-4, 0)]
    [InlineData(-1, 3)]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(7, 3)]
    [InlineData(103, 3)]
    public void AnyIntegerNormalisesIntoZeroToThree(int input, int expected)
    {
        var r = Rotation.FromQuarterTurns(input);
        Assert.Equal(expected, r.QuarterTurns);
        Assert.InRange(r.QuarterTurns, 0, 3);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 90)]
    [InlineData(2, 180)]
    [InlineData(3, 270)]
    public void DegreesFollowQuarterTurns(int turns, double degrees)
        => Assert.Equal(degrees, Rotation.FromQuarterTurns(turns).Degrees);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void OnlyQuarterAndThreeQuarterTurnsSwapAspect(int turns, bool swaps)
        => Assert.Equal(swaps, Rotation.FromQuarterTurns(turns).SwapsAspect);

    [Fact]
    public void ApplyInvertsAspectOnTheQuarterTurns()
    {
        const double landscape = 1.5;   // 3:2

        Assert.Equal(landscape, Rotation.FromQuarterTurns(0).Apply(landscape), precision: 9);
        Assert.Equal(1 / landscape, Rotation.FromQuarterTurns(1).Apply(landscape), precision: 9);
        Assert.Equal(landscape, Rotation.FromQuarterTurns(2).Apply(landscape), precision: 9);
        Assert.Equal(1 / landscape, Rotation.FromQuarterTurns(3).Apply(landscape), precision: 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ApplyPassesThroughAnUndecodedAspectRatherThanDividingByIt(double bad)
    {
        // An item whose decode has not landed reports a non-usable aspect. Inverting it would
        // produce Infinity or NaN and poison the shared-height rule downstream; StageLayout
        // already knows how to fall back, so the value must reach it unchanged.
        Assert.Equal(bad, Rotation.FromQuarterTurns(1).Apply(bad));
    }

    [Fact]
    public void RotationIsAValueType_EqualByQuarterTurns()
    {
        Assert.Equal(Rotation.FromQuarterTurns(2), Rotation.FromQuarterTurns(6));
        Assert.NotEqual(Rotation.FromQuarterTurns(1), Rotation.FromQuarterTurns(3));
    }
}
