using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Fastcull.Models;
using Fastcull.Services;
using Fastcull.ViewModels;
using Windows.Graphics.Imaging;

namespace Fastcull.Benchmarks
{
    /// <summary>
    /// Walks a cursor through the whole corpus the way the app does after PRD 3.3, so peak memory
    /// and cache-hit latency can be measured against the architecture that actually ships rather
    /// than against the eager fan-out it replaced.
    ///
    /// What this reproduces, and why each piece is here:
    ///
    ///   Stage pinning       MainViewModel.RecomputeSlots stages a variable 3-9 photos and pins
    ///                       every one. Modelled at nine - the ceiling - because that is the
    ///                       memory worst case, and because at nine the stage spans +/-4 and is
    ///                       WIDER than the window's own -2 lookbehind.
    ///   Prefetch window     PrefetchCoordinator, verbatim. Same class the app calls.
    ///   Worker pool         DecodeGate, verbatim. Same semaphore the app decodes through.
    ///   Eviction            PrefetchCoordinator's LRU at the 3 GB ceiling, verbatim.
    ///   Thumbnail axis      The bottom filmstrip decodes on container realization, independent of
    ///                       the window, and never releases a thumbnail once decoded. Modelled as
    ///                       a band of realized containers following the cursor, retained for the
    ///                       whole run - because that is what the app does, and 2,000 retained
    ///                       thumbnails are a real (if small) part of the number.
    ///   Zoom                One zoom-tier decode held at a time, released on exit. Layered into
    ///                       the middle of the walk rather than measured alone, because the
    ///                       question is what zoom costs ON TOP of a session already running.
    ///
    /// One modelling difference from the app, stated rather than hidden: the app holds a
    /// SoftwareBitmapSource built from each decode, and this holds the SoftwareBitmap itself. The
    /// committed baseline made the same choice, so the two runs stay directly comparable, but a
    /// SoftwareBitmapSource additionally copies pixels into a composition surface that may live in
    /// GPU memory and is not the app's to sample. Read every peak here as system-RAM only.
    /// </summary>
    internal sealed class WindowedCullSimulation
    {
        /// <summary>The stage ceiling from FilmstripWindow.MaxSlots - the memory worst case.</summary>
        private const int StageSlots = FilmstripWindow.MaxSlots;

        /// <summary>
        /// Bottom-filmstrip containers realized at once. The strip is 150px per slot, so a wide
        /// window realizes roughly this many; ItemsRepeater recycles the rest.
        /// </summary>
        private const int RealizedThumbnails = 20;

        /// <summary>
        /// A fullscreen zoom as this machine actually requests one. Taken from the live diagnostic
        /// that measured the zoom tier decoding at 3440 x 2293 - about 31 MB of BGRA8 - not from
        /// the windowed-stage figure, which is roughly a third of that and would understate the
        /// row this exists to measure.
        /// </summary>
        private const uint ZoomLongEdge = 3440;

        private readonly IReadOnlyList<ScannedPhoto> _photos;
        private readonly List<HarnessItem> _items;
        private readonly PrefetchCoordinator _coordinator = new();
        private readonly List<HarnessItem> _pinned = new();

        public WindowedCullSimulation(IReadOnlyList<ScannedPhoto> photos)
        {
            _photos = photos;
            _items = new List<HarnessItem>(photos.Count);
            for (var i = 0; i < photos.Count; i++)
                _items.Add(new HarnessItem(photos[i], i));
        }

        public sealed record Outcome
        {
            public required long PeakWorkingSetBytes { get; init; }
            public required long PeakManagedBytes { get; init; }
            public required long BaselineWorkingSetBytes { get; init; }
            public required long PeakTrackedResidentBytes { get; init; }
            public required int PeakResidentItems { get; init; }
            public required int TotalEvictions { get; init; }
            public required TimeSpan Elapsed { get; init; }
            public required int Steps { get; init; }

            /// <summary>Per-step navigation cost, sorted, for the cache-hit budget.</summary>
            public required List<double> HitLatenciesMs { get; init; }
            public required int CacheHits { get; init; }
            public required int CacheMisses { get; init; }

