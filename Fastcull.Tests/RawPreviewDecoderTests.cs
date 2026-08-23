using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fastcull.Services;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// Smoke coverage for RAW preview extraction against the real SampleImages corpus (work order
/// D.5). This cannot prove the on-screen render - same sandbox limits as the rest of the suite -
/// but it does prove the decode path works against real .ARW and .CR2 files, which is the part
/// most likely to break silently.
/// </summary>
public class RawPreviewDecoderTests
{
    private static string SampleImagesRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "SampleImages");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("SampleImages not found above " + AppContext.BaseDirectory);
        }
    }

    private static List<string> Sample(string pattern, int take) =>
        Directory.GetFiles(SampleImagesRoot, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(take).ToList();

    public static TheoryData<string> RawSamples
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var f in Sample("*.ARW", 3).Concat(Sample("*.CR2", 3))) data.Add(f);
            return data;
        }
    }

    [Fact]
    public void Corpus_ContainsBothRawFamilies()
    {
        // Guards every theory below: an empty corpus would make them vacuously pass.
        Assert.NotEmpty(Sample("*.ARW", 1));
        Assert.NotEmpty(Sample("*.CR2", 1));
    }

    [Theory]
    [MemberData(nameof(RawSamples))]
    public async Task DecodeThumbnail_ReturnsReasonableBitmap(string path)
    {
        var bitmap = await RawPreviewDecoder.DecodeThumbnailAsync(path);

        Assert.NotNull(bitmap);
        using (bitmap)
        {
            Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0,
                $"{Path.GetFileName(path)} decoded to {bitmap.PixelWidth}x{bitmap.PixelHeight}");

            // Downscaled to the thumbnail tier, with a little slack for rounding. Anything near
            // sensor size would mean the scaling silently did nothing.
            Assert.True(Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) <= 200,
                $"{Path.GetFileName(path)} thumbnail was {bitmap.PixelWidth}x{bitmap.PixelHeight}, expected <= ~160 long edge");
        }
    }

    [Theory]
    [MemberData(nameof(RawSamples))]
    public async Task DecodeDisplayImage_ReturnsReasonableBitmap(string path)
    {
        var bitmap = await RawPreviewDecoder.DecodeDisplayImageAsync(path);

        Assert.NotNull(bitmap);
        using (bitmap)
        {
            Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0,
                $"{Path.GetFileName(path)} decoded to {bitmap.PixelWidth}x{bitmap.PixelHeight}");

            // Big enough to be the display tier rather than the 160 px thumbnail...
            Assert.True(Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) >= 600,
                $"{Path.GetFileName(path)} display tier was only {bitmap.PixelWidth}x{bitmap.PixelHeight}");

            // ...and not absurdly larger than the tier asked for.
            Assert.True(Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) <= 1200,
                $"{Path.GetFileName(path)} display tier was {bitmap.PixelWidth}x{bitmap.PixelHeight}, expected ~960 long edge");
        }
    }

    [Theory]
    [MemberData(nameof(RawSamples))]
    public async Task DecodedAspectRatio_MatchesALandscapePhoto(string path)
    {
        using var bitmap = await RawPreviewDecoder.DecodeDisplayImageAsync(path);

        Assert.NotNull(bitmap);
        var ratio = bitmap!.PixelWidth / (double)bitmap.PixelHeight;

        // Every file in the corpus is landscape; a wildly wrong ratio would mean we decoded
        // something other than the photo (a colour profile blob, say).
        Assert.InRange(ratio, 1.2, 1.9);
    }

    [Fact]
    public async Task NonExistentFile_ReturnsNull_DoesNotThrow()
    {
        Assert.Null(await RawPreviewDecoder.DecodeDisplayImageAsync(@"C:\does\not\exist\nope.ARW"));
    }

    [Fact]
    public async Task NotARawFile_ReturnsNullRatherThanThrowing()
    {
        // A JPEG has no *embedded* preview, so the extractor finds only the file itself; a text
        // file has nothing at all. Neither may throw.
        var temp = Path.Combine(Path.GetTempPath(), $"fastcull-notraw-{Guid.NewGuid():N}.arw");
        await File.WriteAllTextAsync(temp, "this is definitely not a raw file");
        try
        {
            Assert.Null(await RawPreviewDecoder.DecodeDisplayImageAsync(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public async Task Cancellation_IsObserved()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var path = Sample("*.ARW", 1).Single();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RawPreviewDecoder.DecodeDisplayImageAsync(path, cts.Token));
    }

    [Fact]
    public void FindEmbeddedJpegs_IgnoresBytesThatAreNotAStream()
    {
        var junk = new byte[4096];
        Array.Fill(junk, (byte)0xAB);
        Assert.Empty(RawPreviewDecoder.FindEmbeddedJpegs(junk, junk.Length));
    }
}
