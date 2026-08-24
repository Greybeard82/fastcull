using System;

namespace Fastcull.Models
{
    /// <summary>
    /// A user-applied rotation, as a count of 90-degree clockwise quarter turns.
    ///
    /// **This is a delta, not an absolute orientation.** It means "turn whatever the decoder
    /// handed me by this much", never "the final image faces this way".
    ///
    /// That distinction was deliberate and it paid for itself on 2026-08-24, when RAW files
    /// started having their EXIF orientation applied at decode time (see
    /// <see cref="Services.ExifOrientation"/>). Every portrait RAW's decoded baseline turned a
    /// quarter turn that day. Because stored values are deltas on top of that baseline, existing
    /// user rotations stayed semantically correct across the change - "two more quarter turns
    /// than however this file naturally sits" is true before and after - and no migration was
    /// needed. Storing an absolute orientation would have silently re-interpreted every value
    /// already in the database.
    ///
    /// Rotation is a display transform only. It never triggers a re-decode: PRD 3.5 budgets the
    /// decode pipeline, and spending a decode on a transform would waste it and invalidate cache
    /// entries once PRD 3.3 exists.
    /// </summary>
    public readonly record struct Rotation
    {
        /// <summary>No rotation - the image as the decoder produced it.</summary>
        public static readonly Rotation None = new(0);

        /// <summary>Quarter turns clockwise, always normalised to 0-3.</summary>
        public int QuarterTurns { get; }

        private Rotation(int quarterTurns) => QuarterTurns = quarterTurns;

        /// <summary>
        /// Normalises any integer to 0-3, including negatives. C#'s % keeps the sign of the
        /// dividend, so -1 % 4 is -1, not 3 - the extra + 4 is what makes counter-clockwise
        /// wrapping work rather than producing a negative angle.
        /// </summary>
        public static Rotation FromQuarterTurns(int quarterTurns)
            => new(((quarterTurns % 4) + 4) % 4);

        /// <summary>One quarter turn clockwise, wrapping 3 back to 0.</summary>
        public Rotation RotateRight() => FromQuarterTurns(QuarterTurns + 1);

        /// <summary>One quarter turn counter-clockwise, wrapping 0 back to 3.</summary>
        public Rotation RotateLeft() => FromQuarterTurns(QuarterTurns - 1);

        /// <summary>Clockwise angle in degrees: 0, 90, 180 or 270.</summary>
        public double Degrees => QuarterTurns * 90.0;

        /// <summary>
        /// True at 90 and 270, where width and height trade places. Everything that sizes a
        /// rotated photo - the equal-height rule, the tick, the weight bar - depends on this.
        /// </summary>
        public bool SwapsAspect => QuarterTurns % 2 != 0;

        /// <summary>
        /// The aspect ratio a photo presents once this rotation is applied. A 3:2 landscape
        /// turned a quarter turn is 2:3 portrait, and the stage must size it as portrait or it
        /// will overflow its cell.
        /// </summary>
        public double Apply(double decodedAspectRatio)
        {
            if (decodedAspectRatio <= 0 || double.IsNaN(decodedAspectRatio) || double.IsInfinity(decodedAspectRatio))
                return decodedAspectRatio;

            return SwapsAspect ? 1.0 / decodedAspectRatio : decodedAspectRatio;
        }

        public override string ToString() => $"{Degrees:0}deg";
    }
}
