using System;
using Fastcull.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Fastcull.Converters
{
    /// <summary>
    /// Binds directly to a (possibly null) FilmstripItemViewModel rather than its nested
    /// ThumbnailFailed bool - x:Bind's implicit bool-to-Visibility conversion does not
    /// null-propagate correctly through a nested value-type leaf when the root of the path
    /// (e.g. MainViewModel.PreviousItem at the start of the sequence) is null.
    /// </summary>
    public sealed class ThumbnailFailedToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is FilmstripItemViewModel { ThumbnailFailed: true } ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
