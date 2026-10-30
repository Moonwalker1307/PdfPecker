using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Images;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering;

internal sealed partial class PageRenderer
{
    private void DrawImageXObject(PdfStream xobj, PdfDict? res)
    {
        if (_gs.Clip.IsEmpty) return;
        var img = _cache.GetImage(xobj, () => DecodeImage(xobj, null, res, false));
        if (img != null) DrawImage(img);
    }

    private void DrawInlineImage(PdfDict dict, byte[] data, PdfDict? res)
    {
        if (_gs.Clip.IsEmpty) return;
        var img = DecodeImage(dict, data, res, true);
        if (img != null) DrawImage(img);
    }

    private DecodedImage? DecodeImage(PdfDict dict, byte[]? inlineData, PdfDict? res, bool inline)
    {
        try
        {
            return PdfImageDecoder.Decode(_file, dict, inlineData ?? (dict as PdfStream)?.RawData ?? Array.Empty<byte>(), false, res, inline);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or IndexOutOfRangeException
                                       or ArgumentException or OverflowException or InvalidCastException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
    }

    /// <summary>Draws a decoded image into the unit square of the current user space.</summary>
    private void DrawImage(DecodedImage img)
    {
        if (!SetupPaint(false) && img.Stencil != null) return;
        if (img.Stencil == null)
        {
            // Images paint with the non-stroking alpha but not with the fill color or pattern.
            if (_hidden > 0 || _gs.FillAlpha <= 0) return;
            _painter.Canvas = _canvas;
            _painter.Clip = _gs.Clip;
            _painter.Alpha = (int)(Math.Clamp(_gs.FillAlpha, 0, 1) * 255 + 0.5f);
            _painter.Blend = _gs.Blend;
            _painter.SoftMask = _gs.SoftMask;
        }
        var ctm = _gs.Ctm;
        if (!ctm.TryInvert(out var inv)) return;

        // Device size of the image's edges, for choosing a mip level and the sampling filter.
        double devW = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B), devH = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D);
        while (img.Width > 2 * devW + 1 && img.Height > 2 * devH + 1 && img.Width > 1 && img.Height > 1) img = img.Half();
        double scaleX = devW / img.Width, scaleY = devH / img.Height;
        bool nearest = !img.Interpolate && scaleX >= 3 && scaleY >= 3;

        IShader? inner = img.Stencil != null ? _painter.Shader : null;
        uint solid = _painter.Color;
        _painter.Shader = new ImageShader(img, inv, nearest, img.Stencil != null, solid, inner);

        // Rasterize the parallelogram the unit square maps to.
        var clip = _gs.Clip;
        if (ctm.IsAxisAligned)
        {
            var (x0, y0) = ctm.Apply(0, 0);
            var (x1, y1) = ctm.Apply(1, 1);
            _painter.FillRect((float)x0, (float)y0, (float)x1, (float)y1);
        }
        else
        {
            _flat.Clear();
            _flat.BeginContour();
            foreach (var (ux, uy) in new[] { (0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0) })
            {
                var (dx, dy) = ctm.Apply(ux, uy);
                _flat.Add((float)dx, (float)dy);
            }
            _flat.CloseContour();
            FillFlat(_flat, false);
        }
        _painter.Shader = null;
        _ = clip;
    }
}

/// <summary>Samples an image (or a stencil mask painted with a color or another shader) for device pixels.</summary>
internal sealed class ImageShader : IShader
{
    private readonly DecodedImage _img;
    private readonly double _ua, _ub, _uc, _va, _vb, _vc; // image coords = (ua*x + ub*y + uc, va*x + vb*y + vc)
    private readonly bool _nearest, _stencil;
    private readonly uint _color;
    private readonly IShader? _inner;

    public ImageShader(DecodedImage img, in Matrix deviceToUser, bool nearest, bool stencil, uint color, IShader? inner)
    {
        _img = img;
        _nearest = nearest;
        _stencil = stencil;
        _color = color;
        _inner = inner;
        // User unit square → image pixels: u = ux * W, v = (1 - uy) * H.
        var m = deviceToUser;
        _ua = m.A * img.Width; _ub = m.C * img.Width; _uc = m.E * img.Width;
        _va = -m.B * img.Height; _vb = -m.D * img.Height; _vc = (1 - m.F) * img.Height;
    }

