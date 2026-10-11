using System.Runtime.CompilerServices;

namespace PdfPecker.Rendering.Graphics;

/// <summary>A device bitmap of premultiplied RGBA pixels (memory order R, G, B, A).</summary>
internal sealed class Canvas
{
    public readonly int Width, Height;
    public readonly uint[] Pixels;

    public Canvas(int width, int height, uint background)
    {
        Width = width;
        Height = height;
        Pixels = new uint[checked(width * height)];
        if (background != 0) Array.Fill(Pixels, background);
    }

    /// <summary>Packs a straight (non-premultiplied) color into a premultiplied pixel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Pack(int r, int g, int b, int a = 255)
    {
        if (a < 255)
        {
            r = (r * a + 127) / 255;
            g = (g * a + 127) / 255;
            b = (b * a + 127) / 255;
        }
        return (uint)(r | g << 8 | b << 16 | a << 24);
    }

    /// <summary>Scales all four channels of a premultiplied pixel by <paramref name="a"/>/256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Scale(uint p, uint a)
    {
        uint rb = ((p & 0x00FF00FFu) * a >> 8) & 0x00FF00FFu;
        uint ag = ((p >> 8) & 0x00FF00FFu) * a & 0xFF00FF00u;
        return rb | ag;
    }

    /// <summary>Scales all four channels by <paramref name="k"/>/255 with exact rounding (k in 0..255).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Mul255(uint p, uint k)
    {
        uint rb = (p & 0x00FF00FFu) * k + 0x00800080u;
        rb = ((rb + ((rb >> 8) & 0x00FF00FFu)) >> 8) & 0x00FF00FFu;
        uint ag = ((p >> 8) & 0x00FF00FFu) * k + 0x00800080u;
        ag = (ag + ((ag >> 8) & 0x00FF00FFu)) & 0xFF00FF00u;
        return rb | ag;
    }

    /// <summary>Source-over of premultiplied <paramref name="s"/> onto <paramref name="d"/> (opaque stays exactly opaque).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Over(uint s, uint d)
    {
        uint sa = s >> 24;
        if (sa == 255) return s;
        if (sa == 0) return d;
        return s + Mul255(d, 255 - sa);
    }
}

/// <summary>The current clip: a device rectangle, optionally refined by a coverage mask.</summary>
internal sealed class ClipRegion
{
    public readonly int X0, Y0, X1, Y1;
    public readonly byte[]? Mask;
    public readonly int MaskX0, MaskY0, MaskStride;

    public ClipRegion(int x0, int y0, int x1, int y1, byte[]? mask = null, int maskX0 = 0, int maskY0 = 0, int maskStride = 0)
    {
        X0 = x0; Y0 = y0; X1 = Math.Max(x0, x1); Y1 = Math.Max(y0, y1);
        Mask = mask; MaskX0 = maskX0; MaskY0 = maskY0; MaskStride = maskStride;
    }

    public bool IsEmpty => X1 <= X0 || Y1 <= Y0;

    public ClipRegion IntersectRect(int x0, int y0, int x1, int y1)
    {
        int nx0 = Math.Max(X0, x0), ny0 = Math.Max(Y0, y0), nx1 = Math.Min(X1, x1), ny1 = Math.Min(Y1, y1);
        if (nx0 == X0 && ny0 == Y0 && nx1 == X1 && ny1 == Y1) return this;
        return new ClipRegion(nx0, ny0, nx1, ny1, Mask, MaskX0, MaskY0, MaskStride);
    }

