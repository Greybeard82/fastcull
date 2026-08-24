using System;
using Fastcull.ViewModels;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// PRD 1.7.1's scale and pan. The cursor-anchoring maths is the reason this type exists in Core:
/// an anchor that is off by a factor of the scale looks almost right on screen and is unmistakable
/// here.
/// </summary>
public class ZoomTransformTests
{
    private const double W = 2000;   // viewport, in the units the transform works in
    private const double H = 1000;
    private const double Tol = 1e-6;

    // ---- Range and stepping ----

    [Fact]
    public void IdentityIsFittedAndCentred()
    {
        Assert.Equal(1.0, ZoomTransform.Identity.Scale);
        Assert.Equal(0, ZoomTransform.Identity.OffsetX);
        Assert.Equal(0, ZoomTransform.Identity.OffsetY);
        Assert.True(ZoomTransform.Identity.IsFitted);
    }

    [Fact]
    public void OneStepUpIsTwentyPercent()
        => Assert.Equal(1.2, ZoomTransform.SteppedScale(1.0, 1), 6);

    [Fact]
    public void FifteenStepsUpReachesTheCeilingExactly()
    {
        // Raised from ten steps / 300% on 2026-08-24 (PRD 1.7.1). "Exactly" is the assertion that
        // matters: 1.0 plus fifteen 0.2s is 3.9999999999999996 in binary floating point, so this
        // only passes because SteppedScale snaps to the step grid.
        Assert.Equal(4.0, ZoomTransform.SteppedScale(1.0, 15), 6);
        Assert.Equal(3.8, ZoomTransform.SteppedScale(1.0, 14), 6);
    }

    [Fact]
    public void ScaleStopsAtFourHundredPercent()
    {
        Assert.Equal(4.0, ZoomTransform.SteppedScale(4.0, 1), 6);
        Assert.Equal(4.0, ZoomTransform.SteppedScale(1.0, 50), 6);
    }

    [Fact]
    public void TheBandAboveThreeHundredPercentIsNowReachable()
    {
        // The actual behaviour change, stated directly: what used to be the ceiling is now an
        // ordinary rung with five more above it.
        Assert.Equal(3.2, ZoomTransform.SteppedScale(3.0, 1), 6);
        Assert.Equal(3.6, ZoomTransform.SteppedScale(3.0, 3), 6);
    }

    [Fact]
    public void ScaleStopsAtOneHundredPercentGoingDown()
    {
        // The floor, not a midpoint: there is no zooming out past the fit.
        Assert.Equal(1.0, ZoomTransform.SteppedScale(1.0, -1), 6);
        Assert.Equal(1.0, ZoomTransform.SteppedScale(2.0, -50), 6);
    }

    [Fact]
    public void SteppingUpAndBackDownLandsExactlyOnTheFloor()
    {
        // Accumulated drift would leave IsFitted false at an apparent 100%, which would silently
        // keep panning enabled where PRD 1.7.1 says it is a no-op.
        var scale = 1.0;
        for (var i = 0; i < 10; i++) scale = ZoomTransform.SteppedScale(scale, 1);
        for (var i = 0; i < 10; i++) scale = ZoomTransform.SteppedScale(scale, -1);

        Assert.Equal(1.0, scale, 9);
        Assert.True(new ZoomTransform(scale, 0, 0).IsFitted);
    }

    // ---- Clamping ----

    [Fact]
    public void ThereIsNothingToPanAtTheFitScale()
    {
        Assert.Equal(0, ZoomTransform.MaxOffset(W, 1.0));
        Assert.Equal(0, ZoomTransform.MaxOffset(H, 1.0));
    }

    [Fact]
    public void PannableRangeIsHalfTheOverflow()
    {
        // At 2x a 2000-wide viewport the image is 4000 wide; 2000 of overflow, 1000 each side.
        Assert.Equal(1000, ZoomTransform.MaxOffset(W, 2.0), 6);
    }

    [Fact]
    public void ClampingPullsAnOutOfRangeOffsetBackToTheEdge()
    {
        var clamped = new ZoomTransform(2.0, 99999, -99999).Clamped(W, H);

        Assert.Equal(1000, clamped.OffsetX, 6);    // W*(2-1)/2
        Assert.Equal(-500, clamped.OffsetY, 6);    // H*(2-1)/2
    }

