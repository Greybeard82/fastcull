using System;
using System.IO;
using System.Linq;
using Fastcull.Models;
using Fastcull.Services;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// PRD 4.3's bucketing. This is the highest-consequence arithmetic in the app - it decides where
/// somebody's photographs get written - so the precedence rule is pinned from several directions
/// rather than asserted once.
/// </summary>
public class FinishPlannerTests
{
    private const string Root = @"E:\Photos\Canada";

    private static FinishPlan PlanOf(params (string Rel, CullState State)[] photos)
        => FinishPlanner.Plan(
            Root,
            FinishOperation.Move,
            photos.Select(p => (Path.Combine(Root, p.Rel), p.Rel, p.State)));

    // ---- The precedence rule ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void StarsWinOverApproved(int stars)
    {
        // The rule the whole section turns on. Section 1.6 makes every starred photo Picked as
        // well, so a flag-first test would send all of these to Approved and leave Rated empty.
        var state = new CullState(Flag.Picked, stars);

        Assert.Equal(FinishBucket.Rated, FinishPlanner.BucketFor(state));
        Assert.NotEqual(FinishBucket.Approved, FinishPlanner.BucketFor(state));
    }

    [Fact]
    public void AStarredPhotoIsNeverAlsoApproved()
    {
        // Stated as a property of the whole plan, not just of one classification: a three-star
        // photo must appear exactly once, in Rated/3.
        var plan = PlanOf(("a.cr2", new CullState(Flag.Picked, 3)));

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(FinishBucket.Rated, entry.Bucket);
        Assert.Equal(Path.Combine(Root, "Rated", "3", "a.cr2"), entry.DestinationPath);

        Assert.Equal(0, plan.ApprovedCount);
        Assert.Equal(1, plan.StarCount(3));
        Assert.DoesNotContain(plan.Entries, e => e.DestinationPath?.Contains("Approved") == true);
    }

    [Fact]
    public void ApprovedHoldsOnlyPickedWithZeroStars()
    {
        var plan = PlanOf(
            ("picked-plain.cr2", new CullState(Flag.Picked, 0)),
            ("picked-1.cr2", new CullState(Flag.Picked, 1)),
            ("picked-5.cr2", new CullState(Flag.Picked, 5)));

        Assert.Equal(1, plan.ApprovedCount);

        var approved = Assert.Single(plan.Entries.Where(e => e.Bucket == FinishBucket.Approved));
        Assert.Equal("picked-plain.cr2", approved.RelativePath);
    }

    // ---- The other buckets ----

    [Fact]
    public void RejectedGoesToRejected()
    {
        var plan = PlanOf(("bad.cr2", CullState.Default.AsRejected()));

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(FinishBucket.Rejected, entry.Bucket);
        Assert.Equal(Path.Combine(Root, "Rejected", "bad.cr2"), entry.DestinationPath);
    }

    [Fact]
    public void UnratedIsUntouchedAndHasNoDestination()
    {
        var plan = PlanOf(("undecided.cr2", CullState.Default));

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(FinishBucket.Untouched, entry.Bucket);
        Assert.Null(entry.DestinationPath);
        Assert.Equal(1, plan.UntouchedCount);
        Assert.Equal(0, plan.AffectedCount);
    }

    [Fact]
    public void UntouchedPhotosAreExcludedFromMoves()
    {
        var plan = PlanOf(
            ("a.cr2", new CullState(Flag.Picked, 0)),
            ("b.cr2", CullState.Default),
            ("c.cr2", CullState.Default));

        Assert.Single(plan.Moves);
        Assert.Equal(3, plan.Total);
        Assert.Equal(1, plan.AffectedCount);
    }

    // ---- Destination shape ----

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void StarFoldersAreNestedUnderRated(int stars)
    {
        var plan = PlanOf(("x.cr2", new CullState(Flag.Picked, stars)));
        var entry = Assert.Single(plan.Entries);

        Assert.Equal(Path.Combine(Root, "Rated", stars.ToString(), "x.cr2"), entry.DestinationPath);
    }