    public void Shade(int y, int x0, Span<uint> colors)
    {
        int w = _img.Width, h = _img.Height, n = colors.Length;
        double fx = x0 + 0.5, fy = y + 0.5;
        double u = _ua * fx + _ub * fy + _uc, v = _va * fx + _vb * fy + _vc;
        double du = _ua, dv = _va;
        if (_stencil)
        {
            if (_inner != null) _inner.Shade(y, x0, colors);
            var st = _img.Stencil!;
            for (int i = 0; i < n; i++, u += du, v += dv)
            {
                int cov = _nearest ? SampleNearest(st, w, h, u, v) : SampleBilinear(st, w, h, u, v);
                uint c = _inner != null ? colors[i] : _color;
                colors[i] = cov == 255 ? c : cov == 0 ? 0 : Canvas.Scale(c, (uint)(cov + (cov >> 7)));
            }
            return;
        }
        var px = _img.Pixels!;
        if (_nearest)
        {
            for (int i = 0; i < n; i++, u += du, v += dv)
            {
                int iu = Math.Clamp((int)Math.Floor(u), 0, w - 1), iv = Math.Clamp((int)Math.Floor(v), 0, h - 1);
                colors[i] = px[iv * w + iu];
            }
            return;
        }
        for (int i = 0; i < n; i++, u += du, v += dv)
        {
            double su = u - 0.5, sv = v - 0.5;
            int iu = (int)Math.Floor(su), iv = (int)Math.Floor(sv);
            int wu = (int)((su - iu) * 256), wv = (int)((sv - iv) * 256);
            int u0 = Math.Clamp(iu, 0, w - 1), u1 = Math.Clamp(iu + 1, 0, w - 1);
            int v0 = Math.Clamp(iv, 0, h - 1), v1 = Math.Clamp(iv + 1, 0, h - 1);
            uint a = px[v0 * w + u0], b = px[v0 * w + u1], c = px[v1 * w + u0], d = px[v1 * w + u1];
            if (a == b && c == d && a == c) { colors[i] = a; continue; }
            colors[i] = Lerp(Lerp(a, b, wu), Lerp(c, d, wu), wv);
        }
    }

    private static uint Lerp(uint a, uint b, int t)
    {
        uint it = (uint)(256 - t), ut = (uint)t;
        uint rb = ((a & 0x00FF00FF) * it + (b & 0x00FF00FF) * ut) >> 8 & 0x00FF00FF;
        uint ag = (((a >> 8) & 0x00FF00FF) * it + ((b >> 8) & 0x00FF00FF) * ut) >> 8 & 0x00FF00FF;
        return rb | ag << 8;
    }

    private static int SampleNearest(byte[] st, int w, int h, double u, double v)
    {
        int iu = Math.Clamp((int)Math.Floor(u), 0, w - 1), iv = Math.Clamp((int)Math.Floor(v), 0, h - 1);
        return st[iv * w + iu];
    }

    private static int SampleBilinear(byte[] st, int w, int h, double u, double v)
    {
        double su = u - 0.5, sv = v - 0.5;
        int iu = (int)Math.Floor(su), iv = (int)Math.Floor(sv);
        int wu = (int)((su - iu) * 256), wv = (int)((sv - iv) * 256);
        int u0 = Math.Clamp(iu, 0, w - 1), u1 = Math.Clamp(iu + 1, 0, w - 1);
        int v0 = Math.Clamp(iv, 0, h - 1), v1 = Math.Clamp(iv + 1, 0, h - 1);
        int top = st[v0 * w + u0] * (256 - wu) + st[v0 * w + u1] * wu;
        int bot = st[v1 * w + u0] * (256 - wu) + st[v1 * w + u1] * wu;
        return (top * (256 - wv) + bot * wv) >> 16;
    }
}
