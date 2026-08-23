using System;
using Fastcull.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Fastcull.Converters
{
    /// <summary>
    /// Palette for the "Chromeless" look from the Design/ handoff. State is read from a weight
    /// bar under the photo and a thin accent tick above it - never from a border around it, which
    /// is the whole point of the direction ("the interface disappears and the pictures carry the
    /// screen").
    ///
    /// Brushes are static and shared rather than allocated per item: the filmstrip realises these
    /// converters once per visible thumbnail on every scroll, and a fresh SolidColorBrush per call
    /// would churn the UI thread for no benefit.
    /// </summary>
    internal static class ChromelessPalette
    {
        // Nocturne tokens. Duplicated from Themes/Nocturne.xaml because a converter cannot
        // resolve a StaticResource; the XAML dictionary remains the reference copy.
        public static readonly SolidColorBrush Accent = new(Color.FromArgb(0xFF, 0x91, 0x84, 0xD9));
        public static readonly SolidColorBrush Accent200 = new(Color.FromArgb(0xFF, 0xE7, 0xE5, 0xFE));
        public static readonly SolidColorBrush Accent400 = new(Color.FromArgb(0xFF, 0xB5, 0xAB, 0xFC));
        public static readonly SolidColorBrush Neutral600 = new(Color.FromArgb(0xFF, 0x75, 0x79, 0x8C));
        public static readonly SolidColorBrush Neutral800 = new(Color.FromArgb(0xFF, 0x3F, 0x42, 0x4D));
        public static readonly SolidColorBrush Pick = new(Color.FromArgb(0xFF, 0x7F, 0xAE, 0x8E));
        public static readonly SolidColorBrush Reject = new(Color.FromArgb(0xFF, 0xB8, 0x75, 0x6B));
        public static readonly SolidColorBrush Transparent = new(Colors.Transparent);

        /// <summary>Flag colour, or null for an unflagged photo (which has no flag colour at all).</summary>
        public static SolidColorBrush? FlagBrushOrNull(Flag flag) => flag switch
        {
            Flag.Picked => Pick,
            Flag.Rejected => Reject,
            _ => null,
        };
    }

    /// <summary>
    /// The weight bar under each stage photo: neutral-800 when unrated, otherwise the flag
    /// colour. Unlike the thumbnail bar below, this one is always drawn - an unrated photo still
    /// shows a neutral bar, so the bar's absence never has to be distinguished from a dark colour.
    /// </summary>
    public sealed class CullStateToWeightBarBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is CullState state
                ? ChromelessPalette.FlagBrushOrNull(state.Flag) ?? ChromelessPalette.Neutral800
                : ChromelessPalette.Neutral800;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// The 2px mark flush to a filmstrip thumbnail's bottom edge: accent when active, the flag
    /// colour when flagged, and genuinely nothing when an inactive photo is unrated - the strip
    /// stays quiet until the photographer has actually said something about a frame.
    /// </summary>
    public sealed class ThumbnailMarkBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isActive && isActive) return ChromelessPalette.Accent;
            if (value is CullState state) return ChromelessPalette.FlagBrushOrNull(state.Flag) ?? ChromelessPalette.Transparent;
            return ChromelessPalette.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    // ActiveSlotToTickBrushConverter and ActiveSlotToCaptionBrushConverter lived here and keyed
    // off MainViewModel.ActiveSlot with the slot index as a ConverterParameter. With a variable
    // number of slots there is no fixed index to pass, and the item already knows whether it is
    // active - so both now take that bool directly.

    /// <summary>Accent tick above the active stage photo; nothing above the others.</summary>
    public sealed class BoolToAccentBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? ChromelessPalette.Accent : ChromelessPalette.Transparent;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>Active cell's filename brightens to accent-200; the others stay neutral-600.</summary>
    public sealed class BoolToCaptionBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? ChromelessPalette.Accent200 : ChromelessPalette.Neutral600;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>Shows an element only in the active slot - used for the rotate buttons.</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>Inactive thumbnails sit back to 0.42 opacity; the active one is fully lit.</summary>
    public sealed class BoolToThumbnailOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? 1.0 : 0.42;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    // BoolToThumbnailHeightConverter lived here. Thumbnail slot height moved onto
    // FilmstripItemViewModel as ThumbnailSlotHeight, because rotation made the image's own
    // width and height depend on that same value - keeping the height in a converter would have
    // meant three bindings deriving the same number from three different places.
}
