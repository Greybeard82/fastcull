using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Fastcull.Converters
{
    /// <summary>
    /// Bound via x:Bind (compiled, not the reflection-based classic Binding engine) on every
    /// filmstrip item's BorderBrush, evaluated twice per navigation (old active off, new active
    /// on). Must never throw: an uncaught exception here crosses the compiled-binding ABI
    /// boundary unguarded and fail-fast-crashes the process instead of raising a catchable one.
    /// </summary>
    public sealed class BoolToAccentBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush FallbackActiveBrush = new(Colors.DodgerBlue);
        private static readonly SolidColorBrush FallbackInactiveBrush = new(Colors.Gray);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var isActive = value is bool b && b;
            var key = isActive ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush";

            if (Application.Current?.Resources is { } resources &&
                resources.TryGetValue(key, out var resource) &&
                resource is Brush brush)
            {
                return brush;
            }

            return isActive ? FallbackActiveBrush : FallbackInactiveBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
