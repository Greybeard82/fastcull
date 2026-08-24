using System;
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

        public static Task<SoftwareBitmap?> DecodeThumbnailAsync(string filePath, CancellationToken cancellationToken = default)
            => DecodeScaledAsync(filePath, ThumbnailLongEdge, cancellationToken);

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

        private static async Task<SoftwareBitmap?> DecodeScaledAsync(string filePath, uint longEdge, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = await FileRandomAccessStream.OpenAsync(filePath, FileAccessMode.Read)
                    .AsTask().ConfigureAwait(false);

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
