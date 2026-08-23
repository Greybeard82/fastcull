using System;
using System.Collections.Generic;
using System.ComponentModel;
using Fastcull.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Fastcull.Views
{
    /// <summary>
    /// Two independently-controlled filmstrip regions sharing one active-index cursor:
    /// a 3-slot previous/active/next stage that recenters nothing on its own, and a full
    /// scrollable thumbnail strip whose scroll position only ever moves in response to a
    /// direct pointer action here in the View - never as a side effect of a ViewModel change.
    ///
    /// Visual direction is "Chromeless" from the Design/ handoff: no borders, state as a weight
    /// bar under each photo. See FilmstripView.xaml.
    /// </summary>
    public sealed partial class FilmstripView : UserControl
    {
        private const double ThumbWidth = 150;
        private const double ThumbSpacing = 8;

        // Stage geometry. Column spacing and padding are NOT duplicated here on purpose: they
        // are declared once in Themes/Nocturne.xaml, applied by the XAML, and read back off the
        // live Grid below. A private copy of the spacing would silently drift out of sync with
        // the markup and size every photo against a cell width that does not exist.
        private const double TickHeight = 2;
        private const double TickMarginBottom = 2;
        private const double CellSpacing = 12;
        private const double WeightBarHeight = 3;
        private const double CaptionHeight = 14;   // 10px type, measured line box
        private const double TickWidthFraction = 0.18;

        /// <summary>Vertical space in a cell that is not the photo, so the photo gets the rest.</summary>
        private const double CellChromeHeight =
            TickHeight + TickMarginBottom + (CellSpacing * 3) + WeightBarHeight + CaptionHeight;

        private bool _pointerDown;
        private bool _dragging;
        private Point _dragStart;
        private double _dragStartOffset;

        /// <summary>Items currently subscribed for AspectRatio changes, so they can be unhooked.</summary>
        private readonly List<FilmstripItemViewModel> _observedSlotItems = new();

        public MainViewModel ViewModel { get; } = new();

        public FilmstripView()
        {
            InitializeComponent();
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private async void FilmstripView_Loaded(object sender, RoutedEventArgs e)
        {
            Focus(FocusState.Programmatic);
            await ViewModel.LoadAsync();
        }

        // Keyboard is handled once at window level (MainWindow.RootGrid_PreviewKeyDown), per
        // PRD 2.2 and 2.4. Handling KeyDown here failed as soon as focus moved into the bottom
        // ScrollViewer, which consumed arrow keys before this control ever saw them.

        // ------------------------------------------------------------------
        // Equal-height rule
        // ------------------------------------------------------------------

        private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateStageLayout();

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.Slot0Item)
                or nameof(MainViewModel.Slot1Item)
                or nameof(MainViewModel.Slot2Item))
            {
                ObserveSlotItems();
                UpdateStageLayout();
            }
        }

        /// <summary>
        /// Re-subscribes to exactly the three items currently on stage. Their AspectRatio is not
        /// known until their display decode lands, and the shared height depends on it, so the
        /// stage has to re-measure when it arrives.
        /// </summary>
        private void ObserveSlotItems()
        {
            foreach (var item in _observedSlotItems)
                item.PropertyChanged -= SlotItem_PropertyChanged;
            _observedSlotItems.Clear();

            foreach (var item in new[] { ViewModel.Slot0Item, ViewModel.Slot1Item, ViewModel.Slot2Item })
            {
                if (item is null || _observedSlotItems.Contains(item)) continue;
                item.PropertyChanged += SlotItem_PropertyChanged;
                _observedSlotItems.Add(item);
            }
        }

        private void SlotItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Rotation as well as AspectRatio: a quarter turn changes the shape the stage is
            // laid out against, so the shared height has to be recomputed for all three slots -
            // rotating one photo can resize its neighbours, because the rule sizes to the widest.
            if (e.PropertyName is nameof(FilmstripItemViewModel.AspectRatio)
                or nameof(FilmstripItemViewModel.Rotation)
                or nameof(FilmstripItemViewModel.EffectiveAspectRatio))
            {
                UpdateStageLayout();
            }
        }

        /// <summary>
        /// The handoff's load-bearing rule: one shared height across all three photos, so they
        /// line up regardless of differing aspect, and no photo is ever cropped.
        ///
        ///     sharedHeight = min(availableCellHeight, availableCellWidth / widestVisibleAspect)
        ///
        /// Each photo then takes that exact height and derives its own width from its own aspect,
        /// so a portrait frame sits narrow beside a landscape one at identical height. Sizing by
        /// the WIDEST visible aspect is what guarantees the widest photo still fits its cell
        /// horizontally - size by anything narrower and it overflows.
        /// </summary>
        private void UpdateStageLayout()
        {
            var stageWidth = Stage.ActualWidth;
            var stageHeight = Stage.ActualHeight;
            if (stageWidth <= 0 || stageHeight <= 0) return;

            // Read the spacing the Grid actually applied rather than a local constant, so the
            // cell width is always derived from the layout that is really on screen.
            var cellWidth = StageLayout.ComputeCellWidth(stageWidth, Stage.ColumnSpacing, columnCount: 3);
            var cellHeight = stageHeight - CellChromeHeight;
            if (cellWidth <= 0 || cellHeight <= 0) return;

            var aspects = new List<double>(3);
            foreach (var item in new[] { ViewModel.Slot0Item, ViewModel.Slot1Item, ViewModel.Slot2Item })
            {
                // EffectiveAspectRatio, not AspectRatio: a quarter-turned photo presents as
                // portrait and must be sized as portrait.
                if (item is not null) aspects.Add(item.EffectiveAspectRatio);
            }

            // The rule itself lives in Fastcull.Core so it can be unit-tested against the
            // portrait/mixed-aspect cases the all-landscape sample corpus never produces.
            var sharedHeight = StageLayout.ComputeSharedHeight(cellWidth, cellHeight, aspects);
            if (sharedHeight <= 0) return;

            ApplySlot(ViewModel.Slot0Item, Slot0Frame, Slot0Image, Slot0Rotate, Slot0Tick, Slot0Bar, sharedHeight);
            ApplySlot(ViewModel.Slot1Item, Slot1Frame, Slot1Image, Slot1Rotate, Slot1Tick, Slot1Bar, sharedHeight);
            ApplySlot(ViewModel.Slot2Item, Slot2Frame, Slot2Image, Slot2Rotate, Slot2Tick, Slot2Bar, sharedHeight);
        }

        /// <summary>
        /// Sizes one slot's photo, tick and weight bar.
        ///
        /// Rotation is applied as a render transform about the image's centre, but a render
        /// transform does not affect layout - left alone, a turned photo would still OCCUPY its
        /// un-rotated box, overlap its neighbours, and leave the tick and bar sized to the wrong
        /// width. So the layout box and the image are sized separately:
        ///
        ///   frame  - the post-rotation box: sharedHeight tall, and as wide as the rotated
        ///            aspect makes it. This is what participates in layout, and what the tick
        ///            (18%) and weight bar (100%) are measured against.
        ///   image  - the PRE-rotation box, i.e. the frame with width and height swapped on a
        ///            quarter turn. Rotating that about its centre yields a bounding box exactly
        ///            equal to the frame, so the photo lands flush inside it, uncropped.
        ///
        /// Re-decoding to bake in the rotation would be the obvious alternative and is the wrong
        /// one: it would spend a decode on a transform, blow the PRD 3.5 keypress budget, and
        /// invalidate cache entries once PRD 3.3 exists.
        /// </summary>
        private static void ApplySlot(
            FilmstripItemViewModel? item,
            FrameworkElement frame,
            Image image,
            RotateTransform rotate,
            FrameworkElement tick,
            FrameworkElement bar,
            double sharedHeight)
        {
            if (item is null) return;

            var frameWidth = StageLayout.PhotoWidth(sharedHeight, item.EffectiveAspectRatio);
            if (frameWidth <= 0) return;

            frame.Width = frameWidth;
            frame.Height = sharedHeight;

            var swaps = item.Rotation.SwapsAspect;
            image.Width = swaps ? sharedHeight : frameWidth;
            image.Height = swaps ? frameWidth : sharedHeight;
            rotate.Angle = item.RotationAngle;

            // The image lives in a Canvas, and is positioned here rather than by alignment,
            // because a Canvas is the only panel that measures its children with infinite space.
            // Inside an ordinary Grid, WinUI clamps a child's DesiredSize to the space available,
            // so at 90 degrees the image's swapped width (the full shared height) was being cut
            // down to the frame's much narrower width - measured, it rendered 206x204 instead of
            // 206x311. Centring it here puts its rotation origin at the frame's centre, so the
            // rotated bounding box lands exactly on the frame.
            Canvas.SetLeft(image, (frameWidth - image.Width) / 2);
            Canvas.SetTop(image, (sharedHeight - image.Height) / 2);

            // The tick spans 18% of the photo's rendered width; the weight bar spans all of it.
            tick.Width = Math.Max(1, frameWidth * TickWidthFraction);
            bar.Width = Math.Max(1, frameWidth);
        }

        // ------------------------------------------------------------------
        // Pointer input
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // Rotate buttons (PRD 1.11)
        // ------------------------------------------------------------------

        /// <summary>
        /// The rotate buttons sit under the centre slot but act on the ACTIVE item, which is a
        /// different photo at the sequence boundaries (PRD 1.5 moves the active marker to an end
        /// slot at the first and last photo). They therefore call the same MainViewModel entry
        /// points the A/S keys do, and are never wired to "slot 1's item".
        ///
        /// Focus is returned to this control afterwards. Keyboard is owned at window level by
        /// RootGrid_PreviewKeyDown, which tunnels from the root down and so fires wherever focus
        /// sits - but this project has already been bitten once by focus moving into a child that
        /// swallowed arrow keys, so the buttons are IsTabStop="False" and focus is handed back
        /// explicitly rather than left to chance.
        /// </summary>
        /// <summary>
        /// Stops a tap on the rotate buttons from bubbling up to the slot's own Tapped handler.
        ///
        /// Without this the buttons are inside slot 1's tap target, so clicking one rotated the
        /// active photo AND selected slot 1 - measured: the position counter moved from 1/100 to
        /// 2/100 on a rotate click. Rotation must move nothing. Handling Tapped here does not
        /// affect Button.Click, which is raised from the Button's own pointer handling.
        /// </summary>
        private void RotateButtons_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

        private void RotateLeft_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.RotateActiveLeft();
            Focus(FocusState.Programmatic);
        }

        private void RotateRight_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.RotateActiveRight();
            Focus(FocusState.Programmatic);
        }

        /// <summary>Clicking any of the three stage slots makes that photo active (PRD E.3).</summary>
        private void Slot_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tagText }) return;
            if (!int.TryParse(tagText, out var slot)) return;

            var item = slot switch
            {
                0 => ViewModel.Slot0Item,
                1 => ViewModel.Slot1Item,
                2 => ViewModel.Slot2Item,
                _ => null,
            };

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
