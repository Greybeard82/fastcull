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
        internal const uint ThumbnailLongEdge = 160;
        internal const uint DisplayLongEdge = 960;

        public static Task<SoftwareBitmap?> DecodeThumbnailAsync(string filePath, CancellationToken cancellationToken = default)
            => DecodeScaledAsync(filePath, ThumbnailLongEdge, cancellationToken);

        public static Task<SoftwareBitmap?> DecodeDisplayImageAsync(string filePath, CancellationToken cancellationToken = default)
            => DecodeScaledAsync(filePath, DisplayLongEdge, cancellationToken);

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
        public static async Task<SoftwareBitmap?> DecodeScaledFromStreamAsync(
            IRandomAccessStream stream, uint longEdge, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                var scale = Math.Min(1.0, longEdge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
                var transform = new BitmapTransform
                {
                    ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                    ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                    InterpolationMode = BitmapInterpolationMode.Fant,
                };

                // Always awaited to completion, whatever cancellationToken does meanwhile - the
                // result is retrieved either way, so the native operation is never left orphaned.
                var bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
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
