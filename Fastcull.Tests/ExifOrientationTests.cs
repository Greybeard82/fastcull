using Fastcull.Services;
using Windows.Graphics.Imaging;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// The orientation mapping behind the portrait-RAW fix. Worth testing directly because the two
/// mirrored-diagonal cases (5 and 7) are easy to transpose by accident and this repo's sample
/// corpus contains none of them - only 1 and 8 - so nothing else would catch a mistake there.
/// </summary>
public class ExifOrientationTests
{
    [Fact]
    public void NormalIsTheIdentityTransform()
    {
        var (flip, rotation) = ExifOrientation.ToTransform(ExifOrientation.Normal);

        Assert.Equal(BitmapFlip.None, flip);
        Assert.Equal(BitmapRotation.None, rotation);
        Assert.False(ExifOrientation.IsRotated(ExifOrientation.Normal));
        Assert.False(ExifOrientation.SwapsDimensions(ExifOrientation.Normal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-3)]
    [InlineData(int.MaxValue)]
    public void AnOutOfRangeOrientationDegradesToTheIdentity(int bogus)
    {
        var (flip, rotation) = ExifOrientation.ToTransform(bogus);

        Assert.Equal(BitmapFlip.None, flip);
        Assert.Equal(BitmapRotation.None, rotation);
    }

    [Fact]
    public void OrientationSixIsAQuarterTurnClockwise()
    {
        var (flip, rotation) = ExifOrientation.ToTransform(6);

        Assert.Equal(BitmapFlip.None, flip);
        Assert.Equal(BitmapRotation.Clockwise90Degrees, rotation);
    }

    [Fact]
    public void OrientationEightIsThreeQuarterTurnsClockwise()
    {
        // The one that actually occurs in this repo's corpus: nine files carry it, and before the
        // fix all nine decoded landscape.
        var (flip, rotation) = ExifOrientation.ToTransform(8);

        Assert.Equal(BitmapFlip.None, flip);
        Assert.Equal(BitmapRotation.Clockwise270Degrees, rotation);
    }

    [Fact]
    public void OrientationThreeIsAHalfTurnWithNoFlip()
    {
        var (flip, rotation) = ExifOrientation.ToTransform(3);

        Assert.Equal(BitmapFlip.None, flip);
        Assert.Equal(BitmapRotation.Clockwise180Degrees, rotation);
    }

    [Theory]
    [InlineData(2, BitmapFlip.Horizontal)]
    [InlineData(4, BitmapFlip.Vertical)]
    public void TheTwoPlainMirrorsDoNotRotate(int orientation, BitmapFlip expected)
    {
        var (flip, rotation) = ExifOrientation.ToTransform(orientation);

        Assert.Equal(expected, flip);
        Assert.Equal(BitmapRotation.None, rotation);
    }

    [Theory]
    [InlineData(5, BitmapRotation.Clockwise90Degrees)]
    [InlineData(7, BitmapRotation.Clockwise270Degrees)]
    public void TheMirroredDiagonalsFlipVerticallyBeforeRotating(int orientation, BitmapRotation expected)
    {
        // Vertical, not horizontal. WIC flips before it rotates, so orientation 5 - a transpose,
        // (x,y) -> (y,x) - is a vertical flip followed by a 90 clockwise turn. Flipping
        // horizontally first would produce the transverse, which is orientation 7's job.
        var (flip, rotation) = ExifOrientation.ToTransform(orientation);

        Assert.Equal(BitmapFlip.Vertical, flip);
        Assert.Equal(expected, rotation);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    public void OnlyTheQuarterTurnsSwapDimensions(int orientation, bool expected)
        => Assert.Equal(expected, ExifOrientation.SwapsDimensions(orientation));

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(8, true)]
    public void IsRotatedGatesWhetherTheStreamsOwnExifIsIgnored(int orientation, bool expected)
        => Assert.Equal(expected, ExifOrientation.IsRotated(orientation));

    [Fact]
    public void AMissingFileReadsAsNormalRatherThanThrowing()
        => Assert.Equal(ExifOrientation.Normal, ExifOrientation.Read(@"C:\no\such\file.arw"));
}
