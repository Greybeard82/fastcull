using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Fastcull.Services;

namespace Fastcull.ViewModels
{
    public partial class FilmstripItemViewModel : ObservableObject
    {
        public FilmstripItemViewModel(ScannedPhoto photo, int index)
        {
            Photo = photo;
            Index = index;
        }

        public ScannedPhoto Photo { get; }
        public int Index { get; }

        public string FileName => Photo.FileName;
        public string FormatLabel => Path.GetExtension(Photo.FileName).TrimStart('.').ToUpperInvariant();

        [ObservableProperty]
        private bool _isActive;
    }
}
