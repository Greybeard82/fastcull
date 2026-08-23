using System;
using System.Collections.Generic;

namespace Fastcull.ViewModels
{
    /// <summary>
    /// The three-up stage's equal-height rule, from the Design/ handoff.
    ///
    /// The handoff flags this as load-bearing and notes the prototype got it wrong twice, so the
    /// arithmetic lives here - free of WinUI - rather than inline in the View, where it could only
    /// ever be checked by eye. The sample corpus is entirely landscape 3:2, so the portrait and
    /// mixed-aspect cases genuinely cannot be verified by looking at the running app; they are
    /// verified by the tests against this type instead.
    /// </summary>
    public static class StageLayout
    {
        /// <summary>Aspect assumed for a photo whose real dimensions are not decoded yet.</summary>
        public const double DefaultAspectRatio = 1.5;

        /// <summary>
        /// Total horizontal space consumed by the gaps between <paramref name="slotCount"/> slots.
        /// There is always one fewer gap than slot - the classic off-by-one here silently shrinks
        /// or overflows every photo, so it lives in one tested place rather than inline in a View.
        /// </summary>
        public static double ComputeTotalGapWidth(int slotCount, double gapWidth)
        {
            if (slotCount <= 1) return 0;

            var usable = gapWidth > 0 && !double.IsNaN(gapWidth) && !double.IsInfinity(gapWidth)
                ? gapWidth
                : 0;

            return usable * (slotCount - 1);
        }

        /// <summary>
        /// One shared height for every visible photo, sized so the whole set fits the width it
        /// actually has:
        ///
        ///     min(availableHeight, (availableWidth - totalGapWidth) / sumOfVisibleAspects)
        ///
        /// The set's total width at height h is `h * sum(aspects) + gaps`, so solving that for the
        /// available width is exactly this expression. Height is shared, and each photo's width
        /// then follows its own aspect - so a portrait frame stands narrow beside a landscape one
        /// at identical height, and nothing is ever cropped.
        ///
        /// This replaces an earlier rule that gave every photo an equal share of the width and
        /// divided by the WIDEST aspect. The two agree exactly whenever the visible photos share
        /// an aspect (the common all-landscape case regresses nothing), but the old rule handed a
        /// portrait photo a full landscape-sized column and left most of it black - which is how
        /// a 5px gap setting could render as a gap of hundreds of pixels. Sizing to the real set
        /// spends that slack on height for every photo instead.
        /// </summary>
        /// <returns>The shared height, or 0 when there is no room or nothing to lay out.</returns>
        public static double ComputeSharedHeight(
            double availableWidth, double availableHeight, double totalGapWidth, IReadOnlyList<double> aspectRatios)
        {
            if (availableWidth <= 0 || availableHeight <= 0) return 0;
            if (aspectRatios is null || aspectRatios.Count == 0) return 0;

            var usableGaps = totalGapWidth > 0 && !double.IsNaN(totalGapWidth) && !double.IsInfinity(totalGapWidth)
                ? totalGapWidth
                : 0;

            var widthForPhotos = availableWidth - usableGaps;
            if (widthForPhotos <= 0) return 0;

            var sumAspects = 0.0;
            foreach (var aspect in aspectRatios)
            {
                // A non-positive or non-finite aspect means "not decoded yet" (or a corrupt
                // decode); fall back rather than poisoning the sum with 0, NaN or Infinity.
                sumAspects += aspect > 0 && !double.IsNaN(aspect) && !double.IsInfinity(aspect)
                    ? aspect
                    : DefaultAspectRatio;
            }

            if (sumAspects <= 0) return 0;

            // Clamped by the available height so a set of narrow photos cannot grow past the stage.
            return Math.Min(availableHeight, widthForPhotos / sumAspects);
        }

        /// <summary>
        /// Total width the set occupies at <paramref name="sharedHeight"/>, gaps included. Used to
        /// decide whether another photo still fits (PRD 1.5's variable slot count).
        /// </summary>
        public static double ComputeSetWidth(double sharedHeight, double totalGapWidth, IReadOnlyList<double> aspectRatios)
        {
            if (sharedHeight <= 0 || aspectRatios is null || aspectRatios.Count == 0) return 0;

            var total = totalGapWidth > 0 && !double.IsNaN(totalGapWidth) && !double.IsInfinity(totalGapWidth)
                ? totalGapWidth
                : 0;

            foreach (var aspect in aspectRatios)
                total += PhotoWidth(sharedHeight, aspect);

            return total;
        }

        /// <summary>Width of one photo at the shared height, from its own aspect.</summary>
        public static double PhotoWidth(double sharedHeight, double aspectRatio)
        {
            if (sharedHeight <= 0) return 0;

            var usable = aspectRatio > 0 && !double.IsNaN(aspectRatio) && !double.IsInfinity(aspectRatio)
                ? aspectRatio
                : DefaultAspectRatio;

            return sharedHeight * usable;
        }
    }
}
