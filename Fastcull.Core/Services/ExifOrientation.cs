using System;
using System.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Windows.Graphics.Imaging;

namespace Fastcull.Services
{
    /// <summary>
    /// Reads a file's EXIF orientation and converts it into the decode-time transform that puts
    /// the photo upright.
    ///
    /// This exists for RAW specifically. A JPEG carries its orientation in its own EXIF, and WIC
    /// applies it for free via <see cref="ExifOrientationMode.RespectExifOrientation"/>. A RAW
    /// container does not work that way: the orientation lives in the container's TIFF IFD0, and
    /// <see cref="RawPreviewDecoder"/> reaches past it to slice out an embedded JPEG stream. That
    /// slice leaves the orientation tag behind, so WIC is handed bytes that claim to be upright
    /// and every portrait RAW rendered on its side.
    ///
    /// Measured against this repo's SampleImages before the fix: nine files tagged orientation 8
    /// (six .CR2, three .ARW) all decoded to 960x640 landscape when they should have been portrait.
    /// </summary>
    public static class ExifOrientation
    {
        /// <summary>EXIF orientation 1: upright, no transform needed.</summary>
        public const int Normal = 1;

        /// <summary>
        /// The file's EXIF orientation, 1-8. Returns <see cref="Normal"/> for anything unreadable,
        /// absent or out of range - an unrotated photo is a far better failure than a guess, and
        /// this must never be able to throw into a decode.
        /// </summary>
        public static int Read(string filePath)
        {
            try
            {
                var directories = ImageMetadataReader.ReadMetadata(filePath);

                // IFD0 is where the camera writes it. The thumbnail IFD carries a copy that can
                // disagree on some bodies, so it is deliberately not consulted.
                foreach (var ifd0 in directories.OfType<ExifIfd0Directory>())
                {
                    if (!ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var value)) continue;
                    return value is >= 1 and <= 8 ? value : Normal;
                }

                return Normal;
            }
            catch (Exception)
            {
                return Normal;
            }
        }

        /// <summary>
        /// The flip and rotation that bring an image stored with this orientation upright.
        ///
        /// The pairing assumes WIC's documented transform order - scale, then flip, then rotate -
        /// which is what makes the two mirrored-diagonal cases expressible at all. Worked through:
        /// orientation 5 wants a transpose, (x,y) -> (y,x); flipping vertically first gives
        /// (x, H-1-y), and rotating that 90 clockwise gives (y, x). Flipping *horizontally* first
        /// would land on (H-1-y, W-1-x), which is the transverse and belongs to 7.
        /// </summary>
        public static (BitmapFlip Flip, BitmapRotation Rotation) ToTransform(int orientation) => orientation switch
        {
            2 => (BitmapFlip.Horizontal, BitmapRotation.None),
            3 => (BitmapFlip.None, BitmapRotation.Clockwise180Degrees),
            4 => (BitmapFlip.Vertical, BitmapRotation.None),
            5 => (BitmapFlip.Vertical, BitmapRotation.Clockwise90Degrees),
            6 => (BitmapFlip.None, BitmapRotation.Clockwise90Degrees),
            7 => (BitmapFlip.Vertical, BitmapRotation.Clockwise270Degrees),
            8 => (BitmapFlip.None, BitmapRotation.Clockwise270Degrees),
            _ => (BitmapFlip.None, BitmapRotation.None),
        };

        /// <summary>
        /// True when this orientation turns the image a quarter turn, so the decoded width and
        /// height come back swapped relative to the stored ones.
        /// </summary>
        public static bool SwapsDimensions(int orientation) => orientation is 5 or 6 or 7 or 8;

        /// <summary>True when the orientation calls for any transform at all.</summary>
        public static bool IsRotated(int orientation) => orientation is >= 2 and <= 8;
    }
}
