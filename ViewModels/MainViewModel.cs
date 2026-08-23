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
        private int _activeIndex = -1;

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
                .ThenBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase);

            Items.Clear();
            var index = 0;
            foreach (var photo in sorted)
            {
                Items.Add(new FilmstripItemViewModel(photo, index));
                index++;
            }

            SetActiveIndex(Items.Count > 0 ? 0 : -1);
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
            }
        }

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
