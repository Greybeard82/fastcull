using System;
using System.Collections.Generic;
using System.Linq;
using Windows.System;

namespace Fastcull.Input
{
    /// <summary>One physical keystroke, as the router sees it.</summary>
    public readonly record struct KeyStroke(VirtualKey Key, bool IsExtendedKey, bool IsShiftDown);

    /// <summary>A single line of the help overlay: the keys, and what they do.</summary>
    public sealed record KeyBindingRow(string Keys, string Description);

    /// <summary>A titled block of rows.</summary>
    public sealed record KeyBindingSection(string Title, IReadOnlyList<KeyBindingRow> Rows);

    /// <summary>
    /// The keybinding list shown by the H overlay (PRD 2.1.3).
    ///
    /// **This does not restate the key map - it asks <see cref="InputRouter"/> what the key map
    /// is.** Every key shown here is discovered by resolving every <see cref="VirtualKey"/>
    /// through the router and collecting what came back, so a binding added, moved or removed in
    /// the router shows up here without anyone remembering to edit a second list. A hand-written
    /// copy of PRD 2.1.1 would drift the first time a binding changed, and would drift silently,
    /// which is worse than having no help at all.
    ///
    /// Two things here are genuinely authored rather than derived, because they are presentation
    /// and cannot be read off a switch statement: the wording of each command's description, and
    /// the order the sections appear in. Both are pinned by tests - a new AppCommand fails
    /// <c>EveryCommandIsDescribed</c> until it is given words, and a new binding fails
    /// <c>EveryBoundKeyIsShownOrDeliberatelyHidden</c> until it is shown or explicitly hidden.
    /// </summary>
    public static class KeyBindingCatalog
    {
        /// <summary>
        /// The numpad digits wearing navigation keycodes, which arrive with the extended bit
        /// clear when NumLock is off (see <see cref="InputRouter"/>). They are the *same physical
        /// keys* as NumPad1-5, which the overlay already lists, so showing "End" next to "set 1
        /// star" would describe a key the user cannot find. Hidden deliberately, not by omission.
        /// </summary>
        private static readonly HashSet<VirtualKey> NumLockOffAliases =
            [VirtualKey.End, VirtualKey.Down, VirtualKey.PageDown, VirtualKey.Left, VirtualKey.Clear];

        /// <summary>Every keystroke the router maps to something, minus the hidden aliases.</summary>
        public static IReadOnlyCollection<KeyStroke> DisplayedKeys { get; }

        /// <summary>Keystrokes that are bound but deliberately not shown.</summary>
        public static IReadOnlyCollection<KeyStroke> HiddenKeys { get; }

        public static IReadOnlyList<KeyBindingSection> Sections { get; }

        /// <summary>
        /// What each command is called in the overlay. Keyed by command so the compiler and the
        /// tests can both check completeness; the star commands share one row, handled below.
        /// </summary>
        private static readonly Dictionary<AppCommand, string> Descriptions = new()
        {
            [AppCommand.NavigatePrevious] = "Previous photo",
            [AppCommand.NavigateNext] = "Next photo",
            [AppCommand.NavigateFirst] = "Jump to first",
            [AppCommand.NavigateLast] = "Jump to last",

            [AppCommand.LadderUp] = "Rate up one rung",
            [AppCommand.LadderDown] = "Rate down one rung",

            [AppCommand.SetRejected] = "Reject",
            [AppCommand.SetUnflagged] = "Unrate",
            [AppCommand.SetPicked] = "Pick (clears stars)",
            [AppCommand.SetStars] = "Set star rating",

            [AppCommand.RotateLeft] = "Rotate left",
            [AppCommand.RotateRight] = "Rotate right",
            [AppCommand.DeletePhoto] = "Move to Recycle Bin",

            [AppCommand.ToggleZoom] = "Zoom in / out",
            [AppCommand.ToggleFullScreen] = "Fullscreen (no zoom)",
            [AppCommand.ExitZoom] = "Close help, exit zoom, exit fullscreen",
            [AppCommand.ToggleInfo] = "Photo info overlay",
            [AppCommand.ToggleSidebarPin] = "Pin / unpin sidebar",
            [AppCommand.ToggleHelp] = "This help",

            [AppCommand.OpenFolder] = "Open a folder",
        };

