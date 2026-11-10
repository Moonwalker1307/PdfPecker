namespace PdfPecker.Images;

/// <summary>
/// Decoded pixels as the library embeds them: 8-bit samples, one (grey) or three (RGB) per pixel, rows from the top
/// without padding, and an optional alpha plane of one byte per pixel (straight, not premultiplied).
/// </summary>
internal sealed record ImagePlanes(int Width, int Height, byte[] Color, int Colors, byte[]? Alpha)
{
    /// <summary>Whether the alpha plane makes any pixel less than opaque.</summary>
    public bool HasTransparency => Alpha != null && Alpha.AsSpan(0, Width * Height).ContainsAnyExcept((byte)255);

    /// <summary>
    /// The planes turned and flipped as an EXIF orientation of 2 to 8 says (the stored pixel that lands at each place
    /// once the photo is held upright), losslessly; the planes themselves for orientation 1 or an unknown one.
    /// </summary>
    public ImagePlanes Oriented(int orientation)
    {
        if (orientation is < 2 or > 8) return this;
        int w = Width, h = Height;
        bool swap = orientation >= 5;
        int ow = swap ? h : w, oh = swap ? w : h;
        return new ImagePlanes(ow, oh, Orient(Color, Colors, w, h, ow, oh, orientation), Colors,
            Alpha == null ? null : Orient(Alpha, 1, w, h, ow, oh, orientation));
    }

    private static byte[] Orient(byte[] source, int channels, int w, int h, int ow, int oh, int orientation)
    {
        var result = new byte[ow * oh * channels];
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
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
                source.AsSpan((sy * w + sx) * channels, channels).CopyTo(result.AsSpan((y * ow + x) * channels));
            }
        }
        return result;
    }
}
