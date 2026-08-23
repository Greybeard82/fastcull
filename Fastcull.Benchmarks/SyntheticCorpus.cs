using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Fastcull.Benchmarks
{
    /// <summary>
    /// Builds the ~2,000-file corpus the two scale budgets in PRD 3.5 are defined against
    /// ("full scan, 2,000 files" and "peak working set, 2,000 files"). The real SampleImages
    /// corpus is 100 files, so the count has to be manufactured.
    ///
    /// It is manufactured with NTFS hard links, not copies. At an average 56 MB per sample file
    /// a real 2,000-file copy is about 112 GB, which is not a reasonable thing to write to a
    /// developer's disk every time a benchmark runs. A hard link is a genuine directory entry
    /// pointing at the same file data, so every operation the scanner performs behaves
    /// identically: EnumerateFiles finds it, FileInfo.Length is the true length, and
    /// MetadataExtractor parses the real header bytes off the real extent.
    ///
    /// The honest caveat, which the results file repeats: 2,000 entries are backed by only 100
    /// distinct extents, so the OS page cache is far warmer than it would be against 2,000
    /// genuinely distinct files. That biases the scan and decode timings OPTIMISTICALLY. The
    /// harness compensates by also measuring the real 100-file corpus, where every file is
    /// distinct, and comparing per-file cost between the two - if they agree, the linking is
    /// not distorting the result much.
    ///
    /// Hard links need no elevation (unlike symlinks) but do require the same volume, so the
    /// corpus is created under the repo rather than in TEMP. It is gitignored - synthetic
    /// fixture data is never committed.
    /// </summary>
    internal static class SyntheticCorpus
    {
        public const int TargetFileCount = 2000;

        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr reserved);

        public sealed record Result(string Root, int FileCount, int DistinctSourceCount, bool UsedHardLinks);

        /// <summary>
        /// Materialises <paramref name="targetCount"/> entries under <paramref name="destinationRoot"/>
        /// by cycling through the real sample files. Format mix is preserved proportionally, so a
        /// corpus that is 76% RAW in reality stays 76% RAW at 2,000 files - the "mixed set is the
        /// one that finds bugs" point in PRD 3.5 only holds if the mix survives scaling.
        /// </summary>
        public static Result Build(string sourceRoot, string destinationRoot, int targetCount)
        {
            var sources = System.IO.Directory
                .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(FixtureSets.IsSupported)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sources.Count == 0)
                throw new InvalidOperationException($"No supported sample files under {sourceRoot}.");

            System.IO.Directory.CreateDirectory(destinationRoot);

            // Reuse an already-built corpus. Rebuilding 2,000 links every run costs seconds and
            // buys nothing - the content is deterministic.
            var existing = System.IO.Directory.GetFiles(destinationRoot);
            if (existing.Length == targetCount)
                return new Result(destinationRoot, targetCount, sources.Count, UsedHardLinks: true);

            foreach (var stale in existing)
                File.Delete(stale);

            var usedHardLinks = true;

            for (var i = 0; i < targetCount; i++)
            {
                var source = sources[i % sources.Count];
                var name = $"{i:D5}_{Path.GetFileNameWithoutExtension(source)}{Path.GetExtension(source)}";
                var destination = Path.Combine(destinationRoot, name);

                if (!CreateHardLinkW(destination, source, IntPtr.Zero))
                {
                    // Fall back to a real copy only if linking is genuinely unavailable (e.g. the
                    // repo sits on a non-NTFS volume). Loud, because it changes what is measured
                    // and could write ~112 GB.
                    usedHardLinks = false;
                    throw new IOException(
                        $"CreateHardLink failed for '{destination}' (Win32 {Marshal.GetLastWin32Error()}). " +
                        "The synthetic corpus needs NTFS hard links; copying instead would write roughly 112 GB.");
                }
            }

            return new Result(destinationRoot, targetCount, sources.Count, usedHardLinks);
        }

        /// <summary>Total bytes the corpus would occupy if it were copies rather than links.</summary>
        public static long LogicalBytes(string root)
            => System.IO.Directory.EnumerateFiles(root).Sum(p => new FileInfo(p).Length);
    }
}