    /// <summary>Intersects with a path given as rasterized coverage.</summary>
    public ClipRegion IntersectPath(FlatPath devicePath, bool evenOdd, Rasterizer raster)
    {
        int bx0 = Math.Max(X0, (int)MathF.Floor(devicePath.MinX)), by0 = Math.Max(Y0, (int)MathF.Floor(devicePath.MinY));
        int bx1 = Math.Min(X1, (int)MathF.Ceiling(devicePath.MaxX) + 1), by1 = Math.Min(Y1, (int)MathF.Ceiling(devicePath.MaxY) + 1);
        if (devicePath.Count == 0 || bx1 <= bx0 || by1 <= by0) return new ClipRegion(0, 0, 0, 0);
        int w = bx1 - bx0, h = by1 - by0;
        var mask = new byte[w * h];
        if (raster.Begin(bx0, by0, bx1, by1))
        {
            raster.AddPath(devicePath);
            raster.Render(evenOdd, new MaskWriter(mask, bx0, by0, w));
        }
        if (Mask != null)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * w, src = (y + by0 - MaskY0) * MaskStride + (bx0 - MaskX0);
                for (int x = 0; x < w; x++)
                {
                    int m = mask[row + x];
                    if (m != 0) mask[row + x] = (byte)((m * Mask[src + x] + 127) / 255);
                }
            }
        }
        // Shrink the bounds to the rows and columns that are not fully clipped away.
        int minX = w, maxX = -1, minY = h, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (mask[row + x] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                maxY = y;
                break;
            }
            if (maxY == y)
                for (int x = w - 1; x > maxX; x--)
                    if (mask[row + x] != 0) { maxX = x; break; }
        }
        if (maxX < 0) return new ClipRegion(0, 0, 0, 0);
        return new ClipRegion(bx0 + minX, by0 + minY, bx0 + maxX + 1, by0 + maxY + 1, mask, bx0, by0, w);
    }

    private sealed class MaskWriter : ICoverageSink
    {
        private readonly byte[] _mask;
        private readonly int _x0, _y0, _stride;
        public MaskWriter(byte[] mask, int x0, int y0, int stride) { _mask = mask; _x0 = x0; _y0 = y0; _stride = stride; }

        public void Row(int y, int x0, ReadOnlySpan<byte> cov) => cov.CopyTo(_mask.AsSpan((y - _y0) * _stride + x0 - _x0));

        public void Span(int y, int x0, int x1, byte cov) => _mask.AsSpan((y - _y0) * _stride + x0 - _x0, x1 - x0).Fill(cov);
    }
}

/// <summary>Produces premultiplied colors for a run of pixels (images, shadings, patterns).</summary>
internal interface IShader
{
    void Shade(int y, int x0, Span<uint> colors);
}

internal enum BlendMode : byte
{
    Normal, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, HardLight, SoftLight, Difference, Exclusion,
    Hue, Saturation, Color, Luminosity,
}

/// <summary>Composites coverage from the rasterizer onto a canvas with a paint, a clip and a constant alpha.</summary>
internal sealed class Painter : ICoverageSink
{
    public Canvas Canvas = null!;
    public ClipRegion Clip = null!;
    public uint Color; // premultiplied solid color, used when Shader is null
    public IShader? Shader;
    public int Alpha = 255;
    public BlendMode Blend;

    /// <summary>Optional full-canvas soft mask (from an ExtGState /SMask).</summary>
    public byte[]? SoftMask;

    private uint[] _colors = new uint[1024];
    private byte[] _tmp = new byte[1024];

    public void Row(int y, int x0, ReadOnlySpan<byte> cov)
    {
        int x1 = x0 + cov.Length;
        int cx0 = Math.Max(x0, Clip.X0), cx1 = Math.Min(x1, Clip.X1);
        if (cx1 <= cx0 || y < Clip.Y0 || y >= Clip.Y1) return;
        Composite(y, cx0, cov.Slice(cx0 - x0, cx1 - cx0));
    }

    public void Span(int y, int x0, int x1, byte cov)
    {
        int cx0 = Math.Max(x0, Clip.X0), cx1 = Math.Min(x1, Clip.X1);
        if (cx1 <= cx0 || y < Clip.Y0 || y >= Clip.Y1) return;
        int n = cx1 - cx0;
        if (cov == 255 && Clip.Mask == null && SoftMask == null && Alpha == 255 && Shader == null && Blend == BlendMode.Normal && Color >> 24 == 255)
        {
            Canvas.Pixels.AsSpan(y * Canvas.Width + cx0, n).Fill(Color);
            return;
        }
        if (_tmp.Length < n) _tmp = new byte[Math.Max(n, _tmp.Length * 2)];
        var t = _tmp.AsSpan(0, n);
        t.Fill(cov);
        Composite(y, cx0, t);
    }

