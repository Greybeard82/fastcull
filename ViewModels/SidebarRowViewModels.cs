using CommunityToolkit.Mvvm.ComponentModel;
using Fastcull.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Fastcull.ViewModels
{
    /// <summary>
    /// One folder row in the sidebar's tree. Flat rather than nested: the panel renders a single
    /// indented list rather than a WinUI TreeView, because TreeView brings its own chrome and
    /// selection brushes that would have to be fought back to black one by one (PRD 1.10), and an
    /// indent plus a chevron is the whole of what the control was going to give us.
    /// </summary>
    public partial class FolderRowViewModel : ObservableObject
    {
        private const double IndentPerLevel = 12;

        public FolderRowViewModel(FolderNode node, bool isExpanded, bool isCurrent)
        {
            Node = node;
            _isExpanded = isExpanded;
            _isCurrent = isCurrent;
        }

        public FolderNode Node { get; }

        public string Name => Node.Name;

        /// <summary>Everything beneath the folder, which is what clicking it navigates into.</summary>
        public string CountText => Node.TotalPhotoCount.ToString("N0");

        /// <summary>Depth as a left inset. The root sits flush; each level steps in.</summary>
        public Thickness Indent => new(Node.Depth * IndentPerLevel, 0, 0, 0);

        /// <summary>A leaf reserves the chevron's width so its label still lines up with its siblings.</summary>
        public Visibility ChevronVisibility => Node.HasChildren ? Visibility.Visible : Visibility.Collapsed;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ChevronGlyph))]
        private bool _isExpanded;

        /// <summary>Segoe Fluent chevrons: down when open, right when closed.</summary>
        public string ChevronGlyph => IsExpanded ? "" : "";

        /// <summary>
        /// True for the folder holding the active photo. This is what makes a non-filtering tree
        /// worth having: it answers "where am I?" without changing the sequence.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NameBrush))]
        private bool _isCurrent;

        /// <summary>
        /// Resolved from the theme dictionary rather than hardcoded, so the Nocturne ramp stays
        /// the single source of colour. Same approach as the pin glyph's brush.
        /// </summary>
        public Brush NameBrush
            => (Brush)Application.Current.Resources[IsCurrent ? "Accent200Brush" : "Neutral500Brush"];

        /// <summary>False when the subtree holds no photos, so there is nowhere to navigate to.</summary>
        public bool CanNavigate => Node.FirstPhotoIndex >= 0;
    }

    /// <summary>One format row: a label, a count, and a bar scaled against the largest count.</summary>
    public sealed class FormatRowViewModel
    {
        private const double TrackWidth = 104;

        public FormatRowViewModel(FormatCount count, int max)
        {
            Label = count.Label;
            CountText = count.Count.ToString("N0");

            // Scaled to the largest format, not to the total - the same reasoning as the star
            // histogram. A card of 2,000 RAW and 3 JPEG would otherwise draw the JPEG bar at
            // zero, which tells the photographer nothing about there being any.
            BarWidth = max <= 0 || count.Count <= 0
                ? 0
                : System.Math.Max(2, TrackWidth * count.Count / max);
        }

        public string Label { get; }
        public string CountText { get; }
        public double BarWidth { get; }
    }
}
