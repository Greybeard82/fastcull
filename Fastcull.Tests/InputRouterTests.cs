using System.Collections.Generic;
using Fastcull.Input;
using Windows.System;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// The full PRD 2.1 key map, rewritten for the 2026-08-24 one-handed revamp.
///
/// This is the highest-value test set in the project: keyboard input cannot be injected in the
/// dev sandbox, so these tests are the only verification this logic gets. They cover three things
/// deliberately — that every new binding resolves, that the letter keys are *identical* to their
/// arrow twins rather than merely similar, and that every removed binding is genuinely dead
/// rather than quietly still working alongside its replacement.
/// </summary>
public class InputRouterTests
{
    /// <summary>Resolves a key both ways, for bindings that must not depend on the extended bit.</summary>
    private static void AssertResolvesEitherWay(VirtualKey key, AppCommand expected, int payload = 0)
    {
        foreach (var extended in new[] { true, false })
        {
            var result = InputRouter.Resolve(key, extended);
            Assert.Equal(expected, result.Command);
            Assert.Equal(payload, result.Payload);
        }
    }

    // ---- The WASD cluster ----

    [Theory]
    [InlineData(VirtualKey.A, AppCommand.NavigatePrevious)]
    [InlineData(VirtualKey.D, AppCommand.NavigateNext)]
    [InlineData(VirtualKey.W, AppCommand.LadderUp)]
    [InlineData(VirtualKey.S, AppCommand.LadderDown)]
    public void WasdDrivesCursorAndRating(VirtualKey key, AppCommand expected)
        => AssertResolvesEitherWay(key, expected);

    // ---- Arrow keys, and the duplicate contract ----

    [Theory]
    [InlineData(VirtualKey.Left, AppCommand.NavigatePrevious)]
    [InlineData(VirtualKey.Right, AppCommand.NavigateNext)]
    [InlineData(VirtualKey.Up, AppCommand.LadderUp)]
    [InlineData(VirtualKey.Down, AppCommand.LadderDown)]
    public void ArrowKeysStillDriveCursorAndRating(VirtualKey key, AppCommand expected)
        => Assert.Equal(expected, InputRouter.Resolve(key, isExtendedKey: true).Command);

    [Theory]
    [InlineData(VirtualKey.A, VirtualKey.Left)]
    [InlineData(VirtualKey.D, VirtualKey.Right)]
    [InlineData(VirtualKey.W, VirtualKey.Up)]
    [InlineData(VirtualKey.S, VirtualKey.Down)]
    public void TheLetterKeysAreTheSameActionAsTheirArrowTwin(VirtualKey letter, VirtualKey arrow)
    {
        // PRD 2.1: these are same-hand duplicates, not parallel behaviours. Asserting the whole
        // ResolvedInput - command AND payload - is what stops the two drifting apart later.
        var byLetter = InputRouter.Resolve(letter, isExtendedKey: false);
        var byArrow = InputRouter.Resolve(arrow, isExtendedKey: true);

        Assert.Equal(byArrow, byLetter);
    }

    [Fact]
    public void RateUpAndRateDownStepTheLadderRatherThanSettingAFlag()
    {
        // The distinction PRD 2.1 rests on: W/S and Up/Down are the ONLY keys that step.
        foreach (var key in new[] { VirtualKey.W, VirtualKey.Up })
            Assert.Equal(AppCommand.LadderUp, InputRouter.Resolve(key, key == VirtualKey.Up).Command);

        foreach (var key in new[] { VirtualKey.S, VirtualKey.Down })
            Assert.Equal(AppCommand.LadderDown, InputRouter.Resolve(key, key == VirtualKey.Down).Command);
    }

    // ---- Rotation, moved off A/S ----

    [Theory]
    [InlineData(VirtualKey.Q, AppCommand.RotateLeft)]
    [InlineData(VirtualKey.E, AppCommand.RotateRight)]
    public void QAndERotate(VirtualKey key, AppCommand expected)
        => AssertResolvesEitherWay(key, expected);

    [Fact]
    public void RotationNoLongerLivesOnAOrS()
    {
        // A and S are now navigation and rating. If rotation ever resolved from them again the
        // cursor keys would silently start turning photos.
        Assert.NotEqual(AppCommand.RotateLeft, InputRouter.Resolve(VirtualKey.A, false).Command);
        Assert.NotEqual(AppCommand.RotateRight, InputRouter.Resolve(VirtualKey.S, false).Command);
    }

    // ---- Jumps ----

