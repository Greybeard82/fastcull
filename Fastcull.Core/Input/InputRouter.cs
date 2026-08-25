using Windows.System;

namespace Fastcull.Input
{
    /// <summary>Commands the input layer can produce, per PRD 2.1.</summary>
    public enum AppCommand
    {
        None,
        NavigatePrevious,
        NavigateNext,
        NavigateFirst,
        NavigateLast,
        LadderUp,
        LadderDown,
        SetStars,        // payload 0-5

        // Direct flag-set commands, bound to C / Z / X (PRD 2.1.1). Briefly unbound during the
        // first pass of the one-handed revamp; restored once stepping alone proved too slow to
        // reject a highly-rated photo.
        //
        // Note SetPicked means Picked with stars RESET, not CullState.AsPicked() - see the
        // handler in MainViewModel.
        SetPicked,
        SetRejected,
        SetUnflagged,

        RotateRight,     // 90 degrees clockwise
        RotateLeft,      // 90 degrees counter-clockwise
        ToggleZoom,

        /// <summary>
        /// PRD 2.1.1's dismiss key. Not zoom-specific despite the older name: it backs out of
        /// whatever is topmost - the help overlay first, then zoom, then standalone fullscreen.
        /// The priority lives in MainViewModel, because only it knows which of those are open.
        /// </summary>
        ExitZoom,

        /// <summary>
        /// PRD 1.7.3's standalone fullscreen: the same AppWindow presenter zoom uses, without
        /// entering zoom. Bound to Shift+Space.
        /// </summary>
        ToggleFullScreen,

        /// <summary>PRD 1.8.1's on-photo info overlay. Not the full PRD 1.8 HUD, which is unbuilt.</summary>
        ToggleInfo,

        /// <summary>PRD 1.1.1's folder picker - the same action as the sidebar's CHANGE FOLDER.</summary>
        OpenFolder,

        /// <summary>
        /// PRD 1.5's sidebar pin, exactly the action the panel's own pin button performs - both
        /// call SidebarViewModel.TogglePin, so the key and the button can never disagree.
        /// </summary>
        ToggleSidebarPin,

        /// <summary>PRD 2.1.3's keybinding help overlay.</summary>
        ToggleHelp,

        /// <summary>PRD 2.1.2: move the selected photo to the Recycle Bin.</summary>
        DeletePhoto,

        /// <summary>PRD 1.9's undo stack. Ctrl+Z / Ctrl+Y.</summary>
        Undo,
        Redo,
    }

    public readonly record struct ResolvedInput(AppCommand Command, int Payload)
    {
        public static readonly ResolvedInput None = new(AppCommand.None, 0);
    }

