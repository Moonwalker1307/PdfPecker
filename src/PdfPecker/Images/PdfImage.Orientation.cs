namespace PdfPecker.Images;

internal sealed partial class PdfImage
{
    // Turned photos are encoded again at this quality, which keeps full-resolution colour (no chroma subsampling).
    private const int UprightJpegQuality = 95;

    /// <summary>
    /// The image as its file stores it, before its EXIF orientation was applied, for image-orientation: none; null when
    /// the image needed no turning.
    /// </summary>
    public PdfImage? AsStored;

    /// <summary>
    /// A JPEG image turned and flipped as its EXIF orientation says, as browsers show it (image-orientation: from-image),
    /// with the stored image kept in <see cref="AsStored"/> and the EXIF block kept with orientation 1. A camera writes the photo as the sensor saw it and records in
    /// EXIF how to hold it, so a portrait photo from a phone is stored on its side. PDF has no such flag, so the pixels
    /// are turned and encoded again. The image itself is returned when it needs no turning or cannot be turned here:
    /// CMYK, too large, or data the decoder does not read.
    /// </summary>
    private static PdfImage? Upright(PdfImage? image, byte[] jpeg)
    {
        if (image == null) return null;
        int orientation = ExifReader.ReadOrientation(jpeg);
        if (orientation is < 2 or > 8) return image;
        if (image.ColorSpace == DeviceCmyk || (long)image.Width * image.Height > MaxPixels) return image;
        if (Rendering.Images.JpegDecoder.Decode(jpeg) is not { } decoded || decoded.Width != image.Width || decoded.Height != image.Height
            || decoded.Components is not (1 or 3) || decoded.Data.Length < (long)decoded.Width * decoded.Height * decoded.Components) return image;

        int w = decoded.Width, h = decoded.Height, n = decoded.Components;
        bool swap = orientation >= 5;
        int ow = swap ? h : w, oh = swap ? w : h;
        if (ow > Rendering.JpegEncoder.MaxDimension || oh > Rendering.JpegEncoder.MaxDimension) return image;
        var pixels = new uint[ow * oh];
        var data = decoded.Data;
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                // The stored pixel that lands at (x, y) once the photo is held as EXIF says.
                var (sx, sy) = orientation switch
                {
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),
                    6 => (y, h - 1 - x),
                    7 => (w - 1 - y, h - 1 - x),
                    _ => (w - 1 - y, x),
                };
                int at = (sy * w + sx) * n;
                uint r = data[at], g = n == 3 ? data[at + 1] : r, b = n == 3 ? data[at + 2] : r;
                pixels[y * ow + x] = 0xFF000000u | b << 16 | g << 8 | r;
            }
        }
        var encoded = n == 1
            ? Rendering.JpegEncoder.EncodeGray(pixels, ow, oh, UprightJpegQuality)
            : Rendering.JpegEncoder.Encode(pixels, ow, oh, UprightJpegQuality, 0xFFFFFF);
        // The EXIF block goes along, so that the camera, date and place stay readable, saying the photo is now upright.
        if (ExifReader.UprightSegment(jpeg) is { } exif) encoded = [.. encoded.AsSpan(0, 2), .. exif, .. encoded.AsSpan(2)];
        if (FromJpeg(encoded) is not { } upright) return image;
        upright.AsStored = image;
        return upright;
    }
}