    [Fact]
    public void ClampingAlsoConstrainsScaleItself()
    {
        Assert.Equal(4.0, new ZoomTransform(10, 0, 0).Clamped(W, H).Scale, 6);
        Assert.Equal(1.0, new ZoomTransform(0.1, 0, 0).Clamped(W, H).Scale, 6);
    }

    // ---- Panning ----

    [Fact]
    public void PanningAtTheFitScaleIsANoOp()
    {
        var panned = ZoomTransform.Identity.Panned(500, -400, W, H);

        Assert.Equal(ZoomTransform.Identity, panned);
    }

    [Fact]
    public void PanningAboveTheFitScaleMovesTheImage()
    {
        var panned = new ZoomTransform(2.0, 0, 0).Panned(120, -80, W, H);

        Assert.Equal(120, panned.OffsetX, 6);
        Assert.Equal(-80, panned.OffsetY, 6);
    }

    [Fact]
    public void PanningCannotDragPastTheImageEdge()
    {
        var panned = new ZoomTransform(1.5, 0, 0).Panned(100000, 100000, W, H);

        Assert.Equal(ZoomTransform.MaxOffset(W, 1.5), panned.OffsetX, 6);
        Assert.Equal(ZoomTransform.MaxOffset(H, 1.5), panned.OffsetY, 6);
    }

    [Fact]
    public void AxesClampIndependently()
    {
        // A drag that overshoots on one axis must not drag the other back with it.
        var panned = new ZoomTransform(2.0, 0, 0).Panned(100000, 10, W, H);

        Assert.Equal(1000, panned.OffsetX, 6);
        Assert.Equal(10, panned.OffsetY, 6);
    }

    // ---- Cursor anchoring: the reason this type exists ----

    [Fact]
    public void ZoomingAtTheCentreKeepsTheImageCentred()
    {
        var zoomed = ZoomTransform.Identity.ScaledAt(0, 0, 1, W, H);

        Assert.Equal(1.2, zoomed.Scale, 6);
        Assert.Equal(0, zoomed.OffsetX, 6);
        Assert.Equal(0, zoomed.OffsetY, 6);
    }

    /// <summary>Where an image point currently sits on screen, given a transform.</summary>
    private static (double X, double Y) ScreenPointOf(ZoomTransform t, double imageX, double imageY)
        => (imageX * t.Scale + t.OffsetX, imageY * t.Scale + t.OffsetY);

    /// <summary>The image point currently under a screen position.</summary>
    private static (double X, double Y) ImagePointAt(ZoomTransform t, double screenX, double screenY)
        => ((screenX - t.OffsetX) / t.Scale, (screenY - t.OffsetY) / t.Scale);

    [Fact]
    public void ThePointUnderTheCursorStaysUnderTheCursor()
    {
        // The whole contract of "zoom toward the cursor", stated as an invariant rather than as an
        // expected offset value - which is what makes this test survive a refactor of the maths.
        const double cursorX = 400, cursorY = -220;

        var before = new ZoomTransform(1.4, 60, -30);
        var pinned = ImagePointAt(before, cursorX, cursorY);

        var after = before.ScaledAt(cursorX, cursorY, 1, W, H);
        var moved = ScreenPointOf(after, pinned.X, pinned.Y);

        Assert.Equal(cursorX, moved.X, 6);
        Assert.Equal(cursorY, moved.Y, 6);
    }

    [Fact]
    public void TheAnchorHoldsZoomingOutAsWell()
    {
        const double cursorX = -310, cursorY = 145;

        var before = new ZoomTransform(2.4, -100, 55);
        var pinned = ImagePointAt(before, cursorX, cursorY);

        var after = before.ScaledAt(cursorX, cursorY, -1, W, H);
        var moved = ScreenPointOf(after, pinned.X, pinned.Y);

        Assert.Equal(cursorX, moved.X, 6);
        Assert.Equal(cursorY, moved.Y, 6);
    }