    /// <summary>Fills a device-space axis-aligned rectangle with exact fractional edge coverage.</summary>
    public void FillRect(float fx0, float fy0, float fx1, float fy1)
    {
        if (fx0 > fx1) (fx0, fx1) = (fx1, fx0);
        if (fy0 > fy1) (fy0, fy1) = (fy1, fy0);
        int x0 = Math.Max(Clip.X0, (int)MathF.Floor(fx0)), x1 = Math.Min(Clip.X1, (int)MathF.Ceiling(fx1));
        int y0 = Math.Max(Clip.Y0, (int)MathF.Floor(fy0)), y1 = Math.Min(Clip.Y1, (int)MathF.Ceiling(fy1));
        if (x1 <= x0 || y1 <= y0) return;
        int n = x1 - x0;
        if (_tmp.Length < n * 2) _tmp = new byte[Math.Max(n * 2, _tmp.Length * 2)];
        // Horizontal coverage profile.
        var hcov = _tmp.AsSpan(n, n);
        for (int x = x0; x < x1; x++)
        {
            float c = MathF.Min(fx1, x + 1) - MathF.Max(fx0, x);
            hcov[x - x0] = (byte)(Math.Clamp(c, 0, 1) * 255 + 0.5f);
        }
        bool fullH = hcov[0] == 255 && hcov[n - 1] == 255;
        for (int y = y0; y < y1; y++)
        {
            float cy = MathF.Min(fy1, y + 1) - MathF.Max(fy0, y);
            int vc = (int)(Math.Clamp(cy, 0, 1) * 255 + 0.5f);
            if (vc == 0) continue;
            if (vc == 255 && fullH) { Span(y, x0, x1, 255); continue; }
            var row = _tmp.AsSpan(0, n);
            for (int i = 0; i < n; i++) row[i] = (byte)((hcov[i] * vc + 127) / 255);
            Composite(y, x0, row);
        }
    }

    private void Composite(int y, int x0, ReadOnlySpan<byte> cov)
    {
        int n = cov.Length;
        var px = Canvas.Pixels.AsSpan(y * Canvas.Width + x0, n);
        var clip = Clip;
        ReadOnlySpan<byte> mask = clip.Mask != null ? clip.Mask.AsSpan((y - clip.MaskY0) * clip.MaskStride + (x0 - clip.MaskX0), n) : default;
        ReadOnlySpan<byte> soft = SoftMask != null ? SoftMask.AsSpan(y * Canvas.Width + x0, n) : default;
        bool hasMask = clip.Mask != null, hasSoft = SoftMask != null;
        int alpha = Alpha;

        Span<uint> colors = default;
        if (Shader != null)
        {
            if (_colors.Length < n) _colors = new uint[Math.Max(n, _colors.Length * 2)];
            colors = _colors.AsSpan(0, n);
            Shader.Shade(y, x0, colors);
        }
        uint solid = Color;
        bool opaqueSolid = Shader == null && solid >> 24 == 255;

        if (Blend != BlendMode.Normal)
        {
            for (int i = 0; i < n; i++)
            {
                int c = cov[i];
                if (hasMask) c = c * mask[i] * 257 >> 16;
                if (hasSoft) c = c * soft[i] * 257 >> 16;
                if (alpha != 255) c = c * alpha * 257 >> 16;
                if (c == 0) continue;
                uint s = Shader != null ? colors[i] : solid;
                px[i] = BlendPixel(Canvas.Scale(s, (uint)(c + (c >> 7))), px[i], Blend);
            }
            return;
        }

        if (!hasMask && !hasSoft && alpha == 255)
        {
            if (opaqueSolid)
            {
                for (int i = 0; i < n; i++)
                {
                    int c = cov[i];
                    if (c == 0) continue;
                    if (c == 255) { px[i] = solid; continue; }
                    px[i] = Canvas.Mul255(solid, (uint)c) + Canvas.Mul255(px[i], 255 - (uint)c);
                }
                return;
            }
            for (int i = 0; i < n; i++)
            {
                int c = cov[i];
                if (c == 0) continue;
                uint s = Shader != null ? colors[i] : solid;
                if (c != 255) s = Canvas.Scale(s, (uint)(c + (c >> 7)));
                px[i] = Canvas.Over(s, px[i]);
            }
            return;
        }

        for (int i = 0; i < n; i++)
        {
            int c = cov[i];
            if (hasMask) c = c * mask[i] * 257 >> 16;
            if (hasSoft) c = c * soft[i] * 257 >> 16;
            if (alpha != 255) c = c * alpha * 257 >> 16;
            if (c == 0) continue;
            uint s = Shader != null ? colors[i] : solid;
            if (c != 255) s = Canvas.Scale(s, (uint)(c + (c >> 7)));
            px[i] = Canvas.Over(s, px[i]);
        }
    }

    // ------------------------------------------------------------------ blend modes

