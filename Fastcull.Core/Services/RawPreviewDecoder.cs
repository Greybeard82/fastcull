using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Fastcull.Services
{
    /// <summary>
    /// Decodes RAW files (.ARW, .CR2) by lifting out the JPEG preview every RAW container
    /// embeds, then handing those bytes to the same WIC scaled-decode the JPEG/PNG path uses.
    ///
    /// Why not a debayer: PRD 3.2 needs only the thumbnail (~160 px) and display (~960 px)
    /// tiers today - full resolution is zoom-only and out of scope until v0.2. Measured against
    /// this repo's SampleImages, a Sony .ARW embeds a 160x120 thumbnail, a 1616x1080 preview and
    /// a full 7008x4672 JPEG; a Canon .CR2 embeds a 160x120 thumbnail and a full 6000x4000 JPEG.
    /// Every tier this app needs is therefore already present as ordinary JPEG, and extracting
    /// it costs about 30 ms against roughly 1000 ms for WIC's full debayer of the same file.
    ///
    /// This also avoids the package risk PRD section 7 flags: no RAW library is involved at all.
    /// </summary>
    public static class RawPreviewDecoder
    {
        /// <summary>
        /// How far into the file to look for embedded JPEGs. Previews live near the start of
        /// both containers (measured: ARW's full-size JPEG ends by ~4.7 MB, CR2's by ~1.6 MB),
        /// well inside this bound, which also keeps the scan off the multi-megabyte sensor data.
        /// </summary>
        private const int ScanWindowBytes = 16 * 1024 * 1024;

        /// <summary>Smallest candidate worth considering; below this it is a stub, not a preview.</summary>
        private const int MinimumCandidateBytes = 1024;

        public static Task<SoftwareBitmap?> DecodeThumbnailAsync(string filePath, CancellationToken cancellationToken = default)
            => DecodePreviewAsync(filePath, ThumbnailService.ThumbnailLongEdge, cancellationToken);

        public static Task<SoftwareBitmap?> DecodeDisplayImageAsync(string filePath, CancellationToken cancellationToken = default)
            => DecodePreviewAsync(filePath, ThumbnailService.DisplayLongEdge, cancellationToken);

        /// <summary>
        /// Zoom tier for RAW: the same candidate walk as the display tier, just asked for a
        /// bigger size. Nothing new is needed to make this work - the selection below already
        /// picks the smallest embedded JPEG that satisfies the requested edge, so a display-tier
        /// request settles for the small ~1616px preview while a zoom-tier request reaches past
        /// it to the full-sensor-width one the container also carries.
        ///
        /// Measured: an .ARW embeds a 7008x4672 JPEG and a .CR2 a 6000x4000 one, both inside the
        /// 16 MB scan window, so a viewport-sized request is satisfied from data already in the
        /// file with no debayer and no new decoder.
        /// </summary>
        public static Task<SoftwareBitmap?> DecodeZoomImageAsync(
            string filePath, uint longEdge, CancellationToken cancellationToken = default)
            => DecodePreviewAsync(
                filePath,
                Math.Clamp(longEdge, ThumbnailService.DisplayLongEdge, ThumbnailService.MaxZoomLongEdge),
                cancellationToken);

        private static async Task<SoftwareBitmap?> DecodePreviewAsync(string filePath, uint longEdge, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (buffer, length) = await ReadScanWindowAsync(filePath, cancellationToken).ConfigureAwait(false);
                var candidates = FindEmbeddedJpegs(buffer, length);
                if (candidates.Count == 0) return null;

                // Read from the container, not from the candidate: slicing an embedded JPEG out of
                // a RAW leaves the container's TIFF IFD0 - and its orientation tag - behind, which
                // is exactly why every portrait RAW used to render on its side.
                var orientation = ExifOrientation.Read(filePath);

                // Smallest candidate that still covers the tier: decoding a 160x120 thumbnail
                // for the thumbnail tier is far cheaper than decoding the full-size preview and
                // throwing the pixels away. Sorting ascending also means the huge lossless-JPEG
                // sensor blob inside a .CR2 is only ever reached as a last resort - and it fails
                // to decode ("Unsupported JPEG data precision 14"), which is handled below.
                candidates.Sort(static (a, b) => a.Length.CompareTo(b.Length));

                SoftwareBitmap? best = null;

                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var bitmap = await TryDecodeCandidateAsync(buffer, candidate, longEdge, orientation, cancellationToken).ConfigureAwait(false);
                    if (bitmap is null) continue;

                    best?.Dispose();
                    best = bitmap;

                    // Good enough for this tier - stop before touching anything larger.
                    if (Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) >= longEdge) break;
                }

                return best;
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

        private static async Task<(byte[] Buffer, int Length)> ReadScanWindowAsync(string filePath, CancellationToken cancellationToken)
        {
            using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 16, useAsync: true);

            var size = (int)Math.Min(file.Length, ScanWindowBytes);
            var buffer = new byte[size];

            var read = 0;
            while (read < size)
            {
                var n = await file.ReadAsync(buffer.AsMemory(read, size - read), cancellationToken).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }

            return (buffer, read);
        }

        /// <summary>
        /// Locates embedded JPEG streams by their SOI (FF D8 FF) and EOI (FF D9) markers. This is
        /// deliberately vendor-agnostic: Sony and Canon put their previews at different offsets
        /// and describe them with different maker-note tags, but both store ordinary JPEG.
        /// </summary>
        internal static List<JpegCandidate> FindEmbeddedJpegs(byte[] buffer, int length)
        {
            var found = new List<JpegCandidate>();

            for (var i = 0; i + 3 < length; i++)
            {
                if (buffer[i] != 0xFF || buffer[i + 1] != 0xD8 || buffer[i + 2] != 0xFF) continue;

                for (var j = i + 2; j + 1 < length; j++)
                {
                    if (buffer[j] != 0xFF || buffer[j + 1] != 0xD9) continue;

                    var len = j + 2 - i;
                    if (len >= MinimumCandidateBytes) found.Add(new JpegCandidate(i, len));

                    // Resume past this stream rather than inside it, so a marker pair in the
                    // entropy-coded data cannot spawn overlapping duplicates.
                    i = j + 1;
                    break;
                }
            }

            return found;
        }

        private static async Task<SoftwareBitmap?> TryDecodeCandidateAsync(
            byte[] buffer, JpegCandidate candidate, uint longEdge, int orientation, CancellationToken cancellationToken)
        {
            try
            {
                var bytes = new byte[candidate.Length];
                Array.Copy(buffer, candidate.Offset, bytes, 0, candidate.Length);

                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer()).AsTask().ConfigureAwait(false);
                stream.Seek(0);

                return await ThumbnailService.DecodeScaledFromStreamAsync(
                    stream, longEdge, cancellationToken, orientation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // A candidate that is not really a JPEG (notably the lossless-JPEG sensor data
                // in a .CR2) simply does not count as a preview.
                return null;
            }
        }

        internal readonly record struct JpegCandidate(int Offset, int Length);
    }
}
