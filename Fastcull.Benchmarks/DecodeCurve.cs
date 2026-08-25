using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Fastcull.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Fastcull.Benchmarks
{
    /// <summary>
    /// Measures what a decode actually costs as a function of the size asked for, and what the
    /// cheaper routes to a thumbnail would cost instead.
    ///
    /// Written for the "slower hardware leaves photos unloaded after scrolling stops" report. The
    /// in-app trace showed the 160px thumbnail tier costing about the same as the 960px display
    /// tier, which should be impossible if the requested size were driving the work - so the
    /// question is what the decode is really spending its time on, and that needs a controlled
    /// measurement rather than timings sampled under a live scroll.
    ///
    /// Also covers the 4K angle: every previous measurement on this project was taken on a
    /// 3440x1440 machine, and the zoom tier is the one size that scales with the display.
    /// </summary>
    internal static class DecodeCurve
    {
        /// <summary>Sizes worth knowing, and why each one is in the list.</summary>
        private static readonly (uint Edge, string Note)[] Sizes =
        [
            (160,  "thumbnail tier"),
            (320,  ""),
            (480,  ""),
            (960,  "display tier"),
            (1440, ""),
            (2158, "zoom, 3440x1440 dev machine"),
            (3240, "zoom, 3840x2160 at 100%"),
            (4864, "zoom, 3840x2160 at 150%"),
        ];

        public static async Task<int> RunAsync(string corpus, int sampleCount)
        {
            var files = Directory.EnumerateFiles(corpus)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".cr2", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".arw", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(sampleCount)
                .ToList();

            if (files.Count == 0)
            {
                Console.Error.WriteLine($"No decodable files in {corpus}");
                return 2;
            }

            Console.WriteLine($"Corpus : {corpus}");
            Console.WriteLine($"Sample : {files.Count} files");
            Console.WriteLine($"CPU    : {Environment.ProcessorCount} logical processors, "
                              + $"DecodeGate.MaxConcurrency = {DecodeGate.MaxConcurrency}");
            Console.WriteLine();

            await ReportSourceDimensionsAsync(files[0]).ConfigureAwait(false);

            // One untimed pass so the file cache and the WIC codec are warm; otherwise the first
            // size in the list absorbs all the cold-start cost and reads as the expensive one.
            Console.Write("Warming... ");
            foreach (var f in files) await ThumbnailService.DecodeThumbnailAsync(f).ConfigureAwait(false);
            Console.WriteLine("done");
            Console.WriteLine();

            Console.WriteLine("SEQUENTIAL DECODE COST BY REQUESTED LONG EDGE");
            Console.WriteLine("(one decode at a time, so this is pure per-decode cost)");
            Console.WriteLine();
            Console.WriteLine($"  {"edge",6}  {"median",8}  {"mean",8}  {"min",8}  {"max",8}   note");

            // Deliberately NOT through DecodeZoomImageAsync: that clamps its argument up to the
            // 960px display tier, which would silently turn the three smallest rows into 960 and
            // make the curve look flat for the wrong reason.
            var baseline = 0.0;
            foreach (var (edge, note) in Sizes)
            {
                var times = new List<double>();
                foreach (var f in files)
                    times.Add(await TimeAsync(f, s => ScaledAsync(s, edge, BitmapInterpolationMode.Fant)).ConfigureAwait(false));

                var ok = times.Where(t => t >= 0).ToList();
                if (ok.Count == 0) continue;

                var med = Median(ok);
                if (edge == 160) baseline = med;

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {edge,6}  {med,7:F1}ms  {ok.Average(),7:F1}ms  {ok.Min(),7:F1}ms  {ok.Max(),7:F1}ms   {note}"));
            }

            Console.WriteLine();

            Console.WriteLine("THUMBNAIL ROUTES COMPARED (the 160px tier, four ways)");
            Console.WriteLine();

            await CompareThumbnailRoutesAsync(files).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"(160px tier baseline from the curve above: {baseline:F1} ms)");
            Console.WriteLine();

            await ConcurrencySweepAsync(files).ConfigureAwait(false);
            return 0;
        }

        /// <summary>
        /// Aggregate throughput as the number of simultaneous decodes rises.
        ///
        /// This is the measurement that says whether DecodeGate's cap is sized usefully. If
        /// throughput is flat across the sweep, the decodes are saturating something that is not
        /// parallelism - CPU or memory bandwidth - and raising or lowering the cap moves nothing
        /// except how long each individual decode appears to take.
        /// </summary>
        private static async Task ConcurrencySweepAsync(List<string> files)
        {
            Console.WriteLine("THROUGHPUT vs SIMULTANEOUS DECODES (160px tier, the filmstrip's workload)");
            Console.WriteLine();
            Console.WriteLine($"  {"threads",7}  {"total",9}  {"per decode",11}  {"throughput",12}  {"cores busy",11}");

            await SweepAsync(files, viaMemory: false).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("THE SAME SWEEP, DECODING FROM AN IN-MEMORY COPY OF THE FILE");
            Console.WriteLine("(identical decode work; only the stream the decoder reads from differs)");
            Console.WriteLine();
            Console.WriteLine($"  {"threads",7}  {"total",9}  {"per decode",11}  {"throughput",12}  {"cores busy",11}");

            await SweepAsync(files, viaMemory: true).ConfigureAwait(false);
        }

        private static async Task SweepAsync(List<string> files, bool viaMemory)
        {
            foreach (var threads in new[] { 1, 2, 4, 6, 8, 12 })
            {
                // Enough work that the measurement is not dominated by ramp-up.
                var work = new List<string>();
                while (work.Count < 48) work.AddRange(files);
                work = work.Take(48).ToList();

                using var gate = new SemaphoreSlim(threads, threads);

                // CPU time over wall time is how many cores were actually busy. If this stays near
                // 1 while the thread count climbs, the work is serialised inside the decoder and
                // the extra threads are only queueing.
                var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
                var clock = Stopwatch.StartNew();

                await Task.WhenAll(work.Select(async f =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (viaMemory) await MemoryDecodeAsync(f).ConfigureAwait(false);
                        else await TimeAsync(f, s => ScaledAsync(s, 160, BitmapInterpolationMode.Fant)).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                })).ConfigureAwait(false);

                clock.Stop();
                var cpuUsed = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;

                var perDecode = clock.Elapsed.TotalMilliseconds / work.Count * threads;
                var throughput = work.Count / clock.Elapsed.TotalSeconds;
                var coresBusy = cpuUsed.TotalMilliseconds / clock.Elapsed.TotalMilliseconds;

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {threads,7}  {clock.Elapsed.TotalMilliseconds,8:F0}ms  {perDecode,10:F0}ms  {throughput,9:F1}/sec  {coresBusy,10:F2}"));
            }
        }

        private static async Task ReportSourceDimensionsAsync(string file)
        {
            using var stream = await FileRandomAccessStream.OpenAsync(file, FileAccessMode.Read).AsTask().ConfigureAwait(false);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);

            var mp = decoder.PixelWidth * decoder.PixelHeight / 1_000_000.0;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Source : {Path.GetFileName(file)} is {decoder.PixelWidth}x{decoder.PixelHeight} ({mp:F1} MP), "
                + $"{new FileInfo(file).Length / 1024.0 / 1024.0:F1} MB on disk"));
            Console.WriteLine();
        }

        /// <summary>
        /// The four ways to get ~160px out of a JPEG, timed against each other.
        ///
        /// The point of the comparison is to separate the cost of DECODING the full image from the
        /// cost of producing a small one. If they are the same, the tier size is not what the work
        /// is proportional to, and asking for a smaller picture is buying nothing.
        /// </summary>
        private static async Task CompareThumbnailRoutesAsync(List<string> files)
        {
            var fant = new List<double>();
            var linear = new List<double>();
            var preview = new List<double>();
            var embedded = new List<double>();

            foreach (var f in files)
            {
                fant.Add(await TimeAsync(f, s => ScaledAsync(s, 160, BitmapInterpolationMode.Fant)).ConfigureAwait(false));
                linear.Add(await TimeAsync(f, s => ScaledAsync(s, 160, BitmapInterpolationMode.Linear)).ConfigureAwait(false));
                preview.Add(await TimeAsync(f, PreviewAsync).ConfigureAwait(false));
                embedded.Add(await TimeAsync(f, EmbeddedThumbnailAsync).ConfigureAwait(false));
            }

            Row("full decode + Fant scale  (what ships today)", fant);
            Row("full decode + Linear scale", linear);
            Row("decoder.GetPreviewAsync()", preview);
            Row("decoder.GetThumbnailAsync() (EXIF)", embedded);

            static void Row(string label, List<double> times)
            {
                var ok = times.Where(t => t >= 0).ToList();
                if (ok.Count == 0)
                {
                    Console.WriteLine($"  {label,-46}  unavailable for this format");
                    return;
                }

                var missing = ok.Count < times.Count
                    ? string.Create(CultureInfo.InvariantCulture, $"   ({times.Count - ok.Count}/{times.Count} unavailable)")
                    : string.Empty;

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {label,-46}  median {Median(ok),7:F1}ms   mean {ok.Average(),7:F1}ms{missing}"));
            }
        }

        /// <summary>
        /// The same 160px decode, but from an InMemoryRandomAccessStream holding a copy of the
        /// file's bytes. Isolates the stream from the decode: if this scales across cores while
        /// the FileRandomAccessStream version does not, the serialisation is in the file stream,
        /// not in the codec.
        /// </summary>
        private static async Task<double> MemoryDecodeAsync(string file)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);

                var t = Stopwatch.GetTimestamp();
                using var ms = new InMemoryRandomAccessStream();
                await ms.WriteAsync(bytes.AsBuffer()).AsTask().ConfigureAwait(false);
                ms.Seek(0);

                var ok = await ScaledAsync(ms, 160, BitmapInterpolationMode.Fant).ConfigureAwait(false);
                return ok ? Stopwatch.GetElapsedTime(t).TotalMilliseconds : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static bool IsRaw(string file) =>
            file.EndsWith(".cr2", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".arw", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".nef", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns elapsed ms, or -1 when the route is unavailable for this file.
        ///
        /// RAW is routed through RawPreviewDecoder rather than opened with BitmapDecoder directly,
        /// because that is what the app does (PRD 3.2: the embedded JPEG is lifted out of the
        /// container, never debayered) and WIC cannot open a .CR2 at all without the Store Raw
        /// Image Extension. Timing it the other way would measure a path the app never takes.
        /// </summary>
        private static async Task<double> TimeAsync(string file, Func<IRandomAccessStream, Task<bool>> route,
                                                    uint rawEdge = 0)
        {
            try
            {
                if (IsRaw(file))
                {
                    var t0 = Stopwatch.GetTimestamp();
                    using var raw = await RawPreviewDecoder
                        .DecodeDisplayImageAsync(file, CancellationToken.None).ConfigureAwait(false);
                    var ms0 = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    return raw is not null ? ms0 : -1;
                }

                using var stream = await FileRandomAccessStream.OpenAsync(file, FileAccessMode.Read).AsTask().ConfigureAwait(false);
                var t = Stopwatch.GetTimestamp();
                var ok = await route(stream).ConfigureAwait(false);
                var ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                return ok ? ms : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static async Task<bool> ScaledAsync(IRandomAccessStream stream, uint edge, BitmapInterpolationMode mode)
        {
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);

            var scale = Math.Min(1.0, edge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = mode,
            };

            using var bmp = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage)
                .AsTask().ConfigureAwait(false);

            return bmp is not null;
        }

        private static async Task<bool> PreviewAsync(IRandomAccessStream stream)
        {
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
            using var bmp = await decoder.GetPreviewAsync().AsTask().ConfigureAwait(false);
            return bmp is not null;
        }

        private static async Task<bool> EmbeddedThumbnailAsync(IRandomAccessStream stream)
        {
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
            using var bmp = await decoder.GetThumbnailAsync().AsTask().ConfigureAwait(false);
            return bmp is not null;
        }

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }
    }
}