    private static uint BlendPixel(uint s, uint d, BlendMode mode)
    {
        float sa = (s >> 24) / 255f;
        if (sa <= 0) return d;
        float da = (d >> 24) / 255f;
        float sr = (s & 255) / 255f, sg = ((s >> 8) & 255) / 255f, sb = ((s >> 16) & 255) / 255f;
        float dr = (d & 255) / 255f, dg = ((d >> 8) & 255) / 255f, db = ((d >> 16) & 255) / 255f;
        // Unpremultiply.
        float csr = sr / sa, csg = sg / sa, csb = sb / sa;
        float cbr = da > 0 ? dr / da : 0, cbg = da > 0 ? dg / da : 0, cbb = da > 0 ? db / da : 0;
        float br, bg, bb;
        if (mode >= BlendMode.Hue)
        {
            (br, bg, bb) = NonSeparable(mode, cbr, cbg, cbb, csr, csg, csb);
        }
        else
        {
            br = Separable(mode, cbr, csr);
            bg = Separable(mode, cbg, csg);
            bb = Separable(mode, cbb, csb);
        }
        float oa = sa + da - sa * da;
        float or = sr * (1 - da) + dr * (1 - sa) + sa * da * br;
        float og = sg * (1 - da) + dg * (1 - sa) + sa * da * bg;
        float ob = sb * (1 - da) + db * (1 - sa) + sa * da * bb;
        return (uint)(ToByte(or) | ToByte(og) << 8 | ToByte(ob) << 16 | ToByte(oa) << 24);
    }

    private static int ToByte(float v) => (int)(Math.Clamp(v, 0, 1) * 255 + 0.5f);

    private static float Separable(BlendMode mode, float cb, float cs)
    {
        switch (mode)
        {
            case BlendMode.Multiply: return cb * cs;
            case BlendMode.Screen: return cb + cs - cb * cs;
            case BlendMode.Overlay: return HardLight(cs, cb);
            case BlendMode.Darken: return MathF.Min(cb, cs);
            case BlendMode.Lighten: return MathF.Max(cb, cs);
            case BlendMode.ColorDodge: return cb == 0 ? 0 : cs >= 1 ? 1 : MathF.Min(1, cb / (1 - cs));
            case BlendMode.ColorBurn: return cb >= 1 ? 1 : cs <= 0 ? 0 : 1 - MathF.Min(1, (1 - cb) / cs);
            case BlendMode.HardLight: return HardLight(cb, cs);
            case BlendMode.SoftLight:
                if (cs <= 0.5f) return cb - (1 - 2 * cs) * cb * (1 - cb);
                float dd = cb <= 0.25f ? ((16 * cb - 12) * cb + 4) * cb : MathF.Sqrt(cb);
                return cb + (2 * cs - 1) * (dd - cb);
            case BlendMode.Difference: return MathF.Abs(cb - cs);
            case BlendMode.Exclusion: return cb + cs - 2 * cb * cs;
            default: return cs;
        }
    }

    private static float HardLight(float cb, float cs) => cs <= 0.5f ? cb * 2 * cs : Separable(BlendMode.Screen, cb, 2 * cs - 1);

    private static (float, float, float) NonSeparable(BlendMode mode, float br, float bg, float bb, float sr, float sg, float sb)
    {
        static float Lum(float r, float g, float b) => 0.3f * r + 0.59f * g + 0.11f * b;
        static float Sat(float r, float g, float b) => MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));
        static (float, float, float) ClipColor(float r, float g, float b)
        {
            float l = Lum(r, g, b), n = MathF.Min(r, MathF.Min(g, b)), x = MathF.Max(r, MathF.Max(g, b));
            if (n < 0) { r = l + (r - l) * l / (l - n); g = l + (g - l) * l / (l - n); b = l + (b - l) * l / (l - n); }
            if (x > 1) { r = l + (r - l) * (1 - l) / (x - l); g = l + (g - l) * (1 - l) / (x - l); b = l + (b - l) * (1 - l) / (x - l); }
            return (r, g, b);
        }
        static (float, float, float) SetLum(float r, float g, float b, float l)
        {
            float d = l - Lum(r, g, b);
            return ClipColor(r + d, g + d, b + d);
        }
        static (float, float, float) SetSat(float r, float g, float b, float s)
        {
            float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b));
            if (mx <= mn) return (0, 0, 0);
            float f(float c) => (c - mn) * s / (mx - mn);
            return (f(r), f(g), f(b));
        }
        switch (mode)
        {
            case BlendMode.Hue:
            {
                var (r, g, b) = SetSat(sr, sg, sb, Sat(br, bg, bb));
                return SetLum(r, g, b, Lum(br, bg, bb));
            }
            case BlendMode.Saturation:
            {
                var (r, g, b) = SetSat(br, bg, bb, Sat(sr, sg, sb));
                return SetLum(r, g, b, Lum(br, bg, bb));
            }
            case BlendMode.Color: return SetLum(sr, sg, sb, Lum(br, bg, bb));
            default: return SetLum(br, bg, bb, Lum(sr, sg, sb));
        }
    }
}
