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

        /// <summary>
        /// How many photos the stage should show, given the room it has and the shapes of the
        /// photos around the cursor.
        ///
        /// A set only benefits from another photo while it is **height**-bound - that is, while
        /// the shared height is clamped by the available height and the set is therefore narrower
        /// than the space it has. Three portraits on a wide stage leave most of the width unused,
        /// and that slack is exactly what extra photos should fill. Once width becomes the binding
        /// constraint the set already spans the stage, and adding more only shrinks every photo,
        /// so expansion stops there.
        ///
        /// Counts stay odd so the active photo has a literal centre slot (PRD 1.5), and are capped
        /// at <see cref="FilmstripWindow.MaxSlots"/>. The cap is not cosmetic: every staged photo
        /// holds a display-tier decode, and the 5.25 GB peak-working-set failure measured in the
        /// followup3 benchmark is still unfixed because PRD 3.3's cache does not exist. An
        /// uncapped rule on an ultrawide full of extreme crops would make that materially worse.
        /// </summary>
        /// <param name="aspectsForCount">
        /// Given a candidate slot count, returns the effective aspect ratios the window would
        /// contain. The window shifts with the count, so the caller resolves it per candidate.
        /// </param>
        public static int ChooseSlotCount(
            double availableWidth,
            double availableHeight,
            double gapWidth,
            int itemCount,
            Func<int, IReadOnlyList<double>> aspectsForCount,
            int minimumSlots = 3)
        {
            if (itemCount <= 0 || availableWidth <= 0 || availableHeight <= 0) return 0;

            var ceiling = Math.Min(itemCount, FilmstripWindow.MaxSlots);
            var chosen = Math.Min(minimumSlots, ceiling);

            for (var candidate = chosen + 2; candidate <= ceiling; candidate += 2)
            {
                var aspects = aspectsForCount(candidate);
                if (aspects is null || aspects.Count == 0) break;

                var gaps = ComputeTotalGapWidth(candidate, gapWidth);
                var height = ComputeSharedHeight(availableWidth, availableHeight, gaps, aspects);

                // Still height-bound means the set does not yet fill the width, so one more pair
                // of photos can be shown without making any of them smaller.
                if (height < availableHeight) break;

                chosen = candidate;
            }

            return chosen;
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
