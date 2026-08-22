using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
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
        private FilmstripItemViewModel? _previousItem;

        [ObservableProperty]
        private FilmstripItemViewModel? _activeItem;

        [ObservableProperty]
        private FilmstripItemViewModel? _nextItem;

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
                PreviousItem = null;
                NextItem = null;
                return;
            }

            index = Math.Clamp(index, 0, Items.Count - 1);
            if (index == ActiveIndex) return;

            if (ActiveIndex >= 0 && ActiveIndex < Items.Count) Items[ActiveIndex].IsActive = false;
            ActiveIndex = index;
            Items[index].IsActive = true;

            ActiveItem = Items[index];
            PreviousItem = index > 0 ? Items[index - 1] : null;
            NextItem = index < Items.Count - 1 ? Items[index + 1] : null;
        }

        public void MovePrevious() => SetActiveIndex(ActiveIndex - 1);
        public void MoveNext() => SetActiveIndex(ActiveIndex + 1);

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