    /// <summary>
    /// Pure key-to-command resolution, per PRD 2.1 and 2.4. Deliberately XAML-free - it
    /// references no WinUI type beyond <see cref="VirtualKey"/> - so it is unit-testable
    /// headlessly. That matters because keyboard input cannot be injected in the dev sandbox,
    /// making these tests the only way this logic is verified at all.
    ///
    /// **Revamped 2026-08-24 for one-handed (left hand) operation.** The map is built around a
    /// hand resting on WASD: A/D move the cursor, W/S move the rating, and everything else sits
    /// within reach of the same hand. The previous scheme was replaced almost entirely.
    ///
    /// Two properties of that map are contracts rather than coincidences:
    ///
    ///   1. **The letter keys are duplicates of the arrows, not parallel behaviours.** A/D resolve
    ///      to the identical commands as Left/Right, and W/S to the identical commands as Up/Down.
    ///      They are the same action reached by a different finger. If these two ever diverge,
    ///      that is a bug.
    ///   2. **The NumLock split is untouched.** With NumLock off the numpad emits navigation
    ///      keycodes, so NumPad2 arrives as <see cref="VirtualKey.Down"/> and would collide with
    ///      rate-down. The genuine grey navigation keys set the extended-key bit and their numpad
    ///      twins do not, which remains the only reliable way to tell them apart.
    /// </summary>
    public static class InputRouter
    {
        /// <param name="isShiftDown">
        /// Shift changes exactly one binding today: Space, which zooms on its own and toggles
        /// standalone fullscreen with Shift (PRD 1.7.3). It is defaulted so the large existing
        /// body of two-argument callers and tests keeps compiling and keeps meaning what it did.
        /// </param>
        /// <param name="isControlDown">
        /// Ctrl selects PRD 1.9's undo and redo. Handled before everything else, because Z is
        /// already Reject and Y is unbound - a Ctrl chord must never fall through and rate the
        /// photo the user was trying to un-rate.
        /// </param>
        public static ResolvedInput Resolve(VirtualKey key, bool isExtendedKey,
                                            bool isShiftDown = false, bool isControlDown = false)
        {
            if (isControlDown)
            {
                return key switch
                {
                    VirtualKey.Z => new ResolvedInput(AppCommand.Undo, 0),
                    VirtualKey.Y => new ResolvedInput(AppCommand.Redo, 0),

                    // Every other Ctrl chord is swallowed rather than falling through to its
                    // unmodified meaning. Ctrl+S reaching the rating ladder would be a nasty
                    // surprise for a hand trained on every other application.
                    _ => ResolvedInput.None,
                };
            }

            // Resolved first, before the extended/numpad split below, because every key here
            // means the same thing whichever way that bit falls - so none of them can be
            // shadowed by a numpad twin.
            switch (key)
            {
                // ---- Stars: top row and numpad, per PRD 2.1 ----
                case VirtualKey.Number1:
                case VirtualKey.NumberPad1: return new ResolvedInput(AppCommand.SetStars, 1);
                case VirtualKey.Number2:
                case VirtualKey.NumberPad2: return new ResolvedInput(AppCommand.SetStars, 2);
                case VirtualKey.Number3:
                case VirtualKey.NumberPad3: return new ResolvedInput(AppCommand.SetStars, 3);
                case VirtualKey.Number4:
                case VirtualKey.NumberPad4: return new ResolvedInput(AppCommand.SetStars, 4);
                case VirtualKey.Number5:
                case VirtualKey.NumberPad5: return new ResolvedInput(AppCommand.SetStars, 5);

                // ---- The WASD cluster: cursor on A/D, rating on W/S ----
                case VirtualKey.A: return new ResolvedInput(AppCommand.NavigatePrevious, 0);
                case VirtualKey.D: return new ResolvedInput(AppCommand.NavigateNext, 0);
                case VirtualKey.W: return new ResolvedInput(AppCommand.LadderUp, 0);
                case VirtualKey.S: return new ResolvedInput(AppCommand.LadderDown, 0);

                // ---- Direct-set flags, restored 2026-08-24 on the bottom row ----
                //
                // These JUMP to a rung; W/S step to one. Both stay available (PRD 2.1.1) - the
                // first pass of the revamp removed every direct-set key and reaching Rejected
                // from five stars became seven presses of S, which is what brought them back.
                case VirtualKey.Z: return new ResolvedInput(AppCommand.SetRejected, 0);
                case VirtualKey.X: return new ResolvedInput(AppCommand.SetUnflagged, 0);
                case VirtualKey.C: return new ResolvedInput(AppCommand.SetPicked, 0);

                // ---- Rotation, moved off A/S onto the row above (PRD 1.11) ----
                case VirtualKey.Q: return new ResolvedInput(AppCommand.RotateLeft, 0);
                case VirtualKey.E: return new ResolvedInput(AppCommand.RotateRight, 0);

                // ---- Jumps, replacing Home/End ----
                case VirtualKey.R: return new ResolvedInput(AppCommand.NavigateFirst, 0);
                case VirtualKey.T: return new ResolvedInput(AppCommand.NavigateLast, 0);

                // ---- Overlay: F is the one-handed key, I retained as a synonym ----
                case VirtualKey.F:
                case VirtualKey.I: return new ResolvedInput(AppCommand.ToggleInfo, 0);

                // ---- Folder picker (PRD 1.1.1) ----
                case VirtualKey.G: return new ResolvedInput(AppCommand.OpenFolder, 0);

                // ---- Sidebar pin (PRD 1.5) and the help overlay (PRD 2.1.3) ----
                case VirtualKey.V: return new ResolvedInput(AppCommand.ToggleSidebarPin, 0);
                case VirtualKey.H: return new ResolvedInput(AppCommand.ToggleHelp, 0);

                // ---- Zoom and fullscreen ----
                //
                // Space toggles zoom both ways; Shift+Space toggles standalone fullscreen without
                // entering zoom (PRD 1.7.3). They share a key because they are the same gesture at
                // two scopes - make the photo bigger, make the window bigger - and the modifier is
                // what keeps them one keypress apart rather than two keys to remember.
                //
                // Escape only ever dismisses. That asymmetry is the point of having both: Escape
                // can be hit reflexively without any risk of being the key that puts you INTO
                // zoom, and it is a no-op when there is nothing open to back out of.
                case VirtualKey.Space:
                    return isShiftDown
                        ? new ResolvedInput(AppCommand.ToggleFullScreen, 0)
                        : new ResolvedInput(AppCommand.ToggleZoom, 0);

                case VirtualKey.Escape: return new ResolvedInput(AppCommand.ExitZoom, 0);

                // ---- Recycle Bin (PRD 2.1.3) ----
                case VirtualKey.Delete: return new ResolvedInput(AppCommand.DeletePhoto, 0);
            }

            return isExtendedKey ? ResolveExtended(key) : ResolveNumpad(key);
        }

        /// <summary>
        /// Genuine grey navigation keys - the extended-key bit is set.
        ///
        /// These are the arrow-key duplicates of the WASD cluster above, and resolve to exactly
        /// the same commands. Home/End are deliberately gone: R/T replaced them in the revamp.
        /// </summary>
        private static ResolvedInput ResolveExtended(VirtualKey key) => key switch
        {
            VirtualKey.Left => new ResolvedInput(AppCommand.NavigatePrevious, 0),
            VirtualKey.Right => new ResolvedInput(AppCommand.NavigateNext, 0),
            VirtualKey.Up => new ResolvedInput(AppCommand.LadderUp, 0),
            VirtualKey.Down => new ResolvedInput(AppCommand.LadderDown, 0),
            _ => ResolvedInput.None,
        };

        /// <summary>
        /// Numpad with NumLock off - the extended-key bit is clear, so these are digits wearing
        /// navigation keycodes. Numpad 0/6/7/8/9 are unmapped: 0 lost its clear-stars binding in
        /// the revamp, and the rest never had one.
        /// </summary>
        private static ResolvedInput ResolveNumpad(VirtualKey key) => key switch
        {
            VirtualKey.End => new ResolvedInput(AppCommand.SetStars, 1),       // numpad 1
            VirtualKey.Down => new ResolvedInput(AppCommand.SetStars, 2),      // numpad 2
            VirtualKey.PageDown => new ResolvedInput(AppCommand.SetStars, 3),  // numpad 3
            VirtualKey.Left => new ResolvedInput(AppCommand.SetStars, 4),      // numpad 4
            VirtualKey.Clear => new ResolvedInput(AppCommand.SetStars, 5),     // numpad 5
            _ => ResolvedInput.None,
        };
    }
}
