using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Fastcull.Services
{
    /// <summary>
    /// Decodes JPEG/PNG images via WIC (Windows.Graphics.Imaging), per PRD 3.2's decode-pipeline
    /// tiers. Two independent decode paths, never sharing a bitmap: a small ~160px thumbnail for
    /// the bottom scrubber strip, and a larger "display tier" image sized to fill roughly a third
    /// of the screen for the top three-slot comparison view. Both return a plain SoftwareBitmap -
    /// no XAML types touched here, so neither needs the UI thread. Callers marshal the result onto
    /// the UI thread themselves to wrap it as a bindable ImageSource.
    ///
    /// Every WinRT async call here runs to its natural completion via a bare AsTask() - never
    /// AsTask(CancellationToken), which can complete the .NET Task as Canceled before the
    /// underlying native operation actually finishes, leaving that operation's eventual
    /// result/error unretrieved. An unretrieved WinRT async result is what triggers
    /// STATUS_STOWED_EXCEPTION (0xC000027B): a process fail-fast, not a catchable exception.
    /// Cancellation is instead checked BETWEEN stages, so a request to cancel stops the pipeline
    /// from starting the next step but never abandons one already in flight.
    /// </summary>
    public static class ThumbnailService
    {
        // Public because the eviction ceiling in PRD 3.3 needs to estimate how much memory a
        // resident item holds, and that estimate is derived from these tier sizes.
        public const uint ThumbnailLongEdge = 160;
        public const uint DisplayLongEdge = 960;

        /// <summary>
        /// Ceiling on a zoom-tier request. The zoom tier is sized to the viewport, and a viewport
        /// is not usually enormous - but this stops an absurd window size (or a bug) asking for a
        /// decode far larger than any screen, which is the same class of hazard PRD 3.3's
        /// dimension guard exists for. Note the decode never upscales: asking for more than the
        /// file holds simply yields the file's own resolution.
        /// </summary>
        public const uint MaxZoomLongEdge = 8192;

        /// <summary>
        /// The filmstrip's 160px tier.
        ///
        /// Tries the JPEG's own EXIF thumbnail first and only decodes the full image if that is
        /// missing or unusable. A camera has already written a ~160px copy into the file; decoding
        /// 16 megapixels to arrive at the same thing is work nobody needs done, and the filmstrip
        /// asks for it once per photo in the folder.
        /// </summary>
        public static async Task<SoftwareBitmap?> DecodeThumbnailAsync(string filePath, CancellationToken cancellationToken = default)
        {
            var embedded = await TryDecodeEmbeddedThumbnailAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (embedded is not null)
            {
                Diagnostics.PerfTrace.Count("thumb from EXIF");
                return embedded;
            }

            Diagnostics.PerfTrace.Count("thumb from full decode");
            return await DecodeScaledAsync(filePath, ThumbnailLongEdge, cancellationToken).ConfigureAwait(false);
        }

        public static Task<SoftwareBitmap?> DecodeDisplayImageAsync(string filePath, CancellationToken cancellationToken = default)
            => DecodeScaledAsync(filePath, DisplayLongEdge, cancellationToken);

        /// <summary>
        /// Zoom tier: sized to the viewport rather than to a filmstrip slot. The display tier is
        /// ~960px because it only ever fills about a third of the stage; stretching that across a
        /// whole screen is visibly soft, which is what this exists to fix.
        /// </summary>
        public static Task<SoftwareBitmap?> DecodeZoomImageAsync(
            string filePath, uint longEdge, CancellationToken cancellationToken = default)
            => DecodeScaledAsync(filePath, Math.Clamp(longEdge, DisplayLongEdge, MaxZoomLongEdge), cancellationToken);

        /// <summary>
        /// Above this, decode straight from the file rather than buffering it. No camera writes a
        /// file this large; the cap only exists so a pathological input cannot turn six concurrent
        /// decodes into six enormous allocations.
        /// </summary>
        private const long MaxBufferedFileBytes = 256L * 1024 * 1024;

        /// <summary>
        /// An embedded thumbnail below this on its long edge is rejected. Some cameras store a
        /// postage stamp; upscaling it into a 160px slot looks worse than the extra decode costs.
        /// </summary>
        private const uint MinimumEmbeddedLongEdge = 96;

        /// <summary>
        /// How far the embedded thumbnail's aspect may differ from the full image's before it is
        /// rejected. **This guard is not optional.** A great many cameras pad the EXIF thumbnail to
        /// 4:3 with black bars regardless of the picture's real shape, and a 3:2 photo shown from
        /// such a thumbnail would sit in a letterbox that the full decode does not have - PRD 1.10
        /// forbids exactly that kind of non-photograph pixel, and the strip would visibly disagree
        /// with the stage.
        /// </summary>
        private const double MaxAspectDrift = 0.02;

        /// <summary>
        /// Returns the file's own EXIF thumbnail, or null when there is not a usable one.
        ///
        /// Measured on an i5-12400 over 15.9 MP JPEGs: ~14 ms against ~104 ms for the full decode
        /// through <see cref="DecodeScaledAsync"/>. The saving is the whole point - the filmstrip
        /// realizes a container for every photo scrolled past, so this path runs once per photo in
        /// the folder while the display tier runs only for the prefetch window.
        ///
        /// Null is a perfectly ordinary answer: PNGs have no EXIF at all, some JPEGs omit the
        /// thumbnail, and any camera that pads it to the wrong aspect is refused here. Every one of
        /// those falls through to the full decode and simply costs what it always cost.
        /// </summary>
        private static async Task<SoftwareBitmap?> TryDecodeEmbeddedThumbnailAsync(
            string filePath, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var length = new FileInfo(filePath).Length;
                if (length > MaxBufferedFileBytes) return null;

                var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);

                using var stream = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                // Not every container has one, and asking throws rather than returning null on
                // those - so the absence is caught, not tested for.
                using var thumbStream = await decoder.GetThumbnailAsync().AsTask().ConfigureAwait(false);
                if (thumbStream is null || thumbStream.Size == 0) return null;

                var thumbDecoder = await BitmapDecoder.CreateAsync(thumbStream).AsTask().ConfigureAwait(false);

                if (Math.Max(thumbDecoder.PixelWidth, thumbDecoder.PixelHeight) < MinimumEmbeddedLongEdge)
                    return null;

                if (thumbDecoder.PixelHeight == 0 || decoder.PixelHeight == 0) return null;

                var thumbAspect = thumbDecoder.PixelWidth / (double)thumbDecoder.PixelHeight;
                var fullAspect = decoder.PixelWidth / (double)decoder.PixelHeight;
                if (Math.Abs(thumbAspect - fullAspect) / fullAspect > MaxAspectDrift) return null;

                cancellationToken.ThrowIfCancellationRequested();

                // The thumbnail carries no orientation tag of its own - it is stored the same way
                // up as the full image, so the FULL image's tag is the one that applies. Read it
                // from the outer decoder and hand it in explicitly, exactly as the RAW path does.
                var orientation = await ReadOrientationAsync(decoder).ConfigureAwait(false);

                var scale = Math.Min(1.0,
                    ThumbnailLongEdge / (double)Math.Max(thumbDecoder.PixelWidth, thumbDecoder.PixelHeight));

                var (flip, rotation) = ExifOrientation.ToTransform(orientation);

                var transform = new BitmapTransform
                {
                    ScaledWidth = (uint)Math.Max(1, Math.Round(thumbDecoder.PixelWidth * scale)),
                    ScaledHeight = (uint)Math.Max(1, Math.Round(thumbDecoder.PixelHeight * scale)),
                    InterpolationMode = BitmapInterpolationMode.Fant,
                    Flip = flip,
                    Rotation = rotation,
                };

                return await thumbDecoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage)
                    .AsTask().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // No thumbnail, an unreadable one, or a container that does not support the call.
                // All the same answer: use the full decode.
                return null;
            }
        }

        /// <summary>Reads the EXIF orientation tag, defaulting to Normal when absent.</summary>
        private static async Task<int> ReadOrientationAsync(BitmapDecoder decoder)
        {
            try
            {
                var properties = await decoder.BitmapProperties
                    .GetPropertiesAsync(new[] { "System.Photo.Orientation" }).AsTask().ConfigureAwait(false);

                if (properties.TryGetValue("System.Photo.Orientation", out var value) && value?.Value is ushort raw)
                    return raw;
            }
            catch (Exception)
            {
            }

            return ExifOrientation.Normal;
        }

        /// <summary>
        /// **The bytes are read into memory first, and the decoder reads from THAT, not from the
        /// file.** This is not a micro-optimisation - it is the difference between the decode
        /// pipeline using one core and using all of them.
        ///
        /// Measured on an i5-12400 (12 logical) over 15.9 MP JPEGs, 48 decodes at the 160px tier:
        ///
        ///     threads   via FileRandomAccessStream   via an in-memory copy
        ///           1        13.5/sec, 1.00 cores       12.8/sec, 0.98 cores
        ///           4        13.5/sec, 0.99 cores       36.2/sec, 2.91 cores
        ///          12        12.5/sec, 0.98 cores       75.7/sec, 7.81 cores
        ///
        /// Identical decode work in both columns; the only difference is where the decoder reads
        /// its bytes from. Through a FileRandomAccessStream, WIC serialises process-wide - cores
        /// busy pins at 1.00 however many threads ask - so the bounded decode pool was a queue in
        /// front of a single worker and the filmstrip could never load faster than ~13 photos a
        /// second no matter what hardware it ran on. It is also why RAW never showed the symptom:
        /// RawPreviewDecoder already extracts the embedded JPEG into a byte[] and decodes from
        /// memory, so it was scaling to ~10 cores while JPEG sat on one.
        ///
        /// The MemoryStream wraps the array directly rather than copying it into an
        /// InMemoryRandomAccessStream, so the cost is one read of the file and no second buffer.
        /// </summary>
        private static async Task<SoftwareBitmap?> DecodeScaledAsync(string filePath, uint longEdge, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                long fileLength;
                try
                {
                    fileLength = new FileInfo(filePath).Length;
                }
                catch (Exception)
                {
                    fileLength = 0;
                }

                if (fileLength > MaxBufferedFileBytes)
                {
                    using var fileStream = await FileRandomAccessStream.OpenAsync(filePath, FileAccessMode.Read)
                        .AsTask().ConfigureAwait(false);

                    return await DecodeScaledFromStreamAsync(fileStream, longEdge, cancellationToken).ConfigureAwait(false);
                }

                var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);

                using var stream = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
                return await DecodeScaledFromStreamAsync(stream, longEdge, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Shared scaled-decode used by both the WIC file path above and
        /// <see cref="RawPreviewDecoder"/>, which hands in an in-memory stream wrapping a JPEG
        /// preview lifted out of a RAW container. Returns null on any decode failure; propagates
        /// cancellation. The same never-race-a-WinRT-call rule as above applies.
        /// </summary>
        /// <param name="explicitOrientation">
        /// An EXIF orientation (1-8) to apply in place of whatever the stream itself carries.
        /// Only the RAW path passes this: its bytes are a JPEG sliced out of a container, and the
        /// orientation tag stayed behind in the container. When supplied, the stream's own EXIF is
        /// deliberately ignored rather than combined, so the rotation can never be applied twice.
        /// </param>
        public static async Task<SoftwareBitmap?> DecodeScaledFromStreamAsync(
            IRandomAccessStream stream, uint longEdge, CancellationToken cancellationToken,
            int explicitOrientation = ExifOrientation.Normal)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                // PRD 3.3's dimension guard, applied where the decode size is actually chosen.
                // Defence in depth: the tiers above already request modest sizes, but a caller
                // asking for a huge edge on an extreme aspect must never be able to allocate a
                // half-gigabyte buffer here.
                longEdge = DimensionGuard.ClampLongEdge(longEdge, decoder.PixelWidth, decoder.PixelHeight);

                // Scale is computed from the stored dimensions, before any quarter turn. That is
                // correct either way: a rotation swaps width and height but not their maximum,
                // which is what the long edge is measured against.
                var scale = Math.Min(1.0, longEdge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
                var (flip, rotation) = ExifOrientation.ToTransform(explicitOrientation);

                var transform = new BitmapTransform
                {
                    ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                    ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                    InterpolationMode = BitmapInterpolationMode.Fant,
                    Flip = flip,
                    Rotation = rotation,
                };

                // Always awaited to completion, whatever cancellationToken does meanwhile - the
                // result is retrieved either way, so the native operation is never left orphaned.
                var bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    // Respect the stream's own EXIF only when the caller has not supplied one.
                    // Doing both would rotate a portrait RAW twice on any container whose embedded
                    // preview happens to carry its own tag.
                    ExifOrientation.IsRotated(explicitOrientation)
                        ? ExifOrientationMode.IgnoreExifOrientation
                        : ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return bitmap;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
