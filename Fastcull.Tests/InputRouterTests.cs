using Fastcull.Input;
using Windows.System;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// Covers every row of every table in the work order's Task C.3. This is the highest-value
/// test set in the project: keyboard input cannot be injected in the dev sandbox, so these
/// tests are the only verification this logic gets.
/// </summary>
public class InputRouterTests
{
    // ---- Extended keys: the genuine grey navigation cluster ----

    [Theory]
    [InlineData(VirtualKey.Left, AppCommand.NavigatePrevious)]
    [InlineData(VirtualKey.Right, AppCommand.NavigateNext)]
    [InlineData(VirtualKey.Up, AppCommand.LadderUp)]
    [InlineData(VirtualKey.Down, AppCommand.LadderDown)]
    [InlineData(VirtualKey.Home, AppCommand.NavigateFirst)]
    [InlineData(VirtualKey.End, AppCommand.NavigateLast)]
    public void ExtendedNavigationKeys_ResolveToNavigationAndLadder(VirtualKey key, AppCommand expected)
    {
        var result = InputRouter.Resolve(key, isExtendedKey: true);
        Assert.Equal(expected, result.Command);
    }

    // ---- Non-extended: numpad with NumLock off ----

    [Theory]
    [InlineData(VirtualKey.End, 1)]        // numpad 1
    [InlineData(VirtualKey.Down, 2)]       // numpad 2
    [InlineData(VirtualKey.PageDown, 3)]   // numpad 3
    [InlineData(VirtualKey.Left, 4)]       // numpad 4
    [InlineData(VirtualKey.Clear, 5)]      // numpad 5
    [InlineData(VirtualKey.Insert, 0)]     // numpad 0
    public void NonExtendedNumpadKeys_ResolveToSetStars(VirtualKey key, int expectedStars)
    {
        var result = InputRouter.Resolve(key, isExtendedKey: false);
        Assert.Equal(AppCommand.SetStars, result.Command);
        Assert.Equal(expectedStars, result.Payload);
    }

    [Theory]
    [InlineData(VirtualKey.Right)]     // numpad 6
    [InlineData(VirtualKey.Home)]      // numpad 7
    [InlineData(VirtualKey.PageUp)]    // numpad 9
    [InlineData(VirtualKey.Up)]        // numpad 8
    public void NonExtendedUnmappedNumpadKeys_ResolveToNone(VirtualKey key)
    {
        var result = InputRouter.Resolve(key, isExtendedKey: false);
        Assert.Equal(AppCommand.None, result.Command);
    }

    // ---- The NumLock collisions PRD 1.6 calls out as a silent-failure test case ----

    [Fact]
    public void NumLockCollision_Down_IsLadderDownWhenExtended_ButTwoStarsWhenNot()
    {
        Assert.Equal(AppCommand.LadderDown, InputRouter.Resolve(VirtualKey.Down, isExtendedKey: true).Command);

        var numpad = InputRouter.Resolve(VirtualKey.Down, isExtendedKey: false);
        Assert.Equal(AppCommand.SetStars, numpad.Command);
        Assert.Equal(2, numpad.Payload);
    }

    [Fact]
    public void NumLockCollision_Left_IsNavigatePreviousWhenExtended_ButFourStarsWhenNot()
    {
        Assert.Equal(AppCommand.NavigatePrevious, InputRouter.Resolve(VirtualKey.Left, isExtendedKey: true).Command);

        var numpad = InputRouter.Resolve(VirtualKey.Left, isExtendedKey: false);
        Assert.Equal(AppCommand.SetStars, numpad.Command);
        Assert.Equal(4, numpad.Payload);
    }

    [Fact]
    public void NumLockCollision_End_IsNavigateLastWhenExtended_ButOneStarWhenNot()
    {
        Assert.Equal(AppCommand.NavigateLast, InputRouter.Resolve(VirtualKey.End, isExtendedKey: true).Command);

        var numpad = InputRouter.Resolve(VirtualKey.End, isExtendedKey: false);
        Assert.Equal(AppCommand.SetStars, numpad.Command);
        Assert.Equal(1, numpad.Payload);
    }

    // ---- Keys that mean the same thing regardless of the extended flag ----

    [Theory]
    [InlineData(VirtualKey.Number1, 1)]
    [InlineData(VirtualKey.Number2, 2)]
    [InlineData(VirtualKey.Number3, 3)]
    [InlineData(VirtualKey.Number4, 4)]
    [InlineData(VirtualKey.Number5, 5)]
    [InlineData(VirtualKey.Number0, 0)]
    [InlineData(VirtualKey.NumberPad1, 1)]
    [InlineData(VirtualKey.NumberPad2, 2)]
    [InlineData(VirtualKey.NumberPad3, 3)]
    [InlineData(VirtualKey.NumberPad4, 4)]
    [InlineData(VirtualKey.NumberPad5, 5)]
    [InlineData(VirtualKey.NumberPad0, 0)]
    public void DigitKeys_SetStars_RegardlessOfExtendedFlag(VirtualKey key, int expectedStars)
    {
        foreach (var extended in new[] { true, false })
        {
            var result = InputRouter.Resolve(key, extended);
            Assert.Equal(AppCommand.SetStars, result.Command);
            Assert.Equal(expectedStars, result.Payload);
        }
    }

