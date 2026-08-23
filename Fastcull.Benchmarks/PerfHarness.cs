using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fastcull.Input;
using Fastcull.Models;
using Fastcull.Services;
using Fastcull.ViewModels;
using Windows.Graphics.Imaging;
using Windows.System;

namespace Fastcull.Benchmarks
{
    /// <summary>
    /// Asserts the PRD 3.5 performance budgets against the RAW / JPEG / mixed fixture sets, per
    /// PRD 5's architecture (Benchmarks/PerfHarness.cs).
    ///
    /// Two rules this harness holds itself to, because a benchmark that lies is worse than no
    /// benchmark:
    ///
    /// 1. It measures the code path the app actually runs. Where it measures a proxy instead
    ///    (see FirstImage below) it says so in the emitted results file, in that row.
    /// 2. A budget whose feature does not exist yet produces NOT MEASURABLE and a reason, never
    ///    a number obtained by measuring something adjacent and relabelling it.
    /// </summary>
    internal sealed class PerfHarness
    {
        private readonly string _realCorpusRoot;
        private readonly string _syntheticCorpusRoot;
        private readonly HarnessResults _results = new();

        public PerfHarness(string realCorpusRoot, string syntheticCorpusRoot)
        {
            _realCorpusRoot = realCorpusRoot;
            _syntheticCorpusRoot = syntheticCorpusRoot;
        }

        public async Task<HarnessResults> RunAsync()
        {
            RecordEnvironment();

            var realPhotos = await ScanToListAsync(_realCorpusRoot).ConfigureAwait(false);
            var fixtures = FixtureSets.Build(realPhotos);

            foreach (var set in fixtures)
                _results.Notes.Add($"Fixture set `{set.Name}`: {set.Count} real files.");

            await MeasureFullScanAsync().ConfigureAwait(false);
            await MeasureFirstImageAsync(fixtures).ConfigureAwait(false);
            await MeasureDecodeLatencyAsync(fixtures).ConfigureAwait(false);
            MeasureRatingLatency();

            // Two variants, because they answer different questions and only the second is the
            // app's real memory behaviour:
            //   disposed  - what the decode pipeline costs transiently while it churns.
            //   retained  - what the app actually holds. FilmstripItemViewModel keeps Thumbnail
            //               and DisplayImage alive for every item it ever constructed, and
            //               deliberately does not dispose the SoftwareBitmap (its lifetime passes
            //               to SoftwareBitmapSource). Nothing evicts them, because the LRU cache
            //               that would is PRD 3.3, which does not exist. So at 2,000 files the app
            //               holds 4,000 decoded bitmaps at once, and that is the number the PRD
            //               3.5 peak-working-set budget is actually about.
            await MeasureEagerFanOutAsync(retainDecodedBitmaps: false).ConfigureAwait(false);
            await MeasureEagerFanOutAsync(retainDecodedBitmaps: true).ConfigureAwait(false);

            // The architecture that actually ships, as of PRD 3.3. Produces both the new
            // peak-working-set number and the cache-hit row that had no cache to measure before.
            await MeasureWindowedCullAsync().ConfigureAwait(false);

            RecordUnmeasurable();

            return _results;
        }

        // ---------------------------------------------------------------------------------
        // Full scan - PRD 3.5: "Full scan, 2,000 files, NVMe < 8 s (interactive throughout)"
        // ---------------------------------------------------------------------------------

        private async Task MeasureFullScanAsync()
        {
            // Real corpus first: every file distinct, so this is the honest per-file cost.
            var realSw = Stopwatch.StartNew();
            var realPhotos = await ScanToListAsync(_realCorpusRoot).ConfigureAwait(false);
            realSw.Stop();
            var realPerFile = realSw.Elapsed.TotalMilliseconds / Math.Max(1, realPhotos.Count);

            var sw = Stopwatch.StartNew();
            var photos = await ScanToListAsync(_syntheticCorpusRoot).ConfigureAwait(false);
            sw.Stop();
            var syntheticPerFile = sw.Elapsed.TotalMilliseconds / Math.Max(1, photos.Count);

            _results.Budgets.Add(new BudgetResult
            {
                Metric = "Full scan, 2,000 files",
                Budget = "< 8 s",
                MeasuredMs = sw.Elapsed.TotalMilliseconds,
                BudgetMs = 8000,
                Detail = string.Create(CultureInfo.InvariantCulture,
                    $"{photos.Count} files (synthetic, hard-linked). {syntheticPerFile:F2} ms/file."),
            });

            _results.Budgets.Add(new BudgetResult
            {
                Metric = "Full scan, real corpus (control)",
                Budget = "no PRD budget",
                MeasuredMs = realSw.Elapsed.TotalMilliseconds,
                Detail = string.Create(CultureInfo.InvariantCulture,
                    $"{realPhotos.Count} genuinely distinct files. {realPerFile:F2} ms/file - compare against the synthetic {syntheticPerFile:F2} ms/file to judge how much the hard-linked corpus's warm page cache flatters the row above."),
            });
        }

