using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Fastcull.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

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

        /// <summary>Small (~160px) decode for the bottom scrubber strip.</summary>
        [ObservableProperty]
        private ImageSource? _thumbnail;

        [ObservableProperty]
        private bool _thumbnailFailed;

        /// <summary>Larger "display tier" decode for the top three-slot comparison view - a
        /// separate decode from Thumbnail, never the same bitmap, per PRD 3.2.</summary>
        [ObservableProperty]
        private ImageSource? _displayImage;

        [ObservableProperty]
        private bool _displayImageFailed;

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
                ApplyDecodeResult(bitmap, b => Thumbnail = b, () => ThumbnailFailed = true);
            }
            catch (OperationCanceledException)
            {
                // Superseded/torn down before the decode finished - not a failure, just stop.
            }
            catch (Exception)
            {
                _dispatcherQueue.TryEnqueue(() => ThumbnailFailed = true);
            }
        }

        private async Task LoadDisplayImageAsync(CancellationToken cancellationToken)
        {
            try
            {
                var bitmap = await ThumbnailService.DecodeDisplayImageAsync(Photo.FilePath, cancellationToken);
                ApplyDecodeResult(bitmap, b => DisplayImage = b, () => DisplayImageFailed = true);
            }
            catch (OperationCanceledException)
            {
                // Superseded/torn down before the decode finished - not a failure, just stop.
            }
            catch (Exception)
            {
                _dispatcherQueue.TryEnqueue(() => DisplayImageFailed = true);
            }
        }

        private void ApplyDecodeResult(SoftwareBitmap? bitmap, Action<ImageSource> setImage, Action setFailed)
        {
            if (bitmap is null)
            {
                _dispatcherQueue.TryEnqueue(() => setFailed());
                return;
            }

            var enqueued = _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(bitmap);
                    setImage(source);
                }
                catch (Exception)
                {
                    setFailed();
                }
                finally
                {
                    bitmap.Dispose();
                }
            });

            if (!enqueued)
            {
                // Queue isn't accepting work (e.g. shutting down) - the callback above never
                // runs, so nothing else will dispose this native bitmap.
                bitmap.Dispose();
            }
        }
    }
}
