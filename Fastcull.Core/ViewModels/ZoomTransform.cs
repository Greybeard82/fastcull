using System;

namespace Fastcull.ViewModels
{
    /// <summary>
    /// PRD 1.7.1's scale-and-pan state for the zoomed photo: how far in, and how far off-centre.
    ///
    /// Immutable, and pure arithmetic with no WinUI in sight, because the cursor-anchoring maths is
    /// the part of this feature most likely to be subtly wrong - off by a factor of the scale, or
    /// anchored to the wrong origin - and those mistakes are invisible in a screenshot but obvious
    /// in a test.
    ///
    /// Coordinates are **viewport pixels relative to the viewport centre**. The viewport is the
    /// photo's own fit-to-stage frame, which at scale 1 the image exactly fills (the frame is sized
    /// to the photo's aspect, so there is no letterbox inside it - the black bars live outside).
    /// That is what makes the clamp exact: overflow on an axis is simply size x (scale - 1).
    /// </summary>
    public readonly record struct ZoomTransform(double Scale, double OffsetX, double OffsetY)
    {
        /// <summary>Fit-to-stage, centred - what entering zoom produces and what wheel-down floors at.</summary>
        public static readonly ZoomTransform Identity = new(1.0, 0, 0);

        /// <summary>PRD 1.7.1: 100% is a floor, not a midpoint. There is no zooming out past the fit.</summary>
        public const double MinScale = 1.0;

        public const double MaxScale = 3.0;

        /// <summary>One wheel notch. 20% per PRD 1.7.1 - ten steps from floor to ceiling.</summary>
        public const double ScaleStep = 0.2;

        /// <summary>Floating-point slack for "is this exactly 100%".</summary>
        private const double Epsilon = 1e-9;

        /// <summary>True at the floor, where PRD 1.7.1 makes panning a no-op.</summary>
        public bool IsFitted => Scale <= MinScale + Epsilon;

        /// <summary>
        /// How far the image may be moved from centre on each axis before an edge would pull inside
        /// the viewport. Zero at scale 1, which is what makes panning a no-op there without needing
        /// a special case.
        /// </summary>
        public static double MaxOffset(double viewportExtent, double scale)
            => Math.Max(0, (viewportExtent * scale - viewportExtent) / 2);

        /// <summary>
        /// Pulls the offsets back inside the pannable range. Applied after every change, so no
        /// operation can leave the image showing empty space beyond its own edges.
        /// </summary>
        public ZoomTransform Clamped(double viewportWidth, double viewportHeight)
        {
            var scale = Math.Clamp(Scale, MinScale, MaxScale);
            var maxX = MaxOffset(viewportWidth, scale);
            var maxY = MaxOffset(viewportHeight, scale);

            return new ZoomTransform(
                scale,
                Math.Clamp(OffsetX, -maxX, maxX),
                Math.Clamp(OffsetY, -maxY, maxY));
        }

        /// <summary>
        /// The scale this many wheel notches away, clamped to the range. Positive steps zoom in.
        ///
        /// Rounded to the step grid so a long scroll cannot accumulate floating-point drift and
        /// leave the user at 99.9999% - which would read as "stuck just off the floor" and, worse,
        /// would make <see cref="IsFitted"/> false and re-enable panning at what looks like 100%.
        /// </summary>
        public static double SteppedScale(double from, int steps)
        {
            var raw = from + steps * ScaleStep;
            var snapped = Math.Round(raw / ScaleStep) * ScaleStep;
            return Math.Clamp(snapped, MinScale, MaxScale);
        }

        /// <summary>
        /// Scales toward a point, keeping whatever is under that point exactly where it is.
        ///
        /// <paramref name="cursorX"/> and <paramref name="cursorY"/> are relative to the viewport's
        /// CENTRE, not its top-left - the transform is applied about the centre, so working in
        /// centre-relative coordinates is what keeps this to one subtraction instead of a pair of
        /// half-extent corrections that are easy to get backwards.
        ///
        /// The derivation, because it is short and worth not re-deriving later: the image point
        /// under the cursor is <c>i = (cursor - offset) / scale</c>. Requiring that same point to
        /// still sit under the cursor after the change gives <c>cursor = i * newScale + newOffset</c>,
        /// so <c>newOffset = cursor - (cursor - offset) * (newScale / scale)</c>.
        /// </summary>
        public ZoomTransform ScaledAt(
            double cursorX, double cursorY,
            int steps,
            double viewportWidth, double viewportHeight)
        {
            var newScale = SteppedScale(Scale, steps);

            // Already at the rail: nothing moves. Returning early also stops a wheel notch at the
            // ceiling from nudging the pan, which would let the image creep while apparently doing
            // nothing.
            if (Math.Abs(newScale - Scale) < Epsilon) return this;

            var ratio = newScale / Scale;

            var offsetX = cursorX - (cursorX - OffsetX) * ratio;
            var offsetY = cursorY - (cursorY - OffsetY) * ratio;

            return new ZoomTransform(newScale, offsetX, offsetY).Clamped(viewportWidth, viewportHeight);
        }

        /// <summary>
        /// Moves the image by a drag delta, clamped. A no-op at the fit scale, where the clamp
        /// range is zero on both axes - PRD 1.7.1 asks for nothing rather than rubber-banding.
        /// </summary>
        public ZoomTransform Panned(double deltaX, double deltaY, double viewportWidth, double viewportHeight)
            => new ZoomTransform(Scale, OffsetX + deltaX, OffsetY + deltaY)
                .Clamped(viewportWidth, viewportHeight);
    }
}
