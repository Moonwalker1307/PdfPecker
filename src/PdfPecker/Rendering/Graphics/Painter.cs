namespace PdfPecker.Rendering.Graphics;

/// <summary>Composites coverage from the rasterizer onto a canvas with a paint, a clip and a constant alpha.</summary>
internal sealed partial class Painter : ICoverageSink
{
    private const int FullCoverage = 255;
    private const int ScratchSize = 1024;

    // Scratch rows, grown on demand and reused so that painting a row does not allocate.
    private uint[] _colors = new uint[ScratchSize];
    private byte[] _coverage = new byte[ScratchSize];

    public Canvas Canvas { get; set; } = null!;

    public ClipRegion Clip { get; set; } = null!;

    /// <summary>Premultiplied solid color, used when <see cref="Shader"/> is null.</summary>
    public uint Color { get; set; }

    public IShader? Shader { get; set; }

    /// <summary>Constant opacity, 0 to 255.</summary>
    public int Alpha { get; set; } = FullCoverage;

    public BlendMode Blend { get; set; }

    /// <summary>Optional full-canvas soft mask (from an ExtGState /SMask).</summary>
    public byte[]? SoftMask { get; set; }

    /// <summary>True when painting is a plain overwrite with an opaque solid color: no mask, shader, blend mode or transparency.</summary>
    private bool OverwritesWithSolidColor
        => Clip.Mask == null && SoftMask == null && Alpha == FullCoverage && Shader == null && Blend == BlendMode.Normal && Color >> 24 == 255;

    public void Row(int y, int x0, ReadOnlySpan<byte> cov)
    {
        int x1 = x0 + cov.Length;
        int clippedX0 = Math.Max(x0, Clip.X0), clippedX1 = Math.Min(x1, Clip.X1);
        if (clippedX1 <= clippedX0 || y < Clip.Y0 || y >= Clip.Y1) return;
        Composite(y, clippedX0, cov.Slice(clippedX0 - x0, clippedX1 - clippedX0));
    }

    public void Span(int y, int x0, int x1, byte cov)
    {
        int clippedX0 = Math.Max(x0, Clip.X0), clippedX1 = Math.Min(x1, Clip.X1);
        if (clippedX1 <= clippedX0 || y < Clip.Y0 || y >= Clip.Y1) return;
        int count = clippedX1 - clippedX0;
        if (cov == FullCoverage && OverwritesWithSolidColor)
        {
            Canvas.Pixels.AsSpan(y * Canvas.Width + clippedX0, count).Fill(Color);
            return;
        }
        EnsureCoverageCapacity(count);
        var row = _coverage.AsSpan(0, count);
        row.Fill(cov);
        Composite(y, clippedX0, row);
    }

    /// <summary>Fills a device-space axis-aligned rectangle with exact fractional edge coverage.</summary>
    public void FillRect(float fx0, float fy0, float fx1, float fy1)
    {
        if (fx0 > fx1) (fx0, fx1) = (fx1, fx0);
        if (fy0 > fy1) (fy0, fy1) = (fy1, fy0);
        int x0 = Math.Max(Clip.X0, (int)MathF.Floor(fx0)), x1 = Math.Min(Clip.X1, (int)MathF.Ceiling(fx1));
        int y0 = Math.Max(Clip.Y0, (int)MathF.Floor(fy0)), y1 = Math.Min(Clip.Y1, (int)MathF.Ceiling(fy1));
        if (x1 <= x0 || y1 <= y0) return;

        int width = x1 - x0;
        EnsureCoverageCapacity(width * 2);
        // The first half of the scratch buffer holds each row's coverage, the second half the horizontal profile.
        var horizontal = _coverage.AsSpan(width, width);
        for (int x = x0; x < x1; x++)
        {
            float overlap = MathF.Min(fx1, x + 1) - MathF.Max(fx0, x);
            horizontal[x - x0] = (byte)ToCoverage(overlap);
        }
        bool fullWidthEdges = horizontal[0] == FullCoverage && horizontal[width - 1] == FullCoverage;
        for (int y = y0; y < y1; y++)
        {
            float overlap = MathF.Min(fy1, y + 1) - MathF.Max(fy0, y);
            int vertical = ToCoverage(overlap);
            if (vertical == 0) continue;
            if (vertical == FullCoverage && fullWidthEdges)
            {
                Span(y, x0, x1, FullCoverage);
                continue;
            }
            var row = _coverage.AsSpan(0, width);
            for (int i = 0; i < width; i++) row[i] = (byte)((horizontal[i] * vertical + 127) / 255);
            Composite(y, x0, row);
        }
    }

    /// <summary>The fraction of a pixel that is covered (clamped to 0..1) as a coverage value of 0 to 255.</summary>
    private static int ToCoverage(float fraction) => (int)(Math.Clamp(fraction, 0, 1) * FullCoverage + 0.5f);

    private void EnsureCoverageCapacity(int length)
    {
        if (_coverage.Length < length) _coverage = new byte[Math.Max(length, _coverage.Length * 2)];
    }
}
