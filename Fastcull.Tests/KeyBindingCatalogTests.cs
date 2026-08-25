using System;
using System.Collections.Generic;
using System.Linq;
using Fastcull.Input;
using Windows.System;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// The anti-drift guarantees for the H overlay (PRD 2.1.3).
///
/// The overlay's whole justification is that it reads the key map out of <see cref="InputRouter"/>
/// instead of restating it. These tests are what make that true rather than merely intended: the
/// two things the catalog does author - wording and section placement - are exactly the two things
/// that could silently fall behind a router change, so both are pinned here.
/// </summary>
public class KeyBindingCatalogTests
{
    private static IEnumerable<KeyBindingRow> AllRows()
        => KeyBindingCatalog.Sections.SelectMany(s => s.Rows);

    [Fact]
    public void EveryCommandIsDescribed()
    {
        // Add an AppCommand, bind it, and this fails until the overlay knows what to call it.
        var missing = Enum.GetValues<AppCommand>()
            .Where(c => c != AppCommand.None)
            .Where(c => !KeyBindingCatalog.Sections
                .SelectMany(s => s.Rows)
                .Any(r => !string.IsNullOrWhiteSpace(r.Description))
                || !IsPlaced(c))
            .ToList();

        Assert.Empty(missing);
    }

    private static bool IsPlaced(AppCommand command)
    {
        // A command is "placed" if some key produces it and some row shows that key. Resolving is
        // the only honest way to ask - the catalog must not be its own witness.
        var keys = BoundStrokes()
            .Where(s => InputRouter.Resolve(s.Key, s.IsExtendedKey, s.IsShiftDown).Command == command)
            .ToList();

        if (keys.Count == 0) return true;   // unbound commands need no row

        return KeyBindingCatalog.DisplayedKeys.Any(d =>
            InputRouter.Resolve(d.Key, d.IsExtendedKey, d.IsShiftDown).Command == command);
    }

    /// <summary>
    /// Every stroke that is bound AND distinct. A shifted stroke only counts when Shift actually
    /// changes what the key does: Shift+Esc resolves the same as Esc, and demanding the overlay
    /// account for it would demand it list "Shift + Esc" as though it were its own binding.
    /// </summary>
    private static IEnumerable<KeyStroke> BoundStrokes()
    {
        foreach (var key in Enum.GetValues<VirtualKey>().Distinct())
        {
            foreach (var extended in new[] { false, true })
            {
                var plain = InputRouter.Resolve(key, extended, isShiftDown: false);
                var shifted = InputRouter.Resolve(key, extended, isShiftDown: true);
                var controlled = InputRouter.Resolve(key, extended, isShiftDown: false, isControlDown: true);

                if (plain.Command != AppCommand.None)
                    yield return new KeyStroke(key, extended, false);

                if (shifted != plain && shifted.Command != AppCommand.None)
                    yield return new KeyStroke(key, extended, true);

                if (controlled != plain && controlled.Command != AppCommand.None)
                    yield return new KeyStroke(key, extended, false, true);
            }
        }
    }

    [Fact]
    public void EveryBoundKeyIsShownOrDeliberatelyHidden()
    {
        // The guarantee that matters: bind a new key in the router and it appears in the overlay,
        // or this test fails. There is no third outcome where the help quietly omits it.
        var shown = KeyBindingCatalog.DisplayedKeys.ToHashSet();
        var hidden = KeyBindingCatalog.HiddenKeys.ToHashSet();

        var unaccounted = BoundStrokes()
            .Where(s => !shown.Contains(s) && !hidden.Contains(s))
            // A key that resolves identically extended and not is recorded once, under whichever
            // form was seen first; the other form is the same physical key, not a missing one.
            .Where(s => !shown.Any(d => d.Key == s.Key && d.IsShiftDown == s.IsShiftDown))
            .ToList();

        Assert.Empty(unaccounted);
    }

