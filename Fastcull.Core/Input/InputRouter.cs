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
                case VirtualKey.X: return new ResolvedInput(AppCommand.SetUnflagged, 0);
            }

            return isExtendedKey ? ResolveExtended(key) : ResolveNumpad(key);
        }

        /// <summary>Genuine grey navigation keys - the extended-key bit is set.</summary>
        private static ResolvedInput ResolveExtended(VirtualKey key) => key switch
        {
            VirtualKey.Left => new ResolvedInput(AppCommand.NavigatePrevious, 0),
            VirtualKey.Right => new ResolvedInput(AppCommand.NavigateNext, 0),
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
