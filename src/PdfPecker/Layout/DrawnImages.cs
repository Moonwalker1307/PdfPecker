using PdfPecker.Css;
using PdfPecker.Images;

namespace PdfPecker.Layout;

/// <summary>The version of an image that is written for one place it is drawn, as image-rendering and the image quality properties ask.</summary>
internal static class DrawnImages
{
    /// <summary>
    /// The image to write for <paramref name="image"/> drawn <paramref name="width"/> by <paramref name="height"/> points
    /// in style <paramref name="s"/>: enlarged with hard pixel edges for image-rendering: pixelated, otherwise downsampled
    /// and recompressed for -pdfpecker-image-max-resolution and -pdfpecker-image-quality, or the image itself.
    /// </summary>
    public static PdfImage For(ComputedStyle s, PdfImage image, float width, float height)
    {
        if (s.ImagePixelated)
        {
            var pixelated = image.Pixelated(width, height);
            if (!ReferenceEquals(pixelated, image)) return pixelated;
        }
        return s.ImageQuality > 0 || s.ImageMaxDpi > 0 ? image.Compressed(width, height, s.ImageQuality, s.ImageMaxDpi) : image;
    }
}