        // ---------------------------------------------------------------------------------
        // First image on screen - PRD 3.5: "< 1.5 s from folder selection"
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Measured as a proxy: folder-open to the FIRST item's thumbnail-tier decode completing.
        /// It stops short of the final XAML SoftwareBitmapSource marshal, which earlier
        /// measurement in this project put at roughly 30 ms - negligible against a 1.5 s budget.
        /// So this is "first decoded pixels in hand", not literally first pixels lit.
        ///
        /// Measured twice, because the app and the PRD disagree about the sequence:
        ///
        ///   as-app-sequences-it - MainViewModel.LoadAsync awaits the ENTIRE scan, then sorts,
        ///     then registers every photo with SessionStore, and only then constructs the first
        ///     FilmstripItemViewModel (which is what starts a decode). So the first image cannot
        ///     appear until the whole folder has been enumerated.
        ///
        ///   if-it-streamed - decode the first photo the scanner yields, mid-scan. This is what
        ///     PRD 1.2 actually requires ("the first image is on screen while the tail of the
        ///     folder is still being enumerated").
        ///
        /// The gap between the two is the cost of that architectural difference, in milliseconds.
        /// </summary>
        private async Task MeasureFirstImageAsync(IReadOnlyList<FixtureSets.FixtureSet> fixtures)
        {
            foreach (var set in fixtures)
            {
                if (set.Count == 0) continue;

                var first = set.Photos[0];
                var sw = Stopwatch.StartNew();
                using (var bitmap = await DecodeAsync(first, displayTier: false, CancellationToken.None).ConfigureAwait(false))
                {
                    sw.Stop();
                    _results.Budgets.Add(new BudgetResult
                    {
                        Metric = $"First image, `{set.Name}` (decode only, warm folder)",
                        Budget = "< 1.5 s",
                        MeasuredMs = sw.Elapsed.TotalMilliseconds,
                        BudgetMs = 1500,
                        Detail = $"{first.FileName}, thumbnail tier. Excludes the scan - see the two rows below.",
                    });
                }
            }

            // Streaming: first photo the scanner yields, decoded immediately.
            var streamSw = Stopwatch.StartNew();
            ScannedPhoto? firstYielded = null;
            var scanner = new DirectoryScanner();
            await foreach (var photo in scanner.ScanAsync(_syntheticCorpusRoot).ConfigureAwait(false))
            {
                firstYielded = photo;
                break;
            }

            double streamedMs = double.NaN;
            if (firstYielded is not null)
            {
                using var bitmap = await DecodeAsync(firstYielded, displayTier: false, CancellationToken.None).ConfigureAwait(false);
                streamSw.Stop();
                streamedMs = streamSw.Elapsed.TotalMilliseconds;

                _results.Budgets.Add(new BudgetResult
                {
                    Metric = "First image, 2,000 files - **if it streamed** (PRD 1.2 behaviour)",
                    Budget = "< 1.5 s",
                    MeasuredMs = streamedMs,
                    BudgetMs = 1500,
                    Detail = "Folder-open to first decoded thumbnail, decoding the first photo the "
                           + "scanner yields without waiting for the scan to finish. This is the "
                           + "behaviour PRD 1.2 specifies. The app does NOT do this today.",
                });
            }

            // As the app actually sequences it: full scan, then sort, then first decode.
            var appSw = Stopwatch.StartNew();
            var all = await ScanToListAsync(_syntheticCorpusRoot).ConfigureAwait(false);
            var sorted = all
                .OrderBy(p => p.SortTime)
                .ThenBy(p => p.CaptureSubsec)
                .ThenBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sorted.Count > 0)
            {
                using var bitmap = await DecodeAsync(sorted[0], displayTier: false, CancellationToken.None).ConfigureAwait(false);
                appSw.Stop();

                _results.Budgets.Add(new BudgetResult
                {
                    Metric = "First image, 2,000 files - **as the app sequences it today**",
                    Budget = "< 1.5 s",
                    MeasuredMs = appSw.Elapsed.TotalMilliseconds,
                    BudgetMs = 1500,
                    Detail = "MainViewModel.LoadAsync awaits the entire scan and sort before "
                           + "constructing the first FilmstripItemViewModel, which is what starts a "
                           + "decode. Excludes SessionStore registration, so the real app is at "
                           + "least this slow, not less. "
                           + string.Create(CultureInfo.InvariantCulture,
                               $"Difference vs. the streaming row above: {appSw.Elapsed.TotalMilliseconds - streamedMs:+0;-0;0} ms.")
                           + " A near-zero or negative difference does NOT vindicate the architecture -"
                           + " it means the scan is currently too fast (warm cache, 2,000 hard links)"
                           + " for the difference to show. The gap grows with scan cost, which is"
                           + " exactly what a cold 2,000-file NVMe folder has and this run does not.",
                });
            }
        }

