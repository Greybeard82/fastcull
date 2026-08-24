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

        // ---- PRD 1.5 Active Photo / 1.8.1 info overlay ----
        //
        // All nullable, and all read from the SAME metadata pass that resolves the sort time -
        // the directories are already in hand, so these cost a few tag lookups rather than a
        // second file read. Absent values stay null and the UI omits the field entirely; PRD 1.5
        // is explicit that a blank or placeholder row is worse than a missing one.

        /// <summary>Camera make and model, already de-duplicated ("Canon EOS 80D", not "Canon Canon EOS 80D").</summary>
        public string? CameraModel { get; init; }

        public int? PixelWidth { get; init; }
        public int? PixelHeight { get; init; }

        /// <summary>EXIF GPS. Null on the overwhelming majority of files - none of this repo's 101 samples carry any.</summary>
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }

        // The exposure triplet (PRD 1.5 / 1.8.1). Stored as MetadataExtractor's own descriptions -
        // "100 mm", "1/125 sec", "f/5.6" - which are already the display form, so nothing
        // downstream has to re-derive a rational into something readable.
        //
        // Null here means the file carried no figure. Unlike every other field on this type, the
        // UI renders that as "-" rather than omitting the row: for these three, absence is itself
        // information (an adapted or fully manual lens records no aperture). PRD 1.8.1 sets this
        // out as a deliberate exception and asks that it not be tidied back into consistency.
        public string? FocalLength { get; init; }
        public string? ShutterSpeed { get; init; }
        public string? Aperture { get; init; }

        public bool HasCoordinates => Latitude is not null && Longitude is not null;
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

            // One metadata read, shared. ResolveSortTime used to open the file itself; it now
            // takes the directories so the PRD 1.5 fields below do not cost a second pass.
            var directories = ReadMetadata(filePath);

            var (sortTime, source, subsecMs) = ResolveSortTime(directories, filePath);
            var (width, height) = ReadDimensions(directories);
            var (latitude, longitude) = ReadCoordinates(directories);
            var exposure = ReadExposure(directories);
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
                CameraModel = ReadCameraModel(directories),
                PixelWidth = width,
                PixelHeight = height,
                Latitude = latitude,
                Longitude = longitude,
                FocalLength = exposure.FocalLength,
                ShutterSpeed = exposure.ShutterSpeed,
                Aperture = exposure.Aperture,
            };
        }

        /// <summary>
        /// Focal length, exposure time and f-number, as MetadataExtractor's own descriptions.
        ///
        /// Every EXIF SubIFD is consulted rather than only the first: a RAW container commonly
        /// carries more than one, and the one describing the sensor data is not the one holding
        /// the capture settings - the same reason <see cref="ResolveSortTime"/> walks them all.
        /// </summary>
        internal static (string? FocalLength, string? ShutterSpeed, string? Aperture) ReadExposure(
            IReadOnlyList<MetadataExtractor.Directory> directories)
        {
            string? focal = null, shutter = null, aperture = null;

            foreach (var sub in directories.OfType<ExifSubIfdDirectory>())
            {
                focal ??= Clean(sub.GetDescription(ExifDirectoryBase.TagFocalLength));
                shutter ??= Clean(sub.GetDescription(ExifDirectoryBase.TagExposureTime));
                aperture ??= Clean(sub.GetDescription(ExifDirectoryBase.TagFNumber));

                if (focal is not null && shutter is not null && aperture is not null) break;
            }

            return (focal, shutter, aperture);

            static string? Clean(string? raw)
            {
                var trimmed = raw?.Trim();
                return string.IsNullOrEmpty(trimmed) ? null : trimmed;
            }
        }

        private static IReadOnlyList<MetadataExtractor.Directory> ReadMetadata(string filePath)
        {
            try { return ImageMetadataReader.ReadMetadata(filePath); }
            catch { return Array.Empty<MetadataExtractor.Directory>(); }
        }

        /// <summary>
        /// Make plus model, with the make dropped when the model already repeats it - most Canon
        /// bodies report Make "Canon" and Model "Canon EOS 80D", and concatenating blindly gives
        /// "Canon Canon EOS 80D".
        /// </summary>
        internal static string? ReadCameraModel(IReadOnlyList<MetadataExtractor.Directory> directories)
        {
            string? make = null, model = null;

            foreach (var ifd0 in directories.OfType<ExifIfd0Directory>())
            {
                make ??= Clean(ifd0.GetDescription(ExifDirectoryBase.TagMake));
                model ??= Clean(ifd0.GetDescription(ExifDirectoryBase.TagModel));
                if (make is not null && model is not null) break;
            }

            if (model is null) return make;
            if (make is null) return model;

            return model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";

            static string? Clean(string? raw)
            {
                var trimmed = raw?.Trim();
                return string.IsNullOrEmpty(trimmed) ? null : trimmed;
            }
        }

        /// <summary>
        /// Pixel dimensions. RAW carries them on the EXIF SubIFD; a JPEG export often does not and
        /// only the JPEG segment header has them, so both are consulted before giving up.
        /// </summary>
        internal static (int? Width, int? Height) ReadDimensions(IReadOnlyList<MetadataExtractor.Directory> directories)
        {
            foreach (var sub in directories.OfType<ExifSubIfdDirectory>())
            {
                if (sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var w) &&
                    sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var h) &&
                    w > 0 && h > 0)
                {
                    return (w, h);
                }
            }

            foreach (var jpeg in directories.OfType<MetadataExtractor.Formats.Jpeg.JpegDirectory>())
            {
                try
                {
                    var w = jpeg.GetImageWidth();
                    var h = jpeg.GetImageHeight();
                    if (w > 0 && h > 0) return (w, h);
                }
                catch (MetadataException) { /* tag absent - fall through */ }
            }

            return (null, null);
        }

        /// <summary>
        /// EXIF GPS, already converted out of the degrees/minutes/seconds rationals and signed by
        /// hemisphere. MetadataExtractor returns null when the tags are absent or unusable, which
        /// is the common case - see the note on <see cref="ScannedPhoto.Latitude"/>.
        /// </summary>
        internal static (double? Latitude, double? Longitude) ReadCoordinates(IReadOnlyList<MetadataExtractor.Directory> directories)
        {
            foreach (var gps in directories.OfType<GpsDirectory>())
            {
                var location = gps.GetGeoLocation();
                if (location is null) continue;

                // A zero island fix is the classic "GPS present but never locked" artefact.
                if (location.Value.IsZero) continue;

                return (location.Value.Latitude, location.Value.Longitude);
            }

            return (null, null);
        }

        /// <summary>Applies the sort-key hierarchy from PRD 1.3, first tier available wins.</summary>
        private static (DateTime SortTime, TimeSource Source, int? CaptureSubsecMs) ResolveSortTime(
            IReadOnlyList<MetadataExtractor.Directory> directories, string filePath)
        {
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
