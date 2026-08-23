using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fastcull.Services;
using Xunit;

namespace Fastcull.Tests;

/// <summary>Smoke coverage over the real SampleImages corpus (work order G.2).</summary>
public class DirectoryScannerTests
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

    private static async Task<List<ScannedPhoto>> ScanAsync()
    {
        var scanned = new List<ScannedPhoto>();
        await foreach (var photo in new DirectoryScanner().ScanAsync(SampleImagesRoot))
            scanned.Add(photo);
        return scanned;
    }

    [Fact]
    public async Task Scan_FindsEverySupportedFile_AndClassifiesItByFamily()
    {
        // Counts are derived from the folder rather than hardcoded: SampleImages is David's
        // working corpus and its contents change. What matters is that the scanner finds
        // every supported file and puts it in the right family, not that there are N of them.
        var onDisk = Directory.GetFiles(SampleImagesRoot, "*", SearchOption.AllDirectories);
        var expectedJpeg = onDisk.Count(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                          || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));
        var expectedRaw = onDisk.Count(f => f.EndsWith(".arw", StringComparison.OrdinalIgnoreCase)
                                         || f.EndsWith(".cr2", StringComparison.OrdinalIgnoreCase));

        var photos = await ScanAsync();

        Assert.Equal(expectedJpeg, photos.Count(p => p.Family == FormatFamily.Jpeg));
        Assert.Equal(expectedRaw, photos.Count(p => p.Family == FormatFamily.Raw));
        Assert.Equal(expectedJpeg + expectedRaw, photos.Count);

        // Guard against the corpus being emptied out from under the suite, which would make
        // every assertion above trivially true.
        Assert.True(expectedJpeg > 0, "SampleImages contains no JPEGs - the corpus is missing");
        Assert.True(expectedRaw > 0, "SampleImages contains no RAW files - the corpus is missing");
    }

    [Fact]
    public async Task Scan_ProducesNoDuplicatePaths()
    {
        var photos = await ScanAsync();
        Assert.Equal(photos.Count, photos.Select(p => p.FilePath).Distinct().Count());
    }

    [Fact]
    public async Task Scan_SortOrderIsStableAcrossRuns()
    {
        static IEnumerable<string> Sorted(IEnumerable<ScannedPhoto> src) => src
            .OrderBy(p => p.SortTime)
            .ThenBy(p => p.CaptureSubsec)
            .ThenBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.FilePath);

        var first = Sorted(await ScanAsync()).ToList();
        var second = Sorted(await ScanAsync()).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Scan_EveryPhotoHasAResolvedSortTime()
    {
        var photos = await ScanAsync();

        // PRD 1.3: SortTime is never null - the hierarchy always resolves to something.
        Assert.All(photos, p => Assert.NotEqual(default, p.SortTime));
        Assert.All(photos, p => Assert.False(string.IsNullOrWhiteSpace(p.FileName)));
        Assert.All(photos, p => Assert.True(p.FileBytes > 0));
    }

    [Fact]
    public async Task Scan_JpegsResolveCaptureDate_NotFileModified()
    {
        var photos = await ScanAsync();
        var jpegs = photos.Where(p => p.Family == FormatFamily.Jpeg).ToList();

        Assert.NotEmpty(jpegs);
        Assert.All(jpegs, p => Assert.Equal(TimeSource.CaptureDate, p.SortTimeSource));
    }

    [Fact]
    public async Task Scan_MissingDirectory_Throws()
    {
        var scanner = new DirectoryScanner();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
        {
            await foreach (var _ in scanner.ScanAsync(@"C:\does\not\exist\anywhere")) { }
        });
    }
}
