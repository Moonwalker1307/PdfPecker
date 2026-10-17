namespace PdfPecker.Rendering.Images;

/// <summary>A decoded raster image: premultiplied RGBA pixels, or (for stencil masks) coverage only.</summary>
internal sealed class DecodedImage
{
    public int Width, Height;
    public uint[]? Pixels;   // premultiplied RGBA, row 0 = top
    public byte[]? Stencil;  // ImageMask: 255 = paint with the fill color
    public bool Interpolate;
    public bool Opaque;

    private DecodedImage? _half;

    /// <summary>A copy at half resolution (box filter), for drawing images much smaller than their pixel size.</summary>
    public DecodedImage Half()
    {
        if (_half != null) return _half;
        int w = Math.Max(1, Width / 2), h = Math.Max(1, Height / 2);
        var half = new DecodedImage { Width = w, Height = h, Interpolate = Interpolate, Opaque = Opaque };
        if (Pixels != null)
        {
            var src = Pixels;
            var dst = new uint[w * h];
            for (int y = 0; y < h; y++)
            {
                // An odd last row or column is averaged with itself.
                int top = Math.Min(Height - 1, y * 2) * Width, bottom = Math.Min(Height - 1, y * 2 + 1) * Width;
                for (int x = 0; x < w; x++)
                {
                    int left = Math.Min(Width - 1, x * 2), right = Math.Min(Width - 1, x * 2 + 1);
                    dst[y * w + x] = Average(src[top + left], src[top + right], src[bottom + left], src[bottom + right]);
                }
            }
            half.Pixels = dst;
        }
        if (Stencil != null)
        {
            var src = Stencil;
            var dst = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int top = Math.Min(Height - 1, y * 2) * Width, bottom = Math.Min(Height - 1, y * 2 + 1) * Width;
                for (int x = 0; x < w; x++)
                {
                    int left = Math.Min(Width - 1, x * 2), right = Math.Min(Width - 1, x * 2 + 1);
                    dst[y * w + x] = (byte)((src[top + left] + src[top + right] + src[bottom + left] + src[bottom + right] + 2) >> 2);
                }
            }
            half.Stencil = dst;
        }
        Interlocked.CompareExchange(ref _half, half, null);
        return _half;
    }

    /// <summary>The rounded average of four pixels, two channels at a time in the even and odd bytes.</summary>
    private static uint Average(uint a, uint b, uint c, uint d)
    {
        const uint EvenBytes = 0x00FF00FF, Rounding = 0x00020002;
        uint rb = ((a & EvenBytes) + (b & EvenBytes) + (c & EvenBytes) + (d & EvenBytes) + Rounding) >> 2 & EvenBytes;
        uint ag = (((a >> 8) & EvenBytes) + ((b >> 8) & EvenBytes) + ((c >> 8) & EvenBytes) + ((d >> 8) & EvenBytes) + Rounding) >> 2 & EvenBytes;
        return rb | ag << 8;
    }

    public long ByteSize => (Pixels?.LongLength ?? 0) * 4 + (Stencil?.LongLength ?? 0);
}
