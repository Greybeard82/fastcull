using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
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

        public MainViewModel()
        {
            // PositionText depends on the sequence LENGTH as well as the cursor, and the attribute
            // above only covers the cursor. Deleting the photo under the cursor leaves ActiveIndex
            // unchanged, so nothing notified and the counter went on reading "3 / 10" against nine
            // photos - measured, and a bug that predates the undo work. Watching the collection is
            // what makes the total honest, and it also covers undo putting a photo back.
            Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PositionText));
        }

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

        private bool _isFullScreen;

        /// <summary>
        /// PRD 1.7.3's standalone fullscreen: the window loses the taskbar and its own title bar,
        /// but nothing else changes - the stage still shows its 3-9 slots, the sidebar still
        /// reveals, the filmstrip still scrolls. It is more room, not a different mode.
        ///
        /// Deliberately independent of <see cref="IsZoomed"/> rather than folded into it. The
        /// window is fullscreen when *either* is set, so zooming from standalone fullscreen and
        /// then leaving zoom returns to standalone fullscreen rather than to a window - which is
        /// what makes Space usable while already fullscreen instead of fighting it.
        /// </summary>
        public bool IsFullScreen
        {
            get => _isFullScreen;
            set
            {
                if (_isFullScreen == value) return;
                _isFullScreen = value;
                OnPropertyChanged(nameof(IsFullScreen));
            }
        }

        private bool _isHelpVisible;

        /// <summary>
        /// PRD 2.1.3's keybinding overlay. Session-only and non-blocking: it is drawn over the
        /// stage but takes no pointer input and swallows no keys, so a binding can be tried while
        /// it is still on screen.
        /// </summary>
        public bool IsHelpVisible
        {
            get => _isHelpVisible;
            set
            {
                if (_isHelpVisible == value) return;
                _isHelpVisible = value;
                OnPropertyChanged(nameof(IsHelpVisible));
            }
        }

        /// <summary>
        /// Escape backs out of one thing at a time, topmost first (PRD 2.1.1). Returns true if it
        /// consumed something, which keeps the ordering honest: each press dismisses exactly one
        /// layer, so Escape from a zoomed photo inside standalone fullscreen takes two presses and
        /// never drops both at once.
        /// </summary>
        public bool DismissTopmost()
        {
            // Ahead of the help overlay: the finish confirmation is modal and the last thing
            // before files would move, so it is unambiguously what Escape means while it is up.
            if (IsFinishVisible) { CancelFinish(); return true; }
            if (IsHelpVisible) { IsHelpVisible = false; return true; }
            if (IsZoomed) { IsZoomed = false; return true; }
            if (IsFullScreen) { IsFullScreen = false; return true; }
            return false;
        }

        // ------------------------------------------------------------------
        // Finish Session (PRD 4.2)
        // ------------------------------------------------------------------

        private bool _isFinishVisible;

        /// <summary>Whether the finish confirmation is up. Modal, unlike the help overlay.</summary>
        public bool IsFinishVisible
        {
            get => _isFinishVisible;
            set
            {
                if (_isFinishVisible == value) return;
                _isFinishVisible = value;
                OnPropertyChanged(nameof(IsFinishVisible));
            }
        }

        private FinishOperation _finishOperation = FinishOperation.None;

        /// <summary>
        /// Move or Copy. **Starts at None every time the screen opens, and that is the point**
        /// (PRD 4.2): Move is destructive, Copy is not, they sit one click apart, and a default
        /// would be the app making that call on the user's behalf at the worst possible moment.
        /// </summary>
        public FinishOperation FinishOperation
        {
            get => _finishOperation;
            set
            {
                if (_finishOperation == value) return;
                _finishOperation = value;
                OnPropertyChanged(nameof(FinishOperation));
                OnPropertyChanged(nameof(CanConfirmFinish));
                OnPropertyChanged(nameof(IsMoveChosen));
                OnPropertyChanged(nameof(IsCopyChosen));
                OnPropertyChanged(nameof(CopyBorderBrush));
                OnPropertyChanged(nameof(CopyTextBrush));
                OnPropertyChanged(nameof(MoveBorderBrush));
                OnPropertyChanged(nameof(MoveTextBrush));
            }
        }

        public bool IsMoveChosen => FinishOperation == FinishOperation.Move;
        public bool IsCopyChosen => FinishOperation == FinishOperation.Copy;

        private FinishStructure _finishStructure = FinishStructure.Preserve;

        /// <summary>
        /// PRD 4.2.2's layout choice. Unlike Move/Copy this one *does* have a default, and the
        /// default is Preserve: it is the non-destructive reading of an ambiguous situation, since
        /// preserving folders can never make two files collide that would not have collided anyway,
        /// and flattening can. Nothing is overwritten either way - the executor's rename and its
        /// CreateNew see to that - but Preserve keeps the renames rare.
        /// </summary>
        public FinishStructure FinishStructure
        {
            get => _finishStructure;
            set
            {
                if (_finishStructure == value) return;
                _finishStructure = value;
                OnPropertyChanged(nameof(FinishStructure));
                OnPropertyChanged(nameof(IsPreserveChosen));
                OnPropertyChanged(nameof(IsFlatChosen));
                OnPropertyChanged(nameof(PreserveBorderBrush));
                OnPropertyChanged(nameof(PreserveTextBrush));
                OnPropertyChanged(nameof(FlatBorderBrush));
                OnPropertyChanged(nameof(FlatTextBrush));
            }
        }

        public bool IsPreserveChosen => FinishStructure == FinishStructure.Preserve;
        public bool IsFlatChosen => FinishStructure == FinishStructure.Flat;

        public Microsoft.UI.Xaml.Media.Brush PreserveBorderBrush => ChoiceBrush(IsPreserveChosen, border: true);
        public Microsoft.UI.Xaml.Media.Brush PreserveTextBrush => ChoiceBrush(IsPreserveChosen, border: false);
        public Microsoft.UI.Xaml.Media.Brush FlatBorderBrush => ChoiceBrush(IsFlatChosen, border: true);
        public Microsoft.UI.Xaml.Media.Brush FlatTextBrush => ChoiceBrush(IsFlatChosen, border: false);

        // Brushes resolved here rather than through converters, matching SidebarViewModel.PinBrush:
        // the theme dictionary stays the single source of colour, and the XAML stays declarative
        // without three new converter classes for one screen.
        public Microsoft.UI.Xaml.Media.Brush CopyBorderBrush => ChoiceBrush(IsCopyChosen, border: true);
        public Microsoft.UI.Xaml.Media.Brush CopyTextBrush => ChoiceBrush(IsCopyChosen, border: false);
        public Microsoft.UI.Xaml.Media.Brush MoveBorderBrush => ChoiceBrush(IsMoveChosen, border: true);
        public Microsoft.UI.Xaml.Media.Brush MoveTextBrush => ChoiceBrush(IsMoveChosen, border: false);

        private static Microsoft.UI.Xaml.Media.Brush ChoiceBrush(bool chosen, bool border)
        {
            var key = chosen
                ? (border ? "AccentBrush" : "Accent200Brush")
                : (border ? "Neutral900Brush" : "Neutral500Brush");

            return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
        }

        public Visibility FinishResultVisibility =>
            HasFinishResult ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// The gate on the Confirm button: a choice must be made, no run in flight, and no result
        /// already on screen.
        ///
        /// That last clause matters because the idle controls reappear when a run ends
        /// (<see cref="FinishIdleVisibility"/> is simply the inverse of running). A screen that
        /// survives its own run - which is now only the failure case - would otherwise offer a live
        /// Confirm sitting directly under "Done", and pressing it would run the whole batch a
        /// second time. Cancel and reopen to run again.
        /// </summary>
        public bool CanConfirmFinish =>
            FinishOperation != FinishOperation.None && !IsFinishRunning && !HasFinishResult;

        private FinishPlan? _finishSummary;

        /// <summary>
        /// The counts shown on the confirmation, computed by the same <see cref="FinishPlanner"/>
        /// that computes destinations. Deliberately not a second tally: if the summary were
        /// counted separately from the bucketing, the screen could show numbers the plan does not
        /// honour, and the one thing this screen must not do is misdescribe what is about to
        /// happen.
        /// </summary>
        public FinishPlan? FinishSummary => _finishSummary;

        public string FinishApprovedText => (_finishSummary?.ApprovedCount ?? 0).ToString();
        public string FinishRejectedText => (_finishSummary?.RejectedCount ?? 0).ToString();
        public string FinishUnratedText => (_finishSummary?.UntouchedCount ?? 0).ToString();
        public string FinishTotalText => (_finishSummary?.Total ?? 0).ToString();
        public string FinishAffectedText => (_finishSummary?.AffectedCount ?? 0).ToString();
        public string FinishStar1Text => StarText(1);
        public string FinishStar2Text => StarText(2);
        public string FinishStar3Text => StarText(3);
        public string FinishStar4Text => StarText(4);
        public string FinishStar5Text => StarText(5);
        private string StarText(int stars) => (_finishSummary?.StarCount(stars) ?? 0).ToString();

        /// <summary>Names the session on the confirmation, so it is obvious which job is finishing.</summary>
        public string FinishSessionTitle => Sidebar.SessionName;

        private string _finishResult = string.Empty;

        /// <summary>Where the dry-run log ended up, shown after Confirm.</summary>
        public string FinishResult
        {
            get => _finishResult;
            private set
            {
                if (_finishResult == value) return;
                _finishResult = value;
                OnPropertyChanged(nameof(FinishResult));
                OnPropertyChanged(nameof(HasFinishResult));
                OnPropertyChanged(nameof(FinishResultVisibility));
                OnPropertyChanged(nameof(CanConfirmFinish));
            }
        }

        public bool HasFinishResult => !string.IsNullOrEmpty(FinishResult);

        /// <summary>
        /// Opens the confirmation, recomputing the summary from the live sequence. The choice is
        /// reset to None on every open rather than remembered from last time - a remembered Move
        /// is a default wearing a disguise.
        /// </summary>
        public void BeginFinish()
        {
            if (IsEmpty || Items.Count == 0) return;

            FinishOperation = FinishOperation.None;
            FinishResult = string.Empty;

            // Reset alongside the operation, for the same reason: a remembered Flat from last time
            // is a default wearing a disguise, and this one changes where every file lands.
            FinishStructure = FinishStructure.Preserve;

            _finishSummary = BuildPlan(FinishOperation.None);

            NotifySummaryChanged();
            IsFinishVisible = true;
        }

        public void CancelFinish()
        {
            IsFinishVisible = false;
            FinishOperation = FinishOperation.None;
            FinishResult = string.Empty;
        }

        private FinishPlan BuildPlan(FinishOperation operation) => FinishPlanner.Plan(
            CurrentFolder ?? string.Empty,
            operation,
            Items.Select(i => (i.Photo.FilePath, i.Photo.RelativePath, i.CullState)),
            FinishStructure);

        private void NotifySummaryChanged()
        {
            foreach (var name in new[]
            {
                nameof(FinishSummary), nameof(FinishApprovedText), nameof(FinishRejectedText),
                nameof(FinishUnratedText), nameof(FinishTotalText), nameof(FinishAffectedText),
                nameof(FinishStar1Text), nameof(FinishStar2Text), nameof(FinishStar3Text),
                nameof(FinishStar4Text), nameof(FinishStar5Text), nameof(FinishSessionTitle),
            })
            {
                OnPropertyChanged(name);
            }
        }

        private bool _isFinishRunning;

        /// <summary>True while files are actually being written. Swaps the card into progress mode.</summary>
        public bool IsFinishRunning
        {
            get => _isFinishRunning;
            private set
            {
                if (_isFinishRunning == value) return;
                _isFinishRunning = value;

                OnPropertyChanged(nameof(IsFinishRunning));
                OnPropertyChanged(nameof(CanConfirmFinish));
                OnPropertyChanged(nameof(FinishRunningVisibility));
                OnPropertyChanged(nameof(FinishIdleVisibility));
            }
        }

        public Visibility FinishRunningVisibility => IsFinishRunning ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FinishIdleVisibility => IsFinishRunning ? Visibility.Collapsed : Visibility.Visible;

        private int _finishDone;
        private int _finishTotal;
        private string _finishCurrentFile = string.Empty;

        public string FinishProgressText => _finishTotal == 0 ? string.Empty : $"{_finishDone} / {_finishTotal}";
        public string FinishCurrentFile => _finishCurrentFile;
        public double FinishProgressValue => _finishTotal == 0 ? 0 : 100.0 * _finishDone / _finishTotal;

        private CancellationTokenSource? _finishCts;

        /// <summary>Requests a stop. The engine finishes the file in flight, then stops (PRD 4.4).</summary>
        public void CancelFinishRun() => _finishCts?.Cancel();

        /// <summary>
        /// PRD 4.4. Performs the plan: copies, verifies, and on a Move deletes each original only
        /// after its copy is confirmed.
        ///
        /// **The whole thing runs on a worker thread.** CLAUDE.md's UI-thread rule has no exception
        /// for file operations, and this is the largest block of I/O the app ever performs -
        /// potentially thousands of files. Progress comes back through the dispatcher.
        /// </summary>
        public async Task ConfirmFinishAsync()
        {
            if (!CanConfirmFinish || IsFinishRunning) return;

            var plan = BuildPlan(FinishOperation);

            _finishCts?.Dispose();
            _finishCts = new CancellationTokenSource();
            var token = _finishCts.Token;

            _finishDone = 0;
            _finishTotal = plan.AffectedCount;
            _finishCurrentFile = string.Empty;
            FinishResult = string.Empty;
            IsFinishRunning = true;
            NotifyProgressChanged();

            var progress = new Progress<FinishProgress>(p =>
            {
                _finishDone = p.Done;
                _finishTotal = p.Total;
                _finishCurrentFile = p.CurrentFile;
                NotifyProgressChanged();
            });

            FinishRunReport report;
            try
            {
                report = await Task.Run(
                    () => FinishExecutor.ExecuteAsync(plan, new SystemFinishFileSystem(), progress, token),
                    token).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                IsFinishRunning = false;
                FinishResult = $"The operation could not be started: {ex.Message}\nNothing was moved or copied.";
                return;
            }

            IsFinishRunning = false;
            FinishResult = Describe(report);

            // ---- Back to the session (PRD 4.5) ----
            //
            // This used to fire only for a Move that had actually moved something. Every other
            // ending - a Copy, or a Move with nothing to do - left IsFinishVisible true, and the
            // window-level modal guard swallows every command except Escape while it is. The whole
            // keyboard went dead, and since Delete is the key people reach for first, that is what
            // "Delete is broken" turned out to be. Worse, the idle buttons come back when the run
            // ends, so Confirm was live again and a second press would re-run the entire batch.
            //
            // So: any run that ended cleanly closes the screen and reloads the folder. The reload
            // is what makes PRD 4.1's "a reopened session is what is still here" true immediately
            // rather than at the next launch - after a Move the sorted photos have gone and the
            // unrated ones remain to be finished, and after a Copy nothing has left, which is
            // precisely what "originals stay" means.
            //
            // A run with failures keeps the screen, deliberately. The list of what did not go is
            // the one thing the user has to read, and a toast is the wrong place for it.
            if (report.FailedCount == 0 && report.Outcome is FinishOutcome.Completed or FinishOutcome.Cancelled)
            {
                IsFinishVisible = false;
                FinishOperation = FinishOperation.None;
                FinishResult = string.Empty;

                if (CurrentFolder is { } folder) await OpenFolderAsync(folder).ConfigureAwait(true);

                // Raised after the reload so it survives it - the headline is all that is left of
                // the result screen, and the run log holds the detail either way.
                Toast(Headline(report));
            }
        }

        /// <summary>
        /// The one-line version, shared by the result screen and the toast that replaces it on a
        /// clean run. Written once so the two can never word the same outcome differently.
        /// </summary>
        private static string Headline(FinishRunReport report)
        {
            var verb = report.Operation == FinishOperation.Move ? "moved" : "copied";

            var headline = report.Outcome switch
            {
                FinishOutcome.Completed => $"Done. {report.DoneCount} {verb}.",
                FinishOutcome.Cancelled => $"Cancelled. {report.DoneCount} {verb} before stopping; every other original is untouched.",
                FinishOutcome.RefusedNotEnoughSpace => "Not started.",
                _ => $"Stopped. {report.DoneCount} {verb}; every original not yet processed is untouched.",
            };

            // Renames are worth a word even in the short form, and much more so under Flat, where
            // they are expected rather than exceptional (PRD 4.2.2).
            return report.RenamedCount > 0
                ? $"{headline} {report.RenamedCount} renamed to avoid overwriting."
                : headline;
        }

        private static string Describe(FinishRunReport report)
        {
            var lines = new List<string> { Headline(report) };

            if (report.Message is not null) lines.Add(report.Message);
            if (report.FailedCount > 0) lines.Add($"{report.FailedCount} could not be processed.");
            if (report.FailureReportPath is not null) lines.Add($"Details: {report.FailureReportPath}");
            if (report.LogPath is not null) lines.Add($"Log: {report.LogPath}");

            return string.Join(Environment.NewLine, lines);
        }

        private void NotifyProgressChanged()
        {
            OnPropertyChanged(nameof(FinishProgressText));
            OnPropertyChanged(nameof(FinishCurrentFile));
            OnPropertyChanged(nameof(FinishProgressValue));
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
            // PRD 1.8.2: off unless the user asked for it. Checked here rather than at startup so
            // toggling the setting takes effect on the next photo instead of the next launch, and
            // so no request is ever made on behalf of a user who left it off.
            if (!AppSettings.GeocodingEnabled) return;

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
            Sidebar.SessionName = string.Empty;

            // Nothing loaded, so nothing to finish. The dropdown is still populated on purpose:
            // the empty state is exactly where reopening a prior session is most useful.
            Sidebar.CanFinishSession = false;
            RefreshSessions();
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
        /// <param name="sessionName">
        /// PRD 4.1's optional name, supplied only when creating a session. Null on every other
        /// path - reopening a named session must not erase its name just because the reopen came
        /// through the folder picker.
        /// </param>
        public async Task OpenFolderAsync(string root, string? sessionName = null)
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
                // Already-sorted output lives under the scan root (PRD 4.3), so without this the
                // folder reopens showing every photo twice - once at its origin and once in its
                // bucket - and a second Finish Session re-sorts what it already sorted.
                if (FinishPlanner.IsInsideBucket(photo.RelativePath)) continue;

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
                _sessionStore = await SessionStore.OpenAsync(root, name: sessionName);
                await _sessionStore.RegisterPhotosAsync(sorted);
                stored = await _sessionStore.LoadPhotoStatesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FastCull] Session persistence unavailable: {ex}");
                _sessionStore = null;
            }

            // The name comes from the store when there is one, so a reopened session shows the
            // name it was given rather than the folder it happens to live in. With no store - a
            // locked or corrupt database - the folder name is still the right answer.
            // The history closes over items from the previous sequence; undoing one after the
            // folder changed would write a rating onto a photo that is no longer on screen.
            _undoStack.Clear();
            ToastText = string.Empty;

            Sidebar.SessionName = _sessionStore?.DisplayName ?? SessionStore.Describe(sessionName, root);
            RefreshSessions();

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

            Sidebar.CanFinishSession = Items.Count > 0;

            SetActiveIndex(Items.Count > 0 ? 0 : -1);
        }

        /// <summary>
        /// Repopulates PRD 4.1's dropdown. Enumerating the session databases touches disk, so it
        /// runs on a worker; the collection is then filled back on the UI thread.
        /// </summary>
        public async void RefreshSessions()
        {
            try
            {
                var sessions = await Task.Run(() => SessionStore.ListSessions()).ConfigureAwait(true);
                Sidebar.SetSessions(sessions, CurrentFolder);
            }
            catch (Exception ex)
            {
                // A missing or unreadable sessions directory costs the dropdown its contents and
                // nothing else - the open folder is unaffected.
                System.Diagnostics.Debug.WriteLine($"[FastCull] Could not list sessions: {ex}");
            }
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
        public void SetActiveIndex(int index) => SetActiveIndex(index, force: false);

        /// <summary>
        /// Raised once a cursor move is **completely** applied - index, active item, and the
        /// rebuilt stage slots.
        ///
        /// The View used to hang its navigation work off ActiveIndex's PropertyChanged instead,
        /// and that fired from the middle of the update: ActiveIndex was assigned first, so a
        /// handler reading ActiveItem or the stage frames saw the PREVIOUS photo's. While zoomed
        /// that made the zoom-tier re-decode either skip entirely (old item == already-loaded
        /// item) or fire for the photo the user had just left, sized to the un-zoomed 3-up slot -
        /// measured, a 981x1472 decode where 2176x1451 was needed.
        ///
        /// An explicit "done" signal is used rather than moving the assignments around, because
        /// the ordering inside the setter is load-bearing for other reasons and the next person to
        /// reorder it would silently reintroduce this.
        /// </summary>
        public event Action? NavigationCompleted;

        /// <summary>
        /// <paramref name="force"/> re-points at the index even when it has not changed.
        ///
        /// Needed because the no-op guard below identifies a photo by its POSITION, which is only
        /// safe while the sequence is stable. A delete (PRD 2.1.3) puts a different photo at the
        /// same index, and without this the guard would skip the update: measured, the sidebar's
        /// Active Photo panel kept showing the deleted photo's metadata after it was gone.
        /// </summary>
        private void SetActiveIndex(int index, bool force)
        {
            if (Items.Count == 0)
            {
                ActiveIndex = -1;
                ActiveItem = null;
                RecomputeSlots();
                NavigationCompleted?.Invoke();
                return;
            }

            index = Math.Clamp(index, 0, Items.Count - 1);
            if (!force && index == ActiveIndex) return;

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

            // Last, deliberately: everything the View reads is now current.
            NavigationCompleted?.Invoke();
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
        /// PRD 2.1.1's ten-photo jump, bound to Ctrl + Left / Right.
        ///
        /// Clamps rather than wraps, which is the same rule the arrow keys already follow -
        /// SetActiveIndex does the clamping, so a jump from photo 3 lands on the first and a jump
        /// near the end lands on the last. Wrapping would make a key held down cycle the shoot
        /// forever with no way to tell you had passed the end.
        /// </summary>
        public void JumpBackward() => SetActiveIndex(ActiveIndex - InputRouter.JumpSize);
        public void JumpForward() => SetActiveIndex(ActiveIndex + InputRouter.JumpSize);

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

                case AppCommand.JumpBackward: JumpBackward(); break;
                case AppCommand.JumpForward: JumpForward(); break;

                case AppCommand.LadderUp: ApplyRating(s => s.Up()); break;
                case AppCommand.LadderDown: ApplyRating(s => s.Down()); break;
                case AppCommand.SetStars: ApplyRating(s => s.WithStars(input.Payload)); break;
                // Picked with stars RESET, not CullState.AsPicked() - which is a no-op on a photo
                // already at 3-7 rungs and so leaves a 4-star photo at 4 stars. PRD 2.1.1 wants C
                // to be a reliable "demote to plain picked" whose effect does not depend on where
                // the photo already was.
                case AppCommand.SetPicked: ApplyRating(_ => new CullState(Flag.Picked, 0)); break;
                case AppCommand.SetRejected: ApplyRating(s => s.AsRejected()); break;
                case AppCommand.SetUnflagged: ApplyRating(s => s.AsUnflagged()); break;

                case AppCommand.RotateRight: RotateActiveRight(); break;
                case AppCommand.RotateLeft: RotateActiveLeft(); break;

                case AppCommand.ToggleZoom: IsZoomed = !IsZoomed; break;

                // Not "IsZoomed = false" any more: Escape dismisses whatever is topmost, and the
                // help overlay and standalone fullscreen are both things it can back out of.
                case AppCommand.ExitZoom: DismissTopmost(); break;

                case AppCommand.ToggleFullScreen: IsFullScreen = !IsFullScreen; break;

                case AppCommand.ToggleInfo: IsInfoVisible = !IsInfoVisible; break;
                case AppCommand.ToggleHelp: IsHelpVisible = !IsHelpVisible; break;

                // The identical call the sidebar's own pin button makes, so the key and the
                // button cannot drift apart (PRD 1.5).
                case AppCommand.ToggleSidebarPin: Sidebar.TogglePin(); break;

                // Routed through the sidebar's existing request rather than opening a picker here:
                // the picker needs a window handle, FilmstripView already listens for that event,
                // and both paths end in the same OpenFolderAsync (PRD 1.1.1).
                case AppCommand.OpenFolder: Sidebar.RequestChangeFolder(); break;

                case AppCommand.DeletePhoto: DeleteActivePhoto(); break;

                // PRD 1.9. Finish Session is deliberately NOT on this stack - it has its own
                // confirmation, it is the one action the user is already made to think about, and
                // a Ctrl+Z that silently un-sorted two thousand photographs would be far more
                // dangerous than no undo at all.
                case AppCommand.Undo: ApplyHistory(_undoStack.Undo()); break;
                case AppCommand.Redo: ApplyHistory(_undoStack.Redo()); break;
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

            var before = item.CullState;
            var after = transition(before);
            if (after == before) return;

            SetCullState(item, after);

            // PRD 1.9. Pushed after the change, carrying both values - see RatingCommand for why
            // it records the prior state rather than a direction.
            _undoStack.Push(new RatingCommand(item, before, after, SetCullState));
        }

        /// <summary>
        /// The single place a photo's cull state is written.
        ///
        /// Undo and redo go through here too, which is what makes them indistinguishable from a
        /// keypress as far as the sidebar tally, the on-photo weight bar and the filmstrip badge
        /// are concerned. A separate "restore" path would be a second place for those three to be
        /// updated, and eventually one of them would be forgotten.
        /// </summary>
        private void SetCullState(FilmstripItemViewModel item, CullState state)
        {
            item.CullState = state;

            // Fire-and-forget: QueueRating is a non-blocking TryWrite onto the background
            // writer's channel, so the UI thread never awaits the database (PRD 3.1).
            _sessionStore?.QueueRating(item.Photo.FilePath, state);

            // Synchronous, so the sidebar's counts change in the same frame as the weight bar
            // under the photo. A tally that lagged the mark it describes would read as a bug.
            RefreshTally();

            RatingChanged?.Invoke(item);
        }

        /// <summary>Raised after the active item's CullState changes, so persistence can observe it.</summary>
        public event Action<FilmstripItemViewModel>? RatingChanged;

        // ------------------------------------------------------------------
        // Undo / redo (PRD 1.9)
        // ------------------------------------------------------------------

        private readonly UndoStack _undoStack = new();

        /// <summary>Exposed for the view and for tests to inspect depth.</summary>
        public UndoStack History => _undoStack;

        /// <summary>
        /// Applies the result of an undo or a redo.
        ///
        /// **The cursor moves to the photo that changed.** That is a choice: an undo could leave
        /// the cursor where it is, but the whole point of this feature is the fast one-handed
        /// workflow, where the mistake being undone is often several photos back. Undoing a rating
        /// on a photo that is no longer on screen, and showing no sign of it, would look exactly
        /// like nothing having happened - and would invite a second Ctrl+Z that undoes something
        /// the user did not mean to touch. Moving the cursor makes the result visible and keeps
        /// the next keystroke aimed at the photo the user is now looking at.
        /// </summary>
        private void ApplyHistory(UndoResult result)
        {
            switch (result.Outcome)
            {
                case UndoOutcome.NothingToDo:
                    return;

                case UndoOutcome.Failed:
                    Toast(result.Message ?? "That action could not be reversed.");
                    return;
            }

            // A delete's undo has already placed the cursor as part of re-inserting the photo.
            if (result.Command is RatingCommand rating)
            {
                var index = Items.IndexOf(rating.Item);
                if (index >= 0) SetActiveIndex(index, force: true);
            }
        }

        private string _toastText = string.Empty;

        /// <summary>A short-lived message. Empty when nothing is being said.</summary>
        public string ToastText
        {
            get => _toastText;
            private set
            {
                if (_toastText == value) return;
                _toastText = value;

                OnPropertyChanged(nameof(ToastText));
                OnPropertyChanged(nameof(ToastVisibility));
            }
        }

        public Visibility ToastVisibility =>
            string.IsNullOrEmpty(ToastText) ? Visibility.Collapsed : Visibility.Visible;

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _toastTimer;

        /// <summary>
        /// Says something briefly, on screen.
        ///
        /// Needed because the failures this feature has are ones the user must be told about -
        /// an undo that cannot restore a purged file has to say so rather than appearing to do
        /// nothing. Debug.WriteLine is not a user-facing channel.
        /// </summary>
        private void Toast(string message)
        {
            ToastText = message;

            _toastTimer ??= _dispatcherQueue.CreateTimer();
            _toastTimer.IsRepeating = false;
            _toastTimer.Interval = TimeSpan.FromSeconds(6);
            _toastTimer.Tick -= OnToastElapsed;
            _toastTimer.Tick += OnToastElapsed;

            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void OnToastElapsed(object? sender, object e) => ToastText = string.Empty;

        /// <summary>
        /// PRD 2.1.2: moves the selected photo to the Recycle Bin and drops it from the sequence.
        ///
        /// Order matters. The file is moved FIRST, and the sequence is only touched if that
        /// succeeded - a locked or read-only file must leave the strip exactly as it was, rather
        /// than disappearing from view while surviving on disk.
        ///
        /// A Recycle Bin move rather than a permanent delete, which is what lets PRD 1.9's undo
        /// bring it back: the file still exists, and TryRestore puts it where it was.
        /// </summary>
        private void DeleteActivePhoto()
        {
            var item = ActiveItem;
            if (item is null) return;

            var index = ActiveIndex;
            if (index < 0 || index >= Items.Count) return;

            var command = new DeleteCommand(item, index, RemovePhoto, ReinsertPhoto);

            if (!command.Execute())
            {
                Toast($"Could not delete {item.Photo.FileName}. It is still here.");
                return;
            }

            _undoStack.Push(command);
        }

        /// <summary>
        /// Recycles the file and takes the photo out of the sequence. The delete half of
        /// <see cref="DeleteCommand"/>, so it serves the first press and any redo alike.
        ///
        /// Returns false without touching the sequence when the file will not go - PRD 2.1.2 is
        /// explicit that a photo must not vanish from the filmstrip while surviving on disk.
        /// </summary>
        private bool RemovePhoto(FilmstripItemViewModel item, int index)
        {
            Diagnostics.InputTrace.Log("RemovePhoto",
                $"{item.Photo.FileName} pinned={item.IsPinned} exists={System.IO.File.Exists(item.Photo.FilePath)}");

            if (!RecycleBin.TrySend(item.Photo.FilePath, out var why))
            {
                Diagnostics.InputTrace.Log("  RECYCLE FAILED", why);
                System.Diagnostics.Debug.WriteLine($"[FastCull] Could not recycle {item.Photo.FilePath}: {why}");

                // Said out loud rather than swallowed. A silent return is indistinguishable from
                // the Delete key not being wired up, which is exactly how this was reported.
                Toast($"Could not delete {item.Photo.FileName} - {why}");
                return false;
            }

            Diagnostics.InputTrace.Log("  recycled OK");

            // Located rather than assumed: a redo runs against a sequence that has been through an
            // undo, so the recorded index is a hint, not a fact.
            var at = Items.IndexOf(item);
            if (at < 0) return false;

            // Release its decodes before it leaves; nothing else will hold a reference afterwards.
            item.IsPinned = false;
            item.ReleaseZoomImage();
            item.CancelLoad();
            item.CancelThumbnailLoad();

            Items.RemoveAt(at);

            // Position in the sequence is what PRD 3.3's window and eviction are indexed by, so
            // everything after the hole has to be renumbered before the cursor moves.
            for (var i = at; i < Items.Count; i++) Items[i].Index = i;

            if (Items.Count == 0)
            {
                // The folder is still open and still the remembered one - it is simply empty now.
                _pinnedItems.Clear();
                StageItems.Clear();
                ActiveIndex = -1;
                ActiveItem = null;

                // Deleting the last photo left this false, so the stage stayed visible over an
                // empty sequence and the empty-state message never appeared. ReinsertPhoto already
                // clears it on the way back; this is the matching half.
                IsEmpty = true;

                Sidebar.SetActivePhoto(null);
                RefreshTally();
                RebuildFolderViews();
                return true;
            }

            RefreshTally();
            RebuildFolderViews();

            // Staying at the same position lands on the photo that followed the deleted one, so a
            // run of unwanted frames clears without moving the hand. At the end, step back.
            //
            // force: the index usually has not changed, but the photo at it has.
            SetActiveIndex(Math.Min(at, Items.Count - 1), force: true);
            return true;
        }

        /// <summary>
        /// Puts a restored photo back at the position it held (PRD 1.9). Called only after the
        /// file itself has been recovered from the Recycle Bin.
        ///
        /// The cursor lands on it, which is deliberate: an undo the user cannot see is
        /// indistinguishable from an undo that did not happen.
        /// </summary>
        private void ReinsertPhoto(FilmstripItemViewModel item, int index)
        {
            var at = Math.Clamp(index, 0, Items.Count);

            Items.Insert(at, item);
            for (var i = at; i < Items.Count; i++) Items[i].Index = i;

            RefreshTally();
            RebuildFolderViews();

            IsEmpty = false;
            SetActiveIndex(at, force: true);
        }

        /// <summary>Rebuilds the sidebar's format and folder views from the current sequence.</summary>
        private void RebuildFolderViews()
        {
            var photos = Items.Select(i => i.Photo).ToList();

            Sidebar.UpdateFormats(photos.Select(p => (p.FileName, p.Family)));
            Sidebar.UpdateFolderTree(
                string.IsNullOrEmpty(CurrentFolder)
                    ? string.Empty
                    : Path.GetFileName(CurrentFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                photos.Select((p, i) => new FolderTreeEntry(p.RelativePath, i)));
        }

        // FindDefaultSampleImagesRoot is deliberately gone. Startup used to walk up from the
        // executable looking for a "SampleImages" folder and open whatever it found, which meant
        // the app had a hardcoded root and no way to open anything else. PRD 1.1.1 replaces it:
        // the only folders that ever open are ones the user picked, and SampleImages is now
        // reachable only by selecting it like any other folder.
    }
}
