using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Fastcull.Models
{
    /// <summary>How many photos share one file extension, and which family it belongs to.</summary>
    public readonly record struct FormatCount(string Label, Services.FormatFamily Family, int Count);

    /// <summary>
    /// Counts the sequence by file type for the sidebar, per PRD 1.5's "format breakdown".
    ///
    /// Grouped by extension rather than by <see cref="Services.FormatFamily"/>, because the family
    /// is too coarse to be useful here: a mixed card of .ARW and .CR2 is the case PRD 1.3 exists
    /// for and the one a photographer most wants to see broken out, and both are simply "Raw".
    /// The family rides along so the panel can order RAW ahead of everything else.
    ///
    /// Pure and WinUI-free, so it is testable headlessly.
    /// </summary>
    public static class FormatBreakdown
    {
        /// <summary>
        /// Counts by uppercased extension, ordered by count descending then label, so the
        /// dominant format leads. Files with no extension are ignored rather than grouped under
        /// an empty label.
        /// </summary>
        public static List<FormatCount> From(IEnumerable<(string FileName, Services.FormatFamily Family)> photos)
        {
            var counts = new List<FormatCount>();
            if (photos is null) return counts;

            var buckets = new Dictionary<string, (Services.FormatFamily Family, int Count)>(StringComparer.OrdinalIgnoreCase);

            foreach (var (fileName, family) in photos)
            {
                if (string.IsNullOrWhiteSpace(fileName)) continue;

                var extension = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
                if (extension.Length == 0) continue;

                if (buckets.TryGetValue(extension, out var existing))
                    buckets[extension] = (existing.Family, existing.Count + 1);
                else
                    buckets[extension] = (family, 1);
            }

            foreach (var pair in buckets)
                counts.Add(new FormatCount(pair.Key, pair.Value.Family, pair.Value.Count));

            counts.Sort(static (a, b) =>
            {
                var byCount = b.Count.CompareTo(a.Count);
                return byCount != 0 ? byCount : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });

            return counts;
        }

        /// <summary>The largest single count, for scaling the bars. Zero for an empty sequence.</summary>
        public static int Max(IReadOnlyList<FormatCount> counts)
        {
            var max = 0;
            if (counts is null) return max;

            foreach (var c in counts)
                if (c.Count > max) max = c.Count;

            return max;
        }

        /// <summary>e.g. "76 ARW · 20 CR2 · 4 JPG", for a one-line summary.</summary>
        public static string Summarise(IReadOnlyList<FormatCount> counts)
            => counts is null || counts.Count == 0
                ? string.Empty
                : string.Join(" · ", counts.Select(c => $"{c.Count:N0} {c.Label}"));
    }
}
