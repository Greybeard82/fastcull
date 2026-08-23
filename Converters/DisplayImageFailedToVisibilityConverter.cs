using System;
using Fastcull.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Fastcull.Converters
{
    /// <summary>
    /// Mirrors ThumbnailFailedToVisibilityConverter for the top region's larger display-tier
    /// image, which decodes and fails independently of the bottom filmstrip's small thumbnail.
    /// </summary>
    public sealed class DisplayImageFailedToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is FilmstripItemViewModel { DisplayImageFailed: true } ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