    [Theory]
    [InlineData(VirtualKey.R, AppCommand.NavigateFirst)]
    [InlineData(VirtualKey.T, AppCommand.NavigateLast)]
    public void RAndTJumpToTheEnds(VirtualKey key, AppCommand expected)
        => AssertResolvesEitherWay(key, expected);

    // ---- Overlay, folder picker, zoom, delete ----

    [Theory]
    [InlineData(VirtualKey.F)]
    [InlineData(VirtualKey.I)]
    public void FAndIBothToggleTheOverlay(VirtualKey key)
        => AssertResolvesEitherWay(key, AppCommand.ToggleInfo);

    [Fact]
    public void FAndIAreTheSameAction()
        => Assert.Equal(
            InputRouter.Resolve(VirtualKey.I, false),
            InputRouter.Resolve(VirtualKey.F, false));

    [Fact]
    public void GOpensTheFolderPicker()
        => AssertResolvesEitherWay(VirtualKey.G, AppCommand.OpenFolder);

    [Fact]
    public void SpaceTogglesZoom()
        => AssertResolvesEitherWay(VirtualKey.Space, AppCommand.ToggleZoom);

    [Fact]
    public void DeleteRecyclesThePhoto()
        => AssertResolvesEitherWay(VirtualKey.Delete, AppCommand.DeletePhoto);

    // ---- Stars: top row and numpad ----

    [Theory]
    [InlineData(VirtualKey.Number1, 1)]
    [InlineData(VirtualKey.Number2, 2)]
    [InlineData(VirtualKey.Number3, 3)]
    [InlineData(VirtualKey.Number4, 4)]
    [InlineData(VirtualKey.Number5, 5)]
    public void TopRowDigitsSetStars(VirtualKey key, int stars)
        => AssertResolvesEitherWay(key, AppCommand.SetStars, stars);

    [Theory]
    [InlineData(VirtualKey.NumberPad1, 1)]
    [InlineData(VirtualKey.NumberPad2, 2)]
    [InlineData(VirtualKey.NumberPad3, 3)]
    [InlineData(VirtualKey.NumberPad4, 4)]
    [InlineData(VirtualKey.NumberPad5, 5)]
    public void NumpadDigitsSetStars_NumLockOn(VirtualKey key, int stars)
        => AssertResolvesEitherWay(key, AppCommand.SetStars, stars);

    [Theory]
    [InlineData(VirtualKey.End, 1)]        // numpad 1
    [InlineData(VirtualKey.Down, 2)]       // numpad 2
    [InlineData(VirtualKey.PageDown, 3)]   // numpad 3
    [InlineData(VirtualKey.Left, 4)]       // numpad 4
    [InlineData(VirtualKey.Clear, 5)]      // numpad 5
    public void NumpadDigitsSetStars_NumLockOff(VirtualKey key, int stars)
    {
        // The NumLock split, untouched by the revamp: with NumLock off the numpad emits
        // navigation keycodes, and only the extended-key bit separates them from the grey keys.
        var result = InputRouter.Resolve(key, isExtendedKey: false);

        Assert.Equal(AppCommand.SetStars, result.Command);
        Assert.Equal(stars, result.Payload);
    }

    [Theory]
    [InlineData(VirtualKey.Down, AppCommand.LadderDown)]
    [InlineData(VirtualKey.Left, AppCommand.NavigatePrevious)]
    [InlineData(VirtualKey.End, AppCommand.None)]
    public void TheSameKeycodeMeansSomethingElseWhenItIsTheGreyKey(VirtualKey key, AppCommand expected)
    {
        // The other half of the split: numpad 2 and the Down arrow share a keycode, and rating
        // must not fire when the user pressed the grey arrow.
        Assert.Equal(expected, InputRouter.Resolve(key, isExtendedKey: true).Command);
    }

    // ---- Everything the revamp removed is genuinely dead ----

    [Theory]
    [InlineData(VirtualKey.P)]        // was SetPicked - C replaced it
    [InlineData(VirtualKey.U)]        // was SetUnflagged - X replaced it
    [InlineData(VirtualKey.Number0)]  // was clear-stars - nothing replaced it
    public void RemovedBindingsDoNothingAtAll(VirtualKey key)
    {
        // Not "resolves to something else" - resolves to None. A removed key that quietly kept
        // working alongside its replacement is the failure this guards against.
        Assert.Equal(AppCommand.None, InputRouter.Resolve(key, isExtendedKey: true).Command);
        Assert.Equal(AppCommand.None, InputRouter.Resolve(key, isExtendedKey: false).Command);
    }

