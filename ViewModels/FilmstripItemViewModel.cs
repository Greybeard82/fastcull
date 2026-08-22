using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Fastcull.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fastcull.ViewModels
{
    public partial class FilmstripItemViewModel : ObservableObject
    {
        private readonly DispatcherQueue _dispatcherQueue;

        public FilmstripItemViewModel(ScannedPhoto photo, int index)
        {
            Photo = photo;
            Index = index;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            // RAW stays blank until LibRaw is wired up (a later task) - never attempted here,
            // so it never shows the ThumbnailFailed error state either.
            if (photo.Family is FormatFamily.Jpeg or FormatFamily.Png)
            {
                _ = LoadThumbnailAsync();
            }
        }

        public ScannedPhoto Photo { get; }
        public int Index { get; }

        public string FileName => Photo.FileName;
        public string FormatLabel => Path.GetExtension(Photo.FileName).TrimStart('.').ToUpperInvariant();

        [ObservableProperty]
        private bool _isActive;

        [ObservableProperty]
        private ImageSource? _thumbnail;

        [ObservableProperty]
        private bool _thumbnailFailed;

        private async Task LoadThumbnailAsync()
        {
            var bitmap = await ThumbnailService.DecodeThumbnailAsync(Photo.FilePath);

            if (bitmap is null)
            {
                _dispatcherQueue.TryEnqueue(() => ThumbnailFailed = true);
                return;
            }

            _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(bitmap);
                    Thumbnail = source;
                }
                catch
                {
                    ThumbnailFailed = true;
                }
                finally
                {
                    bitmap.Dispose();
                }
            });
        }
    }
}
