namespace PdfPecker.Rendering.Graphics;

/// <summary>The current clip: a device rectangle, optionally refined by a coverage mask.</summary>
internal sealed class ClipRegion
{
    public ClipRegion(int x0, int y0, int x1, int y1, byte[]? mask = null, int maskX0 = 0, int maskY0 = 0, int maskStride = 0)
    {
        X0 = x0;
        Y0 = y0;
        X1 = Math.Max(x0, x1);
        Y1 = Math.Max(y0, y1);
        Mask = mask;
        MaskX0 = maskX0;
        MaskY0 = maskY0;
        MaskStride = maskStride;
    }

    public int X0 { get; }

    public int Y0 { get; }

    public int X1 { get; }

    public int Y1 { get; }

    /// <summary>Coverage inside the rectangle, one byte per pixel, or null when the whole rectangle is inside the clip.</summary>
    public byte[]? Mask { get; }

    /// <summary>The device position of the mask's top left pixel.</summary>
    public int MaskX0 { get; }

    public int MaskY0 { get; }

    /// <summary>The number of bytes in one row of the mask.</summary>
    public int MaskStride { get; }

    public bool IsEmpty => X1 <= X0 || Y1 <= Y0;

    public ClipRegion IntersectRect(int x0, int y0, int x1, int y1)
    {
        int newX0 = Math.Max(X0, x0), newY0 = Math.Max(Y0, y0);
        int newX1 = Math.Min(X1, x1), newY1 = Math.Min(Y1, y1);
        if (newX0 == X0 && newY0 == Y0 && newX1 == X1 && newY1 == Y1) return this;
        return new ClipRegion(newX0, newY0, newX1, newY1, Mask, MaskX0, MaskY0, MaskStride);
    }

    /// <summary>Intersects with a path given as rasterized coverage.</summary>
    public ClipRegion IntersectPath(FlatPath devicePath, bool evenOdd, Rasterizer raster)
    {
        int left = Math.Max(X0, (int)MathF.Floor(devicePath.MinX)), top = Math.Max(Y0, (int)MathF.Floor(devicePath.MinY));
        int right = Math.Min(X1, (int)MathF.Ceiling(devicePath.MaxX) + 1), bottom = Math.Min(Y1, (int)MathF.Ceiling(devicePath.MaxY) + 1);
        if (devicePath.Count == 0 || right <= left || bottom <= top) return Empty();

        int width = right - left, height = bottom - top;
        var mask = new byte[width * height];
        if (raster.Begin(left, top, right, bottom))
        {
            raster.AddPath(devicePath);
            raster.Render(evenOdd, new MaskWriter(mask, left, top, width));
        }
        if (Mask != null) MultiplyByCurrentMask(mask, left, top, width, height);

        if (!TryFindCoveredBounds(mask, width, height, out int minX, out int minY, out int maxX, out int maxY)) return Empty();
        return new ClipRegion(left + minX, top + minY, left + maxX + 1, top + maxY + 1, mask, left, top, width);
    }

    private static ClipRegion Empty() => new(0, 0, 0, 0);

    /// <summary>Scales the new mask, whose top left pixel is at (<paramref name="left"/>, <paramref name="top"/>), by this clip's own mask.</summary>
    private void MultiplyByCurrentMask(byte[] mask, int left, int top, int width, int height)
    {
        var current = Mask!;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int source = (y + top - MaskY0) * MaskStride + (left - MaskX0);
            for (int x = 0; x < width; x++)
            {
                int coverage = mask[row + x];
                if (coverage != 0) mask[row + x] = (byte)((coverage * current[source + x] + 127) / 255);
            }
        }
    }

    /// <summary>Finds the rows and columns of the mask that are not fully clipped away, so the clip can shrink its rectangle to them.</summary>
    private static bool TryFindCoveredBounds(byte[] mask, int width, int height, out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = width;
        minY = height;
        maxX = -1;
        maxY = -1;
        for (int y = 0; y < height; y++)
        {
            var row = mask.AsSpan(y * width, width);
            int first = row.IndexOfAnyExcept((byte)0);
            if (first < 0) continue;
            minX = Math.Min(minX, first);
            maxX = Math.Max(maxX, row.LastIndexOfAnyExcept((byte)0));
            minY = Math.Min(minY, y);
            maxY = y;
        }
        return maxX >= 0;
    }

    /// <summary>Writes rasterized coverage into a clip mask.</summary>
    private sealed class MaskWriter(byte[] mask, int x0, int y0, int stride) : ICoverageSink
    {
        public void Row(int y, int rowX0, ReadOnlySpan<byte> cov) => cov.CopyTo(mask.AsSpan((y - y0) * stride + rowX0 - x0));

        public void Span(int y, int spanX0, int spanX1, byte cov) => mask.AsSpan((y - y0) * stride + spanX0 - x0, spanX1 - spanX0).Fill(cov);
    }
}
