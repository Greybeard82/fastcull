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
            }
        }

        private SessionStore? _sessionStore;

        public async Task LoadAsync()
        {
            var root = FindDefaultSampleImagesRoot();
            var scanner = new DirectoryScanner();
            var scanned = new List<ScannedPhoto>();

            await foreach (var photo in scanner.ScanAsync(root))
            {
                scanned.Add(photo);
            }

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

            SetActiveIndex(Items.Count > 0 ? 0 : -1);
        }

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
        }

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

            RatingChanged?.Invoke(item);
        }

        /// <summary>Raised after the active item's CullState changes, so persistence can observe it.</summary>
        public event Action<FilmstripItemViewModel>? RatingChanged;

        private static string FindDefaultSampleImagesRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "SampleImages");
                if (Directory.Exists(candidate)) return candidate;
            }

            return Directory.GetCurrentDirectory();
        }
    }
}
