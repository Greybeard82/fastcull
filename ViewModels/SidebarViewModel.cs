using System;
using System.Collections.Generic;
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

        /// <summary>Pinned wins: a pinned panel stays up whatever the pointer does.</summary>
        public bool IsShown => IsPinned || IsHovered;

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

        /// <summary>e.g. "12 of 100 decided" - the one line that says how far the cull has got.</summary>
        public string ProgressText => _tally.Total == 0
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
