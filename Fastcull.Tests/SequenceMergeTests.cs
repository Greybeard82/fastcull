using System;
using System.Collections.Generic;
using System.Linq;
using Fastcull.ViewModels;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// PRD 1.2's streaming scan, at the point where it could go wrong: the order photos land in.
///
/// The scan yields in filesystem order and PRD 1.3 wants capture order, so every arrival is
/// merged into its correct position rather than appended and sorted at the end. Sorting at the
/// end would reshuffle the filmstrip under a cursor the user is already culling with - the one
/// outcome this design exists to prevent - so the property that matters is that the sequence is
/// correct after EVERY batch, not just the last one.
/// </summary>
public class SequenceMergeTests
{
    private static readonly IComparer<int> Ints = Comparer<int>.Default;

    [Fact]
    public void AnEmptySequenceTakesAnythingAtZero()
        => Assert.Equal(0, SequenceMerge.FindInsertionPoint(Array.Empty<int>(), 5, Ints));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 1)]
    [InlineData(25, 2)]
    [InlineData(35, 3)]
    [InlineData(45, 4)]
    public void ACandidateLandsWhereOrderRequires(int candidate, int expected)
        => Assert.Equal(expected, SequenceMerge.FindInsertionPoint(new[] { 10, 20, 30, 40 }, candidate, Ints));

    [Fact]
    public void EqualKeysInsertAfterTheirEquals()
    {
        // Ties must not reverse. A burst of photos sharing a capture second is ordinary - a
        // continuous-drive sequence does exactly that - and inserting before equals would show
        // them backwards.
        // Index 4: after the last existing 20, before the 30.
        Assert.Equal(4, SequenceMerge.FindInsertionPoint(new[] { 10, 20, 20, 20, 30 }, 20, Ints));
    }

    [Fact]
    public void AnAppendIsFoundWithoutSearching()
    {
        // The common case: files normally arrive in name order and name order normally tracks
        // capture order, so almost every arrival belongs at the end.
        var sorted = Enumerable.Range(0, 1000).ToArray();
        Assert.Equal(1000, SequenceMerge.FindInsertionPoint(sorted, 5000, Ints));
    }

    [Fact]
    public void MergingArrivalsInAnyOrderProducesTheSortedSequence()
    {
        // The whole contract, stated as a property: whatever order the scanner yields in, the
        // sequence built by repeated insertion equals the sequence a full sort would produce.
        var rng = new Random(20260825);

        for (var trial = 0; trial < 200; trial++)
        {
            var arrivals = Enumerable.Range(0, 60).OrderBy(_ => rng.Next()).ToList();
            var built = new List<int>();

            foreach (var value in arrivals)
                built.Insert(SequenceMerge.FindInsertionPoint(built, value, Ints), value);

            Assert.Equal(Enumerable.Range(0, 60).ToList(), built);
        }
    }

    [Fact]
    public void TheSequenceIsSortedAfterEverySingleInsert()
    {
        // Not just at the end. A user can be culling while the scan is still running, so the
        // sequence has to be correct at every intermediate state too.
        var rng = new Random(11);
        var arrivals = Enumerable.Range(0, 200).OrderBy(_ => rng.Next()).ToList();
        var built = new List<int>();

        foreach (var value in arrivals)
        {
            built.Insert(SequenceMerge.FindInsertionPoint(built, value, Ints), value);

            for (var i = 1; i < built.Count; i++)
                Assert.True(built[i - 1] <= built[i],
                    $"sequence unsorted at position {i} after inserting {value}");
        }
    }

    /// <summary>
    /// The cursor-identity rule MergeBatch implements, exercised against the same insertion
    /// points the real merge uses.
    ///
    /// An insert at or before the active index shifts the active photo's ordinal by one. If the
    /// index is not nudged to follow it, a different photograph slides under a user who may be
    /// mid-keystroke - which is the reshuffle this whole design exists to avoid.
    /// </summary>
    [Fact]
    public void TheCursorKeepsItsPhotoWhenEarlierArrivalsInsert()
    {
        var rng = new Random(7);

        for (var trial = 0; trial < 100; trial++)
        {
            var built = new List<int> { 100, 200, 300, 400, 500 };
            var activeIndex = rng.Next(built.Count);
            var watched = built[activeIndex];

            foreach (var value in Enumerable.Range(0, 40).Select(_ => rng.Next(0, 600)))
            {
                var at = SequenceMerge.FindInsertionPoint(built, value, Ints);
                built.Insert(at, value);

                if (at <= activeIndex) activeIndex++;
            }

            Assert.Equal(watched, built[activeIndex]);
        }
    }
}