        // ---------------------------------------------------------------------------------
        // Next/previous, cache miss - PRD 3.5: "< 250 ms to display tier"
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Every navigation is a cache miss today, because PRD 3.3's cache was never built, so
        /// this measures exactly what the PRD row describes: a cold decode to display tier.
        /// Sequential and one at a time - that is what one arrow keypress costs.
        /// </summary>
        private async Task MeasureDecodeLatencyAsync(IReadOnlyList<FixtureSets.FixtureSet> fixtures)
        {
            const int SampleCap = 24;

            foreach (var set in fixtures)
            {
                if (set.Count == 0) continue;

                foreach (var displayTier in new[] { false, true })
                {
                    var samples = new List<double>();
                    foreach (var photo in set.Photos.Take(SampleCap))
                    {
                        var sw = Stopwatch.StartNew();
                        using var bitmap = await DecodeAsync(photo, displayTier, CancellationToken.None).ConfigureAwait(false);
                        sw.Stop();
                        samples.Add(sw.Elapsed.TotalMilliseconds);
                    }

                    if (samples.Count == 0) continue;

                    var tier = displayTier ? "display" : "thumbnail";
                    samples.Sort();

                    _results.Budgets.Add(new BudgetResult
                    {
                        Metric = $"Next/prev cache miss, `{set.Name}`, {tier} tier (median)",
                        Budget = displayTier ? "< 250 ms" : "< 250 ms",
                        MeasuredMs = Percentile(samples, 0.50),
                        BudgetMs = 250,
                        Detail = string.Create(CultureInfo.InvariantCulture,
                            $"n={samples.Count}, p95 {Percentile(samples, 0.95):F1} ms, max {samples[^1]:F1} ms."),
                    });
                }
            }
        }

        // ---------------------------------------------------------------------------------
        // Rating keypress - PRD 3.5: "Rating keypress to border change < 16 ms"
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Measures the app's own latency from a resolved keypress to the new cull state:
        /// InputRouter.Resolve plus the CullState ladder transition, which is the whole
        /// decision path.
        ///
        /// What this deliberately does NOT include, and why: the PropertyChanged raise and the
        /// border repaint live on FilmstripItemViewModel, which is in the WinUI project, not in
        /// Fastcull.Core - it depends on DispatcherQueue, Brush and Visibility, so a WinUI-free
        /// harness cannot construct one. The compositor repaint that follows is GPU-scheduled and
        /// outside the app's control to benchmark meaningfully in any case. Treat this row as the
        /// app's decision latency, not as end-to-end pixels.
        /// </summary>
        private void MeasureRatingLatency()
        {
            const int Iterations = 100_000;

            // Warm the JIT so the first call's compilation cost is not attributed to the ladder.
            for (var i = 0; i < 1000; i++)
                _ = InputRouter.Resolve(VirtualKey.Up, false).Command;

            var state = CullState.Default;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < Iterations; i++)
            {
                var resolved = InputRouter.Resolve(i % 2 == 0 ? VirtualKey.Up : VirtualKey.Down, isExtendedKey: true);
                state = resolved.Command switch
                {
                    AppCommand.LadderUp => state.Up(),
                    AppCommand.LadderDown => state.Down(),
                    _ => state,
                };
            }
            sw.Stop();

