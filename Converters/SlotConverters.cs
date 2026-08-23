using System;
using Fastcull.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Fastcull.Converters
{
    // ActiveSlotToBrushConverter and BoolToActiveRingBrushConverter lived here and drew the blue
    // active ring described by PRD 1.5. The Design/ handoff's "Chromeless" direction removes every
    // border, so both are gone; active-ness now reads as the accent tick and thumbnail mark in
    // ChromelessConverters.cs.

    /// <summary>
    /// Collapses a slot's chrome entirely when the slot is empty (PRD 1.5 / E.3).
    ///
    /// This also makes the slots' nested-path visibility bindings safe. x:Bind does not
    /// null-propagate a value-type leaf, so ViewModel.SlotNItem.IsStarBadgeVisible yields
    /// default(Visibility) - i.e. Visible - when the slot is empty. Because every such element
    /// lives inside the Grid this converter governs, an empty slot renders nothing regardless.
    /// </summary>
    public sealed class ItemToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is not null ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
