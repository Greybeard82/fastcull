using System.Collections.Generic;
using System.Linq;
using Fastcull.Models;
using Fastcull.Services;
using Xunit;

namespace Fastcull.Tests;

public class FormatBreakdownTests
{
    private static List<FormatCount> Count(params (string Name, FormatFamily Family)[] files)
        => FormatBreakdown.From(files);

    [Fact]
    public void AnEmptySequenceCountsNothing()
    {
        Assert.Empty(FormatBreakdown.From(System.Array.Empty<(string, FormatFamily)>()));
        Assert.Empty(FormatBreakdown.From(null!));
        Assert.Equal(0, FormatBreakdown.Max(new List<FormatCount>()));
        Assert.Equal(string.Empty, FormatBreakdown.Summarise(new List<FormatCount>()));
    }

    [Fact]
    public void ExtensionsAreCountedSeparatelyEvenWithinOneFamily()
    {
        // The point of the breakdown: .ARW and .CR2 are both Raw, and telling them apart is
        // exactly what a mixed two-body card needs.
        var counts = Count(
            ("a.ARW", FormatFamily.Raw),
            ("b.arw", FormatFamily.Raw),
            ("c.CR2", FormatFamily.Raw),
            ("d.jpg", FormatFamily.Jpeg));

        Assert.Equal(3, counts.Count);
        Assert.Equal(2, counts.Single(c => c.Label == "ARW").Count);
        Assert.Equal(1, counts.Single(c => c.Label == "CR2").Count);
        Assert.Equal(1, counts.Single(c => c.Label == "JPG").Count);
    }

    [Fact]
    public void ExtensionCasingIsNormalisedToUpper()
    {
        var counts = Count(("a.ArW", FormatFamily.Raw), ("b.arw", FormatFamily.Raw));

        var only = Assert.Single(counts);
        Assert.Equal("ARW", only.Label);
        Assert.Equal(2, only.Count);
    }

    [Fact]
    public void TheDominantFormatLeads()
    {
        var counts = Count(
            ("a.jpg", FormatFamily.Jpeg),
            ("b.arw", FormatFamily.Raw), ("c.arw", FormatFamily.Raw), ("d.arw", FormatFamily.Raw),
            ("e.cr2", FormatFamily.Raw), ("f.cr2", FormatFamily.Raw));

        Assert.Equal(new[] { "ARW", "CR2", "JPG" }, counts.Select(c => c.Label).ToArray());
    }

    [Fact]
    public void EqualCountsFallBackToAlphabeticalSoTheOrderIsStable()
    {
        var counts = Count(("z.zzz", FormatFamily.Other), ("a.aaa", FormatFamily.Other));

        Assert.Equal(new[] { "AAA", "ZZZ" }, counts.Select(c => c.Label).ToArray());
    }

    [Fact]
    public void TheFamilyRidesAlongWithEachCount()
    {
        var counts = Count(("a.arw", FormatFamily.Raw), ("b.jpg", FormatFamily.Jpeg));

        Assert.Equal(FormatFamily.Raw, counts.Single(c => c.Label == "ARW").Family);
        Assert.Equal(FormatFamily.Jpeg, counts.Single(c => c.Label == "JPG").Family);
    }

    [Theory]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData("   ")]
    public void FilesWithNoUsableExtensionAreSkippedRatherThanBucketedUnderBlank(string name)
        => Assert.Empty(Count((name, FormatFamily.Other)));

    [Fact]
    public void MaxIsTheLargestCountForScalingBars()
    {
        var counts = Count(
            ("a.arw", FormatFamily.Raw), ("b.arw", FormatFamily.Raw), ("c.arw", FormatFamily.Raw),
            ("d.jpg", FormatFamily.Jpeg));

        Assert.Equal(3, FormatBreakdown.Max(counts));
    }

    [Fact]
    public void SummariseReadsAsASentenceOfCounts()
    {
        var counts = Count(
            ("a.arw", FormatFamily.Raw), ("b.arw", FormatFamily.Raw),
            ("c.jpg", FormatFamily.Jpeg));

        Assert.Equal("2 ARW · 1 JPG", FormatBreakdown.Summarise(counts));
    }

    [Fact]
    public void CountsMatchTheRepoCorpusShape()
    {
        // Mirrors this repo's SampleImages: 77 ARW, 20 CR2, 4 JPG.
        var files = Enumerable.Range(0, 77).Select(i => ($"a{i}.ARW", FormatFamily.Raw))
            .Concat(Enumerable.Range(0, 20).Select(i => ($"b{i}.CR2", FormatFamily.Raw)))
            .Concat(Enumerable.Range(0, 4).Select(i => ($"c{i}.jpg", FormatFamily.Jpeg)));

        var counts = FormatBreakdown.From(files);

        Assert.Equal("77 ARW · 20 CR2 · 4 JPG", FormatBreakdown.Summarise(counts));
        Assert.Equal(101, counts.Sum(c => c.Count));
    }
}