    [Theory]
    [InlineData(VirtualKey.Home)]
    [InlineData(VirtualKey.End)]
    public void HomeAndEndNoLongerJump(VirtualKey key)
    {
        // R and T replaced them. End still means numpad-1 when NOT extended, which is why this
        // asserts only the extended (genuine grey key) case.
        Assert.Equal(AppCommand.None, InputRouter.Resolve(key, isExtendedKey: true).Command);
    }

    [Fact]
    public void ExactlyOneKeyReachesEachDirectSetCommand()
    {
        // PRD 2.1.1: the direct-set flags are back, on Z/X/C. Sweeping every VirtualKey rather
        // than asserting the three individually is what catches the real risk - a SECOND key
        // quietly resolving to the same command, which is how the old P/C pair drifted apart.
        var byCommand = new Dictionary<AppCommand, List<VirtualKey>>
        {
            [AppCommand.SetRejected] = [],
            [AppCommand.SetUnflagged] = [],
            [AppCommand.SetPicked] = [],
        };

        foreach (var key in System.Enum.GetValues<VirtualKey>())
        {
            foreach (var extended in new[] { true, false })
            {
                var command = InputRouter.Resolve(key, extended).Command;
                if (byCommand.TryGetValue(command, out var keys) && !keys.Contains(key))
                    keys.Add(key);
            }
        }

        Assert.Equal([VirtualKey.Z], byCommand[AppCommand.SetRejected]);
        Assert.Equal([VirtualKey.X], byCommand[AppCommand.SetUnflagged]);
        Assert.Equal([VirtualKey.C], byCommand[AppCommand.SetPicked]);
    }

    [Theory]
    [InlineData(VirtualKey.Z, AppCommand.SetRejected)]
    [InlineData(VirtualKey.X, AppCommand.SetUnflagged)]
    [InlineData(VirtualKey.C, AppCommand.SetPicked)]
    public void TheBottomRowJumpsStraightToAFlag(VirtualKey key, AppCommand expected)
        => AssertResolvesEitherWay(key, expected);

    [Fact]
    public void JumpingAndSteppingAreDifferentCommands()
    {
        // The distinction PRD 2.1.1 rests on: Z/X/C set a rung outright, W/S walk to one. Both
        // are available; neither is implemented in terms of the other.
        foreach (var jump in new[] { VirtualKey.Z, VirtualKey.X, VirtualKey.C })
        {
            var command = InputRouter.Resolve(jump, isExtendedKey: false).Command;
            Assert.NotEqual(AppCommand.LadderUp, command);
            Assert.NotEqual(AppCommand.LadderDown, command);
        }
    }

    // ---- Zoom: Space toggles, Escape only exits ----

    [Fact]
    public void EscapeExitsZoom()
        => AssertResolvesEitherWay(VirtualKey.Escape, AppCommand.ExitZoom);

    [Fact]
    public void EscapeCanNeverBeTheKeyThatEntersZoom()
    {
        // The whole reason both keys exist. Space is a toggle and so can enter; Escape resolves
        // to ExitZoom unconditionally, which is a no-op when not zoomed. If Escape ever resolved
        // to ToggleZoom it would start pulling the user INTO zoom when they meant to back out.
        Assert.NotEqual(AppCommand.ToggleZoom, InputRouter.Resolve(VirtualKey.Escape, true).Command);
        Assert.NotEqual(AppCommand.ToggleZoom, InputRouter.Resolve(VirtualKey.Escape, false).Command);
    }

    // ---- Shift+Space: standalone fullscreen ----

    [Fact]
    public void ShiftSpaceTogglesStandaloneFullScreen()
    {
        foreach (var extended in new[] { true, false })
            Assert.Equal(AppCommand.ToggleFullScreen,
                InputRouter.Resolve(VirtualKey.Space, extended, isShiftDown: true).Command);
    }

    [Fact]
    public void SpaceWithoutShiftIsStillZoomAndNeverFullScreen()
    {
        // The two live on one key, so the modifier is the only thing keeping them apart. If this
        // ever inverted, every zoom would resize the window instead.
        foreach (var extended in new[] { true, false })
            Assert.Equal(AppCommand.ToggleZoom,
                InputRouter.Resolve(VirtualKey.Space, extended, isShiftDown: false).Command);
    }

    [Fact]
    public void ShiftChangesNothingExceptSpace()
    {
        // Holding Shift while rating or navigating must not silently mean something else - a
        // caps-locked hand on WASD should still cull. Space is the one documented exception.
        foreach (var key in System.Enum.GetValues<VirtualKey>())
        {
            if (key == VirtualKey.Space) continue;

            foreach (var extended in new[] { true, false })
            {
                Assert.Equal(
                    InputRouter.Resolve(key, extended, isShiftDown: false),
                    InputRouter.Resolve(key, extended, isShiftDown: true));
            }
        }
    }

