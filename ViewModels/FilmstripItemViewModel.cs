using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Fastcull.Models;
using Fastcull.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace Fastcull.ViewModels
{
    public partial class FilmstripItemViewModel : ObservableObject, ICacheableItem, IDisposable
    {
        private readonly DispatcherQueue _dispatcherQueue;

        // Two independent load axes: the display tier follows the prefetch window, the thumbnail
        // follows the filmstrip's own virtualization. They cancel independently.
        private CancellationTokenSource? _displayCts;
        private CancellationTokenSource? _thumbnailCts;
        private bool _displayStarted;
        private bool _thumbnailStarted;

        /// <summary>
        /// Constructing an item decodes NOTHING. It used to start both a thumbnail and a
        /// display-tier decode here, and MainViewModel constructs one of these per scanned file -
        /// so opening a 2,000-photo folder launched 4,000 decodes at once, which is how the
        /// measured peak working set reached 5.25 GB against a 4 GB budget.
        ///
        /// Decoding is now driven by the cursor: PrefetchCoordinator calls BeginLoad on the photos
        /// inside the sliding window and CancelLoad on the ones that leave it (PRD 3.3).
        /// </summary>
        public FilmstripItemViewModel(ScannedPhoto photo, int index)
        {
            Photo = photo;
            Index = index;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }

        /// <summary>Formats this app can decode at all; anything else never loads.</summary>
        private bool IsDecodable =>
            Photo.Family is FormatFamily.Jpeg or FormatFamily.Png or FormatFamily.Raw;

        // ------------------------------------------------------------------
        // ICacheableItem
        // ------------------------------------------------------------------

        /// <summary>True once either tier holds decoded pixels eviction could reclaim.</summary>
        public bool IsResident => Thumbnail is not null || DisplayImage is not null || ZoomImage is not null;

        /// <summary>
        /// Rough memory held, for the eviction ceiling. Thumbnail and display tier are estimated
        /// from their known long edges at BGRA8; the zoom tier is measured, because it is by far
        /// the largest single allocation in the app (~31 MB at a fullscreen 2176px long edge) and
        /// guessing it would make the ceiling meaningless whenever zoom is open.
        /// </summary>
        public long ResidentBytes
        {
            get
            {
                long bytes = 0;
                if (Thumbnail is not null) bytes += EstimateBytes(ThumbnailService.ThumbnailLongEdge, EffectiveAspectRatio);
                if (DisplayImage is not null) bytes += EstimateBytes(ThumbnailService.DisplayLongEdge, EffectiveAspectRatio);
                bytes += _zoomImageBytes;
                return bytes;
            }
        }

        /// <summary>
        /// What <see cref="Evict"/> would actually release: the display and zoom tiers. The
        /// thumbnail is excluded because eviction deliberately keeps it - the bottom filmstrip may
        /// still be showing it - so counting it here would promise memory that never comes back.
        /// </summary>
        public long EvictableBytes
        {
            get
            {
                if (IsPinned) return 0;

                long bytes = _zoomImageBytes;
                if (DisplayImage is not null) bytes += EstimateBytes(ThumbnailService.DisplayLongEdge, EffectiveAspectRatio);
                return bytes;
            }
        }

        private static long EstimateBytes(uint longEdge, double aspectRatio)
        {
            var aspect = aspectRatio > 0 && !double.IsNaN(aspectRatio) && !double.IsInfinity(aspectRatio)
                ? aspectRatio
                : StageLayout.DefaultAspectRatio;

            var shortEdge = aspect >= 1 ? longEdge / aspect : longEdge * aspect;
            return (long)(longEdge * shortEdge * 4);   // BGRA8
        }

        /// <summary>
        /// Set by MainViewModel for the photos currently on stage. A pinned item is never an
        /// eviction candidate - dropping one blanks a photo the user is looking at.
        /// </summary>
        public bool IsPinned { get; set; }

        /// <summary>
        /// Starts the display-tier decode - the expensive one, ~2.4 MB per photo - if it is not
        /// already loaded or loading. Driven by the prefetch window.
        /// </summary>
        public void BeginLoad()
        {
            if (!IsDecodable || _displayStarted) return;

            _displayStarted = true;
            _displayCts = new CancellationTokenSource();
            _ = LoadDisplayImageAsync(_displayCts.Token);
        }

        /// <summary>
        /// Cancels an in-flight display load. Anything already decoded stays - cancelling is about
        /// not spending work on a photo that left the window, not discarding work already done.
        /// </summary>
        public void CancelLoad()
        {
            if (_displayCts is null) return;

            _displayCts.Cancel();
            _displayCts.Dispose();
            _displayCts = null;

            // Only restartable if nothing landed; a completed load stays completed.
            if (DisplayImage is null) _displayStarted = false;
        }

        /// <summary>
        /// Starts the ~160px thumbnail. Driven by the bottom filmstrip realizing a container, NOT
        /// by the prefetch window: the strip is a scrubbing surface that shows every photo in the
        /// folder, so gating thumbnails behind the window would leave most of it blank. The
        /// ItemsRepeater virtualizes, so only the handful actually on screen ever decode.
        ///
        /// A thumbnail is ~77 KB against the display tier's ~2.4 MB, so this axis is not what the
        /// memory ceiling is about - but it still has no business decoding 2,000 of them at once.
        /// </summary>
        public void BeginThumbnailLoad()
        {
            if (!IsDecodable || _thumbnailStarted) return;

            _thumbnailStarted = true;
            _thumbnailCts = new CancellationTokenSource();
            _ = LoadThumbnailAsync(_thumbnailCts.Token);
        }

        /// <summary>Cancels an in-flight thumbnail decode when its container is recycled.</summary>
        public void CancelThumbnailLoad()
        {
            if (_thumbnailCts is null) return;

            _thumbnailCts.Cancel();
            _thumbnailCts.Dispose();
            _thumbnailCts = null;

            if (Thumbnail is null) _thumbnailStarted = false;
        }

        /// <summary>
        /// Drops the decoded images so the GC can reclaim them, and allows a later reload.
        ///
        /// Nothing is Disposed here, deliberately: a SoftwareBitmap handed to
        /// SoftwareBitmapSource.SetBitmapAsync is still being copied into a composition surface on
        /// a XAML worker thread, and destroying it mid-copy fail-fasts the process with
        /// 0xC000027B - the exact crash this project already fixed once. Releasing the last
        /// managed reference is the safe equivalent.
        /// </summary>
        public void Evict()
        {
            if (IsPinned) return;

            CancelLoad();
            ReleaseZoomImage();

            // The thumbnail is deliberately left alone: it is tiny, and the bottom filmstrip may
            // still be showing it. Eviction is about the display tier.
            DisplayImage = null;
            DisplayImageFailed = false;
            _displayStarted = false;
        }

        public ScannedPhoto Photo { get; }
        public int Index { get; }

        // ------------------------------------------------------------------
        // PRD 1.5 Active Photo / 1.8.1 info overlay
        //
        // One set of fields, two surfaces: the sidebar panel and the on-photo overlay read exactly
        // the same properties, so they can never disagree about the photo they both describe.
        // Every field follows PRD 1.5's rule - absent means the row is omitted, never blank.
        // ------------------------------------------------------------------

        /// <summary>Mirrored from MainViewModel, per-item because the stage is a templated repeater.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(InfoOverlayVisibility))]
        private bool _isInfoVisible;

        /// <summary>Only the active photo carries the overlay - it describes one photo, not all nine.</summary>
        public Visibility InfoOverlayVisibility =>
            IsInfoVisible && IsActive ? Visibility.Visible : Visibility.Collapsed;

        public string DeviceText => Photo.CameraModel ?? string.Empty;
        public Visibility DeviceVisibility => Field(Photo.CameraModel);

        public string ResolutionText
        {
            get
            {
                if (Photo.PixelWidth is not int w || Photo.PixelHeight is not int h || w <= 0 || h <= 0)
                    return string.Empty;

                var megapixels = w * (double)h / 1_000_000;
                return $"{w:N0} × {h:N0}  ·  {megapixels:0.#} MP";
            }
        }

        public Visibility ResolutionVisibility => Field(ResolutionText);

        /// <summary>
        /// The capture date resolved by PRD 1.3. Marked when it came from the file's modified time
        /// rather than from EXIF, because an exported PNG's timestamp is not a capture date and
        /// presenting it as one would be a quiet lie.
        /// </summary>
        public string CapturedText => Photo.SortTimeSource == TimeSource.FileModified
            ? $"{Photo.SortTime:d MMM yyyy, HH:mm} (file date)"
            : $"{Photo.SortTime:d MMM yyyy, HH:mm}";

        public Visibility CapturedVisibility => Visibility.Visible;   // always resolvable, per PRD 1.3

        /// <summary>
        /// The place name once geocoding resolves it, or raw coordinates until then, or nothing at
        /// all when the file carries no GPS - which is the common case (PRD 1.8.2).
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlaceText))]
        [NotifyPropertyChangedFor(nameof(PlaceVisibility))]
        private string? _placeName;

        public string PlaceText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(PlaceName)) return PlaceName!;

                return Photo.Latitude is double lat && Photo.Longitude is double lon
                    ? GeoFormat.Coordinates(lat, lon)
                    : string.Empty;
            }
        }

        public Visibility PlaceVisibility => Field(PlaceText);

        /// <summary>
        /// Where the photo lives: the path relative to the scan root when it sits in a subfolder,
        /// and the scan root's own folder name when it does not.
        ///
        /// The relative path is empty for a photo in the root, and rendering that as "." tells the
        /// user nothing - so the root falls back to the real containing folder's name, which is
        /// what they would call it.
        /// </summary>
        public string FolderText
        {
            get
            {
                var relativeDirectory = Path.GetDirectoryName(Photo.RelativePath);
                if (!string.IsNullOrEmpty(relativeDirectory)) return relativeDirectory;

                var containing = Path.GetFileName(Path.GetDirectoryName(Photo.FilePath));
                return string.IsNullOrEmpty(containing) ? "." : containing;
            }
        }

        public Visibility FolderVisibility => Visibility.Visible;

        private static Visibility Field(string? value)
            => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

        public string FileName => Photo.FileName;
        public string FormatLabel => Path.GetExtension(Photo.FileName).TrimStart('.').ToUpperInvariant();

        /// <summary>Filename with the extension stripped - the Chromeless caption shows only this.</summary>
        public string DisplayName => Path.GetFileNameWithoutExtension(Photo.FileName);

        /// <summary>
        /// Width / height of the decoded display image. The stage's equal-height rule needs every
        /// visible photo's aspect to pick one shared height, and it needs it before deciding
        /// layout - so this starts at a 3:2 guess (the overwhelmingly common still-camera aspect,
        /// and the same fallback the prototype uses) and is corrected the moment the real decode
        /// lands.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EffectiveAspectRatio))]
        private double _aspectRatio = 1.5;

        /// <summary>Fixed slot width of a bottom-filmstrip thumbnail.</summary>
        public const double ThumbnailSlotWidth = 150;

        // The band is 108px with 8px padding top and bottom, leaving 92. The mark below each
        // thumbnail needs 4px of gap plus its own 2px, so the tallest thumbnail can only be 86.
        private const double ActiveThumbnailHeight = 82;
        private const double InactiveThumbnailHeight = 70;

        /// <summary>
        /// User rotation (PRD 1.11), as quarter turns on top of whatever the decode produced.
        /// Set only via MainViewModel, exactly like CullState.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EffectiveAspectRatio))]
        [NotifyPropertyChangedFor(nameof(RotationAngle))]
        [NotifyPropertyChangedFor(nameof(ThumbnailImageWidth))]
        [NotifyPropertyChangedFor(nameof(ThumbnailImageHeight))]
        [NotifyPropertyChangedFor(nameof(ThumbnailImageLeft))]
        [NotifyPropertyChangedFor(nameof(ThumbnailImageTop))]
        private Rotation _rotation = Rotation.None;

        /// <summary>
        /// The aspect the photo actually presents on screen, rotation included. Everything that
        /// lays the stage out consumes this rather than <see cref="AspectRatio"/> - a quarter
        /// turn swaps width and height, and sizing against the un-rotated aspect makes a rotated
        /// photo overflow its cell or leave a gap.
        /// </summary>
        public double EffectiveAspectRatio => Rotation.Apply(AspectRatio);

        /// <summary>Clockwise render angle in degrees.</summary>
        public double RotationAngle => Rotation.Degrees;

        /// <summary>
        /// Mirrored from MainViewModel.IsZoomed. The stage is a templated repeater bound to this
        /// type, so a template can only see per-item properties - a global flag has to be pushed
        /// down to be visible to the markup.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StageChromeVisibility))]
        [NotifyPropertyChangedFor(nameof(DimensionLimitedVisibility))]
        [NotifyPropertyChangedFor(nameof(ZoomLoadingVisibility))]
        private bool _isZoomed;

        /// <summary>
        /// The accent tick, weight bar and caption row are hidden while zoomed, so the photo gets
        /// the whole stage. State is still readable from the item's mark in the bottom filmstrip,
        /// which stays visible.
        /// </summary>
        public Visibility StageChromeVisibility => IsZoomed ? Visibility.Collapsed : Visibility.Visible;

        // ------------------------------------------------------------------
        // Stage geometry
        // ------------------------------------------------------------------
        //
        // The stage is a templated repeater over a variable number of photos, so its geometry is
        // exposed as bindable properties rather than assigned to x:Name'd elements from
        // code-behind. That is not just tidiness: ItemsRepeater does not propagate DataContext to
        // realized containers under x:Bind (the bottom strip's Thumbnail_Tapped already works
        // around this via GetElementIndex), so reaching into realized containers by name to size
        // them would be the fragile path. Binding is the one that works.
        //
        // The View computes the single shared height for the whole visible set, then pushes it
        // through ApplyStageMetrics; every value below derives from that.

        /// <summary>Post-rotation layout box: what participates in layout and what the tick and bar measure against.</summary>
        [ObservableProperty] private double _stageFrameWidth;
        [ObservableProperty] private double _stageFrameHeight;

        /// <summary>Pre-rotation image box - the frame with width and height swapped on a quarter turn.</summary>
        [ObservableProperty] private double _stageImageWidth;
        [ObservableProperty] private double _stageImageHeight;

        /// <summary>Canvas offsets that centre the image in its frame, so it rotates about the frame's centre.</summary>
        [ObservableProperty] private double _stageImageLeft;
        [ObservableProperty] private double _stageImageTop;

        /// <summary>Accent tick above the active photo: 18% of the rendered width.</summary>
        [ObservableProperty] private double _stageTickWidth;

        /// <summary>State weight bar: the full rendered width.</summary>
        [ObservableProperty] private double _stageBarWidth;

        /// <summary>Fraction of the photo's rendered width the active-tick spans.</summary>
        public const double TickWidthFraction = 0.18;

        /// <summary>
        /// Sizes this photo for the stage at the set's shared height.
        ///
        /// Rotation is drawn as a render transform about the image's centre, and a render
        /// transform does not affect layout - so the layout box and the image are sized
        /// separately. The frame takes the post-rotation shape; the image is laid out with width
        /// and height swapped on a quarter turn, so that rotating it about its centre yields a
        /// bounding box exactly equal to the frame. Re-decoding to bake the rotation in would
        /// spend a decode on a transform and blow the PRD 3.5 keypress budget.
        /// </summary>
        public void ApplyStageMetrics(double sharedHeight)
        {
            if (sharedHeight <= 0) return;

            var frameWidth = StageLayout.PhotoWidth(sharedHeight, EffectiveAspectRatio);
            if (frameWidth <= 0) return;

            StageFrameWidth = frameWidth;
            StageFrameHeight = sharedHeight;

            var swaps = Rotation.SwapsAspect;
            StageImageWidth = swaps ? sharedHeight : frameWidth;
            StageImageHeight = swaps ? frameWidth : sharedHeight;

            StageImageLeft = (frameWidth - StageImageWidth) / 2;
            StageImageTop = (sharedHeight - StageImageHeight) / 2;

            StageTickWidth = Math.Max(1, frameWidth * TickWidthFraction);
            StageBarWidth = Math.Max(1, frameWidth);
        }

        /// <summary>Height of this thumbnail's slot: the active one stands 12px taller.</summary>
        public double ThumbnailSlotHeight => IsActive ? ActiveThumbnailHeight : InactiveThumbnailHeight;

        /// <summary>
        /// Pre-rotation size of the thumbnail image. At 90/270 the image is laid out with width
        /// and height swapped so that, once rotated about its centre, its bounding box is exactly
        /// the slot - it fills the slot without overlapping its neighbours.
        /// </summary>
        public double ThumbnailImageWidth => Rotation.SwapsAspect ? ThumbnailSlotHeight : ThumbnailSlotWidth;

        public double ThumbnailImageHeight => Rotation.SwapsAspect ? ThumbnailSlotWidth : ThumbnailSlotHeight;

        /// <summary>
        /// Canvas offsets that centre the thumbnail image in its slot. The image sits in a Canvas
        /// for the same reason the stage photo does: every other panel clamps a child's desired
        /// size to the space available, which would crush the swapped dimension on a quarter turn.
        /// </summary>
        public double ThumbnailImageLeft => (ThumbnailSlotWidth - ThumbnailImageWidth) / 2;

        public double ThumbnailImageTop => (ThumbnailSlotHeight - ThumbnailImageHeight) / 2;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ThumbnailSlotHeight))]
        [NotifyPropertyChangedFor(nameof(ThumbnailImageWidth))]
        [NotifyPropertyChangedFor(nameof(ThumbnailImageHeight))]
        [NotifyPropertyChangedFor(nameof(ThumbnailImageLeft))]
        [NotifyPropertyChangedFor(nameof(ThumbnailImageTop))]
        [NotifyPropertyChangedFor(nameof(InfoOverlayVisibility))]
        private bool _isActive;

        /// <summary>Position on the PRD 1.6 cull ladder. Set only via MainViewModel.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateBorderBrush))]
        [NotifyPropertyChangedFor(nameof(StarBadgeText))]
        [NotifyPropertyChangedFor(nameof(IsStarBadgeVisible))]
        [NotifyPropertyChangedFor(nameof(StarString))]
        private CullState _cullState = CullState.Default;

        /// <summary>
        /// Stars as a run of filled stars for the Chromeless caption row, empty at zero stars.
        /// The ladder guarantees 0-5 (PRD 1.6), so this cannot produce a runaway string.
        /// </summary>
        public string StarString => CullState.Stars >= 1 ? new string('★', CullState.Stars) : string.Empty;

        /// <summary>Red = rejected, yellow = unrated, green = picked (PRD 1.5).</summary>
        public Brush StateBorderBrush => CullState.Flag switch
        {
            Flag.Rejected => RejectedBrush,
            Flag.Picked => PickedBrush,
            _ => UnratedBrush,
        };

        public string StarBadgeText => CullState.Stars >= 1 ? CullState.Stars.ToString() : string.Empty;

        public Visibility IsStarBadgeVisible => CullState.Stars >= 1 ? Visibility.Visible : Visibility.Collapsed;

        // Windows-standard hues, shared by every item rather than allocated per item.
        private static readonly SolidColorBrush RejectedBrush = new(Color.FromArgb(0xFF, 0xE8, 0x11, 0x23));
        private static readonly SolidColorBrush UnratedBrush = new(Color.FromArgb(0xFF, 0xFF, 0xB9, 0x00));
        private static readonly SolidColorBrush PickedBrush = new(Color.FromArgb(0xFF, 0x10, 0x89, 0x3E));

        /// <summary>Small (~160px) decode for the bottom scrubber strip.</summary>
        [ObservableProperty]
        private ImageSource? _thumbnail;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ThumbnailFailedVisibility))]
        private bool _thumbnailFailed;

        public Visibility ThumbnailFailedVisibility => ThumbnailFailed ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Larger "display tier" decode for the top three-slot comparison view - a
        /// separate decode from Thumbnail, never the same bitmap, per PRD 3.2.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StageImageSource))]
        private ImageSource? _displayImage;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayImageFailedVisibility))]
        private bool _displayImageFailed;

        /// <summary>
        /// Zoom-tier decode, sized to the viewport. Null until one has been requested and has
        /// landed - and null again the moment zoom exits or the zoomed photo changes, so a
        /// viewport-sized bitmap never lingers behind the stage's existing retention problem.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StageImageSource))]
        private ImageSource? _zoomImage;

        /// <summary>
        /// What the stage actually draws: the zoom-tier image once it exists, the display tier
        /// until then. The Prime Directive's "never a blank frame" is why this falls back rather
        /// than waiting - the softer image is on screen from the first frame of the zoom, and the
        /// sharp one replaces it in place when it is ready.
        /// </summary>
        public ImageSource? StageImageSource => ZoomImage ?? DisplayImage;

        private CancellationTokenSource? _zoomCts;

        /// <summary>Measured size of the zoom decode, for the eviction ceiling. Zero when none is held.</summary>
        private long _zoomImageBytes;

        /// <summary>
        /// True when PRD 3.3's 512 MB dimension guard reduced this photo's decode, so full
        /// resolution is not available for it. Surfaced on the stage the same way a failed decode
        /// is, rather than letting the photo look mysteriously soft with no explanation.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DimensionLimitedVisibility))]
        private bool _dimensionLimited;

        public Visibility DimensionLimitedVisibility =>
            DimensionLimited && IsZoomed ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// True while a zoom-tier decode is in flight for this photo (PRD 1.7's loading indicator).
        ///
        /// The window it marks is real: entering zoom shows the display-tier image immediately and
        /// swaps the larger decode in when it lands, so for a moment the photo on screen is not the
        /// one that was asked for. Without the indicator a soft frame is indistinguishable from a
        /// finished one that is simply soft - the exact confusion that hid a silently failing zoom
        /// decode for three rounds of investigation.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ZoomLoadingVisibility))]
        private bool _isZoomLoading;

        public Visibility ZoomLoadingVisibility =>
            IsZoomLoading && IsZoomed ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Decodes this photo at <paramref name="longEdge"/> for zoom, then swaps it in.
        ///
        /// Follows the rules established after the 0xC000027B stowed-exception crash: the WinRT
        /// calls are awaited to completion rather than raced against a token, cancellation is
        /// observed between stages, and the UI assignment is marshalled onto the dispatcher.
        ///
        /// RAW is excluded on instruction. Note that RAW does NOT go blank when zoomed - it keeps
        /// showing its display-tier image, which comes from the embedded JPEG preview; it simply
        /// does not get the sharper re-decode.
        /// </summary>
        public async Task LoadZoomImageAsync(uint longEdge)
        {
            if (Photo.Family is not (FormatFamily.Jpeg or FormatFamily.Png or FormatFamily.Raw)) return;

            // PRD 3.3's dimension guard. A photo whose decode at this size would exceed 512 MB is
            // capped and flagged rather than attempted - one stitched panorama is enough to take
            // the app out, and losing a 45-minute cull to an OOM is worse than a soft zoom.
            var capped = DimensionGuard.ClampLongEdge(longEdge, EffectiveAspectRatio);
            DimensionLimited = capped < longEdge;
            longEdge = capped;

            // Cancel any in-flight decode but KEEP whatever is already on screen. This method is
            // re-entered for the same photo when the stage resizes (the fullscreen transition
            // above all), and clearing here would drop the zoom image back to the display tier
            // for the length of the new decode - a visible softening flash mid-zoom.
            CancelZoomDecode();

            var cts = new CancellationTokenSource();
            _zoomCts = cts;

            // Raised before the decode starts and cleared in the finally below, so EVERY exit -
            // success, cancellation, failure, or a resize superseding this request - takes the
            // indicator down. A loading mark that outlived its decode would be a permanent
            // artefact on a photo that is no longer loading anything.
            IsZoomLoading = true;

            try
            {
                // NO ConfigureAwait(false) here, deliberately. This method is started from the UI
                // thread, so an uncaptured context resumes on the thread pool - and everything
                // below touches SoftwareBitmapSource, which is a XAML type with UI-thread
                // affinity. Constructing it off-thread threw a COMException that the catch below
                // used to swallow, so the zoom image was never assigned and the stage silently
                // kept rendering the ~960px display tier upscaled.
                //
                // The pixel decode still happens off the UI thread: ThumbnailService and
                // RawPreviewDecoder each use ConfigureAwait(false) internally, so only the
                // resumption after the decode comes back here. Same shape as
                // LoadDisplayImageAsync, which has always been correct for this reason.
                var bitmap = await DecodeZoomTierAsync(longEdge, cts.Token);

                if (bitmap is null || cts.IsCancellationRequested) return;

                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(bitmap);

                if (cts.IsCancellationRequested) return;

                // Already on the UI thread, per the note above, so this is a direct assignment.
                _zoomImageBytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;   // BGRA8
                ZoomImage = source;
            }
            catch (OperationCanceledException)
            {
                // Zoom exited or the photo changed before the decode landed - not a failure.
            }
            catch (Exception ex)
            {
                // Swallowing keeps a failed zoom from taking the app down mid-cull, but it must
                // leave a trace. This exact catch previously logged only to Debug.WriteLine, and
                // a COMException hid in it through three rounds of investigation while the stage
                // quietly rendered an upscaled display-tier image.
                App.LogToFile("ZoomTierFailed",
                    $"{Photo.FileName} at longEdge={longEdge}, "
                    + $"onUiThread={_dispatcherQueue.HasThreadAccess}{Environment.NewLine}{ex}");

                System.Diagnostics.Debug.WriteLine($"[FastCull] Zoom decode failed: {ex}");

                if (System.Diagnostics.Debugger.IsAttached) System.Diagnostics.Debugger.Break();
            }
            finally
            {
                // Only the request that is still current clears the flag. A superseded decode
                // (the stage resized and re-requested) must not switch the indicator off while
                // its replacement is still running.
                if (ReferenceEquals(_zoomCts, cts)) IsZoomLoading = false;
            }
        }

        /// <summary>
        /// Picks the zoom decoder the same way DecodeTierAsync picks the display one: RAW through
        /// RawPreviewDecoder's embedded-JPEG path, with WIC as the fallback, everything else
        /// straight through WIC.
        ///
        /// RAW used to be excluded here on the premise that it had no decode pipeline. It has had
        /// one since the embedded-preview work, and the containers carry a full-sensor-width JPEG
        /// - so the exclusion was capping 96 of the 100 sample files at the ~960px display tier
        /// for no reason.
        /// </summary>
        private async Task<SoftwareBitmap?> DecodeZoomTierAsync(uint longEdge, CancellationToken cancellationToken)
        {
            if (Photo.Family != FormatFamily.Raw)
                return await ThumbnailService.DecodeZoomImageAsync(Photo.FilePath, longEdge, cancellationToken)
                                             .ConfigureAwait(false);

            var raw = await RawPreviewDecoder.DecodeZoomImageAsync(Photo.FilePath, longEdge, cancellationToken)
                                             .ConfigureAwait(false);
            if (raw is not null) return raw;

            // Same last resort the display tier uses: WIC can decode RAW directly, but only when
            // the Store Raw Image Extension happens to be installed.
            return await ThumbnailService.DecodeZoomImageAsync(Photo.FilePath, longEdge, cancellationToken)
                                         .ConfigureAwait(false);
        }

        /// <summary>
        /// Drops the zoom-tier image and cancels any decode still in flight.
        ///
        /// The reference is released rather than Disposed on purpose. Disposing the underlying
        /// SoftwareBitmap after SetBitmapAsync is exactly what fail-fasted the process with
        /// 0xC000027B before - XAML copies those pixels into a composition surface on its own
        /// worker thread, and destroying the bitmap out from under that copy is fatal. Dropping
        /// the last reference is what makes it collectable, and is the safe equivalent here.
        /// </summary>
        public void ReleaseZoomImage()
        {
            CancelZoomDecode();
            ZoomImage = null;
            _zoomImageBytes = 0;
        }

        /// <summary>Stops an in-flight zoom decode without disturbing the image already shown.</summary>
        private void CancelZoomDecode()
        {
            _zoomCts?.Cancel();
            _zoomCts?.Dispose();
            _zoomCts = null;

            // Cleared here as well as in LoadZoomImageAsync's finally, and this is the path that
            // matters: nulling _zoomCts above means the cancelled decode's own finally can no
            // longer recognise itself as current, so without this the indicator would survive its
            // decode - stuck on a photo that has stopped loading. A re-request raises it again
            // immediately afterwards, so a supersede does not flicker.
            IsZoomLoading = false;
        }

        /// <summary>
        /// Exposed as a Visibility so the top slots can bind it through a nested path
        /// (ViewModel.SlotNItem.DisplayImageFailedVisibility) rather than through a converter on
        /// the item reference. A converter on the reference only re-evaluates when a different
        /// item enters the slot, so a decode that fails while the photo is already on screen
        /// would never show its placeholder. Same bug class as the star badge below.
        /// </summary>
        public Visibility DisplayImageFailedVisibility => DisplayImageFailed ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Cancels any in-flight thumbnail/display loads for this item. Safe to call even
        /// after they've completed. Cancellation is observed cooperatively between decode stages
        /// in ThumbnailService, never mid-WinRT-call, so nothing native is ever left orphaned.</summary>
        public void Dispose()
        {
            CancelLoad();
            CancelThumbnailLoad();
            ReleaseZoomImage();
        }

        /// <summary>
        /// RAW goes through RawPreviewDecoder, which lifts the embedded JPEG preview out of the
        /// container rather than debayering (PRD 3.2 needs only the thumbnail and display tiers,
        /// and both are already present as JPEG inside .ARW/.CR2). If that returns null the file
        /// gets one attempt through WIC, which can decode RAW directly but only when the Store
        /// Raw Image Extension happens to be installed - a fallback, never the primary path.
        /// Anything still null lands on the error placeholder: PRD 1.1 says a file that fails to
        /// decode is shown as such, never silently dropped.
        /// </summary>
        private Task<SoftwareBitmap?> DecodeTierAsync(bool displayTier, CancellationToken cancellationToken)
        {
            // Through the gate: PRD 3.3 caps concurrent decodes at min(6, coreCount - 2). The
            // sliding window decides which photos to decode; this decides how many run at once.
            return DecodeGate.RunAsync(() =>
            {
                if (Photo.Family != FormatFamily.Raw)
                {
                    return displayTier
                        ? ThumbnailService.DecodeDisplayImageAsync(Photo.FilePath, cancellationToken)
                        : ThumbnailService.DecodeThumbnailAsync(Photo.FilePath, cancellationToken);
                }

                return DecodeRawWithFallbackAsync(displayTier, cancellationToken);
            }, cancellationToken);
        }

        private async Task<SoftwareBitmap?> DecodeRawWithFallbackAsync(bool displayTier, CancellationToken cancellationToken)
        {
            var bitmap = displayTier
                ? await RawPreviewDecoder.DecodeDisplayImageAsync(Photo.FilePath, cancellationToken)
                : await RawPreviewDecoder.DecodeThumbnailAsync(Photo.FilePath, cancellationToken);

            if (bitmap is not null) return bitmap;

            return displayTier
                ? await ThumbnailService.DecodeDisplayImageAsync(Photo.FilePath, cancellationToken)
                : await ThumbnailService.DecodeThumbnailAsync(Photo.FilePath, cancellationToken);
        }

        private async Task LoadThumbnailAsync(CancellationToken cancellationToken)
        {
            try
            {
                var bitmap = await DecodeTierAsync(displayTier: false, cancellationToken);
                await ApplyDecodeResultAsync(bitmap, b => Thumbnail = b, () => ThumbnailFailed = true);
            }
            catch (OperationCanceledException)
            {
                // Superseded/torn down before the decode finished - not a failure, just stop.
            }
            catch (Exception)
            {
                _dispatcherQueue.TryEnqueue(() => { try { ThumbnailFailed = true; } catch { } });
            }
        }

        private async Task LoadDisplayImageAsync(CancellationToken cancellationToken)
        {
            try
            {
                var bitmap = await DecodeTierAsync(displayTier: true, cancellationToken);

                // Publish the real aspect before the image itself, so the stage's shared-height
                // pass runs against true dimensions rather than the 3:2 guess and the photo does
                // not visibly resize a frame after appearing.
                if (bitmap is not null && bitmap.PixelHeight > 0)
                {
                    var ratio = bitmap.PixelWidth / (double)bitmap.PixelHeight;
                    _dispatcherQueue.TryEnqueue(() => { try { AspectRatio = ratio; } catch { } });
                }

                await ApplyDecodeResultAsync(bitmap, b => DisplayImage = b, () => DisplayImageFailed = true);
            }
            catch (OperationCanceledException)
            {
                // Superseded/torn down before the decode finished - not a failure, just stop.
            }
            catch (Exception)
            {
                _dispatcherQueue.TryEnqueue(() => { try { DisplayImageFailed = true; } catch { } });
            }
        }

        /// <summary>
        /// Turns a decoded SoftwareBitmap into a bindable ImageSource and applies it.
        /// SetBitmapAsync - itself a WinRT async call - is awaited here in the caller's already
        /// background context, never inside a DispatcherQueue callback: the callback that follows
        /// is a plain synchronous delegate with no async work in flight, so nothing can be
        /// abandoned mid-await across that native callback boundary (same failure class as
        /// documented at the top of ThumbnailService.cs, different call site).
        /// </summary>
        private async Task ApplyDecodeResultAsync(SoftwareBitmap? bitmap, Action<ImageSource> setImage, Action setFailed)
        {
            if (bitmap is null)
            {
                _dispatcherQueue.TryEnqueue(() => { try { setFailed(); } catch { } });
                return;
            }

            SoftwareBitmapSource source;
            try
            {
                source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(bitmap);
            }
            catch (Exception)
            {
                bitmap.Dispose();
                _dispatcherQueue.TryEnqueue(() => { try { setFailed(); } catch { } });
                return;
            }

            // NOTE: the SoftwareBitmap is deliberately NOT disposed once SetBitmapAsync has
            // succeeded. Assigning the SoftwareBitmapSource to an Image starts an ASYNCHRONOUS
            // copy of the pixels into a composition surface, which XAML runs on its own native
            // worker thread. Disposing the bitmap here destroyed it out from under that copy,
            // and the resulting COM failure was stowed and fail-fasted the process - exit code
            // 0xC000027B, with this native stack from C:\dumps\Fastcull.exe_260823_034244.dmp:
            //     Microsoft_UI_Xaml!AsyncCopyToSurfaceTask::CopyOperation
            //     Microsoft_UI_Xaml!AsyncCopyToSurfaceTask::Execute
            //     Microsoft_UI_Xaml!AsyncImageFactory::WorkCallback
            //     ntdll!TppWorkerThread
            // The bitmap's lifetime therefore belongs to the SoftwareBitmapSource from here on;
            // it is reclaimed by the GC/finalizer. (A real LRU cache lands with PRD 3.3.)
            var enqueued = _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    setImage(source);
                }
                catch (Exception)
                {
                    try { setFailed(); } catch { }
                }
            });

            if (!enqueued)
            {
                _dispatcherQueue.TryEnqueue(() => { try { setFailed(); } catch { } });
            }
        }
    }
}