        /// <summary>The order sections appear in, and which commands land in each.</summary>
        private static readonly (string Title, AppCommand[] Commands)[] Layout =
        [
            ("Move", [AppCommand.NavigatePrevious, AppCommand.NavigateNext,
                      AppCommand.NavigateFirst, AppCommand.NavigateLast]),

            ("Rate - step", [AppCommand.LadderUp, AppCommand.LadderDown]),

            ("Rate - jump", [AppCommand.SetRejected, AppCommand.SetUnflagged,
                             AppCommand.SetPicked, AppCommand.SetStars]),

            ("Photo", [AppCommand.RotateLeft, AppCommand.RotateRight, AppCommand.DeletePhoto]),

            ("View", [AppCommand.ToggleZoom, AppCommand.ToggleFullScreen, AppCommand.ToggleInfo,
                      AppCommand.ToggleSidebarPin, AppCommand.ToggleHelp, AppCommand.ExitZoom]),

            ("Session", [AppCommand.OpenFolder]),
        ];

        static KeyBindingCatalog()
        {
            var displayed = new List<KeyStroke>();
            var hidden = new List<KeyStroke>();

            // Command -> the strokes that produce it, in discovery order.
            var strokesByCommand = new Dictionary<AppCommand, List<KeyStroke>>();

            foreach (var key in Enum.GetValues<VirtualKey>().Distinct())
            {
                foreach (var extended in new[] { false, true })
                {
                    var plain = InputRouter.Resolve(key, extended, isShiftDown: false);
                    var shifted = InputRouter.Resolve(key, extended, isShiftDown: true);

                    Record(key, extended, isShiftDown: false, plain);

                    // Only when Shift actually changes the outcome. Every other key resolves the
                    // same with Shift held, and listing "Shift+W" alongside "W" would be noise.
                    if (shifted != plain) Record(key, extended, isShiftDown: true, shifted);
                }
            }

            void Record(VirtualKey key, bool extended, bool isShiftDown, ResolvedInput resolved)
            {
                if (resolved.Command == AppCommand.None) return;

                var stroke = new KeyStroke(key, extended, isShiftDown);

                // The NumLock-off twins only ever appear with the extended bit clear; the same
                // keycodes WITH the bit set are the genuine grey navigation keys and must show.
                if (!extended && NumLockOffAliases.Contains(key))
                {
                    hidden.Add(stroke);
                    return;
                }

                // A key that means the same thing extended and not (every letter) would otherwise
                // be listed twice.
                var list = strokesByCommand.TryGetValue(resolved.Command, out var existing)
                    ? existing
                    : strokesByCommand[resolved.Command] = [];

                if (list.Any(s => s.Key == key && s.IsShiftDown == isShiftDown)) return;

                list.Add(stroke);
                displayed.Add(stroke);
            }

            DisplayedKeys = displayed;
            HiddenKeys = hidden;

            Sections = Layout
                .Select(section => new KeyBindingSection(
                    section.Title,
                    section.Commands
                        .Where(strokesByCommand.ContainsKey)
                        .Select(c => new KeyBindingRow(
                            Format(strokesByCommand[c], c),
                            Descriptions.TryGetValue(c, out var d) ? d : c.ToString()))
                        .ToList()))
                .Where(s => s.Rows.Count > 0)
                .ToList();
        }

        /// <summary>
        /// Renders a command's keys. Stars collapse to a range: the five digits and their five
        /// numpad twins are ten strokes for one idea, and printing all ten would bury the rest of
        /// the list.
        /// </summary>
        private static string Format(List<KeyStroke> strokes, AppCommand command)
        {
            if (command == AppCommand.SetStars) return "1 - 5   /   Num 1 - 5";

            return string.Join("   /   ", strokes.Select(Name).Distinct());
        }

        private static string Name(KeyStroke stroke)
        {
            var name = stroke.Key switch
            {
                VirtualKey.Left => "<-",
                VirtualKey.Right => "->",
                VirtualKey.Up => "Up",
                VirtualKey.Down => "Down",
                VirtualKey.Space => "Space",
                VirtualKey.Escape => "Esc",
                VirtualKey.Delete => "Del",
                VirtualKey.Enter => "Enter",
                >= VirtualKey.Number0 and <= VirtualKey.Number9
                    => ((int)stroke.Key - (int)VirtualKey.Number0).ToString(),
                >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9
                    => "Num " + ((int)stroke.Key - (int)VirtualKey.NumberPad0),
                _ => stroke.Key.ToString(),
            };

            return stroke.IsShiftDown ? "Shift + " + name : name;
        }
    }
}
