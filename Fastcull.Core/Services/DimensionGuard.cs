using System;

namespace Fastcull.Services
{
    /// <summary>
    /// PRD 3.3's dimension guard: no single decode may exceed 512 MB in memory.
    ///
    /// The concrete failure mode the PRD names is a stitched panorama - one 100 MP-plus TIFF
    /// decoded at full size is enough to take the app down on its own, and "out of memory while
    /// culling" loses the whole session. The guard caps such a file to a downscaled decode and
    /// flags that full resolution is not available for it, rather than trying and dying.
    ///
    /// 512 MB at BGRA8 is 134,217,728 pixels - roughly 134 MP, or about 14,000 x 9,500.
    /// </summary>
    public static class DimensionGuard
    {
        public const long MaxDecodedBytes = 512L * 1024 * 1024;

        /// <summary>BGRA8, which is what every decode in this app produces.</summary>
        public const int BytesPerPixel = 4;

        /// <summary>Total pixels that fit inside the ceiling.</summary>
        public const long MaxPixels = MaxDecodedBytes / BytesPerPixel;

        /// <summary>Memory a decode at this long edge and aspect would occupy.</summary>
        public static long EstimateBytes(uint longEdge, double aspectRatio)
        {
            var aspect = Usable(aspectRatio);
            double shortEdge = aspect >= 1 ? longEdge / aspect : longEdge * aspect;
            return (long)(longEdge * shortEdge * BytesPerPixel);
        }

        /// <summary>
        /// The largest long edge that stays inside the ceiling for this aspect, or the request
        /// itself when it already fits.
        ///
        /// Returns at least 1: a cap that produced zero would turn an oversized photo into a
        /// failed decode, which is exactly the outcome the guard exists to avoid.
        /// </summary>
        public static uint ClampLongEdge(uint requestedLongEdge, double aspectRatio)
        {
            if (requestedLongEdge == 0) return 0;
            if (EstimateBytes(requestedLongEdge, aspectRatio) <= MaxDecodedBytes) return requestedLongEdge;

            // pixels = longEdge^2 / aspect (for a landscape aspect >= 1), so the limit is
            // longEdge = sqrt(maxPixels * aspect). Same expression works either orientation once
            // the aspect is normalised to "long edge over short edge".
            var aspect = Usable(aspectRatio);
            var longOverShort = aspect >= 1 ? aspect : 1 / aspect;

            var limit = Math.Sqrt(MaxPixels * longOverShort);
            return (uint)Math.Max(1, Math.Floor(limit));
        }

        /// <summary>Overload for callers that know the source's real pixel dimensions.</summary>
        public static uint ClampLongEdge(uint requestedLongEdge, uint sourceWidth, uint sourceHeight)
        {
            if (sourceWidth == 0 || sourceHeight == 0) return requestedLongEdge;
            return ClampLongEdge(requestedLongEdge, sourceWidth / (double)sourceHeight);
        }

        /// <summary>True when the guard would reduce this request.</summary>
        public static bool WouldLimit(uint requestedLongEdge, double aspectRatio)
            => ClampLongEdge(requestedLongEdge, aspectRatio) < requestedLongEdge;

        private static double Usable(double aspectRatio)
            => aspectRatio > 0 && !double.IsNaN(aspectRatio) && !double.IsInfinity(aspectRatio)
                ? aspectRatio
                : 1.5;
    }
}
