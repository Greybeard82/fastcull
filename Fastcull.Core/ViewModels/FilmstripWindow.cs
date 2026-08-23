using System;

namespace Fastcull.ViewModels
{
    /// <summary>Which photos the top region shows, how many, and which slot holds the active one.</summary>
    public readonly record struct SlotWindow(int WindowStart, int ActiveSlot, int SlotCount);

    /// <summary>
    /// The stage window rule from PRD 1.5. The active photo is normally the centre slot, but at
    /// the sequence ends the active marker moves toward an end slot rather than the window
    /// scrolling past the boundary. Pure and XAML-free so it can be unit-tested headlessly.
    ///
    /// The slot count is no longer fixed at three - the stage shows as many photos as actually
    /// fit (see <see cref="StageLayout.ChooseSlotCount"/>). Counts are kept odd so "the active
    /// photo is the centre slot" stays literally true; an even count has no centre.
    /// </summary>
    public static class FilmstripWindow
    {
        /// <summary>Hard ceiling on simultaneously-staged photos. See StageLayout.ChooseSlotCount.</summary>
        public const int MaxSlots = 9;

        /// <summary>
        /// count == 0 gives (0, -1, 0). Otherwise WindowStart is the index of slot 0 and
        /// ActiveSlot is its offset within the window. activeIndex and slots are clamped rather
        /// than throwing.
        /// </summary>
        public static SlotWindow Compute(int activeIndex, int count, int slots = 3)
        {
            if (count <= 0 || slots <= 0) return new SlotWindow(0, -1, 0);

            slots = Math.Clamp(slots, 1, Math.Min(count, MaxSlots));
            activeIndex = Math.Clamp(activeIndex, 0, count - 1);

            // slots / 2 places the active photo dead centre for an odd count; the clamp is what
            // stops the window running off either end, which is when the active marker moves to
            // an end slot instead.
            var windowStart = Math.Clamp(activeIndex - (slots / 2), 0, count - slots);
            return new SlotWindow(windowStart, activeIndex - windowStart, slots);
        }
    }
}
