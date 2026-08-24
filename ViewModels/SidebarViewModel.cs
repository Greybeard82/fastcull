using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Fastcull.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Fastcull.ViewModels
{
    /// <summary>
    /// The left sidebar of PRD 1.5: folder identity, file counts, live rating tallies.
    ///
    /// Owned by <see cref="MainViewModel"/>, which pushes a fresh tally in whenever a rating
    /// changes. Kept separate from MainViewModel rather than folded into it because this is panel
    /// state - what is shown, whether it is pinned - and mixing that into the class that owns the
    /// photo sequence and the cursor is how MainViewModel becomes a god object.
    ///
    /// First pass. PRD 1.5 also lists a folder tree, a format breakdown and Finish Session; none
    /// of those are here. Finish Session in particular is deliberately a disabled placeholder -
    /// PRD 4.1's modal and the whole batch-export path do not exist in the codebase.
    /// </summary>
    public partial class SidebarViewModel : ObservableObject
    {
        /// <summary>
        /// Panel width. Also the width of the gutter the stage gives up when pinned, so the two
        /// cannot drift apart - see MainWindow.xaml.
        /// </summary>
        public const double PanelWidth = 232;

        /// <summary>Width of the histogram's longest bar, at the panel's current padding.</summary>
        private const double HistogramTrackWidth = 104;

        // ------------------------------------------------------------------
        // Visibility and pinning
        // ------------------------------------------------------------------

        /// <summary>
        /// Pinned keeps the panel on screen and hands it a permanent gutter, so it never covers a
        /// photo. Session-only by design: PRD 3.1's session database stores per-photo state, and
        /// putting a UI preference in it would mean a schema migration for a toggle.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsShown))]
        [NotifyPropertyChangedFor(nameof(Visibility))]
        [NotifyPropertyChangedFor(nameof(GutterWidth))]
        [NotifyPropertyChangedFor(nameof(PinGlyph))]
        [NotifyPropertyChangedFor(nameof(PinTooltip))]
        [NotifyPropertyChangedFor(nameof(PinBrush))]
        private bool _isPinned;

        /// <summary>Pointer is over the panel or its left-edge hot zone.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsShown))]
        [NotifyPropertyChangedFor(nameof(Visibility))]
        private bool _isHovered;

        /// <summary>
        /// Pinned or hovered keeps the panel up; so does a scan slow enough to be worth watching.
        ///
        /// The scan case exists because PRD 1.2 puts the progress pill in this panel, and a panel
        /// that auto-hides would show it to nobody. It is deliberately gated on the scan having
        /// already run for a moment (see MainViewModel) rather than on the scan starting - a
        /// hundred-file folder scans in about 200 ms, and revealing the panel for that long is a
        /// flash at startup rather than information.
        /// </summary>
        public bool IsShown => IsPinned || IsHovered || IsScanRevealed;

        public Visibility Visibility => IsShown ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// What the stage gives up. Zero when unpinned - the panel then overlays rather than
        /// reflowing, so a photo being compared never resizes just because the pointer drifted
        /// left. See the note in MainWindow.xaml.
        /// </summary>
        public double GutterWidth => IsPinned ? PanelWidth : 0;

        /// <summary>Filled pin when engaged, outline when not. Segoe Fluent Icons.</summary>
        public string PinGlyph => IsPinned ? "" : "";

        public string PinTooltip => IsPinned ? "Unpin sidebar" : "Pin sidebar open";

        /// <summary>
        /// Accent when engaged, muted when not - the glyph swap alone is a small target to read at
        /// 13px. Resolved from the theme dictionary rather than hardcoded so the Nocturne ramp
        /// stays the single source of colour.
        /// </summary>
        public Brush PinBrush => (Brush)Application.Current.Resources[IsPinned ? "AccentBrush" : "Neutral700Brush"];

        public void TogglePin() => IsPinned = !IsPinned;

        // ------------------------------------------------------------------
        // Folder identity
        // ------------------------------------------------------------------

        [ObservableProperty]
        private string _folderName = string.Empty;

        [ObservableProperty]
        private string _folderPath = string.Empty;

        /// <summary>
        /// Raised when the change-folder control is activated (PRD 1.1.1). The View owns the
        /// picker, because it needs the window handle; this only asks for one.
        /// </summary>
        public event Action? ChangeFolderRequested;

        public void RequestChangeFolder() => ChangeFolderRequested?.Invoke();

        public void SetFolder(string? root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                FolderName = string.Empty;
                FolderPath = string.Empty;
                return;
            }

            FolderPath = root;

            // TrimEnd first: GetFileName on a path with a trailing separator returns empty, which
            // would leave the panel captioned with nothing on a perfectly valid folder.
            var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            FolderName = string.IsNullOrEmpty(name) ? trimmed : name;
        }

        // ------------------------------------------------------------------
        // Live tallies
        // ------------------------------------------------------------------

        private CullTally _tally = CullTally.Empty;

        public int TotalCount => _tally.Total;
        public int PickedCount => _tally.Picked;
        public int RejectedCount => _tally.Rejected;
        public int UnflaggedCount => _tally.Unflagged;

        public string TotalText => _tally.Total.ToString("N0");
        public string PickedText => _tally.Picked.ToString("N0");
        public string RejectedText => _tally.Rejected.ToString("N0");
        public string UnflaggedText => _tally.Unflagged.ToString("N0");

        /// <summary>
        /// e.g. "12 of 100 decided" - the one line that says how far the cull has got. Blank while
        /// a scan is running: the pill is already saying what is happening, and "No photos" next
        /// to "1,957 files found" reads as a contradiction rather than as a sequence not built yet.
        /// </summary>
        public string ProgressText => IsScanRevealed
            ? string.Empty
            : _tally.Total == 0
                ? "No photos"
                : $"{_tally.Decided:N0} of {_tally.Total:N0} decided";

        public string Star1Text => _tally.StarCount(1).ToString("N0");
        public string Star2Text => _tally.StarCount(2).ToString("N0");
        public string Star3Text => _tally.StarCount(3).ToString("N0");
        public string Star4Text => _tally.StarCount(4).ToString("N0");
        public string Star5Text => _tally.StarCount(5).ToString("N0");

        public double Star1BarWidth => BarWidth(1);
        public double Star2BarWidth => BarWidth(2);
        public double Star3BarWidth => BarWidth(3);
        public double Star4BarWidth => BarWidth(4);
        public double Star5BarWidth => BarWidth(5);

        /// <summary>
        /// Bar length relative to the tallest bar, not to the total: a folder with three 5-star
        /// photos out of two thousand would otherwise render five bars all indistinguishable from
        /// zero, which tells the photographer nothing.
        /// </summary>
        private double BarWidth(int stars)
        {
            var max = _tally.MaxStarCount;
            if (max <= 0) return 0;

            var count = _tally.StarCount(stars);
            if (count <= 0) return 0;

            // Floor at 2px so a level with a real, nonzero count never renders as nothing.
            return Math.Max(2, HistogramTrackWidth * count / max);
        }

        // ------------------------------------------------------------------
        // Active Photo panel (PRD 1.5)
        // ------------------------------------------------------------------

        /// <summary>
        /// The photo the cursor is on. The panel binds straight through to it rather than copying
        /// its fields, so the sidebar and the on-photo overlay (PRD 1.8.1) are literally reading
        /// the same properties and cannot drift apart - including a place name that arrives after
        /// the photo did.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ActivePhotoVisibility))]
        private FilmstripItemViewModel? _activePhoto;

        public Visibility ActivePhotoVisibility => ActivePhoto is null ? Visibility.Collapsed : Visibility.Visible;

        public void SetActivePhoto(FilmstripItemViewModel? item) => ActivePhoto = item;

        // ------------------------------------------------------------------
        // Scan progress (PRD 1.2)
        // ------------------------------------------------------------------

        /// <summary>Set by MainViewModel once a scan has run long enough to be worth showing.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsShown))]
        [NotifyPropertyChangedFor(nameof(Visibility))]
        [NotifyPropertyChangedFor(nameof(ScanPillVisibility))]
        [NotifyPropertyChangedFor(nameof(ProgressText))]
        private bool _isScanRevealed;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ScanProgressText))]
        private int _scanFoundCount;

        /// <summary>PRD 1.2's pill: "N files found", until the scan completes.</summary>
        public string ScanProgressText => $"{ScanFoundCount:N0} files found";

        public Visibility ScanPillVisibility => IsScanRevealed ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Reports scan progress. Cheap enough to call per file.</summary>
        public void ReportScanProgress(int found, bool reveal)
        {
            ScanFoundCount = found;
            if (reveal && !IsScanRevealed) IsScanRevealed = true;
        }

        /// <summary>Scan finished: the pill goes away and the panel stops being held open by it.</summary>
        public void CompleteScan() => IsScanRevealed = false;

        // ------------------------------------------------------------------
        // Format breakdown (PRD 1.5)
        // ------------------------------------------------------------------

        public ObservableCollection<FormatRowViewModel> Formats { get; } = new();

        /// <summary>e.g. "77 ARW · 20 CR2 · 4 JPG". Empty before anything is scanned.</summary>
        [ObservableProperty]
        private string _formatSummary = string.Empty;

        /// <summary>Hides the section header while there is nothing under it - during a scan, above all.</summary>
        [ObservableProperty]
        private Visibility _formatsVisibility = Visibility.Collapsed;

        public void UpdateFormats(IEnumerable<(string FileName, Fastcull.Services.FormatFamily Family)> photos)
        {
            var counts = FormatBreakdown.From(photos);
            var max = FormatBreakdown.Max(counts);

            Formats.Clear();
            foreach (var count in counts) Formats.Add(new FormatRowViewModel(count, max));

            FormatSummary = FormatBreakdown.Summarise(counts);
            FormatsVisibility = counts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ------------------------------------------------------------------
        // Folder tree (PRD 1.5)
        // ------------------------------------------------------------------

        public ObservableCollection<FolderRowViewModel> FolderRows { get; } = new();

        /// <summary>Raised when a folder row is chosen. Carries the sequence index to move to.</summary>
        public event Action<int>? FolderNavigationRequested;

        private FolderNode? _folderRoot;

        /// <summary>
        /// Which folders are open, keyed by relative path. Held here rather than on the rows so
        /// expansion survives a rebuild - the rows are recreated whenever the tree is reflattened.
        /// </summary>
        private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Relative directory of the active photo, for the "you are here" highlight.</summary>
        private string _currentFolder = string.Empty;

        /// <summary>
        /// True once the scanned folder actually has subfolders. A tree of a single node tells the
        /// user nothing they cannot read from the folder name above it, so the section hides.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FolderTreeVisibility))]
        private bool _hasFolderTree;

        public Visibility FolderTreeVisibility => HasFolderTree ? Visibility.Visible : Visibility.Collapsed;

        public void UpdateFolderTree(string rootName, IEnumerable<FolderTreeEntry> entries)
        {
            _folderRoot = FolderTree.Build(rootName, entries);

            // Open the root by default so its immediate subfolders are visible without a click;
            // deeper levels stay closed so a deep tree cannot flood a 232px panel.
            _expanded.Add(_folderRoot.RelativePath);

            HasFolderTree = _folderRoot.HasChildren;
            RebuildFolderRows();
        }

        /// <summary>Moves the "you are here" highlight. Called as the cursor moves.</summary>
        public void SetCurrentFolder(string? relativeDirectory)
        {
            var folder = relativeDirectory ?? string.Empty;
            if (string.Equals(folder, _currentFolder, StringComparison.OrdinalIgnoreCase)) return;

            _currentFolder = folder;
            foreach (var row in FolderRows)
                row.IsCurrent = IsCurrentFolder(row.Node);
        }

        public void ToggleFolder(FolderRowViewModel row)
        {
            if (row is null || !row.Node.HasChildren) return;

            if (!_expanded.Remove(row.Node.RelativePath))
                _expanded.Add(row.Node.RelativePath);

            RebuildFolderRows();
        }

        /// <summary>
        /// Moves the cursor to the first photo in this folder's subtree. Deliberately NOT a
        /// filter - see the note on <see cref="FolderNode.FirstPhotoIndex"/>.
        /// </summary>
        public void NavigateToFolder(FolderRowViewModel row)
        {
            if (row is null || !row.CanNavigate) return;
            FolderNavigationRequested?.Invoke(row.Node.FirstPhotoIndex);
        }

        private bool IsCurrentFolder(FolderNode node)
            => string.Equals(node.RelativePath, _currentFolder, StringComparison.OrdinalIgnoreCase);

        private void RebuildFolderRows()
        {
            FolderRows.Clear();
            if (_folderRoot is null) return;

            foreach (var node in FolderTree.Flatten(_folderRoot, n => _expanded.Contains(n.RelativePath)))
                FolderRows.Add(new FolderRowViewModel(node, _expanded.Contains(node.RelativePath), IsCurrentFolder(node)));
        }

        /// <summary>
        /// Recounts from the sequence and raises everything the panel binds to.
        ///
        /// Called on every rating change. The recount is O(n) over the folder - about 10 µs at
        /// 2,000 photos - which is what lets the panel be correct by construction rather than by
        /// keeping a running total in step with the ladder.
        /// </summary>
        public void Update(IEnumerable<CullState> states)
        {
            _tally = CullTally.From(states);

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(PickedCount));
            OnPropertyChanged(nameof(RejectedCount));
            OnPropertyChanged(nameof(UnflaggedCount));

            OnPropertyChanged(nameof(TotalText));
            OnPropertyChanged(nameof(PickedText));
            OnPropertyChanged(nameof(RejectedText));
            OnPropertyChanged(nameof(UnflaggedText));
            OnPropertyChanged(nameof(ProgressText));

            for (var star = 1; star <= 5; star++)
            {
                OnPropertyChanged($"Star{star}Text");
                OnPropertyChanged($"Star{star}BarWidth");
            }
        }
    }
}