    [Theory]
    [InlineData(VirtualKey.C, AppCommand.SetPicked)]
    [InlineData(VirtualKey.Z, AppCommand.SetRejected)]
    [InlineData(VirtualKey.X, AppCommand.SetRejected)]
    public void FlagLetterKeys_ResolveRegardlessOfExtendedFlag(VirtualKey key, AppCommand expected)
    {
        Assert.Equal(expected, InputRouter.Resolve(key, isExtendedKey: true).Command);
        Assert.Equal(expected, InputRouter.Resolve(key, isExtendedKey: false).Command);
    }

    [Theory]
    [InlineData(VirtualKey.P, AppCommand.SetPicked)]
    [InlineData(VirtualKey.U, AppCommand.SetUnflagged)]
    public void P_And_U_AreMappedAgain(VirtualKey key, AppCommand expected)
    {
        // This reverses PRD 2.1, which unmapped both outright ("not retained as aliases") and
        // had a test asserting they resolved to None. Restored 2026-08-23 by explicit
        // instruction. Recorded rather than quietly changed - the previous state was a decision.
        Assert.Equal(expected, InputRouter.Resolve(key, isExtendedKey: true).Command);
        Assert.Equal(expected, InputRouter.Resolve(key, isExtendedKey: false).Command);
    }

    [Fact]
    public void X_IsRejected_Again_And_U_IsWhatClears()
    {
        // X previously meant Unflagged - it had been reassigned away from Rejected, guarded by a
        // test asserting it was NOT Rejected. That reassignment is now itself reversed: X rejects
        // and U is what clears to unrated.
        Assert.Equal(AppCommand.SetRejected, InputRouter.Resolve(VirtualKey.X, isExtendedKey: false).Command);
        Assert.Equal(AppCommand.SetUnflagged, InputRouter.Resolve(VirtualKey.U, isExtendedKey: false).Command);
    }

    [Fact]
    public void ArrowUpAndDown_StepTheLadder_WhileTheFlagKeysSetDirectly()
    {
        // Two different rating gestures coexist: the arrows walk the PRD 1.6 ladder one position
        // at a time, while P/X/U jump straight to a flag. Asserted together because they were
        // briefly collapsed into one - Up/Down set flags directly - and that made states 3-7
        // (Picked with 1-5 stars) unreachable from the keyboard.
        Assert.Equal(AppCommand.LadderUp, InputRouter.Resolve(VirtualKey.Up, isExtendedKey: true).Command);
        Assert.Equal(AppCommand.LadderDown, InputRouter.Resolve(VirtualKey.Down, isExtendedKey: true).Command);

        Assert.Equal(AppCommand.SetPicked, InputRouter.Resolve(VirtualKey.P, isExtendedKey: false).Command);
        Assert.Equal(AppCommand.SetRejected, InputRouter.Resolve(VirtualKey.X, isExtendedKey: false).Command);
        Assert.Equal(AppCommand.SetUnflagged, InputRouter.Resolve(VirtualKey.U, isExtendedKey: false).Command);

        // The NumLock split still holds: a non-extended Down is numpad 2, not a ladder step.
        Assert.Equal(AppCommand.SetStars, InputRouter.Resolve(VirtualKey.Down, isExtendedKey: false).Command);
    }

    [Fact]
    public void NewFlagKeys_DoNotCollideWithNumpadOrNavigationLogic()
    {
        // Z/X/C are not numpad keys on any standard layout, so the extended-key split must
        // not change their meaning. Verified rather than assumed.
        foreach (var key in new[] { VirtualKey.Z, VirtualKey.X, VirtualKey.C })
        {
            var extended = InputRouter.Resolve(key, isExtendedKey: true);
            var notExtended = InputRouter.Resolve(key, isExtendedKey: false);

            Assert.Equal(extended.Command, notExtended.Command);
            Assert.Equal(extended.Payload, notExtended.Payload);
            Assert.NotEqual(AppCommand.None, extended.Command);
            Assert.NotEqual(AppCommand.SetStars, extended.Command);
        }
    }

    // ---- Rotation (PRD 1.11) ----

    [Theory]
    [InlineData(VirtualKey.A, AppCommand.RotateLeft)]
    [InlineData(VirtualKey.S, AppCommand.RotateRight)]
    public void RotationKeys_ResolveRegardlessOfExtendedFlag(VirtualKey key, AppCommand expected)
    {
        // Letter keys, so the NumLock/extended split must never shadow them.
        Assert.Equal(expected, InputRouter.Resolve(key, isExtendedKey: true).Command);
        Assert.Equal(expected, InputRouter.Resolve(key, isExtendedKey: false).Command);
    }

