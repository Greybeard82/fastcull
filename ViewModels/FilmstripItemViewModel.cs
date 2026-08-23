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
    public partial class FilmstripItemViewModel : ObservableObject, IDisposable
    {
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly CancellationTokenSource _loadCts = new();

        public FilmstripItemViewModel(ScannedPhoto photo, int index)
        {
            Photo = photo;
            Index = index;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            if (photo.Family is FormatFamily.Jpeg or FormatFamily.Png or FormatFamily.Raw)
            {
                _ = LoadThumbnailAsync(_loadCts.Token);
                _ = LoadDisplayImageAsync(_loadCts.Token);
            }
        }

        public ScannedPhoto Photo { get; }
        public int Index { get; }

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
        private ImageSource? _displayImage;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayImageFailedVisibility))]
        private bool _displayImageFailed;

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
            _loadCts.Cancel();
            _loadCts.Dispose();
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
            if (Photo.Family != FormatFamily.Raw)
            {
                return displayTier
                    ? ThumbnailService.DecodeDisplayImageAsync(Photo.FilePath, cancellationToken)
                    : ThumbnailService.DecodeThumbnailAsync(Photo.FilePath, cancellationToken);
            }

            return DecodeRawWithFallbackAsync(displayTier, cancellationToken);
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
