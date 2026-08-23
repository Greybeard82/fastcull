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

        [ObservableProperty]
        private FilmstripItemViewModel? _slot0Item;

        [ObservableProperty]
        private FilmstripItemViewModel? _slot1Item;

        [ObservableProperty]
        private FilmstripItemViewModel? _slot2Item;

        /// <summary>0, 1 or 2 - which of the three slots holds the active photo; -1 when empty.</summary>
        [ObservableProperty]
        private int _activeSlot = -1;

        [ObservableProperty]
        private FilmstripItemViewModel? _activeItem;

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
            Dictionary<string, CullState> stored = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                _sessionStore = await SessionStore.OpenAsync(root);
                await _sessionStore.RegisterPhotosAsync(sorted);
                stored = await _sessionStore.LoadRatingsAsync();
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
                if (stored.TryGetValue(photo.FilePath, out var state)) item.CullState = state;
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
        /// Recomputes all three slots from ActiveIndex - never carried forward incrementally, so
        /// the window is guaranteed correct after any jump, not just a +/-1 step (PRD 1.5, E.3).
        /// </summary>
        private void RecomputeSlots()
        {
            var window = FilmstripWindow.Compute(ActiveIndex, Items.Count);
            ActiveSlot = window.ActiveSlot;

            Slot0Item = ItemAtOrNull(window.WindowStart + 0);
            Slot1Item = ItemAtOrNull(window.WindowStart + 1);
            Slot2Item = ItemAtOrNull(window.WindowStart + 2);
        }

        private FilmstripItemViewModel? ItemAtOrNull(int index)
            => index >= 0 && index < Items.Count ? Items[index] : null;

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
            }
        }

        /// <summary>Rotates the active photo 90 degrees clockwise (PRD 1.11).</summary>
        public void RotateActiveRight() => ApplyRotation(r => r.RotateRight());

        /// <summary>Rotates the active photo 90 degrees counter-clockwise (PRD 1.11).</summary>
        public void RotateActiveLeft() => ApplyRotation(r => r.RotateLeft());

        /// <summary>
        /// Applies a quarter turn to the active item only. Synchronous and awaits nothing, so the
        /// photo turns within one frame - PRD 3.5 budgets this at the same &lt; 16 ms as a rating
        /// keypress, on the same reasoning: it is a transform, never a re-decode.
        ///
        /// Rotation moves no cursor and changes no rating, exactly as ApplyRating changes no
        /// cursor and no rotation. The two are entirely independent axes.
        /// </summary>
        private void ApplyRotation(Func<Rotation, Rotation> transition)
        {
            var item = ActiveItem;
            if (item is null) return;

            var updated = transition(item.Rotation);
            if (updated == item.Rotation) return;

            item.Rotation = updated;

            RotationChanged?.Invoke(item);
        }

        /// <summary>Raised after the active item's Rotation changes.</summary>
        public event Action<FilmstripItemViewModel>? RotationChanged;

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
