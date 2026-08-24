using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Fastcull.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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

        /// <summary>Window start at the last navigation, so the slide knows how far the set moved.</summary>
        private int _lastWindowStart = -1;

        /// <summary>The in-flight stage slide, retargeted rather than queued on rapid navigation.</summary>
        private Storyboard? _slide;

        /// <summary>The in-flight rotation sweep, and the transform it is driving.</summary>
        private Storyboard? _rotate;
        private RotateTransform? _rotateDelta;

        /// <summary>The in-flight zoom transition.</summary>
        private Storyboard? _zoom;

        /// <summary>The stage's normal padding, so zoom can reclaim it and give it back.</summary>
        private Thickness _stagePadding;

        /// <summary>The one item currently holding a zoom-tier decode, so it can be released.</summary>
        private FilmstripItemViewModel? _zoomLoadedItem;

        /// <summary>The long edge that decode was requested at, so a resize can re-request.</summary>
        private uint _zoomLoadedEdge;

        /// <summary>
        /// Navigation slide duration. Fast enough not to sit between the user and the next photo,
        /// slow enough to read as movement. Declared once in Themes/Nocturne.xaml so it is
        /// tunable in one place; the literal here is only the fallback if that lookup fails.
        /// </summary>
        private static double SlideMilliseconds =>
            Application.Current?.Resources?.TryGetValue("NavigationSlideMilliseconds", out var value) == true
            && value is double ms
                ? ms
                : 110;

        public MainViewModel ViewModel { get; } = new();

        public FilmstripView()
        {
            InitializeComponent();
            _stagePadding = StageHost.Padding;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.StageItems.CollectionChanged += StageItems_CollectionChanged;
            ViewModel.RotationChanged += OnRotationChanged;
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

        /// <summary>
        /// Re-lays out the stage, then re-checks the zoom decode against the new geometry.
        ///
        /// The second half is what fixes the fullscreen race: FilmstripView is subscribed to
        /// IsZoomed before MainWindow is, so the zoom decode was requested against the still-
        /// windowed stage and the window only grew afterwards. Hanging the re-request off the
        /// size change rather than off the zoom toggle fixes that transition and an ordinary
        /// window resize mid-zoom with the same code - the trigger is "the photo's rendered size
        /// changed", whatever caused it.
        /// </summary>
        private void StageHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateStageLayout();
            RefreshZoomImage();
        }

        /// <summary>
        /// Runs on every navigation, whatever the source - keyboard, a stage click or a thumbnail
        /// click. Three things happen, in this order and none of them gating the others:
        ///
        ///   1. the stage re-measures, because the active photo's shape can change how many slots
        ///      fit;
        ///   2. the stage slides, which is decorative only;
        ///   3. the bottom strip scrolls to keep the active thumbnail in view.
        ///
        /// Step 3 used to be wired to Thumbnail_Tapped alone, so arrowing through a folder left
        /// the strip wherever it was and the active thumbnail simply drifted off screen.
        /// </summary>
        private void OnNavigated()
        {
            UpdateStageLayout();
            AnimateStageSlide();
            CenterOn(ViewModel.ActiveIndex);
            RefreshZoomImage();
        }

        /// <summary>
        /// Keeps exactly one zoom-tier decode alive: the active photo's, and only while zoomed.
        ///
        /// The display tier is sized for a filmstrip slot (~960px), so stretching it across a
        /// whole screen is visibly soft. This asks for a decode sized to the viewport instead, in
        /// physical pixels - RasterizationScale matters here, since on a 150% display a
        /// 1400-DIP viewport is 2100 real pixels and decoding to 1400 would still be soft.
        ///
        /// The previous photo's zoom image is released before the next is requested, so switching
        /// photos while zoomed never accumulates viewport-sized bitmaps. That restraint matters
        /// more than usual: the stage's own retention problem (PRD 3.3, still unbuilt) is already
        /// the largest memory risk in the app, and this must not stack on top of it.
        /// </summary>
        private void RefreshZoomImage()
        {
            if (!ViewModel.IsZoomed)
            {
                _zoomLoadedItem?.ReleaseZoomImage();
                _zoomLoadedItem?.ResetZoomTransform();
                _zoomLoadedItem = null;
                _zoomLoadedEdge = 0;
                _isPanning = false;
                return;
            }

            var item = ViewModel.ActiveItem;
            if (item is null) return;

            // PRD 1.7.1: scale and pan reset on every entry to zoom and every change of photo.
            // Reset before the guard below, not after, because a same-photo re-request at a new
            // size returns early and must NOT throw away a scale the user has already dialled in
            // mid-zoom - the fullscreen transition triggers exactly that re-request.
            if (!ReferenceEquals(item, _zoomLoadedItem))
            {
                item.ResetZoomTransform();
                _isPanning = false;
            }

            var longEdge = ComputeZoomLongEdge(item);
            if (longEdge == 0) return;

            // Re-request when the PHOTO changes or when the size it needs changes. Keying on the
            // photo alone was the bug: entering zoom requests a decode sized to the still-windowed
            // stage, the window then goes fullscreen, the element grows - and nothing asked again.
            // Measured, that shipped a 1424px decode into a 2158px box, a 1.5x upscale.
            if (ReferenceEquals(item, _zoomLoadedItem) && longEdge == _zoomLoadedEdge) return;

            // Only clear when moving to a different photo. A same-photo re-request keeps the
            // current image visible until the sharper one lands.
            if (!ReferenceEquals(item, _zoomLoadedItem)) _zoomLoadedItem?.ReleaseZoomImage();

            _zoomLoadedItem = item;
            _zoomLoadedEdge = longEdge;

            // Fire-and-forget by design: the display-tier image is already on screen, so nothing
            // is waiting on this. It swaps itself in when it lands.
            _ = item.LoadZoomImageAsync(longEdge);
        }

        /// <summary>
        /// The decode size the zoomed photo actually needs, in physical pixels.
        ///
        /// Measured from the PHOTO's own rendered box, not the viewport: an aspect-fit photo is
        /// letterboxed inside the stage, so sizing to the viewport over-decodes by the whole
        /// letterbox margin - on a 3440x1440 stage a 3:2 photo renders 2158 wide, and asking for
        /// 3440 would decode 60% more pixels than are ever drawn.
        ///
        /// Quantised up to a 64px step so sub-pixel jitter during a resize cannot retrigger a
        /// full decode for a handful of pixels.
        /// </summary>
        private uint ComputeZoomLongEdge(FilmstripItemViewModel item)
        {
            var dips = Math.Max(item.StageFrameWidth, item.StageFrameHeight);

            // Before the first layout pass the frame has no size yet; the host is the best
            // available stand-in, and the next size change corrects it.
            if (dips <= 0) dips = Math.Max(StageHost.ActualWidth, StageHost.ActualHeight);
            if (dips <= 0) return 0;

            var scale = XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;

            var pixels = Math.Ceiling(dips * scale / 64.0) * 64.0;
            return (uint)Math.Clamp(pixels, 1, 8192);
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveIndex))
                OnNavigated();
            else if (e.PropertyName == nameof(MainViewModel.IsZoomed))
                OnZoomChanged();
        }

        /// <summary>
        /// Grows the active photo into the whole stage, or puts it back.
        ///
        /// Measured rather than modelled: the photo's rect is read off the live visual tree before
        /// the change and again after, and the animation is whatever transform maps the second
        /// onto the first. That avoids re-deriving the layout arithmetic here and stays correct as
        /// the stage rules change. UpdateLayout() forces the intervening layout pass to complete
        /// synchronously so the "after" measurement is real rather than stale.
        ///
        /// Same shape as the slide and the rotation sweep: the end state is applied instantly and
        /// the animation runs backwards from where things were, settling at identity.
        /// </summary>
        private void OnZoomChanged()
        {
            var before = MeasureActivePhoto();

            // The host's padding is part of what zoom reclaims.
            StageHost.Padding = ViewModel.IsZoomed ? new Thickness(0) : _stagePadding;

            UpdateStageLayout();
            StageHost.UpdateLayout();

            // The window start moved because the slot count changed; forget it so the next
            // navigation does not animate a slide across a shift that was never a navigation.
            _lastWindowStart = ViewModel.StageItems.Count > 0 ? ViewModel.StageItems[0].Index : -1;

            RefreshZoomImage();

            var after = MeasureActivePhoto();
            if (before is null || after is null) return;

            AnimateZoom(before.Value, after.Value);
        }

        // ------------------------------------------------------------------
        // PRD 1.7.1: mouse-wheel scale zoom and click-drag panning
        //
        // Only ever active while zoomed. In stage view the wheel and the pointer belong to the
        // bottom filmstrip (PRD 2.4), and none of these handlers touch anything unless IsZoomed.
        // ------------------------------------------------------------------

        private bool _isPanning;
        private Point _panLastPosition;

        /// <summary>The active photo's frame element, or null when it is not realized.</summary>
        private FrameworkElement? FindActiveStageFrame()
        {
            if (ViewModel.ActiveItem is null) return null;

            var slot = ViewModel.StageItems.IndexOf(ViewModel.ActiveItem);
            if (slot < 0) return null;

            if (StageRepeater.TryGetElement(slot) is not FrameworkElement container) return null;
            return container.FindName("StageFrame") as FrameworkElement;
        }

        /// <summary>
        /// The pointer's position relative to the active frame's CENTRE, which is the coordinate
        /// space ZoomTransform works in. Null when the pointer is outside the photo, so a wheel
        /// notch over the black letterbox does not anchor to a point that is not on the image.
        /// </summary>
        private Point? PointerInFrame(PointerRoutedEventArgs e)
        {
            var frame = FindActiveStageFrame();
            if (frame is null || frame.ActualWidth <= 0 || frame.ActualHeight <= 0) return null;

            var p = e.GetCurrentPoint(frame).Position;

            if (p.X < 0 || p.Y < 0 || p.X > frame.ActualWidth || p.Y > frame.ActualHeight) return null;

            return new Point(p.X - frame.ActualWidth / 2, p.Y - frame.ActualHeight / 2);
        }

        private void StageHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!ViewModel.IsZoomed || ViewModel.ActiveItem is null) return;

            var delta = e.GetCurrentPoint(StageHost).Properties.MouseWheelDelta;
            if (delta == 0) return;

            // One notch is 120; a high-resolution wheel can report fractions of that, and dividing
            // would round them to zero and make the wheel feel dead. Sign is enough.
            var steps = delta > 0 ? 1 : -1;

            // Anchor to the pointer when it is over the photo; fall back to the centre when it is
            // over the letterbox, which zooms without a preferred point rather than doing nothing.
            var anchor = PointerInFrame(e) ?? new Point(0, 0);

            if (ViewModel.ActiveItem.ScaleZoomAt(anchor.X, anchor.Y, steps))
                e.Handled = true;
        }

        private void StageHost_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!ViewModel.IsZoomed || ViewModel.ActiveItem is null) return;
            if (!ViewModel.ActiveItem.CanPan) return;          // nothing to pan at the fit scale

            var point = e.GetCurrentPoint(StageHost);
            if (!point.Properties.IsLeftButtonPressed) return;

            _isPanning = StageHost.CapturePointer(e.Pointer);
            _panLastPosition = point.Position;
            e.Handled = _isPanning;
        }

        private void StageHost_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPanning || ViewModel.ActiveItem is null) return;

            var position = e.GetCurrentPoint(StageHost).Position;

            // Deltas rather than an absolute origin: the clamp can stop the image short of the
            // pointer, and anchoring to where the drag began would then make the photo lurch when
            // the pointer came back off the edge.
            ViewModel.ActiveItem.PanZoom(position.X - _panLastPosition.X, position.Y - _panLastPosition.Y);

            _panLastPosition = position;
            e.Handled = true;
        }

        private void StageHost_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPanning) return;

            StageHost.ReleasePointerCapture(e.Pointer);
            _isPanning = false;
            e.Handled = true;
        }

        /// <summary>
        /// Capture can be lost without a release - the window deactivating, or the pointer being
        /// taken by another element. Without this the drag would stay armed and the next pointer
        /// move would jump the image.
        /// </summary>
        private void StageHost_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
            => _isPanning = false;

        /// <summary>Centre and height of the active photo's frame, in StageHost coordinates.</summary>
        private (Point Centre, double Height)? MeasureActivePhoto()
        {
            var slot = ViewModel.StageItems.IndexOf(ViewModel.ActiveItem!);
            if (slot < 0) return null;

            if (StageRepeater.TryGetElement(slot) is not FrameworkElement container) return null;
            if (container.FindName("StageFrame") is not FrameworkElement frame) return null;
            if (frame.ActualHeight <= 0) return null;

            var centre = frame.TransformToVisual(StageHost)
                              .TransformPoint(new Point(frame.ActualWidth / 2, frame.ActualHeight / 2));

            return (centre, frame.ActualHeight);
        }

        /// <summary>
        /// Animates the stage from where the active photo used to be to where it now is.
        ///
        /// The scale is about StageSlide's own centre, so the translate has to be expressed
        /// relative to that centre too: a point p maps to C + (p - C) * s + t, and solving for
        /// "after lands on before" gives t = before - C - (after - C) * s.
        /// </summary>
        private void AnimateZoom((Point Centre, double Height) before, (Point Centre, double Height) after)
        {
            if (after.Height <= 0 || before.Height <= 0) return;

            var scale = before.Height / after.Height;

            var origin = StageSlide.TransformToVisual(StageHost)
                                   .TransformPoint(new Point(StageSlide.ActualWidth / 2, StageSlide.ActualHeight / 2));

            var offsetX = before.Centre.X - origin.X - ((after.Centre.X - origin.X) * scale);
            var offsetY = before.Centre.Y - origin.Y - ((after.Centre.Y - origin.Y) * scale);

            _zoom?.Stop();
            _slide?.Stop();

            StageZoomTransform.ScaleX = scale;
            StageZoomTransform.ScaleY = scale;
            StageSlideTransform.X = offsetX;
            StageSlideTransform.Y = offsetY;

            _zoom = new Storyboard();
            AddSettleToIdentity(_zoom, StageZoomTransform, "ScaleX", scale, 1);
            AddSettleToIdentity(_zoom, StageZoomTransform, "ScaleY", scale, 1);
            AddSettleToIdentity(_zoom, StageSlideTransform, "X", offsetX, 0);
            AddSettleToIdentity(_zoom, StageSlideTransform, "Y", offsetY, 0);

            _zoom.Completed += (_, _) =>
            {
                StageZoomTransform.ScaleX = 1;
                StageZoomTransform.ScaleY = 1;
                StageSlideTransform.X = 0;
                StageSlideTransform.Y = 0;
            };
            _zoom.Begin();
        }

        /// <summary>One animation of the shared profile: same duration and easing as everything else.</summary>
        private static void AddSettleToIdentity(Storyboard storyboard, DependencyObject target, string property, double from, double to)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(SlideMilliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,
            };

            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
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
            if (ViewModel.IsZoomed) { UpdateZoomLayout(); return; }

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
        /// Zoomed layout: one photo, aspect-fit into the whole stage area.
        ///
        /// Everything the normal path reserves is reclaimed - the neighbours (slot count drops to
        /// one), the host's padding, and the tick/bar/caption chrome (hidden via the item's
        /// StageChromeVisibility). What is left is a plain "contain" fit: the photo grows until it
        /// meets the viewport's height or its width, whichever comes first, and is never cropped.
        ///
        /// It re-fits the display-tier image that is already decoded and requests no decode of any
        /// kind, so this is not PRD 1.7's 1:1 inspection.
        /// </summary>
        private void UpdateZoomLayout()
        {
            var width = StageHost.ActualWidth;
            var height = StageHost.ActualHeight;
            if (width <= 0 || height <= 0) return;

            ViewModel.StageSlotCount = 1;

            var item = ViewModel.StageItems.Count > 0 ? ViewModel.StageItems[0] : null;
            if (item is null) return;

            var sharedHeight = StageLayout.ComputeSharedHeight(
                width, height, totalGapWidth: 0, new[] { item.EffectiveAspectRatio });
            if (sharedHeight <= 0) return;

            item.ApplyStageMetrics(sharedHeight);
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

            // OnNavigated handles centring and the slide - SetActiveIndex is the only call needed.
            ViewModel.SetActiveIndex(ViewModel.StageItems[slot].Index);
        }

        /// <summary>
        /// Starts a thumbnail decode as its container comes on screen, and cancels it when the
        /// container is recycled. This is the filmstrip's own virtualization doing the gating -
        /// the prefetch window governs the display tier, not this.
        /// </summary>
        private void ThumbRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (args.Index >= 0 && args.Index < ViewModel.Items.Count)
                ViewModel.Items[args.Index].BeginThumbnailLoad();
        }

        private void ThumbRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            var index = sender.GetElementIndex(args.Element);
            if (index >= 0 && index < ViewModel.Items.Count)
                ViewModel.Items[index].CancelThumbnailLoad();
        }

        private void Thumbnail_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // ItemsRepeater does not propagate DataContext to realized elements when the
            // DataTemplate uses x:Bind/x:DataType, so the item must be resolved through the
            // repeater itself rather than through sender.DataContext (which is always null here).
            if (sender is not UIElement element) return;
            var index = ThumbRepeater.GetElementIndex(element);
            if (index < 0) return;

            // OnNavigated centres the strip now, for every navigation source rather than this one.
            ViewModel.SetActiveIndex(index);
        }

        /// <summary>
        /// Scrolls the bottom strip so the active thumbnail is centred.
        ///
        /// ChangeView's own eased scroll is the animation here - a composition animation would be
        /// fighting the ScrollViewer for ownership of the offset, and ChangeView already
        /// retargets rather than queues when it is called again mid-scroll, which is exactly the
        /// coalescing behaviour key repeat needs.
        ///
        /// It bails during a pointer drag: the drag path writes HorizontalOffset directly, and an
        /// animation running against it would fight the pointer and snap back on release.
        /// </summary>
        private void CenterOn(int index)
        {
            if (_dragging || index < 0) return;

            var viewport = BottomScroll.ActualWidth;
            if (viewport <= 0) return;

            var itemCenter = index * (ThumbWidth + ThumbSpacing) + ThumbWidth / 2;
            var target = Math.Max(0, itemCenter - viewport / 2);
            BottomScroll.ChangeView(target, null, null, false);
        }

        // ------------------------------------------------------------------
        // Navigation slide
        // ------------------------------------------------------------------

        /// <summary>
        /// Slides the stage by one slot pitch when the window shifts.
        ///
        /// Purely decorative: the active index, the ratings and the whole stage layout have
        /// already changed by the time this runs, and nothing waits for it to finish. The
        /// container it translates paints no background, so it adds no non-black pixels
        /// (PRD 1.10).
        ///
        /// Retargets rather than queues. Holding an arrow key fires navigations far faster than
        /// the animation completes, so each one reads the offset the content is CURRENTLY drawn
        /// at, adds the new shift to it, and animates that to zero - the slide always converges
        /// on the settled position instead of falling behind a backlog. The accumulated offset is
        /// clamped so a long key-hold cannot wind up an absurd distance to unwind.
        /// </summary>
        private void AnimateStageSlide()
        {
            if (ViewModel.StageItems.Count == 0) { _lastWindowStart = -1; return; }

            // While zoomed the stage holds a single photo, so one "slot pitch" is the whole
            // viewport - sliding by that would read as a hard swipe across the screen rather than
            // a step. Changing photo while zoomed simply swaps it.
            if (ViewModel.IsZoomed)
            {
                _lastWindowStart = ViewModel.StageItems[0].Index;
                return;
            }

            var windowStart = ViewModel.StageItems[0].Index;

            // First layout, or the window did not move (which is what happens at the sequence
            // boundaries, where the active marker moves between slots instead).
            if (_lastWindowStart < 0) { _lastWindowStart = windowStart; return; }

            var shift = windowStart - _lastWindowStart;
            _lastWindowStart = windowStart;
            if (shift == 0) return;

            var pitch = AverageSlotPitch();
            if (pitch <= 0) return;

            // Read the animated value BEFORE stopping, so a mid-flight retarget starts from where
            // the content actually is rather than snapping to the base value first.
            var current = StageSlideTransform.X;
            _slide?.Stop();

            var from = Math.Clamp(current + (shift * pitch), -2 * pitch, 2 * pitch);
            StageSlideTransform.X = from;

            var animation = new DoubleAnimation
            {
                From = from,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(SlideMilliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,
            };

            Storyboard.SetTarget(animation, StageSlideTransform);
            Storyboard.SetTargetProperty(animation, "X");

            _slide = new Storyboard();
            _slide.Children.Add(animation);
            _slide.Completed += (_, _) => StageSlideTransform.X = 0;
            _slide.Begin();
        }

        /// <summary>
        /// Sweeps the photo through the quarter turn just applied.
        ///
        /// By the time this runs the rotation state, the persisted value and the whole stage
        /// layout have already changed - the bound transform is at the settled angle and the
        /// frame is already the new shape. So the animation runs on a SECOND rotation composed
        /// on top: it starts at the opposite of the turn, which cancels the settled angle back to
        /// where the photo visually was, and settles at zero. Exactly the shape of the navigation
        /// slide, and it shares that animation's duration and easing.
        ///
        /// Like the slide it retargets rather than queues, so holding A or S sweeps continuously
        /// instead of falling behind, and the accumulated delta is clamped so a long hold cannot
        /// wind up several turns' worth of unwinding.
        /// </summary>
        private void OnRotationChanged(FilmstripItemViewModel item, int quarterTurns)
        {
            // Settle any in-flight sweep first, including one on a different photo - otherwise
            // stopping it would leave that photo's delta transform parked off-zero.
            if (_rotateDelta is not null)
            {
                _rotate?.Stop();
                _rotateDelta.Angle = 0;
                _rotateDelta = null;
            }
            _rotate = null;

            var delta = FindRotationDelta(item);
            if (delta is null) return;   // not realized - the photo still rotates, just without the sweep

            var from = Math.Clamp(delta.Angle - (90.0 * quarterTurns), -180, 180);
            delta.Angle = from;

            var animation = new DoubleAnimation
            {
                From = from,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(SlideMilliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,
            };

            Storyboard.SetTarget(animation, delta);
            Storyboard.SetTargetProperty(animation, "Angle");

            _rotateDelta = delta;
            _rotate = new Storyboard();
            _rotate.Children.Add(animation);
            _rotate.Completed += (_, _) => { delta.Angle = 0; _rotateDelta = null; };
            _rotate.Begin();
        }

        /// <summary>
        /// The animated half of the staged photo's TransformGroup, or null when the item is not
        /// realized. Resolved through the repeater's own TryGetElement and the template's named
        /// Image rather than by walking the visual tree.
        /// </summary>
        private RotateTransform? FindRotationDelta(FilmstripItemViewModel item)
        {
            var slot = ViewModel.StageItems.IndexOf(item);
            if (slot < 0) return null;

            if (StageRepeater.TryGetElement(slot) is not FrameworkElement container) return null;
            if (container.FindName("StagePhoto") is not Image image) return null;
            if (image.RenderTransform is not TransformGroup group || group.Children.Count < 2) return null;

            return group.Children[1] as RotateTransform;
        }

        /// <summary>
        /// Mean slot pitch across the staged photos. Slots vary in width - that is the whole point
        /// of the layout - so there is no single true pitch; the mean is what reads as "moved by
        /// one" without the slide distance jumping around as shapes change.
        /// </summary>
        private double AverageSlotPitch()
        {
            var count = ViewModel.StageItems.Count;
            if (count == 0) return 0;

            var total = 0.0;
            foreach (var item in ViewModel.StageItems) total += item.StageFrameWidth;

            return (total / count) + StageStack.Spacing;
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