            /// <summary>
            /// Navigation cost with nothing decoding and the heap settled, sorted. Separates the
            /// decision itself from the GC pauses the concurrent decode pipeline inflicts on it.
            /// </summary>
            public required List<double> QuiescedLatenciesMs { get; init; }

            public required long ZoomBytes { get; init; }
            public required long PeakDuringZoomBytes { get; init; }
            public required int ZoomAtIndex { get; init; }
            public required double ZoomDecodeMs { get; init; }
            public required uint ZoomLongEdge { get; init; }
            public required string? Abort { get; init; }
        }

        /// <summary>
        /// One pass of the cursor from the first photo to the last, one step at a time, with the
        /// prefetch settling at each step.
        ///
        /// Settling at every step is deliberate and is the pessimistic choice: it means every
        /// photo genuinely decodes, so nothing is skipped by a cancellation the way it would be if
        /// the user out-ran the decoder. A run where the cursor moved faster than the pool could
        /// keep up would show LESS memory, not more, and would be measuring the wrong thing.
        /// </summary>
        public async Task<Outcome> RunAsync()
        {
            // Start from a clean heap - this runs after the eager fan-out measurements, and their
            // garbage is not this measurement's to report.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var process = Process.GetCurrentProcess();
            process.Refresh();
            var baseline = process.WorkingSet64;

            long peakWorkingSet = baseline;
            long peakManaged = 0;

            using var samplerCts = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!samplerCts.Token.IsCancellationRequested)
                {
                    process.Refresh();
                    peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                    peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(forceFullCollection: false));
                    try { await Task.Delay(25, samplerCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            });

            const long AbortWorkingSetBytes = 16L * 1024 * 1024 * 1024;
            var abortAfter = TimeSpan.FromMinutes(12);
            string? abort = null;

            var hitLatencies = new List<double>();
            var hits = 0;
            var misses = 0;
            var totalEvictions = 0;
            long peakTracked = 0;
            var peakResidentItems = 0;

            var zoomAt = _items.Count / 2;
            long zoomBytes = 0;
            long peakDuringZoom = 0;
            double zoomDecodeMs = double.NaN;

            var sw = Stopwatch.StartNew();
            var steps = 0;

            for (var index = 0; index < _items.Count; index++)
            {
                // The cache-hit question, asked before the pass that would satisfy it: was this
                // photo already decoded when the cursor arrived? Only a yes is a cache hit, and
                // recording it separately is what stops this row quietly measuring a miss.
                var alreadyResident = _items[index].Display is not null;

                var stepSw = Stopwatch.StartNew();
                Navigate(index);
                stepSw.Stop();

                if (index > 0)
                {
                    if (alreadyResident)
                    {
                        hits++;
                        hitLatencies.Add(stepSw.Elapsed.TotalMilliseconds);
                    }
                    else
                    {
                        misses++;
                    }
                }

                // Off the critical path: the prefetch working ahead, which the user never waits on.
                await SettleAsync().ConfigureAwait(false);

                totalEvictions += _coordinator.LastEvictionCount;
                peakTracked = Math.Max(peakTracked, TrackedResidentBytes(out var residentItems));
                peakResidentItems = Math.Max(peakResidentItems, residentItems);
                steps++;

                if (index == zoomAt)
                {
                    var zoomSw = Stopwatch.StartNew();
                    zoomBytes = await _items[index].LoadZoomAsync(ZoomLongEdge).ConfigureAwait(false);
                    zoomSw.Stop();
                    zoomDecodeMs = zoomSw.Elapsed.TotalMilliseconds;

                    // Sample while it is actually held. The point of doing this mid-walk is that
                    // the zoom sits on top of a steady-state cache, not on an empty one.
                    process.Refresh();
                    peakDuringZoom = process.WorkingSet64;
                    peakWorkingSet = Math.Max(peakWorkingSet, peakDuringZoom);
                    peakTracked = Math.Max(peakTracked, TrackedResidentBytes(out _));

                    // Exit zoom, exactly as the app does on Space/Escape or a photo change.
                    _items[index].ReleaseZoom();
                }

                process.Refresh();
                if (process.WorkingSet64 > AbortWorkingSetBytes)
                {
                    abort = string.Create(CultureInfo.InvariantCulture,
                        $"working set passed {AbortWorkingSetBytes / 1024.0 / 1024 / 1024:F0} GB at step {steps:N0} of {_items.Count:N0}");
                    break;
                }
                if (sw.Elapsed > abortAfter)
                {
                    abort = string.Create(CultureInfo.InvariantCulture,
                        $"exceeded {abortAfter.TotalMinutes:F0} min at step {steps:N0} of {_items.Count:N0}");
                    break;
                }
            }

