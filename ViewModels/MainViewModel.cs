using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Fastcull.Input;
using Fastcull.Models;
using Fastcull.Services;
using Microsoft.UI.Xaml;

namespace Fastcull.ViewModels
{
    /// <summary>
    /// Owns the sorted photo sequence and the single active-index cursor that both
    /// filmstrip regions read from. PreviousItem/ActiveItem/NextItem are always recomputed
    /// from the current ActiveIndex - never carried forward incrementally - so the top
    /// region's neighbors are guaranteed correct after any jump, not just a +/-1 step.
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        public ObservableCollection<FilmstripItemViewModel> Items { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PositionText))]
        private int _activeIndex = -1;

        /// <summary>Session position counter for the title bar, e.g. "1204 / 2000".</summary>
        public string PositionText => Items.Count == 0 ? string.Empty : $"{ActiveIndex + 1} / {Items.Count}";

        /// <summary>
        /// The photos currently on stage, in display order. Variable length: the stage shows as
        /// many as actually fit (PRD 1.5), which the View decides from the geometry and pushes
        /// back through <see cref="StageSlotCount"/>.
        /// </summary>
        public ObservableCollection<FilmstripItemViewModel> StageItems { get; } = new();

        private int _stageSlotCount = 3;

        /// <summary>
        /// How many slots the stage should show. Set by the View once it knows how many fit;
        /// changing it rebuilds <see cref="StageItems"/>. Clamped to the window rule's ceiling.
        /// </summary>
        public int StageSlotCount
        {
            get => _stageSlotCount;
            set
            {
                var clamped = Math.Clamp(value, 1, FilmstripWindow.MaxSlots);
                if (clamped == _stageSlotCount) return;
                _stageSlotCount = clamped;
                RecomputeSlots();
            }
        }

        [ObservableProperty]
        private FilmstripItemViewModel? _activeItem;

        private bool _isZoomed;

        /// <summary>
        /// Whether the active photo fills the stage on its own, with its neighbours hidden.
        ///
        /// A deliberately simple first pass: it re-fits the display-tier image that is already
        /// decoded, and adds no decode of any kind. It is NOT the 1:1 inspection of PRD 1.7 -
        /// there is no full-resolution decode, no Tier A/B distinction, no panning and no HUD.
        ///
        /// The flag is mirrored onto the items because the stage is a templated repeater bound to
        /// the item type, so the template can only see per-item properties.
        /// </summary>
        public bool IsZoomed
        {
            get => _isZoomed;
            set
            {
                if (_isZoomed == value) return;
                _isZoomed = value;

                foreach (var item in Items) item.IsZoomed = value;

                OnPropertyChanged(nameof(IsZoomed));
                OnPropertyChanged(nameof(FilmstripBandVisibility));
            }
        }

        /// <summary>
        /// Captured at construction, which happens on the UI thread. Needed because the only
        /// asynchronous thing this class owns - the geocoding callback below - completes on a
        /// thread-pool thread and must marshal back before touching a bound property.
        /// </summary>
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        private bool _isInfoVisible;

        /// <summary>
        /// PRD 1.8.1's on-photo info overlay. Mirrored onto the items for the same reason
        /// <see cref="IsZoomed"/> is: the stage is a templated repeater bound to the item type, so
        /// the template can only see per-item properties.
        ///
        /// Session-only. It is a glance, not a preference, and nothing persists it.
        /// </summary>
        public bool IsInfoVisible
        {
            get => _isInfoVisible;
            set
            {
                if (_isInfoVisible == value) return;
                _isInfoVisible = value;

                foreach (var item in Items) item.IsInfoVisible = value;

                OnPropertyChanged(nameof(IsInfoVisible));
            }
        }

        /// <summary>
        /// PRD 1.8.2's reverse geocoding. Constructed once and shared, so its cache spans the
        /// session rather than the photo.
        /// </summary>
        private readonly PlaceLookup _places = new(new NominatimPlaceResolver());

        /// <summary>
        /// Fills in the active photo's place, without ever making anything wait for it.
        ///
        /// Three outcomes, in order of cost: no GPS at all and the field stays empty; a cached
        /// name and it appears instantly; otherwise the raw coordinates show immediately (via
        /// PlaceText's own fallback) and a background lookup may replace them later. Nothing here
        /// awaits, and nothing downstream of navigation depends on it.
        /// </summary>
        private void ResolvePlace(FilmstripItemViewModel item)
        {
            if (item.Photo.Latitude is not double lat || item.Photo.Longitude is not double lon) return;
            if (!string.IsNullOrWhiteSpace(item.PlaceName)) return;

            if (_places.TryGetCached(lat, lon, out var cached))
            {
                // A cached null is a remembered failure: the coordinates already on screen are the
                // correct final answer, so there is nothing to do.
                if (cached is not null) item.PlaceName = cached;
                return;
            }

            _places.BeginResolve(lat, lon, name =>
                _dispatcherQueue.TryEnqueue(() =>
                {
                    // The cursor may have moved on, and the item may even have been evicted. Both
                    // are fine: the name is cached either way, so arriving late costs nothing.
                    try { item.PlaceName = name; } catch { }
                }));
        }

        private SessionStore? _sessionStore;

        /// <summary>
        /// The left panel of PRD 1.5. Owned here because its tallies are a view of this class's
        /// sequence, but kept as its own type so panel state does not accumulate on this one.
        /// </summary>
        public SidebarViewModel Sidebar { get; } = new();

        /// <summary>
        /// Recounts the sidebar from the current sequence. The single place this happens, called
        /// from the two events that can change a count: the folder loading, and a rating changing.
        /// </summary>
        private void RefreshTally() => Sidebar.Update(Items.Select(i => i.CullState));

        // ------------------------------------------------------------------
        // Empty state (PRD 1.1.1)
        //
        // First run and a folder that has gone away land in the same place on purpose. Neither is
        // an error: a card that is not plugged in is an ordinary event for this app's users.
        // ------------------------------------------------------------------

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EmptyStateVisibility))]
        [NotifyPropertyChangedFor(nameof(StageVisibility))]
        [NotifyPropertyChangedFor(nameof(FilmstripBandVisibility))]
        private bool _isEmpty = true;

        /// <summary>Names the folder that could not be opened. Empty on a genuine first run.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EmptyStateDetailVisibility))]
        private string _emptyStateDetail = string.Empty;

        public Visibility EmptyStateVisibility => IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        public Visibility StageVisibility => IsEmpty ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>
        /// The bottom strip hides for two unrelated reasons - zoom takes the whole window, and the
        /// empty state has no photos to strip. Computed here rather than stacked as two converter
        /// bindings, which XAML cannot combine.
        /// </summary>
        public Visibility FilmstripBandVisibility =>
            !IsEmpty && !IsZoomed ? Visibility.Visible : Visibility.Collapsed;

        public Visibility EmptyStateDetailVisibility =>
            string.IsNullOrWhiteSpace(EmptyStateDetail) ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>
        /// Clears the sequence and shows the call to action. <paramref name="unopenableFolder"/>
        /// is the path that could not be opened, or null on a first run with nothing recorded.
        /// </summary>
        private void ShowEmptyState(string? unopenableFolder)
        {
            Items.Clear();
            StageItems.Clear();
            _pinnedItems.Clear();

            ActiveIndex = -1;
            ActiveItem = null;
            CurrentFolder = null;

            Sidebar.SetFolder(null);
            Sidebar.SetActivePhoto(null);
            Sidebar.CompleteScan();
            RefreshTally();
            Sidebar.UpdateFormats(System.Array.Empty<(string, FormatFamily)>());
            Sidebar.UpdateFolderTree(string.Empty, System.Array.Empty<FolderTreeEntry>());

            EmptyStateDetail = string.IsNullOrWhiteSpace(unopenableFolder)
                ? string.Empty
                : $"Could not open {unopenableFolder}";

            IsEmpty = true;
        }

        /// <summary>The folder currently open, or null when the app is on the empty state.</summary>
        public string? CurrentFolder { get; private set; }

        /// <summary>
        /// Startup (PRD 1.1.1). Reopens the last folder and resumes it, or shows the empty state.
        ///
        /// There is no default folder and no path baked into the app: a folder here is an
        /// unfinished job, and the only ones that ever open are ones the user chose.
        /// </summary>
        public async Task LoadAsync()
        {
            Sidebar.FolderNavigationRequested -= SetActiveIndex;
            Sidebar.FolderNavigationRequested += SetActiveIndex;

            var remembered = AppSettings.GetResumableFolder();
            if (remembered is null)
            {
                // First run, or a folder that has gone away. Both land here on purpose - the
                // empty state names the folder it could not open when there was one.
                ShowEmptyState(AppSettings.ReadRaw());
                return;
            }

            await OpenFolderAsync(remembered);
        }

        /// <summary>
        /// Loads and resumes a folder. The single path both launch and the sidebar's
        /// change-folder control run, so there is no separate "open" flow to drift out of step.
        /// </summary>
        public async Task OpenFolderAsync(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;

            // Close the outgoing folder's writer first. Its ratings are already durable - PRD 3.1
            // writes them as they happen - so this is a flush and a handle release, not a save.
            await ShutdownAsync();

            CurrentFolder = root;
            IsEmpty = false;
            EmptyStateDetail = string.Empty;

            // Remembered before the scan rather than after: a folder that takes a while to load is
            // still the folder the user chose, and a crash mid-scan should not lose that choice.
            AppSettings.SetLastFolder(root);

            Sidebar.SetFolder(root);

            var scanner = new DirectoryScanner();
            var scanned = new List<ScannedPhoto>();

            // PRD 1.2's progress pill. The count is real and updates as the scanner yields, which
            // it genuinely does - DirectoryScanner is an IAsyncEnumerable over a channel, and this
            // await frees the UI thread between files.
            //
            // What this is NOT is PRD 1.2's full requirement, which also wants the first image on
            // screen while the tail is still being enumerated. That needs the sequence itself to
            // be built incrementally, which is a larger change than a progress counter - see the
            // run report. This pill is honest about what it measures rather than a placeholder.
            var scanStarted = System.Diagnostics.Stopwatch.StartNew();

            // A folder can vanish between being chosen and being scanned - an unplugged card, a
            // path that resolved a moment ago. Falling back to the empty state is the same
            // outcome PRD 1.1.1 gives a remembered folder that no longer exists.
            IAsyncEnumerable<ScannedPhoto> scan;
            try
            {
                scan = scanner.ScanAsync(root);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FastCull] Scan could not start for {root}: {ex}");
                ShowEmptyState(root);
                return;
            }

            await foreach (var photo in scan)
            {
                scanned.Add(photo);

                // Only reveal the panel once the scan has run long enough that progress is worth
                // watching; below that it would be a flash at startup.
                Sidebar.ReportScanProgress(scanned.Count, scanStarted.ElapsedMilliseconds > ScanRevealDelayMs);
            }

            Sidebar.CompleteScan();

            var sorted = scanned
                .OrderBy(p => p.SortTime)
                .ThenBy(p => p.CaptureSubsec)
                .ThenBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Persistence must never stop the app opening: a locked or corrupt session DB
            // degrades to an in-memory session rather than an empty filmstrip.
            Dictionary<string, StoredPhotoState> stored = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                _sessionStore = await SessionStore.OpenAsync(root);
                await _sessionStore.RegisterPhotosAsync(sorted);
                stored = await _sessionStore.LoadPhotoStatesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FastCull] Session persistence unavailable: {ex}");
                _sessionStore = null;
            }

            Items.Clear();
            var index = 0;
            foreach (var photo in sorted)
            {
                var item = new FilmstripItemViewModel(photo, index);
                if (stored.TryGetValue(photo.FilePath, out var state))
                {
                    item.CullState = state.Cull;
                    item.Rotation = state.Rotation;
                }
                Items.Add(item);
                index++;
            }

            // After the restore loop, not before: stored ratings from a previous session are part
            // of the count, so a folder reopened mid-cull shows its real progress immediately.
            RefreshTally();

            // Both derive from the sequence and neither changes again until the folder does, so
            // they are built once here rather than on every rating like the tally.
            Sidebar.UpdateFormats(sorted.Select(p => (p.FileName, p.Family)));
            Sidebar.UpdateFolderTree(
                Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                sorted.Select((p, i) => new FolderTreeEntry(p.RelativePath, i)));

            SetActiveIndex(Items.Count > 0 ? 0 : -1);
        }

        /// <summary>
        /// How long a scan must run before the sidebar reveals itself to show progress. Below
        /// this the reveal reads as a flicker; above it, the user is actually waiting.
        /// </summary>
        private const long ScanRevealDelayMs = 400;

        /// <summary>Flushes pending rating writes and closes the session database.</summary>
        public async Task ShutdownAsync()
        {
            if (_sessionStore is null) return;
            var store = _sessionStore;
            _sessionStore = null;

            // ConfigureAwait(false): this must never require the UI thread to resume. Without
            // it, a caller that blocks the UI thread waiting on this method deadlocks - the
            // continuation needs the very thread the caller is holding.
            await store.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>Sole entry point for changing the active photo. Never touches scroll position - that is a View concern.</summary>
        public void SetActiveIndex(int index)
        {
            if (Items.Count == 0)
            {
                ActiveIndex = -1;
                ActiveItem = null;
                RecomputeSlots();
                return;
            }

            index = Math.Clamp(index, 0, Items.Count - 1);
            if (index == ActiveIndex) return;

            if (ActiveIndex >= 0 && ActiveIndex < Items.Count) Items[ActiveIndex].IsActive = false;
            ActiveIndex = index;
            Items[index].IsActive = true;
            ActiveItem = Items[index];

            // Keeps the folder tree's "you are here" mark on the cursor. The tree does not filter,
            // so this highlight is the whole of how it answers where in the shoot you are.
            Sidebar.SetCurrentFolder(Path.GetDirectoryName(Items[index].Photo.RelativePath) ?? string.Empty);

            // The sidebar's Active Photo panel (PRD 1.5) reads the item directly, so it follows
            // the cursor for free - including any place name that lands later.
            Sidebar.SetActivePhoto(Items[index]);
            ResolvePlace(Items[index]);

            RecomputeSlots();
        }

        /// <summary>
        /// Recomputes the whole stage window from ActiveIndex - never carried forward
        /// incrementally, so it is guaranteed correct after any jump, not just a +/-1 step
        /// (PRD 1.5, E.3).
        ///
        /// StageItems is patched in place rather than cleared and refilled: a clear would
        /// unrealize every container in the repeater and re-realize it on the next line, which
        /// throws away the decoded images of photos that did not actually leave the stage. Since
        /// navigation moves the window by one, all but one item is normally common to both.
        /// </summary>
        private void RecomputeSlots()
        {
            var window = FilmstripWindow.Compute(ActiveIndex, Items.Count, StageSlotCount);

            if (window.SlotCount <= 0)
            {
                StageItems.Clear();
                return;
            }

            for (var slot = 0; slot < window.SlotCount; slot++)
            {
                var item = Items[window.WindowStart + slot];

                if (slot < StageItems.Count)
                {
                    if (!ReferenceEquals(StageItems[slot], item)) StageItems[slot] = item;
                }
                else
                {
                    StageItems.Add(item);
                }
            }

            while (StageItems.Count > window.SlotCount)
                StageItems.RemoveAt(StageItems.Count - 1);

            PinStageItems();

            // Pinning must settle before the coordinator runs: it treats pinned items as
            // never-cancel, never-evict, and at a nine-photo stage the stage is wider than the
            // window's own lookbehind.
            _prefetch.OnCursorMoved(ActiveIndex, Items);
        }

        /// <summary>Sliding window, bounded pool and LRU eviction (PRD 3.3).</summary>
        private readonly PrefetchCoordinator _prefetch = new();

        /// <summary>Resident decoded bytes at the last cursor move. Surfaced for the perf harness.</summary>
        public long ResidentBytes => _prefetch.ResidentBytes;

        /// <summary>The prefetch range currently held. Surfaced for the perf harness.</summary>
        public PrefetchRange PrefetchRange => _prefetch.CurrentRange;

        /// <summary>
        /// Marks exactly the on-stage photos as pinned, and makes sure they are loading.
        ///
        /// Pinning is what stops eviction picking a photo the user is currently looking at -
        /// dropping one of those would blank it on screen, which is a visible bug rather than a
        /// memory saving (PRD 3.3).
        /// </summary>
        private void PinStageItems()
        {
            foreach (var item in _pinnedItems)
                if (!StageItems.Contains(item)) item.IsPinned = false;

            _pinnedItems.Clear();

            foreach (var item in StageItems)
            {
                item.IsPinned = true;
                item.BeginLoad();
                _pinnedItems.Add(item);
            }
        }

        private readonly List<FilmstripItemViewModel> _pinnedItems = new();

        public void MovePrevious() => SetActiveIndex(ActiveIndex - 1);
        public void MoveNext() => SetActiveIndex(ActiveIndex + 1);
        public void MoveFirst() => SetActiveIndex(0);
        public void MoveLast() => SetActiveIndex(Items.Count - 1);

        /// <summary>
        /// Applies a resolved input command. Navigation changes only the cursor; rating changes
        /// only the active item's state. The two never affect each other (PRD 2.1, D.2).
        /// </summary>
        public void Execute(ResolvedInput input)
        {
            switch (input.Command)
            {
                case AppCommand.NavigatePrevious: MovePrevious(); break;
                case AppCommand.NavigateNext: MoveNext(); break;
                case AppCommand.NavigateFirst: MoveFirst(); break;
                case AppCommand.NavigateLast: MoveLast(); break;

                case AppCommand.LadderUp: ApplyRating(s => s.Up()); break;
                case AppCommand.LadderDown: ApplyRating(s => s.Down()); break;
                case AppCommand.SetStars: ApplyRating(s => s.WithStars(input.Payload)); break;
                case AppCommand.SetPicked: ApplyRating(s => s.AsPicked()); break;
                case AppCommand.SetRejected: ApplyRating(s => s.AsRejected()); break;
                case AppCommand.SetUnflagged: ApplyRating(s => s.AsUnflagged()); break;

                case AppCommand.RotateRight: RotateActiveRight(); break;
                case AppCommand.RotateLeft: RotateActiveLeft(); break;

                case AppCommand.ToggleZoom: IsZoomed = !IsZoomed; break;
                case AppCommand.ExitZoom: IsZoomed = false; break;

                case AppCommand.ToggleInfo: IsInfoVisible = !IsInfoVisible; break;
            }
        }

        /// <summary>Rotates the active photo 90 degrees clockwise (PRD 1.11).</summary>
        public void RotateActiveRight() => ApplyRotation(r => r.RotateRight(), quarterTurns: 1);

        /// <summary>Rotates the active photo 90 degrees counter-clockwise (PRD 1.11).</summary>
        public void RotateActiveLeft() => ApplyRotation(r => r.RotateLeft(), quarterTurns: -1);

        /// <summary>
        /// Applies a quarter turn to the active item only. Synchronous and awaits nothing, so the
        /// photo turns within one frame - PRD 3.5 budgets this at the same &lt; 16 ms as a rating
        /// keypress, on the same reasoning: it is a transform, never a re-decode.
        ///
        /// Rotation moves no cursor and changes no rating, exactly as ApplyRating changes no
        /// cursor and no rotation. The two are entirely independent axes.
        /// </summary>
        private void ApplyRotation(Func<Rotation, Rotation> transition, int quarterTurns)
        {
            var item = ActiveItem;
            if (item is null) return;

            var updated = transition(item.Rotation);
            if (updated == item.Rotation) return;

            item.Rotation = updated;

            // Same fire-and-forget channel the ratings use (PRD 3.1): a non-blocking TryWrite,
            // never an awaited database call on the UI thread.
            _sessionStore?.QueueRotation(item.Photo.FilePath, updated);

            RotationChanged?.Invoke(item, quarterTurns);
        }

        /// <summary>
        /// Raised after the active item's Rotation changes, with the signed quarter turns just
        /// applied (+1 clockwise, -1 counter-clockwise).
        ///
        /// The direction has to be carried rather than derived from the before/after angles: a
        /// turn from 270 to 0 is +1 quarter turn, but the angles differ by -270, and animating
        /// that difference would spin the photo three-quarters of the way backwards.
        /// </summary>
        public event Action<FilmstripItemViewModel, int>? RotationChanged;

        /// <summary>
        /// Applies a ladder transition to the active item only. Synchronous and awaits nothing,
        /// so the border updates within one frame (PRD 1.6). Rating never moves the cursor.
        /// </summary>
        private void ApplyRating(Func<CullState, CullState> transition)
        {
            var item = ActiveItem;
            if (item is null) return;

            var updated = transition(item.CullState);
            if (updated == item.CullState) return;

            item.CullState = updated;

            // Fire-and-forget: QueueRating is a non-blocking TryWrite onto the background
            // writer's channel, so the UI thread never awaits the database (PRD 3.1).
            _sessionStore?.QueueRating(item.Photo.FilePath, updated);

            // Synchronous, so the sidebar's counts change in the same frame as the weight bar
            // under the photo. A tally that lagged the mark it describes would read as a bug.
            RefreshTally();

            RatingChanged?.Invoke(item);
        }

        /// <summary>Raised after the active item's CullState changes, so persistence can observe it.</summary>
        public event Action<FilmstripItemViewModel>? RatingChanged;

        // FindDefaultSampleImagesRoot is deliberately gone. Startup used to walk up from the
        // executable looking for a "SampleImages" folder and open whatever it found, which meant
        // the app had a hardcoded root and no way to open anything else. PRD 1.1.1 replaces it:
        // the only folders that ever open are ones the user picked, and SampleImages is now
        // reachable only by selecting it like any other folder.
    }
}
