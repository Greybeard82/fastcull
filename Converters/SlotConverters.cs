using System;
using Fastcull.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Fastcull.Converters
{
    /// <summary>
    /// Blue active ring for the slot whose index matches ActiveSlot, transparent otherwise
    /// (PRD 1.5). Pass the slot index 0/1/2 as ConverterParameter.
    /// </summary>
    public sealed class ActiveSlotToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush ActiveBrush = new(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
        private static readonly SolidColorBrush InactiveBrush = new(Colors.Transparent);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not int activeSlot) return InactiveBrush;
            if (parameter is null || !int.TryParse(parameter.ToString(), out var slotIndex)) return InactiveBrush;
            return activeSlot == slotIndex ? ActiveBrush : InactiveBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>Blue active ring for a bottom-filmstrip thumbnail, transparent when inactive.</summary>
    public sealed class BoolToActiveRingBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush ActiveBrush = new(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
        private static readonly SolidColorBrush InactiveBrush = new(Colors.Transparent);

        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? ActiveBrush : InactiveBrush;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Star-badge visibility for a slot. Binds the item itself rather than its Visibility
    /// property because x:Bind does not null-propagate a value-type leaf when the root of the
    /// path is null - an empty slot would otherwise show a badge.
    /// </summary>
    public sealed class ItemToStarBadgeVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is FilmstripItemViewModel item && item.CullState.Stars >= 1
                ? Visibility.Visible
                : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>Collapses a slot's chrome entirely when the slot is empty (PRD 1.5 / E.3).</summary>
    public sealed class ItemToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is not null ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