    [Fact]
    public void TheAnchorHoldsAcrossAWholeScrollUp()
    {
        // Fifteen notches at a fixed cursor, checking the invariant at every step - the case where
        // a per-step error accumulates into something obvious. Raising the ceiling to 400% added
        // five more steps for that error to compound over, which is the point of re-running it
        // over the full new range rather than just changing the final assertion.
        const double cursorX = 250, cursorY = 90;
        var t = ZoomTransform.Identity;

        for (var i = 0; i < 15; i++)
        {
            var pinned = ImagePointAt(t, cursorX, cursorY);
            var next = t.ScaledAt(cursorX, cursorY, 1, W, H);

            // Only assert the anchor where the clamp did not have to intervene; at the edges the
            // clamp legitimately wins over the anchor, which is the correct precedence.
            var maxX = ZoomTransform.MaxOffset(W, next.Scale);
            var maxY = ZoomTransform.MaxOffset(H, next.Scale);
            var unclamped = Math.Abs(next.OffsetX) < maxX - Tol && Math.Abs(next.OffsetY) < maxY - Tol;

            if (unclamped)
            {
                var moved = ScreenPointOf(next, pinned.X, pinned.Y);
                Assert.Equal(cursorX, moved.X, 5);
                Assert.Equal(cursorY, moved.Y, 5);
            }

            t = next;
        }

        Assert.Equal(4.0, t.Scale, 6);
    }

    [Fact]
    public void TheAnchorStillHoldsOnTheFinalStepIntoTheNewCeiling()
    {
        // The specific worry with a raised ceiling: that the last notch before the rail anchors
        // differently from the ones before it. 3.8 -> 4.0 is an ordinary step and must behave like
        // one - the point under the cursor stays under the cursor.
        const double cursorX = 120, cursorY = 40;
        var before = new ZoomTransform(3.8, 0, 0);

        var pinned = ImagePointAt(before, cursorX, cursorY);
        var after = before.ScaledAt(cursorX, cursorY, 1, W, H);

        Assert.Equal(4.0, after.Scale, 6);

        // Guard the guard: an anchor assertion is vacuous if the clamp silently took over.
        Assert.True(Math.Abs(after.OffsetX) < ZoomTransform.MaxOffset(W, after.Scale) - Tol,
            "expected this step to be anchor-driven rather than clamped");

        var moved = ScreenPointOf(after, pinned.X, pinned.Y);
        Assert.Equal(cursorX, moved.X, 5);
        Assert.Equal(cursorY, moved.Y, 5);
    }

    [Fact]
    public void ZoomingAtTheCeilingChangesNothingAtAll()
    {
        var at4x = new ZoomTransform(4.0, 200, 100);

        Assert.Equal(at4x, at4x.ScaledAt(500, 300, 1, W, H));
    }

    [Fact]
    public void ZoomingOutAtTheFloorChangesNothingAtAll()
    {
        Assert.Equal(ZoomTransform.Identity, ZoomTransform.Identity.ScaledAt(500, 300, -1, W, H));
    }

    [Fact]
    public void ZoomingBackOutToTheFloorRecentresTheImage()
    {
        // The clamp does this on its own: at scale 1 the pannable range is zero, so any offset
        // accumulated on the way up is pulled back to centre on the way down.
        var t = ZoomTransform.Identity;
        for (var i = 0; i < 15; i++) t = t.ScaledAt(800, 400, 1, W, H);

        Assert.True(Math.Abs(t.OffsetX) > 0, "expected the zoom-in to have moved off centre");

        for (var i = 0; i < 15; i++) t = t.ScaledAt(800, 400, -1, W, H);

        Assert.Equal(1.0, t.Scale, 6);
        Assert.Equal(0, t.OffsetX, 6);
        Assert.Equal(0, t.OffsetY, 6);
        Assert.True(t.IsFitted);
    }

    [Fact]
    public void ADegenerateViewportDoesNotProduceNonsense()
    {
        var t = ZoomTransform.Identity.ScaledAt(0, 0, 1, 0, 0);

        Assert.Equal(1.2, t.Scale, 6);
        Assert.Equal(0, t.OffsetX, 6);
        Assert.Equal(0, t.OffsetY, 6);
    }
}
