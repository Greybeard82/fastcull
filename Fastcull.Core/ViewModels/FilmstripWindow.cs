using System;

namespace Fastcull.ViewModels
{
    /// <summary>Which three photos the top region shows, and which slot holds the active one.</summary>
    public readonly record struct SlotWindow(int WindowStart, int ActiveSlot);

    /// <summary>
    /// The three-slot window rule from PRD 1.5. The active photo is normally the centre slot,
    /// but at the sequence ends the active marker moves to an end slot rather than the window
    /// scrolling past the boundary. Pure and XAML-free so it can be unit-tested headlessly.
    /// </summary>
    public static class FilmstripWindow
    {
        /// <summary>
        /// count == 0 gives (0, -1). Otherwise WindowStart is the index of slot 0 and
        /// ActiveSlot is 0, 1 or 2. activeIndex is clamped rather than throwing.
        /// </summary>
        public static SlotWindow Compute(int activeIndex, int count)
        {
            if (count <= 0) return new SlotWindow(0, -1);

            activeIndex = Math.Clamp(activeIndex, 0, count - 1);
            var windowStart = Math.Clamp(activeIndex - 1, 0, Math.Max(0, count - 3));
            return new SlotWindow(windowStart, activeIndex - windowStart);
        }
    }
}