    [Fact]
    public void A_TurnsLeft_And_S_TurnsRight()
    {
        // The keys run the way the photo does: A is left of S and turns the photo left.
        //
        // This reverses the original mapping, which had A meaning clockwise. That was specified
        // deliberately and guarded by a test saying not to swap it - and it still turned out
        // backwards in the hand, so it was reversed on 2026-08-23. Recorded here rather than
        // quietly changed, because the previous direction was an explicit decision too.
        Assert.Equal(AppCommand.RotateLeft, InputRouter.Resolve(VirtualKey.A, isExtendedKey: false).Command);
        Assert.Equal(AppCommand.RotateRight, InputRouter.Resolve(VirtualKey.S, isExtendedKey: false).Command);
    }

    [Fact]
    public void RotationKeys_CarryNoPayload_AndAreNotRatingOrNavigationCommands()
    {
        foreach (var key in new[] { VirtualKey.A, VirtualKey.S })
        {
            var resolved = InputRouter.Resolve(key, isExtendedKey: false);

            Assert.Equal(0, resolved.Payload);
            Assert.NotEqual(AppCommand.SetStars, resolved.Command);
            Assert.NotEqual(AppCommand.LadderUp, resolved.Command);
            Assert.NotEqual(AppCommand.LadderDown, resolved.Command);
            Assert.NotEqual(AppCommand.NavigateNext, resolved.Command);
            Assert.NotEqual(AppCommand.NavigatePrevious, resolved.Command);
        }
    }

    [Fact]
    public void RotationKeys_DoNotCollideWithTheFlagLetters()
    {
        var rotation = new[] { VirtualKey.A, VirtualKey.S };
        var flags = new[] { VirtualKey.Z, VirtualKey.X, VirtualKey.C };

        foreach (var r in rotation)
            foreach (var f in flags)
                Assert.NotEqual(
                    InputRouter.Resolve(f, isExtendedKey: false).Command,
                    InputRouter.Resolve(r, isExtendedKey: false).Command);
    }

    // ---- Zoom ----

    [Theory]
    [InlineData(VirtualKey.Space, AppCommand.ToggleZoom)]
    [InlineData(VirtualKey.Escape, AppCommand.ExitZoom)]
    public void ZoomKeys_ResolveRegardlessOfExtendedFlag(VirtualKey key, AppCommand expected)
    {
        Assert.Equal(expected, InputRouter.Resolve(key, isExtendedKey: true).Command);
        Assert.Equal(expected, InputRouter.Resolve(key, isExtendedKey: false).Command);
    }

    [Fact]
    public void EscapeOnlyExits_ItNeverToggles()
    {
        // Escape must be safe to press when already un-zoomed, so it is a distinct command
        // rather than a second toggle - otherwise it would zoom IN from the un-zoomed state.
        Assert.Equal(AppCommand.ExitZoom, InputRouter.Resolve(VirtualKey.Escape, isExtendedKey: false).Command);
        Assert.NotEqual(AppCommand.ToggleZoom, InputRouter.Resolve(VirtualKey.Escape, isExtendedKey: false).Command);
    }

    [Fact]
    public void ZoomKeys_DoNotCollideWithRatingRotationOrNavigation()
    {
        foreach (var key in new[] { VirtualKey.Space, VirtualKey.Escape })
        {
            var resolved = InputRouter.Resolve(key, isExtendedKey: false);

            Assert.Equal(0, resolved.Payload);
            Assert.NotEqual(AppCommand.SetStars, resolved.Command);
            Assert.NotEqual(AppCommand.RotateLeft, resolved.Command);
            Assert.NotEqual(AppCommand.RotateRight, resolved.Command);
            Assert.NotEqual(AppCommand.NavigateNext, resolved.Command);
            Assert.NotEqual(AppCommand.NavigatePrevious, resolved.Command);
        }
    }

    // ---- Everything else is None ----

    [Theory]
    [InlineData(VirtualKey.Q)]
    [InlineData(VirtualKey.Enter)]
    [InlineData(VirtualKey.Tab)]
    [InlineData(VirtualKey.Delete)]
    [InlineData(VirtualKey.F1)]
    [InlineData(VirtualKey.Control)]
    [InlineData(VirtualKey.Shift)]
    [InlineData(VirtualKey.Number6)]
    [InlineData(VirtualKey.Number9)]
    [InlineData(VirtualKey.NumberPad6)]
    [InlineData(VirtualKey.NumberPad9)]
    public void UnmappedKeys_ResolveToNone(VirtualKey key)
    {
        Assert.Equal(AppCommand.None, InputRouter.Resolve(key, isExtendedKey: true).Command);
        Assert.Equal(AppCommand.None, InputRouter.Resolve(key, isExtendedKey: false).Command);
    }

    [Fact]
    public void PageUpAndPageDown_ExtendedAreUnmapped()
    {
        // Only the non-extended (numpad 3) form of PageDown means anything; PRD 2.1 does not
        // bind the grey PageUp/PageDown in filmstrip mode.
        Assert.Equal(AppCommand.None, InputRouter.Resolve(VirtualKey.PageUp, isExtendedKey: true).Command);
        Assert.Equal(AppCommand.None, InputRouter.Resolve(VirtualKey.PageDown, isExtendedKey: true).Command);
    }
}