            var perKeypressMs = sw.Elapsed.TotalMilliseconds / Iterations;

            _results.Budgets.Add(new BudgetResult
            {
                Metric = "Rating keypress to new cull state (decision path only)",
                Budget = "< 16 ms",
                MeasuredMs = perKeypressMs,
                BudgetMs = 16,
                Detail = string.Create(CultureInfo.InvariantCulture, $"n={Iterations:N0}, final state {state.Flag}/{state.Stars}.")
                    + " InputRouter.Resolve + CullState transition only - the PropertyChanged raise"
                    + " lives in the WinUI project and the compositor repaint is not the app's to time.",
            });
        }

        // ---------------------------------------------------------------------------------
        // Peak working set, and the B.5 unbounded-fan-out question
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// PRD 3.5: "Peak working set, 2,000 files < 4 GB system RAM".
        ///
        /// Reproduces what FilmstripItemViewModel's constructor does today: for EVERY scanned
        /// item it starts both a thumbnail decode and a display-tier decode, fire-and-forget, with
        /// nothing bounding how many run at once. MainViewModel.LoadAsync constructs those in a
        /// tight loop over every scanned file, so at 2,000 files that is 4,000 decode jobs
        /// launched with no window and no throttle.
        ///
        /// This matters more than it looks: RawPreviewDecoder allocates a 16 MB scan-window
        /// buffer synchronously, before its first await, so each launched RAW job takes its 16 MB
        /// up front whether or not a worker is free to run it.
        ///
        /// The harness cannot construct FilmstripItemViewModel itself (WinUI types), so it
        /// replicates the launch pattern against the same Fastcull.Core decoders the ViewModel
        /// calls. Everything below the ViewModel is therefore the real code path; only the XAML
        /// marshal at the very top is absent.
        /// </summary>
        private async Task MeasureEagerFanOutAsync(bool retainDecodedBitmaps)
        {
            var photos = await ScanToListAsync(_syntheticCorpusRoot).ConfigureAwait(false);

            // When retaining, every decoded bitmap is held for the whole run - see the call site
            // for why that is the app's real behaviour rather than a pessimistic variant.
            var retained = retainDecodedBitmaps ? new List<SoftwareBitmap>(photos.Count * 2) : null;

            // Abort rails. The point is to find out what happens, not to wedge the machine: the
            // 4 GB budget is already decided long before either of these trips.
            const long AbortWorkingSetBytes = 16L * 1024 * 1024 * 1024;
            var abortAfter = TimeSpan.FromMinutes(5);

            var process = Process.GetCurrentProcess();
            long peakWorkingSet = 0;
            long peakManaged = 0;
            var aborted = false;
            var abortReason = string.Empty;

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

            var baselineWorkingSet = process.WorkingSet64;
            var sw = Stopwatch.StartNew();
            var jobs = new List<Task>(photos.Count * 2);
            using var jobCts = new CancellationTokenSource();

            var launched = 0;
            foreach (var photo in photos)
            {
                // Exactly the shape of FilmstripItemViewModel's constructor: two unawaited decodes.
                jobs.Add(DecodeJobAsync(photo, displayTier: false, retained, jobCts.Token));
                jobs.Add(DecodeJobAsync(photo, displayTier: true, retained, jobCts.Token));
                launched += 2;

                process.Refresh();
                if (process.WorkingSet64 > AbortWorkingSetBytes)
                {
                    aborted = true;
                    abortReason = string.Create(CultureInfo.InvariantCulture,
                        $"working set passed {AbortWorkingSetBytes / 1024.0 / 1024 / 1024:F0} GB after "
                        + $"{launched:N0} of {photos.Count * 2:N0} jobs were launched");
                    break;
                }
                if (sw.Elapsed > abortAfter)
                {
                    aborted = true;
                    abortReason = string.Create(CultureInfo.InvariantCulture,
                        $"exceeded {abortAfter.TotalMinutes:F0} min after {launched:N0} of {photos.Count * 2:N0} jobs were launched");
                    break;
                }
            }

            var launchElapsed = sw.Elapsed;

            if (aborted) jobCts.Cancel();

            try
            {
                await Task.WhenAll(jobs).WaitAsync(abortAfter).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                aborted = true;
                abortReason = string.IsNullOrEmpty(abortReason)
                    ? $"decode jobs did not all complete within {abortAfter.TotalMinutes:F0} min"
                    : abortReason;
                jobCts.Cancel();
            }
            catch (Exception ex)
            {
                aborted = true;
                abortReason = $"a decode job faulted: {ex.GetType().Name}: {ex.Message}";
            }

            sw.Stop();
            samplerCts.Cancel();
            try { await sampler.ConfigureAwait(false); } catch { /* sampler is best-effort */ }

            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);

            var retainedCount = retained?.Count ?? 0;

            var detail = string.Create(CultureInfo.InvariantCulture,
                $"{launched:N0} decode jobs launched over {photos.Count:N0} files, unthrottled, mirroring FilmstripItemViewModel's constructor. Launch loop {launchElapsed.TotalSeconds:F1} s, all jobs drained at {sw.Elapsed.TotalSeconds:F1} s. Baseline working set {baselineWorkingSet / 1024.0 / 1024:F0} MB. Peak managed heap {peakManaged / 1024.0 / 1024:F0} MB.")
                + (retainDecodedBitmaps
                    ? string.Create(CultureInfo.InvariantCulture,
                        $" {retainedCount:N0} decoded bitmaps held live at peak - the app never releases them.")
                    : " Each bitmap disposed as soon as it decoded, which the app does NOT do - see the retained row.")
                + (aborted ? $" **ABORTED**: {abortReason}." : string.Empty);

            _results.Memory.Add(new MemoryResult
            {
                Metric = retainDecodedBitmaps
                    ? "Peak working set, 2,000 files — **eager fan-out, the architecture PRD 3.3 replaced**"
                    : "Peak working set, 2,000 files (transient decode only, bitmaps disposed)",
                Budget = "< 4 GB",
                MeasuredMb = peakWorkingSet / 1024.0 / 1024,
                BudgetMb = 4096,

                // Both variants measure the pre-PRD-3.3 fan-out, which no longer ships. They are
                // the control the shipping row is compared against, not a gate - see
                // MemoryResult.Informational.
                Informational = true,
                Detail = detail
                    + (retainDecodedBitmaps
                        ? " **Reference row, does not gate the build**: this is the architecture PRD 3.3 replaced,"
                          + " kept in the same run as the shipping path so the before/after comparison is like-for-like"
                          + " on one machine rather than across two runs on different days."
                        : " **Reference row, does not gate the build**: measures the old fan-out's transient cost."),
            });

            _results.Budgets.Add(new BudgetResult
            {
                Metric = retainDecodedBitmaps
                    ? "Drain all 2,000 files' decodes (retaining bitmaps)"
                    : "Drain all 2,000 files' decodes (disposing bitmaps)",
                Budget = "no PRD budget",
                MeasuredMs = sw.Elapsed.TotalMilliseconds,
                Detail = aborted
                    ? $"ABORTED - {abortReason}."
                    : "Every item's thumbnail and display decode, launched unthrottled and drained.",
            });

            if (retained is not null)
            {
                foreach (var bitmap in retained) bitmap.Dispose();
                retained.Clear();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        // ---------------------------------------------------------------------------------
        // The shipping architecture: sliding window, bounded pool, LRU eviction (PRD 3.3)
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Walks the cursor through all 2,000 files the way the app does now, and reads three
        /// PRD 3.5 rows off that one pass:
        ///
        ///   - Peak working set, with eviction active. The number the 5.25 GB failure was about.
        ///   - Next/previous cache hit, which had no cache to measure until this task.
        ///   - Peak working set with a zoom held mid-session, since the zoom tier is ~31 MB and
        ///     lands on top of a steady-state cache rather than replacing it.
        ///
        /// See <see cref="WindowedCullSimulation"/> for exactly which app behaviours are
        /// reproduced and which one modelling difference is knowingly accepted.
        /// </summary>
        private async Task MeasureWindowedCullAsync()
        {
            var photos = await ScanToListAsync(_syntheticCorpusRoot).ConfigureAwait(false);
            var sorted = photos
                .OrderBy(p => p.SortTime)
                .ThenBy(p => p.CaptureSubsec)
                .ThenBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var simulation = new WindowedCullSimulation(sorted);
            var outcome = await simulation.RunAsync().ConfigureAwait(false);

            var peakMb = outcome.PeakWorkingSetBytes / 1024.0 / 1024;
            var trackedMb = outcome.PeakTrackedResidentBytes / 1024.0 / 1024;
            var zoomMb = outcome.ZoomBytes / 1024.0 / 1024;

            _results.Memory.Add(new MemoryResult
            {
                Metric = "Peak working set, 2,000 files — **PRD 3.3 window + LRU eviction (the shipping path)**",
                Budget = "< 4 GB",
                MeasuredMb = peakMb,
                BudgetMb = 4096,
                Detail = string.Create(CultureInfo.InvariantCulture,
                    $"Cursor stepped through all {outcome.Steps:N0} photos, settling the prefetch at every step so every photo genuinely decodes. "
                    + $"Nine stage slots (FilmstripWindow.MaxSlots - the worst case), +5/-2 window, DecodeGate at {DecodeGate.MaxConcurrency} workers, {PrefetchCoordinator.DefaultCeilingBytes / 1024 / 1024 / 1024} GB LRU ceiling. "
                    + $"{outcome.TotalEvictions:N0} evictions over the run. Peak {outcome.PeakResidentItems:N0} items resident at once, {trackedMb:F0} MB of tracked decoded pixels. "
                    + $"Baseline working set {outcome.BaselineWorkingSetBytes / 1024.0 / 1024:F0} MB. Peak managed heap {outcome.PeakManagedBytes / 1024.0 / 1024:F0} MB. "
                    + $"Walk took {outcome.Elapsed.TotalSeconds:F1} s.")
                    + (outcome.Abort is null ? string.Empty : $" **ABORTED**: {outcome.Abort}."),
            });

            _results.Memory.Add(new MemoryResult
            {
                Metric = "Peak working set with a zoom held mid-session",
                Budget = "< 4 GB",
                MeasuredMb = outcome.PeakDuringZoomBytes / 1024.0 / 1024,
                BudgetMb = 4096,
                Detail = string.Create(CultureInfo.InvariantCulture,
                    $"Sampled at photo {outcome.ZoomAtIndex:N0} of {outcome.Steps:N0}, with the cache already at steady state - not on an empty heap. "
                    + $"Zoom tier decoded at a {outcome.ZoomLongEdge}px long edge in {outcome.ZoomDecodeMs:F0} ms, holding {zoomMb:F0} MB of BGRA8. ")
                    + "Released immediately after, as the app does on Space/Escape or a photo change. Only one zoom is ever alive, so this does not accumulate.",
            });

            if (outcome.HitLatenciesMs.Count == 0)
            {
                _results.Budgets.Add(BudgetResult.NotBuilt(
                    "Next/previous, cache hit",
                    "< 16 ms",
                    "The cache exists now, but this run produced no cache hit to time - every step found the "
                    + "incoming photo undecoded, which would mean the prefetch window is not working. Treat "
                    + "this as a harness or prefetch failure, not as a missing feature."));
            }
            else
            {
                var hitRate = outcome.CacheHits * 100.0 / Math.Max(1, outcome.CacheHits + outcome.CacheMisses);

                _results.Budgets.Add(new BudgetResult
                {
                    Metric = "Next/previous, cache hit",
                    Budget = "< 16 ms",
                    MeasuredMs = Percentile(outcome.HitLatenciesMs, 0.50),
                    BudgetMs = 16,
                    Detail = string.Create(CultureInfo.InvariantCulture,
                        $"n={outcome.HitLatenciesMs.Count:N0} hits, {outcome.CacheMisses:N0} misses ({hitRate:F1}% hit rate). "
                        + $"p95 {Percentile(outcome.HitLatenciesMs, 0.95):F1} ms, max {outcome.HitLatenciesMs[^1]:F1} ms.")
                        + " A hit is counted only where the incoming photo's display-tier bitmap was ALREADY decoded when the"
                        + " cursor arrived - a step that had to decode is recorded as a miss, so this row cannot quietly"
                        + " report a decode as a cache hit. **This is measured while the decode pipeline is saturated**"
                        + " (the walk settles the prefetch at every step), so it includes GC pauses the decode work inflicts"
                        + " on the navigating thread - see the quiesced row below, which isolates the decision itself."
                        + " Excludes the SoftwareBitmapSource marshal (~30 ms, measured earlier in this project).",
                });

                if (outcome.QuiescedLatenciesMs.Count > 0)
                {
                    _results.Budgets.Add(new BudgetResult
                    {
                        Metric = "Next/previous, cache hit — **decision only, heap quiesced**",
                        Budget = "< 16 ms",
                        MeasuredMs = Percentile(outcome.QuiescedLatenciesMs, 0.50),
                        BudgetMs = 16,
                        Detail = string.Create(CultureInfo.InvariantCulture,
                            $"n={outcome.QuiescedLatenciesMs.Count:N0}, p95 {Percentile(outcome.QuiescedLatenciesMs, 0.95) * 1000:F0} µs, "
                            + $"max {outcome.QuiescedLatenciesMs[^1] * 1000:F0} µs.")
                            + " Same navigation work - stage window, pin pass, and the coordinator's O(n) sweep over all"
                            + " 2,000 items - with nothing decoding and the heap collected first. The gap between this and"
                            + " the row above is not the cache: it is what GC pressure from the decode pipeline costs the"
                            + " navigating thread while it is running flat out.",
                    });
                }
            }
        }

        // ---------------------------------------------------------------------------------
        // Rows that cannot be honestly measured yet
        // ---------------------------------------------------------------------------------

        private void RecordUnmeasurable()
        {
            _results.Budgets.Add(BudgetResult.NotBuilt(
                "Enter zoom, Tier A cached",
                "< 50 ms",
                "Zoom mode does not exist (PRD 1.7, scheduled v0.2). Nothing to enter, nothing to time."));

            _results.Budgets.Add(BudgetResult.NotBuilt(
                "Enter zoom, Tier B",
                "< 400 ms CPU / < 100 ms GPU",
                "Zoom mode does not exist (v0.2), and there is additionally no debayer implementation at all "
                + "for the Tier B path to use - see PRD 3.4's open gap."));

            _results.Budgets.Add(BudgetResult.NotBuilt(
                "Pan at 1:1",
                "sustained 60 fps",
                "Zoom mode does not exist (v0.2). A frame-rate measurement also needs a compositor and a real "
                + "window, which a headless harness does not have."));
        }

        // ---------------------------------------------------------------------------------
        // Shared plumbing
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The same decoder selection FilmstripItemViewModel.DecodeTierAsync makes: RAW through
        /// RawPreviewDecoder with a WIC fallback, everything else straight through WIC.
        /// </summary>
        private static async Task<SoftwareBitmap?> DecodeAsync(ScannedPhoto photo, bool displayTier, CancellationToken ct)
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

        /// <summary>
        /// One decode job. When <paramref name="retained"/> is non-null the decoded bitmap is kept
        /// alive for the rest of the run instead of being disposed, which is what the app does.
        /// </summary>
        private static async Task DecodeJobAsync(
            ScannedPhoto photo, bool displayTier, List<SoftwareBitmap>? retained, CancellationToken ct)
        {
            try
            {
                var bitmap = await DecodeAsync(photo, displayTier, ct).ConfigureAwait(false);
                if (bitmap is null) return;

                if (retained is null)
                {
                    bitmap.Dispose();
                    return;
                }

                lock (retained) retained.Add(bitmap);
            }
            catch (OperationCanceledException)
            {
                // Aborted by the rails above - expected, not a failure.
            }
        }

        private static async Task<List<ScannedPhoto>> ScanToListAsync(string root)
        {
            var scanner = new DirectoryScanner();
            var list = new List<ScannedPhoto>();
            await foreach (var photo in scanner.ScanAsync(root).ConfigureAwait(false))
                list.Add(photo);
            return list;
        }

        private static double Percentile(List<double> sortedSamples, double percentile)
        {
            if (sortedSamples.Count == 0) return double.NaN;
            var index = (int)Math.Ceiling(percentile * sortedSamples.Count) - 1;
            return sortedSamples[Math.Clamp(index, 0, sortedSamples.Count - 1)];
        }

        private void RecordEnvironment()
        {
            _results.Environment["Machine"] = Environment.MachineName;
            _results.Environment["OS"] = Environment.OSVersion.VersionString;
            _results.Environment["Logical processors"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture);
            _results.Environment["Scan workers (coreCount - 2)"] =
                Math.Max(1, Environment.ProcessorCount - 2).ToString(CultureInfo.InvariantCulture);
            _results.Environment["Runtime"] = Environment.Version.ToString();
            _results.Environment["Build"] =
#if DEBUG
                "Debug";
#else
                "Release";
#endif
            _results.Environment["Server GC"] = System.Runtime.GCSettings.IsServerGC.ToString();
        }
    }
}
