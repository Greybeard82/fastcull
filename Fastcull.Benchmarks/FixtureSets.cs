using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fastcull.Services;

namespace Fastcull.Benchmarks
{
    /// <summary>
    /// PRD 3.5's three fixture sets - all-RAW, all-JPEG, and mixed - as filtered views over the
    /// real SampleImages folder rather than three hand-curated directories. A view cannot drift
    /// out of sync with the corpus the way a curated copy does, and adding a new sample file
    /// automatically joins the right set.
    ///
    /// "The mixed set is the one that finds bugs" (PRD 3.5): mixed interleaves the families so a
    /// RAW decode and a JPEG decode are adjacent in the sequence, which is what a real folder
    /// looks like and what exposes per-family assumptions in the decode path.
    /// </summary>
    internal static class FixtureSets
    {
        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".arw", ".cr3", ".cr2", ".nef", ".raf", ".orf", ".rw2", ".dng", ".pef", ".srw",
            ".jpg", ".jpeg", ".jfif",
            ".heic", ".heif", ".avif",
            ".png",
            ".tif", ".tiff",
            ".webp", ".bmp", ".gif",
        };

        public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

        public sealed record FixtureSet(string Name, IReadOnlyList<ScannedPhoto> Photos)
        {
            public int Count => Photos.Count;
        }

        public static IReadOnlyList<FixtureSet> Build(IReadOnlyList<ScannedPhoto> all)
        {
            var raw = all.Where(p => p.Family == FormatFamily.Raw).ToList();
            var jpeg = all.Where(p => p.Family == FormatFamily.Jpeg).ToList();

            return new List<FixtureSet>
            {
                new("all-RAW", raw),
                new("all-JPEG", jpeg),
                new("mixed", Interleave(raw, jpeg)),
            };
        }

        /// <summary>
        /// Alternates families so neighbouring items need different decoders. A mixed set built by
        /// simple concatenation would run all the RAW work first and never place a RAW decode next
        /// to a JPEG decode, which is precisely the adjacency worth testing.
        /// </summary>
        private static List<ScannedPhoto> Interleave(IReadOnlyList<ScannedPhoto> a, IReadOnlyList<ScannedPhoto> b)
        {
            var result = new List<ScannedPhoto>(a.Count + b.Count);
            for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
            {
                if (i < a.Count) result.Add(a[i]);
                if (i < b.Count) result.Add(b[i]);
            }
            return result;
        }
    }
}
