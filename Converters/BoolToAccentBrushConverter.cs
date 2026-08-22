using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Fastcull.Converters
{
    public sealed class BoolToAccentBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var isActive = value is bool b && b;
            var key = isActive ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush";
            return (Brush)Application.Current.Resources[key];
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
