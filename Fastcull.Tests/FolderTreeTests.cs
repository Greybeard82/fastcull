using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fastcull.Models;
using Xunit;

namespace Fastcull.Tests;

public class FolderTreeTests
{
    private static string P(params string[] parts) => string.Join(Path.DirectorySeparatorChar, parts);

    private static FolderNode Build(params (string Path, int Index)[] entries)
        => FolderTree.Build("Shoot", entries.Select(e => new FolderTreeEntry(e.Path, e.Index)));

    [Fact]
    public void AnEmptySequenceStillProducesARootToRender()
    {
        var root = FolderTree.Build("Shoot", System.Array.Empty<FolderTreeEntry>());

        Assert.Equal("Shoot", root.Name);
        Assert.Equal(0, root.TotalPhotoCount);
        Assert.False(root.HasChildren);
        Assert.Equal(-1, root.FirstPhotoIndex);
    }

    [Fact]
    public void NullEntriesAreTreatedAsEmptyRatherThanThrowing()
        => Assert.Equal(0, FolderTree.Build("Shoot", null!).TotalPhotoCount);

    [Fact]
    public void AFlatFolderPutsEveryPhotoOnTheRoot()
    {
        var root = Build(("a.arw", 0), ("b.arw", 1), ("c.arw", 2));

        Assert.False(root.HasChildren);
        Assert.Equal(3, root.DirectPhotoCount);
        Assert.Equal(3, root.TotalPhotoCount);
        Assert.Equal(0, root.FirstPhotoIndex);
    }

    [Fact]
    public void SubfoldersBecomeChildren()
    {
        var root = Build(
            ("top.arw", 0),
            (P("day1", "a.arw"), 1),
            (P("day1", "b.arw"), 2),
            (P("day2", "c.arw"), 3));

        Assert.Equal(1, root.DirectPhotoCount);
        Assert.Equal(4, root.TotalPhotoCount);
        Assert.Equal(2, root.Children.Count);

        var day1 = root.Children.Single(c => c.Name == "day1");
        Assert.Equal(2, day1.DirectPhotoCount);
        Assert.Equal(2, day1.TotalPhotoCount);
        Assert.Equal(1, day1.Depth);
    }

    [Fact]
    public void NestingGoesArbitrarilyDeepAndTotalsRollUp()
    {
        var root = Build(
            (P("a", "b", "c", "deep.arw"), 7),
            (P("a", "shallow.arw"), 3));

        var a = Assert.Single(root.Children);
        Assert.Equal(2, a.TotalPhotoCount);
        Assert.Equal(1, a.DirectPhotoCount);

        var b = Assert.Single(a.Children);
        var c = Assert.Single(b.Children);

        Assert.Equal(3, c.Depth);
        Assert.Equal(1, c.TotalPhotoCount);
        Assert.Equal(7, c.FirstPhotoIndex);

        // The root's total is everything beneath it, not just its own files.
        Assert.Equal(2, root.TotalPhotoCount);
        Assert.Equal(0, root.DirectPhotoCount);
    }

    [Fact]
    public void FirstPhotoIndexIsTheEarliestInTheWholeSubtree()
    {
        // The earliest photo lives in a grandchild, not directly in the folder clicked.
        var root = Build(
            (P("a", "late.arw"), 90),
            (P("a", "b", "early.arw"), 4));

        var a = Assert.Single(root.Children);
        Assert.Equal(4, a.FirstPhotoIndex);
        Assert.Equal(4, root.FirstPhotoIndex);
    }

    [Fact]
    public void ChildrenAreOrderedByName()
    {
        var root = Build(
            (P("zebra", "z.arw"), 0),
            (P("alpha", "a.arw"), 1),
            (P("Mango", "m.arw"), 2));

        Assert.Equal(new[] { "alpha", "Mango", "zebra" }, root.Children.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void RelativePathIsCarriedForEachNode()
    {
        var root = Build((P("day1", "sub", "x.arw"), 0));

        var day1 = Assert.Single(root.Children);
        var sub = Assert.Single(day1.Children);

        Assert.Equal(string.Empty, root.RelativePath);
        Assert.Equal("day1", day1.RelativePath);
        Assert.Equal(P("day1", "sub"), sub.RelativePath);
    }

    // ---- Flatten ----

    [Fact]
    public void FlattenCollapsedShowsOnlyTheRoot()
    {
        var root = Build((P("a", "x.arw"), 0), (P("b", "y.arw"), 1));

        var flat = FolderTree.Flatten(root, static _ => false);

        Assert.Single(flat);
        Assert.Same(root, flat[0]);
    }

    [Fact]
    public void FlattenExpandedWalksDepthFirstInDisplayOrder()
    {
        var root = Build(
            (P("a", "x.arw"), 0),
            (P("a", "deep", "y.arw"), 1),
            (P("b", "z.arw"), 2));

        var flat = FolderTree.Flatten(root, static _ => true);

        Assert.Equal(new[] { "Shoot", "a", "deep", "b" }, flat.Select(n => n.Name).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 1 }, flat.Select(n => n.Depth).ToArray());
    }

    [Fact]
    public void FlattenDescendsOnlyIntoExpandedFolders()
    {
        var root = Build(
            (P("a", "x.arw"), 0),
            (P("a", "deep", "y.arw"), 1),
            (P("b", "z.arw"), 2));

        // Root open, "a" closed: its child must not appear.
        var flat = FolderTree.Flatten(root, n => n.Depth == 0);

        Assert.Equal(new[] { "Shoot", "a", "b" }, flat.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void FlattenOfNullIsEmptyRatherThanThrowing()
        => Assert.Empty(FolderTree.Flatten(null, static _ => true));
}
