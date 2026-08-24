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

        // Direct flag-set commands. UNBOUND as of the 2026-08-24 one-handed revamp: C/P/Z/X/U
        // were removed and nothing replaced them, so the ladder is reachable only by stepping
        // (PRD 2.1.1 records the consequence). The members are kept because they are the model's
        // vocabulary and CullState still implements the transitions - reintroducing a direct-set
        // key later is a binding, not a rewrite.
        SetPicked,
        SetRejected,
        SetUnflagged,

        RotateRight,     // 90 degrees clockwise
        RotateLeft,      // 90 degrees counter-clockwise
        ToggleZoom,
        ExitZoom,

        /// <summary>PRD 1.8.1's on-photo info overlay. Not the full PRD 1.8 HUD, which is unbuilt.</summary>
        ToggleInfo,

        /// <summary>PRD 1.1.1's folder picker - the same action as the sidebar's CHANGE FOLDER.</summary>
        OpenFolder,

        /// <summary>PRD 2.1.2: move the selected photo to the Recycle Bin.</summary>
        DeletePhoto,
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
        public static ResolvedInput Resolve(VirtualKey key, bool isExtendedKey)
        {
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

                // ---- Zoom. Space toggles; Escape was removed with the revamp ----
                case VirtualKey.Space: return new ResolvedInput(AppCommand.ToggleZoom, 0);

                // ---- Recycle Bin (PRD 2.1.2) ----
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
