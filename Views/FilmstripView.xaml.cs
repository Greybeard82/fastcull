using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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

        /// <summary>
        /// Must equal StageCaptionHeight in Themes/Nocturne.xaml. This was 14 - "10px type,
        /// measured line box" - but the caption row also holds the rotate buttons, which are
        /// taller than that. The layout math was therefore working from a smaller chrome height
        /// than the markup actually reserved, making every photo slightly too tall.
        /// </summary>
        private const double CaptionHeight = 22;

        /// <summary>Vertical space in a cell that is not the photo, so the photo gets the rest.</summary>
        private const double CellChromeHeight =
            TickHeight + TickMarginBottom + (CellSpacing * 3) + WeightBarHeight + CaptionHeight;

        private bool _pointerDown;
        private bool _dragging;
        private Point _dragStart;
        private double _dragStartOffset;

        /// <summary>Items currently subscribed for AspectRatio changes, so they can be unhooked.</summary>
        private readonly List<FilmstripItemViewModel> _observedSlotItems = new();

        /// <summary>Guards the count/geometry loop: committing a slot count rebuilds StageItems,
        /// whose CollectionChanged would otherwise re-enter the computation that caused it.</summary>
        private bool _updatingStageLayout;

        public MainViewModel ViewModel { get; } = new();

        public FilmstripView()
        {
            InitializeComponent();
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.StageItems.CollectionChanged += StageItems_CollectionChanged;
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

        private void StageHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateStageLayout();

        /// <summary>
        /// Runs on every navigation, whatever the source. The active photo's shape can change how
        /// many slots fit, so the stage re-measures.
        /// </summary>
        private void OnNavigated() => UpdateStageLayout();

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveIndex))
                OnNavigated();
        }

        private void StageItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            ObserveSlotItems();
            UpdateStageLayout();
        }

        /// <summary>
        /// Re-subscribes to exactly the items currently on stage. Their AspectRatio is not known
        /// until their display decode lands, and the shared height depends on it, so the stage
        /// has to re-measure when it arrives.
        /// </summary>
        private void ObserveSlotItems()
        {
            foreach (var item in _observedSlotItems)
                item.PropertyChanged -= SlotItem_PropertyChanged;
            _observedSlotItems.Clear();

            foreach (var item in ViewModel.StageItems)
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
        /// <summary>
        /// Chooses how many photos the stage shows, then sizes them all to one shared height.
        ///
        /// The two are circular - the count depends on the shapes, and which shapes are on stage
        /// depends on the count - so candidate windows are evaluated against ViewModel.Items
        /// directly, without mutating anything, and only the winner is committed.
        /// </summary>
        private void UpdateStageLayout()
        {
            if (_updatingStageLayout) return;

            // Available space comes from the host, not from the repeater: the repeater sizes to
            // its own content and centres, so its width is the photos' width, not the room.
            var availableWidth = StageHost.ActualWidth - StageHost.Padding.Left - StageHost.Padding.Right;
            var availableHeight = StageHost.ActualHeight - StageHost.Padding.Top - StageHost.Padding.Bottom
                                  - CellChromeHeight;
            if (availableWidth <= 0 || availableHeight <= 0) return;

            var gap = StageStack.Spacing;

            _updatingStageLayout = true;
            try
            {
                var slots = StageLayout.ChooseSlotCount(
                    availableWidth, availableHeight, gap, ViewModel.Items.Count, AspectsForCandidate);

                if (slots <= 0) return;

                // Committing the count rebuilds StageItems; the re-entrancy guard stops the
                // resulting CollectionChanged from starting this all over again.
                ViewModel.StageSlotCount = slots;

                var aspects = new List<double>(ViewModel.StageItems.Count);
                foreach (var item in ViewModel.StageItems) aspects.Add(item.EffectiveAspectRatio);
                if (aspects.Count == 0) return;

                var totalGaps = StageLayout.ComputeTotalGapWidth(aspects.Count, gap);
                var sharedHeight = StageLayout.ComputeSharedHeight(
                    availableWidth, availableHeight, totalGaps, aspects);
                if (sharedHeight <= 0) return;

                foreach (var item in ViewModel.StageItems) item.ApplyStageMetrics(sharedHeight);
            }
            finally
            {
                _updatingStageLayout = false;
            }

            ObserveSlotItems();
        }

        /// <summary>
        /// Effective aspects the window would hold at a candidate slot count. Read-only: this
        /// peeks at the window rule without committing to it.
        /// </summary>
        private IReadOnlyList<double> AspectsForCandidate(int slots)
        {
            var window = FilmstripWindow.Compute(ViewModel.ActiveIndex, ViewModel.Items.Count, slots);
            var result = new List<double>(Math.Max(0, window.SlotCount));

            for (var i = 0; i < window.SlotCount; i++)
            {
                var index = window.WindowStart + i;
                if (index >= 0 && index < ViewModel.Items.Count)
                    result.Add(ViewModel.Items[index].EffectiveAspectRatio);
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Pointer input
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // Rotate buttons (PRD 1.11)
        // ------------------------------------------------------------------

        /// <summary>
        /// Stops a tap on the rotate buttons from bubbling up to the enclosing slot's own Tapped
        /// handler.
        ///
        /// Without this the buttons sit inside their slot's tap target, so clicking one rotated
        /// the active photo AND re-selected that slot - measured, the position counter moved from
        /// 1/100 to 2/100 on a rotate click. Rotation must move nothing. Handling Tapped here does
        /// not affect Button.Click, which is raised from the Button's own pointer handling.
        /// </summary>
        private void RotateButtons_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

        /// <summary>
        /// The rotate buttons render in the ACTIVE slot - wherever that is, which at the first and
        /// last photo of the sequence is an end slot rather than the centre (PRD 1.5). Exactly one
        /// slot shows them, bound to the item's own IsActive.
        ///
        /// Focus is returned to this control afterwards. Keyboard is owned at window level by
        /// RootGrid_PreviewKeyDown, which tunnels from the root down and so fires wherever focus
        /// sits - but this project has already been bitten once by focus moving into a child that
        /// swallowed arrow keys, so the buttons are IsTabStop="False" and focus is handed back
        /// explicitly rather than left to chance.
        /// </summary>
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

        /// <summary>
        /// Clicking any stage slot makes that photo active (PRD E.3). The slot index is resolved
        /// through the repeater rather than a Tag, because the slots are templated now and their
        /// count varies - and ItemsRepeater does not give realized containers a DataContext under
        /// x:Bind, so sender.DataContext would be null.
        /// </summary>
        private void Slot_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not UIElement element) return;

            var slot = StageRepeater.GetElementIndex(element);
            if (slot < 0 || slot >= ViewModel.StageItems.Count) return;

            ViewModel.SetActiveIndex(ViewModel.StageItems[slot].Index);
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
