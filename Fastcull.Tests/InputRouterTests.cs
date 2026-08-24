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
    [InlineData(VirtualKey.P)]        // was SetPicked
    [InlineData(VirtualKey.C)]        // was SetPicked
    [InlineData(VirtualKey.X)]        // was SetRejected
    [InlineData(VirtualKey.Z)]        // was SetRejected
    [InlineData(VirtualKey.U)]        // was SetUnflagged
    [InlineData(VirtualKey.Number0)]  // was clear-stars
    [InlineData(VirtualKey.Escape)]   // was ExitZoom
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
    public void NoDirectFlagSetKeyRemains()
    {
        // PRD 2.1.1's consequence, pinned: the ladder is reachable only by stepping. If a
        // direct-set key is reintroduced later this test is the one that should be updated
        // deliberately, rather than the behaviour changing unnoticed.
        var everyKey = System.Enum.GetValues<VirtualKey>();

        foreach (var key in everyKey)
        {
            foreach (var extended in new[] { true, false })
            {
                var command = InputRouter.Resolve(key, extended).Command;

                Assert.NotEqual(AppCommand.SetPicked, command);
                Assert.NotEqual(AppCommand.SetRejected, command);
                Assert.NotEqual(AppCommand.SetUnflagged, command);
            }
        }
    }

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
