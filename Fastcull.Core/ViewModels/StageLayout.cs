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
        /// One shared height for every visible photo:
        ///
        ///     min(availableCellHeight, availableCellWidth / widestVisibleAspect)
        ///
        /// Dividing by the WIDEST visible aspect is the part that matters. The widest photo is the
        /// one that runs out of horizontal room first, so sizing to it guarantees every photo fits
        /// its cell; sizing to anything narrower lets the widest overflow. Height is shared, width
        /// is then derived per photo from its own aspect - so a portrait frame stands narrow beside
        /// a landscape one at exactly the same height, and nothing is ever cropped.
        /// </summary>
        /// <returns>The shared height, or 0 when there is no room or nothing to lay out.</returns>
        public static double ComputeSharedHeight(double cellWidth, double cellHeight, IReadOnlyList<double> aspectRatios)
        {
            if (cellWidth <= 0 || cellHeight <= 0) return 0;
            if (aspectRatios is null || aspectRatios.Count == 0) return 0;

            var widest = 0.0;
            foreach (var aspect in aspectRatios)
            {
                // A non-positive or non-finite aspect means "not decoded yet" (or a corrupt
                // decode); fall back rather than poisoning the max with 0 or NaN, either of
                // which would blow the division below up into Infinity.
                var usable = aspect > 0 && !double.IsNaN(aspect) && !double.IsInfinity(aspect)
                    ? aspect
                    : DefaultAspectRatio;

                if (usable > widest) widest = usable;
            }

            if (widest <= 0) widest = DefaultAspectRatio;

            return Math.Min(cellHeight, cellWidth / widest);
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
