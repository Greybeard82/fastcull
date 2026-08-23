using System;

namespace Fastcull.Models
{
    public enum Flag { Unflagged = 0, Picked = 1, Rejected = 2 }

    /// <summary>
    /// An immutable point on the eight-state cull ladder of PRD 1.6.
    ///
    /// The ladder replaces the old two-independent-axes model because stars are meaningful
    /// only on a picked photo. Invalid (Flag, Stars) pairs - a rejected photo carrying stars,
    /// say - throw from the constructor rather than being silently normalised, so a bug
    /// surfaces in a test instead of in the database.
    /// </summary>
    public readonly record struct CullState
    {
        public static readonly CullState Default = new(Flag.Unflagged, 0);

        public Flag Flag { get; }
        public int Stars { get; }

        public CullState(Flag flag, int stars)
        {
            if (stars is < 0 or > 5)
                throw new ArgumentOutOfRangeException(nameof(stars), stars, "Stars must be 0-5.");

            if (stars >= 1 && flag != Flag.Picked)
                throw new ArgumentException($"Stars >= 1 requires Flag.Picked, got {flag} with {stars} stars.", nameof(flag));

            if (flag is Flag.Rejected or Flag.Unflagged && stars != 0)
                throw new ArgumentException($"{flag} requires Stars == 0, got {stars}.", nameof(stars));

            Flag = flag;
            Stars = stars;
        }

        /// <summary>Position on the ladder: 0 = Rejected, 1 = Unrated, 2 = Picked, 3-7 = Picked with 1-5 stars.</summary>
        public int LadderIndex => Flag switch
        {
            Flag.Rejected => 0,
            Flag.Unflagged => 1,
            _ => 2 + Stars,
        };

        /// <summary>Inverse of <see cref="LadderIndex"/>; clamps out-of-range input to 0..7.</summary>
        public static CullState FromLadderIndex(int index)
        {
            index = Math.Clamp(index, 0, 7);
            return index switch
            {
                0 => new CullState(Flag.Rejected, 0),
                1 => new CullState(Flag.Unflagged, 0),
                _ => new CullState(Flag.Picked, index - 2),
            };
        }

        public CullState Up() => FromLadderIndex(LadderIndex + 1);
        public CullState Down() => FromLadderIndex(LadderIndex - 1);

        /// <summary>Sets stars 0-5. Any value >= 1 implies Picked; 0 keeps the current flag (PRD 2.1).</summary>
        public CullState WithStars(int stars)
        {
            if (stars is < 0 or > 5)
                throw new ArgumentOutOfRangeException(nameof(stars), stars, "Stars must be 0-5.");

            if (stars == 0) return new CullState(Flag, 0);
            return new CullState(Flag.Picked, stars);
        }

        /// <summary>Ladder index 2 if currently 0 or 1; a no-op at 3-7 so it never discards stars.</summary>
        public CullState AsPicked() => LadderIndex >= 3 ? this : new CullState(Flag.Picked, 0);

        public CullState AsRejected() => new(Flag.Rejected, 0);
        public CullState AsUnflagged() => new(Flag.Unflagged, 0);
    }
}