    [Fact]
    public void DestinationsSitUnderTheSourceRoot()
    {
        // PRD 4.3: sorted in place, not into a separately chosen target.
        var plan = PlanOf(
            ("a.cr2", new CullState(Flag.Picked, 0)),
            ("b.cr2", CullState.Default.AsRejected()),
            ("c.cr2", new CullState(Flag.Picked, 2)));

        Assert.All(plan.Moves, e => Assert.StartsWith(Root, e.DestinationPath!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RelativePathIsPreservedInsideTheBucket()
    {
        // PRD 4.4's collision policy: two cards can both hold DSC_0001.ARW, and flattening them
        // into one folder would have one silently overwrite the other.
        var plan = PlanOf(
            (Path.Combine("CardA", "DSC_0001.ARW"), new CullState(Flag.Picked, 0)),
            (Path.Combine("CardB", "DSC_0001.ARW"), new CullState(Flag.Picked, 0)));

        var destinations = plan.Moves.Select(e => e.DestinationPath).ToList();

        Assert.Equal(2, destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(Path.Combine(Root, "Approved", "CardA", "DSC_0001.ARW"), destinations);
        Assert.Contains(Path.Combine(Root, "Approved", "CardB", "DSC_0001.ARW"), destinations);
    }

    // ---- Counts ----

    [Fact]
    public void TheBucketsSumToTheTotal()
    {
        // The confirmation screen shows these rows; if they do not sum, it is lying about what
        // will happen. Nothing may be counted twice, which is the same property as the
        // stars-win rule seen from the summary's side.
        var plan = PlanOf(
            ("p1.cr2", new CullState(Flag.Picked, 0)),
            ("p2.cr2", new CullState(Flag.Picked, 0)),
            ("r1.cr2", CullState.Default.AsRejected()),
            ("s1.cr2", new CullState(Flag.Picked, 1)),
            ("s3a.cr2", new CullState(Flag.Picked, 3)),
            ("s3b.cr2", new CullState(Flag.Picked, 3)),
            ("s5.cr2", new CullState(Flag.Picked, 5)),
            ("u1.cr2", CullState.Default),
            ("u2.cr2", CullState.Default));

        var starTotal = Enumerable.Range(1, 5).Sum(plan.StarCount);
        var sum = plan.ApprovedCount + plan.RejectedCount + starTotal + plan.UntouchedCount;

        Assert.Equal(plan.Total, sum);
        Assert.Equal(9, plan.Total);
        Assert.Equal(2, plan.ApprovedCount);
        Assert.Equal(1, plan.RejectedCount);
        Assert.Equal(1, plan.StarCount(1));
        Assert.Equal(2, plan.StarCount(3));
        Assert.Equal(1, plan.StarCount(5));
        Assert.Equal(2, plan.UntouchedCount);
        Assert.Equal(7, plan.AffectedCount);
    }

    [Fact]
    public void AnEmptyFolderPlansNothing()
    {
        var plan = FinishPlanner.Plan(Root, FinishOperation.Copy, []);

        Assert.Equal(0, plan.Total);
        Assert.Empty(plan.Moves);
    }

    // ---- The rendered log ----

    [Fact]
    public void TheLogSaysPlainlyThatNothingMoved()
    {
        // Stage 1's whole safety story is that Confirm does not touch files. If the log ever
        // stops saying so, somebody will read one and believe photos were sorted.
        var text = FinishPlanner.Render(PlanOf(("a.cr2", new CullState(Flag.Picked, 0))), DateTimeOffset.Now);

        Assert.Contains("NO FILES WERE MOVED OR COPIED", text);
        Assert.Contains("dry run", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheLogRecordsTheChosenOperation()
    {
        var move = FinishPlanner.Render(PlanOf(("a.cr2", new CullState(Flag.Picked, 0))), DateTimeOffset.Now);
        Assert.Contains("Operation   : MOVE", move);

        var copyPlan = FinishPlanner.Plan(Root, FinishOperation.Copy,
            [(Path.Combine(Root, "a.cr2"), "a.cr2", new CullState(Flag.Picked, 0))]);
        Assert.Contains("Operation   : COPY", FinishPlanner.Render(copyPlan, DateTimeOffset.Now));
    }

    [Fact]
    public void TheLogListsUntouchedPhotosToo()
    {
        // A dry run exists to answer "why was this one not sorted?", which it cannot do if the
        // unsorted photos are missing from it.
        var text = FinishPlanner.Render(PlanOf(("skipme.cr2", CullState.Default)), DateTimeOffset.Now);

        Assert.Contains("[UNTOUCHED] skipme.cr2", text);
    }

    [Fact]
    public void TheLogShowsEveryDestination()
    {
        var text = FinishPlanner.Render(
            PlanOf(
                ("a.cr2", new CullState(Flag.Picked, 0)),
                ("b.cr2", CullState.Default.AsRejected()),
                ("c.cr2", new CullState(Flag.Picked, 4))),
            DateTimeOffset.Now);

        Assert.Contains(Path.Combine(Root, "Approved", "a.cr2"), text);
        Assert.Contains(Path.Combine(Root, "Rejected", "b.cr2"), text);
        Assert.Contains(Path.Combine(Root, "Rated", "4", "c.cr2"), text);
    }

    // ---- The name fallback ----

    [Theory]
    [InlineData(@"E:\Photos\Canada", null, "Canada")]
    [InlineData(@"E:\Photos\Canada\", null, "Canada")]
    [InlineData(@"E:\Photos\Canada", "", "Canada")]
    [InlineData(@"E:\Photos\Canada", "   ", "Canada")]
    [InlineData(@"E:\Photos\Canada", "Wedding", "Wedding")]
    [InlineData(@"E:\Photos\Canada", "  Wedding  ", "Wedding")]
    public void AnUnnamedSessionFallsBackToTheFolderName(string root, string? name, string expected)
        => Assert.Equal(expected, SessionStore.Describe(name, root));

    // ---- Bucket folders are excluded from the scan ----

    [Theory]
    [InlineData("Approved/a.jpg")]
    [InlineData("Rejected/a.jpg")]
    [InlineData("Rated/3/a.jpg")]
    [InlineData("Rated/3/CardA/a.jpg")]
    [InlineData("approved/a.jpg")]      // Windows paths are case-insensitive
    public void SortedOutputIsNotRescanned(string relative)
        => Assert.True(FinishPlanner.IsInsideBucket(relative.Replace('/', Path.DirectorySeparatorChar)));

    [Theory]
    [InlineData("a.jpg")]
    [InlineData("CardA/a.jpg")]
    [InlineData("ApprovedShots/a.jpg")]     // a real folder that merely starts with the word
    [InlineData("CardA/Approved/a.jpg")]    // only the TOP level is an output bucket
    public void OrdinaryFoldersAreStillScanned(string relative)
        => Assert.False(FinishPlanner.IsInsideBucket(relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void ADriveRootStillProducesALabel()
    {
        // Path.GetFileName(@"E:\") is empty, which would render a blank entry in the dropdown.
        Assert.False(string.IsNullOrWhiteSpace(SessionStore.Describe(null, @"E:\")));
    }
}
