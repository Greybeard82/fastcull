using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Fastcull.Services
{
    /// <summary>Broad format grouping, per PRD 1.1. Drives decoder selection in later phases.</summary>
    public enum FormatFamily
    {
        Raw,
        Jpeg,
        Heif,
        Png,
        Tiff,
        Other
    }

    /// <summary>Which tier of PRD 1.3's sort-key hierarchy a photo's SortTime came from.</summary>
    public enum TimeSource
    {
        CaptureDate = 1,
        DigitizedDate = 2,
        FileModified = 3
    }

    /// <summary>
    /// Per-file result of a directory scan: identity, detected format, and resolved sort time.
    /// Deliberately excludes rating, companion grouping, and camera metadata, which belong to
    /// the full PhotoItem model in PRD 5.1.
    /// </summary>
    public sealed class ScannedPhoto
    {
        public required string FilePath { get; init; }
        public required string RelativePath { get; init; }
        public required string FileName { get; init; }
        public required FormatFamily Family { get; init; }
        public long FileBytes { get; init; }
        public DateTime SortTime { get; init; }
        public TimeSource SortTimeSource { get; init; }
        public int? CaptureSubsec { get; init; }
    }

    /// <summary>
    /// Recursive, parallel discovery of supported image files under a root, per PRD 1.2.
    /// Touches file headers only (via MetadataExtractor) - never pixel data. Results stream
    /// as they are found; callers apply the final sort themselves once the scan completes.
    /// </summary>
    public sealed class DirectoryScanner
    {
        private static readonly IReadOnlyDictionary<string, FormatFamily> ExtensionToFamily =
            new Dictionary<string, FormatFamily>(StringComparer.OrdinalIgnoreCase)
            {
                [".arw"] = FormatFamily.Raw,
                [".cr3"] = FormatFamily.Raw,
                [".cr2"] = FormatFamily.Raw,
                [".nef"] = FormatFamily.Raw,
                [".raf"] = FormatFamily.Raw,
                [".orf"] = FormatFamily.Raw,
                [".rw2"] = FormatFamily.Raw,
                [".dng"] = FormatFamily.Raw,
                [".pef"] = FormatFamily.Raw,
                [".srw"] = FormatFamily.Raw,

                [".jpg"] = FormatFamily.Jpeg,
                [".jpeg"] = FormatFamily.Jpeg,
                [".jfif"] = FormatFamily.Jpeg,

                [".heic"] = FormatFamily.Heif,
                [".heif"] = FormatFamily.Heif,
                [".avif"] = FormatFamily.Heif,

                [".png"] = FormatFamily.Png,

                [".tif"] = FormatFamily.Tiff,
                [".tiff"] = FormatFamily.Tiff,

                [".webp"] = FormatFamily.Other,
                [".bmp"] = FormatFamily.Other,
                [".gif"] = FormatFamily.Other,
            };

        public async IAsyncEnumerable<ScannedPhoto> ScanAsync(
            string rootPath,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Root path must be provided.", nameof(rootPath));
            if (!System.IO.Directory.Exists(rootPath))
                throw new DirectoryNotFoundException($"Scan root not found: {rootPath}");

            var channel = Channel.CreateUnbounded<ScannedPhoto>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

            int workerCount = Math.Max(1, Environment.ProcessorCount - 2);

            var producer = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(
                        EnumerateSupportedFiles(rootPath),
                        new ParallelOptions { MaxDegreeOfParallelism = workerCount, CancellationToken = cancellationToken },
                        async (filePath, ct) =>
                        {
                            var photo = ParsePhoto(rootPath, filePath, ct);
                            await channel.Writer.WriteAsync(photo, ct).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            }, cancellationToken);

            try
            {
                await foreach (var photo in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield return photo;
                }
            }
            finally
            {
                try { await producer.ConfigureAwait(false); }
                catch { /* already surfaced above via the channel */ }
            }
        }

        private static IEnumerable<string> EnumerateSupportedFiles(string rootPath)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
            };

            foreach (var filePath in System.IO.Directory.EnumerateFiles(rootPath, "*", options))
            {
                if (ExtensionToFamily.ContainsKey(Path.GetExtension(filePath)))
                    yield return filePath;
            }
        }

        private static ScannedPhoto ParsePhoto(string rootPath, string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var family = ExtensionToFamily.TryGetValue(Path.GetExtension(filePath), out var f) ? f : FormatFamily.Other;
            var (sortTime, source, subsecMs) = ResolveSortTime(filePath);
            var fileBytes = new FileInfo(filePath).Length;

            return new ScannedPhoto
            {
                FilePath = filePath,
                RelativePath = Path.GetRelativePath(rootPath, filePath),
                FileName = Path.GetFileName(filePath),
                Family = family,
                FileBytes = fileBytes,
                SortTime = sortTime,
                SortTimeSource = source,
                CaptureSubsec = subsecMs,
            };
        }

        /// <summary>Applies the sort-key hierarchy from PRD 1.3, first tier available wins.</summary>
        private static (DateTime SortTime, TimeSource Source, int? CaptureSubsecMs) ResolveSortTime(string filePath)
        {
            IReadOnlyList<MetadataExtractor.Directory> directories;
            try
            {
                directories = ImageMetadataReader.ReadMetadata(filePath);
            }
            catch
            {
                directories = Array.Empty<MetadataExtractor.Directory>();
            }

            // RAW containers commonly carry more than one ExifSubIfdDirectory (e.g. one
            // describing the raw sensor data, one with the actual capture metadata) - the tag
            // we want may not be on the first one, so every IFD is checked before falling back.
            var subIfds = directories.OfType<ExifSubIfdDirectory>().ToList();

            foreach (var subIfd in subIfds)
            {
                if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var captured))
                {
                    var subsecMs = ParseSubsecondMilliseconds(subIfd.GetString(ExifDirectoryBase.TagSubsecondTimeOriginal));
                    if (subsecMs is int ms1)
                        captured = captured.AddMilliseconds(ms1);
                    return (captured, TimeSource.CaptureDate, subsecMs);
                }
            }

            foreach (var subIfd in subIfds)
            {
                if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var digitized))
                {
                    var subsecMs = ParseSubsecondMilliseconds(subIfd.GetString(ExifDirectoryBase.TagSubsecondTimeDigitized));
                    if (subsecMs is int ms2)
                        digitized = digitized.AddMilliseconds(ms2);
                    return (digitized, TimeSource.DigitizedDate, subsecMs);
                }
            }

            return (File.GetLastWriteTime(filePath), TimeSource.FileModified, null);
        }

        /// <summary>EXIF SubSecTime tags are ASCII digits of a decimal fraction (e.g. "40" == .40s), not raw milliseconds.</summary>
        private static int? ParseSubsecondMilliseconds(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            raw = raw.Trim();
            if (raw.Length == 0 || !raw.All(char.IsDigit))
                return null;

            return (int)Math.Round(double.Parse("0." + raw, CultureInfo.InvariantCulture) * 1000);
        }
    }
}
