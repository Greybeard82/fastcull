using System;
using Fastcull.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Fastcull.Views
{
    /// <summary>
    /// Two independently-controlled filmstrip regions sharing one active-index cursor:
    /// a 3-slot previous/active/next strip that recenters nothing on its own, and a full
    /// scrollable thumbnail strip whose scroll position only ever moves in response to a
    /// direct pointer action here in the View - never as a side effect of a ViewModel change.
    /// </summary>
    public sealed partial class FilmstripView : UserControl
    {
        private const double ThumbWidth = 140;
        private const double ThumbSpacing = 8;

        private bool _pointerDown;
        private bool _dragging;
        private Point _dragStart;
        private double _dragStartOffset;

        public MainViewModel ViewModel { get; } = new();

        public FilmstripView()
        {
            InitializeComponent();
        }

        private async void FilmstripView_Loaded(object sender, RoutedEventArgs e)
        {
            Focus(FocusState.Programmatic);
            await ViewModel.LoadAsync();
        }

        private void FilmstripView_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Left)
            {
                ViewModel.MovePrevious();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Right)
            {
                ViewModel.MoveNext();
                e.Handled = true;
            }
        }

        private void PreviousSlot_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var item = ViewModel.PreviousItem;
            if (item is not null) ViewModel.SetActiveIndex(item.Index);
        }

        private void NextSlot_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var item = ViewModel.NextItem;
            if (item is not null) ViewModel.SetActiveIndex(item.Index);
        }

        private void Thumbnail_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // ItemsRepeater does not propagate DataContext to realized elements when the
            // DataTemplate uses x:Bind/x:DataType, so the item must be resolved through the
            // repeater itself rather than through sender.DataContext (which is always null here).
            if (sender is not UIElement element) return;
            var index = ThumbRepeater.GetElementIndex(element);
            if (index < 0) return;

            ViewModel.SetActiveIndex(index);
            CenterOn(index);
        }

        private void CenterOn(int index)
        {
            var viewport = BottomScroll.ActualWidth;
            if (viewport <= 0) return;

            var itemCenter = index * (ThumbWidth + ThumbSpacing) + ThumbWidth / 2;
            var target = Math.Max(0, itemCenter - viewport / 2);
            BottomScroll.ChangeView(target, null, null, false);
        }

        private void BottomScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var delta = e.GetCurrentPoint(BottomScroll).Properties.MouseWheelDelta;
            BottomScroll.ChangeView(BottomScroll.HorizontalOffset - delta, null, null, true);
            e.Handled = true;
        }

        private void BottomScroll_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;

            _pointerDown = true;
            _dragging = false;
            _dragStart = e.GetCurrentPoint(BottomScroll).Position;
            _dragStartOffset = BottomScroll.HorizontalOffset;
        }

        private void BottomScroll_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerDown) return;

            var pos = e.GetCurrentPoint(BottomScroll).Position;
            var dx = pos.X - _dragStart.X;

            if (!_dragging && Math.Abs(dx) > 4)
            {
                _dragging = true;
                BottomScroll.CapturePointer(e.Pointer);
            }

            if (_dragging)
            {
                BottomScroll.ChangeView(_dragStartOffset - dx, null, null, true);
                e.Handled = true;
            }
        }

        private void BottomScroll_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_dragging) BottomScroll.ReleasePointerCapture(e.Pointer);
            _pointerDown = false;
            _dragging = false;
        }
    }
}