    [Fact]
    public void TheDefaultOverloadMeansShiftIsNotHeld()
        => Assert.Equal(
            InputRouter.Resolve(VirtualKey.Space, false, isShiftDown: false),
            InputRouter.Resolve(VirtualKey.Space, false));

    // ---- Ctrl: undo and redo (PRD 1.9) ----

    [Fact]
    public void CtrlZUndoesAndCtrlYRedoes()
    {
        foreach (var extended in new[] { true, false })
        {
            Assert.Equal(AppCommand.Undo,
                InputRouter.Resolve(VirtualKey.Z, extended, isControlDown: true).Command);
            Assert.Equal(AppCommand.Redo,
                InputRouter.Resolve(VirtualKey.Y, extended, isControlDown: true).Command);
        }
    }

    [Fact]
    public void CtrlZIsNotReject()
    {
        // Z alone is Reject. If the Ctrl chord fell through to it, the keystroke meant to take a
        // rating back would apply the very rating being undone.
        Assert.Equal(AppCommand.SetRejected, InputRouter.Resolve(VirtualKey.Z, false).Command);
        Assert.NotEqual(AppCommand.SetRejected,
            InputRouter.Resolve(VirtualKey.Z, false, isControlDown: true).Command);
    }

    [Fact]
    public void EveryOtherCtrlChordIsSwallowed()
    {
        // Ctrl+S reaching the rating ladder would be a nasty surprise for a hand trained on every
        // other application.
        foreach (var key in System.Enum.GetValues<VirtualKey>())
        {
            if (key is VirtualKey.Z or VirtualKey.Y) continue;

            foreach (var extended in new[] { true, false })
                Assert.Equal(AppCommand.None,
                    InputRouter.Resolve(key, extended, isControlDown: true).Command);
        }
    }

    [Fact]
    public void CtrlBeatsShiftOnTheSameKey()
        => Assert.Equal(AppCommand.Undo,
            InputRouter.Resolve(VirtualKey.Z, false, isShiftDown: true, isControlDown: true).Command);

    [Fact]
    public void WithoutCtrlNothingResolvesToUndoOrRedo()
    {
        foreach (var key in System.Enum.GetValues<VirtualKey>())
        {
            foreach (var extended in new[] { true, false })
            {
                foreach (var shift in new[] { true, false })
                {
                    var command = InputRouter.Resolve(key, extended, shift).Command;
                    Assert.NotEqual(AppCommand.Undo, command);
                    Assert.NotEqual(AppCommand.Redo, command);
                }
            }
        }
    }

    // ---- Sidebar pin and help ----

    [Fact]
    public void VTogglesTheSidebarPin()
        => AssertResolvesEitherWay(VirtualKey.V, AppCommand.ToggleSidebarPin);

    [Fact]
    public void HTogglesTheHelpOverlay()
        => AssertResolvesEitherWay(VirtualKey.H, AppCommand.ToggleHelp);

    [Fact]
    public void SpaceAndEscapeAreNotTheSameCommand()
        => Assert.NotEqual(
            InputRouter.Resolve(VirtualKey.Space, false).Command,
            InputRouter.Resolve(VirtualKey.Escape, false).Command);

    [Fact]
    public void NoKeyClearsStars()
    {
        // SetStars with payload 0 was the clear-stars binding on 0/NumPad0. Both are gone.
        foreach (var key in System.Enum.GetValues<VirtualKey>())
        {
            foreach (var extended in new[] { true, false })
            {
                var resolved = InputRouter.Resolve(key, extended);
                if (resolved.Command == AppCommand.SetStars)
                    Assert.InRange(resolved.Payload, 1, 5);
            }
        }
    }

    // ---- Everything else is None ----

    [Theory]
    [InlineData(VirtualKey.Enter)]
    [InlineData(VirtualKey.Tab)]
    [InlineData(VirtualKey.F1)]
    [InlineData(VirtualKey.Control)]
    [InlineData(VirtualKey.Shift)]
    [InlineData(VirtualKey.B)]
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
    public void ResolveIsPureAndRepeatable()
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(AppCommand.NavigateNext, InputRouter.Resolve(VirtualKey.D, false).Command);
            Assert.Equal(AppCommand.LadderUp, InputRouter.Resolve(VirtualKey.W, false).Command);
        }
    }
}