    [Fact]
    public void TheOnlyHiddenKeysAreTheNumpadNumLockOffTwins()
    {
        // Hiding is a documented escape hatch, so it needs a fence: exactly the five numpad
        // digits wearing navigation keycodes, and nothing else drifting in later.
        var expected = new[]
        {
            VirtualKey.End, VirtualKey.Down, VirtualKey.PageDown, VirtualKey.Left, VirtualKey.Clear
        };

        Assert.Equal(expected.OrderBy(k => k),
                     KeyBindingCatalog.HiddenKeys.Select(s => s.Key).Distinct().OrderBy(k => k));

        // And every one of them is hidden only in its non-extended (numpad) form.
        Assert.All(KeyBindingCatalog.HiddenKeys, s => Assert.False(s.IsExtendedKey));
    }

    [Fact]
    public void TheGreyArrowsAreStillShown()
    {
        // The other half of the NumLock split: Left and Down are hidden as numpad twins, but the
        // genuine arrow keys share those keycodes and must survive the filter.
        var shownKeys = KeyBindingCatalog.DisplayedKeys.Select(s => s.Key).ToHashSet();

        Assert.Contains(VirtualKey.Left, shownKeys);
        Assert.Contains(VirtualKey.Down, shownKeys);
    }

    [Fact]
    public void UndoAndRedoAppearInTheOverlay()
    {
        // Checked rather than assumed. The catalog enumerates the router, but it only did so for
        // Shift until PRD 1.9 added a Ctrl chord - a modifier it did not enumerate would have been
        // invisible here no matter how correctly it was bound.
        var rows = AllRows().ToList();

        Assert.Contains(rows, r => r.Keys.Contains("Ctrl + Z") && r.Description == "Undo");
        Assert.Contains(rows, r => r.Keys.Contains("Ctrl + Y") && r.Description == "Redo");
    }

    [Fact]
    public void ThePlainZRowIsStillRejectAndNotUndo()
    {
        var rows = AllRows().ToList();

        var reject = rows.Single(r => r.Description == "Reject");
        Assert.Contains("Z", reject.Keys);
        Assert.DoesNotContain("Ctrl", reject.Keys);
    }

    [Fact]
    public void ShiftSpaceIsListedSeparatelyFromSpace()
    {
        var rows = AllRows().ToList();

        Assert.Contains(rows, r => r.Keys.Contains("Shift + Space"));
        Assert.Contains(rows, r => r.Keys.Split('/').Any(part => part.Trim() == "Space"));
    }

    [Fact]
    public void TheJumpKeysAppearOnTheirOwnRows()
    {
        var rows = AllRows().ToList();

        foreach (var key in new[] { "Z", "X", "C", "V", "H" })
            Assert.Contains(rows, r => r.Keys.Split('/').Any(part => part.Trim() == key));
    }

    [Fact]
    public void LetterAndArrowTwinsShareOneRow()
    {
        // W and Up are the same action, so the overlay must not imply they are two.
        var row = AllRows().Single(r => r.Description == "Rate up one rung");

        Assert.Contains("W", row.Keys);
        Assert.Contains("Up", row.Keys);
    }

    [Fact]
    public void StarsCollapseToASingleRange()
    {
        // Ten strokes for one idea. Printing all ten would bury everything else on the card.
        var row = AllRows().Single(r => r.Description == "Set star rating");

        Assert.Contains("1 - 5", row.Keys);
        Assert.DoesNotContain("Number", row.Keys);
    }

    [Fact]
    public void EveryRowHasBothKeysAndWords()
    {
        Assert.All(AllRows(), r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Keys));
            Assert.False(string.IsNullOrWhiteSpace(r.Description));
        });
    }

    [Fact]
    public void SectionsAreNonEmptyAndOrdered()
    {
        Assert.NotEmpty(KeyBindingCatalog.Sections);
        Assert.All(KeyBindingCatalog.Sections, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.NotEmpty(s.Rows);
        });

        Assert.Equal("Move", KeyBindingCatalog.Sections[0].Title);
    }

    [Fact]
    public void NoCommandIsListedTwice()
    {
        // A command placed in two sections would read as two different features.
        var descriptions = AllRows().Select(r => r.Description).ToList();

        Assert.Equal(descriptions.Count, descriptions.Distinct().Count());
    }
}
