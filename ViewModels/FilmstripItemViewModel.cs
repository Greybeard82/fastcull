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

            // RAW stays blank until LibRaw is wired up (a later task) - never attempted here,
            // so it never shows either *Failed error state either.
            if (photo.Family is FormatFamily.Jpeg or FormatFamily.Png)
            {
                _ = LoadThumbnailAsync(_loadCts.Token);
                _ = LoadDisplayImageAsync(_loadCts.Token);
            }
        }

        public ScannedPhoto Photo { get; }
        public int Index { get; }

        public string FileName => Photo.FileName;
        public string FormatLabel => Path.GetExtension(Photo.FileName).TrimStart('.').ToUpperInvariant();

        [ObservableProperty]
        private bool _isActive;

        /// <summary>Position on the PRD 1.6 cull ladder. Set only via MainViewModel.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateBorderBrush))]
        [NotifyPropertyChangedFor(nameof(StarBadgeText))]
        [NotifyPropertyChangedFor(nameof(IsStarBadgeVisible))]
        private CullState _cullState = CullState.Default;

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

        private async Task LoadThumbnailAsync(CancellationToken cancellationToken)
        {
            try
            {
                var bitmap = await ThumbnailService.DecodeThumbnailAsync(Photo.FilePath, cancellationToken);
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
                var bitmap = await ThumbnailService.DecodeDisplayImageAsync(Photo.FilePath, cancellationToken);
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
