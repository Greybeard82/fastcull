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
    [InlineData(VirtualKey.X, AppCommand.SetUnflagged)]
    public void FlagLetterKeys_ResolveRegardlessOfExtendedFlag(VirtualKey key, AppCommand expected)
    {
        Assert.Equal(expected, InputRouter.Resolve(key, isExtendedKey: true).Command);
        Assert.Equal(expected, InputRouter.Resolve(key, isExtendedKey: false).Command);
    }

    [Fact]
    public void X_IsUnflagged_NotRejected_AfterTheRemap()
    {
        // X was reassigned rather than removed: it used to mean Rejected. A stale duplicate
        // mapping here would silently reject photos the user meant to clear.
        Assert.Equal(AppCommand.SetUnflagged, InputRouter.Resolve(VirtualKey.X, isExtendedKey: false).Command);
        Assert.NotEqual(AppCommand.SetRejected, InputRouter.Resolve(VirtualKey.X, isExtendedKey: false).Command);
    }

    [Theory]
    [InlineData(VirtualKey.P)]
    [InlineData(VirtualKey.U)]
    public void OldFlagKeys_AreNowFullyUnmapped(VirtualKey key)
    {
        // P and U are not retained as aliases (PRD 2.1). Asserted explicitly rather than
        // just deleted from the old theory - a removed test proves nothing.
        Assert.Equal(AppCommand.None, InputRouter.Resolve(key, isExtendedKey: true).Command);
        Assert.Equal(AppCommand.None, InputRouter.Resolve(key, isExtendedKey: false).Command);
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

    // ---- Everything else is None ----

    [Theory]
    [InlineData(VirtualKey.A)]
    [InlineData(VirtualKey.Q)]
    [InlineData(VirtualKey.Space)]
    [InlineData(VirtualKey.Enter)]
    [InlineData(VirtualKey.Escape)]
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