            sw.Stop();
            samplerCts.Cancel();
            try { await sampler.ConfigureAwait(false); } catch { /* sampler is best-effort */ }

            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);

            hitLatencies.Sort();

            var quiesced = MeasureQuiescedNavigation();

            var outcome = new Outcome
            {
                PeakWorkingSetBytes = peakWorkingSet,
                PeakManagedBytes = peakManaged,
                BaselineWorkingSetBytes = baseline,
                PeakTrackedResidentBytes = peakTracked,
                PeakResidentItems = peakResidentItems,
                TotalEvictions = totalEvictions,
                Elapsed = sw.Elapsed,
                Steps = steps,
                HitLatenciesMs = hitLatencies,
                QuiescedLatenciesMs = quiesced,
                CacheHits = hits,
                CacheMisses = misses,
                ZoomBytes = zoomBytes,
                PeakDuringZoomBytes = peakDuringZoom,
                ZoomAtIndex = zoomAt,
                ZoomDecodeMs = zoomDecodeMs,
                ZoomLongEdge = ZoomLongEdge,
                Abort = abort,
            };

            Teardown();
            return outcome;
        }

        /// <summary>
        /// The same navigation work, with nothing decoding and the heap collected first.
        ///
        /// This exists because the walk's own number cannot distinguish two very different
        /// stories: a decision that is genuinely expensive, versus a cheap decision whose thread
        /// keeps getting suspended by GC pauses from the decode pipeline running alongside it.
        /// Both are real costs the app pays, but only the first would be a reason to change the
        /// coordinator, so the results file reports them separately rather than guessing.
        ///
        /// The cursor oscillates between two adjacent indices near the end of the sequence. Both
        /// sit inside each other's window and stage, so every item stays resident and no decode
        /// starts - what is left is purely the decision: the stage window, the pin pass, and the
        /// coordinator's O(n) sweep over all 2,000 items.
        /// </summary>
        private List<double> MeasureQuiescedNavigation()
        {
            const int Samples = 500;

            if (_items.Count < 12) return new List<double>();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var a = _items.Count - 6;
            var b = _items.Count - 5;

            // Settle both positions first, untimed, so the first sample is not paying for a
            // one-off transition into the oscillation.
            Navigate(a);
            Navigate(b);

            var samples = new List<double>(Samples);
            for (var i = 0; i < Samples; i++)
            {
                var target = i % 2 == 0 ? a : b;
                var sw = Stopwatch.StartNew();
                Navigate(target);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }

            samples.Sort();
            return samples;
        }

        /// <summary>
        /// One navigation step: stage window, pinning, prefetch and eviction - the whole of what
        /// MainViewModel.SetActiveIndex triggers, minus the XAML it cannot construct headlessly.
        /// </summary>
        private void Navigate(int activeIndex)
        {
            var window = FilmstripWindow.Compute(activeIndex, _items.Count, StageSlots);

            // PinStageItems: unpin whatever left the stage, pin and load whatever is on it.
            foreach (var item in _pinned) item.IsPinned = false;
            _pinned.Clear();

            for (var slot = 0; slot < window.SlotCount; slot++)
            {
                var item = _items[window.WindowStart + slot];
                item.IsPinned = true;
                item.BeginLoad();
                _pinned.Add(item);
            }

            _coordinator.OnCursorMoved(activeIndex, _items);

            // The bottom filmstrip's own axis, independent of the prefetch window.
            var stripStart = Math.Max(0, activeIndex - RealizedThumbnails / 2);
            var stripEnd = Math.Min(_items.Count - 1, stripStart + RealizedThumbnails - 1);

            for (var i = 0; i < _items.Count; i++)
            {
                if (i >= stripStart && i <= stripEnd) _items[i].BeginThumbnailLoad();
                else _items[i].CancelThumbnailLoad();
            }
        }

        private async Task SettleAsync()
        {
            // Two passes: a load started by the first pass's await can start another.
            for (var pass = 0; pass < 2; pass++)
            {
                List<Task>? pending = null;
                foreach (var item in _items)
                {
                    var task = item.Pending;
                    if (task is not null) (pending ??= new List<Task>()).Add(task);
                }

                if (pending is null) return;
                try { await Task.WhenAll(pending).ConfigureAwait(false); }
                catch { /* a cancelled or failed decode is not this measurement's concern */ }
            }
        }

        private long TrackedResidentBytes(out int residentItems)
        {
            long total = 0;
            residentItems = 0;
            foreach (var item in _items)
            {
                if (!item.IsResident) continue;
                residentItems++;
                total += item.ResidentBytes;
            }
            return total;
        }

        private void Teardown()
        {
            foreach (var item in _items) item.DisposeAll();
            _pinned.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // -------------------------------------------------------------------------------------
        // The item
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// FilmstripItemViewModel's caching behaviour without its WinUI half. The two load axes,
        /// the pin rule, the byte estimate and - critically - the "evict never disposes" rule are
        /// all reproduced, because those are what the measurement is about.
        ///
        /// Not reproduced: the SoftwareBitmapSource marshal onto the UI thread. A headless harness
        /// has no UI thread, and the marshal was separately measured at roughly 30 ms.
        /// </summary>
        private sealed class HarnessItem : ICacheableItem
        {
            private readonly ScannedPhoto _photo;
            private CancellationTokenSource? _displayCts;
            private CancellationTokenSource? _thumbnailCts;
            private bool _displayStarted;
            private bool _thumbnailStarted;
            private long _zoomBytes;

            public HarnessItem(ScannedPhoto photo, int index)
            {
                _photo = photo;
                Index = index;
            }

            public int Index { get; }
            public bool IsPinned { get; set; }

            public SoftwareBitmap? Display { get; private set; }
            public SoftwareBitmap? Thumbnail { get; private set; }
            public SoftwareBitmap? Zoom { get; private set; }

            /// <summary>Whichever load is in flight, so the simulation can wait for a settled state.</summary>
            public Task? Pending { get; private set; }

            public bool IsResident => Display is not null || Thumbnail is not null || Zoom is not null;

            /// <summary>
            /// The same estimate FilmstripItemViewModel.ResidentBytes makes: tiers derived from
            /// their known long edges at BGRA8, zoom measured because it dwarfs both.
            /// </summary>
            public long ResidentBytes
            {
                get
                {
                    long bytes = 0;
                    if (Thumbnail is not null) bytes += Estimate(ThumbnailService.ThumbnailLongEdge);
                    if (Display is not null) bytes += Estimate(ThumbnailService.DisplayLongEdge);
                    return bytes + _zoomBytes;
                }
            }

            /// <summary>What Evict actually gives back - the thumbnail survives it.</summary>
            public long EvictableBytes
                => IsPinned ? 0 : _zoomBytes + (Display is not null ? Estimate(ThumbnailService.DisplayLongEdge) : 0);

            private long Estimate(uint longEdge)
            {
                var shortEdge = longEdge / 1.5;   // the ViewModel's own 3:2 default
                return (long)(longEdge * shortEdge * 4);
            }

            public void BeginLoad()
            {
                if (_displayStarted) return;
                _displayStarted = true;
                _displayCts = new CancellationTokenSource();
                Pending = LoadAsync(displayTier: true, _displayCts.Token);
            }

            public void CancelLoad()
            {
                if (_displayCts is null) return;
                _displayCts.Cancel();
                _displayCts.Dispose();
                _displayCts = null;
                if (Display is null) _displayStarted = false;
            }

            public void BeginThumbnailLoad()
            {
                if (_thumbnailStarted) return;
                _thumbnailStarted = true;
                _thumbnailCts = new CancellationTokenSource();
                Pending = LoadAsync(displayTier: false, _thumbnailCts.Token);
            }

            public void CancelThumbnailLoad()
            {
                if (_thumbnailCts is null) return;
                _thumbnailCts.Cancel();
                _thumbnailCts.Dispose();
                _thumbnailCts = null;
                if (Thumbnail is null) _thumbnailStarted = false;
            }

            /// <summary>
            /// Drops the display tier only, and disposes NOTHING - the same rule the ViewModel
            /// follows, for the same reason (0xC000027B on a bitmap XAML is still copying). This
            /// harness could safely dispose, since it has no compositor, but then it would be
            /// measuring a reclamation the app never gets. Whether dropping the last managed
            /// reference actually returns the memory is precisely the question.
            /// </summary>
            public void Evict()
            {
                if (IsPinned) return;

                CancelLoad();
                ReleaseZoom();

                // The thumbnail is deliberately left alone, exactly as in the app.
                Display = null;
                _displayStarted = false;
            }

            public async Task<long> LoadZoomAsync(uint longEdge)
            {
                var capped = DimensionGuard.ClampLongEdge(longEdge, 1.5);
                var bitmap = _photo.Family == FormatFamily.Raw
                    ? await RawPreviewDecoder.DecodeZoomImageAsync(_photo.FilePath, capped, CancellationToken.None).ConfigureAwait(false)
                      ?? await ThumbnailService.DecodeZoomImageAsync(_photo.FilePath, capped, CancellationToken.None).ConfigureAwait(false)
                    : await ThumbnailService.DecodeZoomImageAsync(_photo.FilePath, capped, CancellationToken.None).ConfigureAwait(false);

                if (bitmap is null) return 0;

                Zoom = bitmap;
                _zoomBytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
                return _zoomBytes;
            }

            public void ReleaseZoom()
            {
                // The app cannot dispose here (SoftwareBitmapSource owns it by then); the harness
                // holds the bitmap directly and nothing else can be copying it, so disposing is
                // both safe and the closer analogue of the composition surface being torn down.
                Zoom?.Dispose();
                Zoom = null;
                _zoomBytes = 0;
            }

            private async Task LoadAsync(bool displayTier, CancellationToken ct)
            {
                try
                {
                    var bitmap = await DecodeGate.RunAsync(
                        () => DecodeTierAsync(_photo, displayTier, ct), ct).ConfigureAwait(false);

                    if (bitmap is null) return;

                    if (ct.IsCancellationRequested)
                    {
                        // Nothing has been handed to XAML yet, so this one is safe to drop.
                        bitmap.Dispose();
                        return;
                    }

                    if (displayTier) Display = bitmap;
                    else Thumbnail = bitmap;
                }
                catch (OperationCanceledException)
                {
                    // Left the window before the decode landed - the point of the token.
                }
                finally
                {
                    Pending = null;
                }
            }

            private static async Task<SoftwareBitmap?> DecodeTierAsync(ScannedPhoto photo, bool displayTier, CancellationToken ct)
            {
                if (photo.Family != FormatFamily.Raw)
                {
                    return displayTier
                        ? await ThumbnailService.DecodeDisplayImageAsync(photo.FilePath, ct).ConfigureAwait(false)
                        : await ThumbnailService.DecodeThumbnailAsync(photo.FilePath, ct).ConfigureAwait(false);
                }

                var bitmap = displayTier
                    ? await RawPreviewDecoder.DecodeDisplayImageAsync(photo.FilePath, ct).ConfigureAwait(false)
                    : await RawPreviewDecoder.DecodeThumbnailAsync(photo.FilePath, ct).ConfigureAwait(false);

                if (bitmap is not null) return bitmap;

                return displayTier
                    ? await ThumbnailService.DecodeDisplayImageAsync(photo.FilePath, ct).ConfigureAwait(false)
                    : await ThumbnailService.DecodeThumbnailAsync(photo.FilePath, ct).ConfigureAwait(false);
            }

            /// <summary>Teardown only - the run is over and nothing can still be reading these.</summary>
            public void DisposeAll()
            {
                CancelLoad();
                CancelThumbnailLoad();
                ReleaseZoom();
                Display?.Dispose();
                Display = null;
                Thumbnail?.Dispose();
                Thumbnail = null;
            }
        }
    }
}
