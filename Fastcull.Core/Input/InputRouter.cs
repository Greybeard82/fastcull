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
        SetPicked,
        SetRejected,
        SetUnflagged,
        RotateRight,     // 90 degrees clockwise
        RotateLeft,      // 90 degrees counter-clockwise
        ToggleZoom,
        ExitZoom,

        /// <summary>PRD 1.8.1's on-photo info overlay. Not the full PRD 1.8 HUD, which is unbuilt.</summary>
        ToggleInfo,
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
    /// The NumLock problem (PRD 1.6): with NumLock off the numpad emits navigation keycodes,
    /// so NumPad2 arrives as <see cref="VirtualKey.Down"/> and collides with ladder-down.
    /// The genuine grey navigation keys set the extended-key bit; their numpad twins do not,
    /// which is the only reliable way to tell them apart.
    /// </summary>
    public static class InputRouter
    {
        public static ResolvedInput Resolve(VirtualKey key, bool isExtendedKey)
        {
            // Keys that mean the same thing regardless of the extended flag are resolved
            // first, so they can never be shadowed by the navigation/numpad split below.
            switch (key)
            {
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
                case VirtualKey.Number0:
                case VirtualKey.NumberPad0: return new ResolvedInput(AppCommand.SetStars, 0);
                // Z/X/C sit adjacent under the left hand so the ladder can be driven without
                // looking down (PRD 2.1). Note X is reassigned, not inherited: it used to mean
                // Rejected and now means Unflagged. P and U are deliberately unmapped.
                case VirtualKey.C: return new ResolvedInput(AppCommand.SetPicked, 0);
                case VirtualKey.Z: return new ResolvedInput(AppCommand.SetRejected, 0);
                // P/X/U flag keys, restored 2026-08-23 by explicit instruction. Note this
                // REVERSES two earlier decisions: PRD 2.1 had unmapped P and U outright, and had
                // reassigned X from Rejected to Unflagged. X is Rejected again, and U is what
                // clears to unrated.
                case VirtualKey.P: return new ResolvedInput(AppCommand.SetPicked, 0);
                case VirtualKey.X: return new ResolvedInput(AppCommand.SetRejected, 0);
                case VirtualKey.U: return new ResolvedInput(AppCommand.SetUnflagged, 0);
                // Rotation (PRD 1.11). A turns counter-clockwise and S turns clockwise, so the
                // keys run the way the photo does: A is left of S, and turns the photo left.
                // (This is a deliberate reversal of the original mapping, made 2026-08-23 after
                // the first one proved wrong in the hand.)
                case VirtualKey.A: return new ResolvedInput(AppCommand.RotateLeft, 0);
                case VirtualKey.S: return new ResolvedInput(AppCommand.RotateRight, 0);
                // Zoom (PRD 2.1/2.2). Space toggles; Escape only ever exits, so it is safe to
                // press when already un-zoomed. Neither is a numpad key, so the extended-key
                // split below cannot shadow them.
                case VirtualKey.Space: return new ResolvedInput(AppCommand.ToggleZoom, 0);
                case VirtualKey.Escape: return new ResolvedInput(AppCommand.ExitZoom, 0);
                // Info overlay (PRD 1.8.1). Resolved here rather than in the extended/numpad split
                // below for the same reason as the others: it means the same thing either way, and
                // it works identically in both stage and zoom views.
                case VirtualKey.I: return new ResolvedInput(AppCommand.ToggleInfo, 0);
            }

            return isExtendedKey ? ResolveExtended(key) : ResolveNumpad(key);
        }

        /// <summary>Genuine grey navigation keys - the extended-key bit is set.</summary>
        private static ResolvedInput ResolveExtended(VirtualKey key) => key switch
        {
            VirtualKey.Left => new ResolvedInput(AppCommand.NavigatePrevious, 0),
            VirtualKey.Right => new ResolvedInput(AppCommand.NavigateNext, 0),
            // Up/Down step the PRD 1.6 ladder one position, clamped at both ends. They briefly
            // set flags directly instead; reverted 2026-08-23 so the ladder is reachable from the
            // keyboard again. P/X/U and the digits stay direct-set - only these two step.
            VirtualKey.Up => new ResolvedInput(AppCommand.LadderUp, 0),
            VirtualKey.Down => new ResolvedInput(AppCommand.LadderDown, 0),
            VirtualKey.Home => new ResolvedInput(AppCommand.NavigateFirst, 0),
            VirtualKey.End => new ResolvedInput(AppCommand.NavigateLast, 0),
            _ => ResolvedInput.None,
        };

        /// <summary>
        /// Numpad with NumLock off - the extended-key bit is clear, so these are digits
        /// wearing navigation keycodes. Numpad 6/7/9/8 (Right/Home/PageUp/Up) are unmapped.
        /// </summary>
        private static ResolvedInput ResolveNumpad(VirtualKey key) => key switch
        {
            VirtualKey.End => new ResolvedInput(AppCommand.SetStars, 1),       // numpad 1
            VirtualKey.Down => new ResolvedInput(AppCommand.SetStars, 2),      // numpad 2
            VirtualKey.PageDown => new ResolvedInput(AppCommand.SetStars, 3),  // numpad 3
            VirtualKey.Left => new ResolvedInput(AppCommand.SetStars, 4),      // numpad 4
            VirtualKey.Clear => new ResolvedInput(AppCommand.SetStars, 5),     // numpad 5
            VirtualKey.Insert => new ResolvedInput(AppCommand.SetStars, 0),    // numpad 0
            _ => ResolvedInput.None,
        };
    }
}
