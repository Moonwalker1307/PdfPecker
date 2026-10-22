using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering;

internal sealed partial class PageRenderer
{
    private const int MaxTileSize = 2048;
    private Dictionary<(PdfObject, int, int, int, int, uint), TileShader?>? _tiles;

    // The device pixels the fill being set up can touch, while a pattern for it is created; null for the whole clip.
    private (int X0, int Y0, int X1, int Y1)? _fillArea;

    /// <summary>
    /// The device pixels a path can cover, from its points and control points (which bound its curves), with a margin
    /// for anti-aliasing; null when it has no finite points.
    /// </summary>
    private static (int X0, int Y0, int X1, int Y1)? DeviceBounds(PathData path, in Matrix ctm)
    {
        var points = path.Points;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i + 1 < points.Count; i += 2)
        {
            var (x, y) = ctm.Apply(points[i], points[i + 1]);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        if (!(minX <= maxX && minY <= maxY) || !double.IsFinite(minX) || !double.IsFinite(maxX) || !double.IsFinite(minY) || !double.IsFinite(maxY))
            return null;
        const double Margin = 2;
        static int Floor(double v) => (int)Math.Clamp(Math.Floor(v - Margin), int.MinValue / 2, int.MaxValue / 2);
        static int Ceiling(double v) => (int)Math.Clamp(Math.Ceiling(v + Margin) + 1, int.MinValue / 2, int.MaxValue / 2);
        return (Floor(minX), Floor(minY), Ceiling(maxX), Ceiling(maxY));
    }

    /// <summary>The <c>sh</c> operator: paints a shading over the current clip.</summary>
    private void PaintShadingOperator(string name, PdfDict? res)
    {
        var shading = _file.Resolve(_file.GetDict(res, "Shading")?[name]) as PdfDict;
        if (shading == null || _gs.Clip.IsEmpty) return;
        var shader = CreateShadingShader(shading, _gs.Ctm, false);
        if (shader == null) return;
        var pt = _painter;
        pt.Canvas = _canvas;
        pt.Clip = _gs.Clip;
        pt.Alpha = (int)(Math.Clamp(_gs.FillAlpha, 0, 1) * 255 + 0.5f);
        pt.Blend = _gs.Blend;
        pt.SoftMask = _gs.SoftMask;
        pt.Shader = shader;
        var bbox = _file.GetNumbers(shading, "BBox");
        if (bbox is { Length: >= 4 })
        {
            var p = new PathData();
            p.Rect(Math.Min(bbox[0], bbox[2]), Math.Min(bbox[1], bbox[3]), Math.Abs(bbox[2] - bbox[0]), Math.Abs(bbox[3] - bbox[1]));
            _flat.Flatten(p, _gs.Ctm, Tolerance);
            FillFlat(_flat, false);
        }
        else
        {
            var c = _gs.Clip;
            pt.FillRect(c.X0, c.Y0, c.X1, c.Y1);
        }
        pt.Shader = null;
    }

    /// <summary>Creates a shader for a pattern color (tiling or shading pattern).</summary>
    private IShader? CreatePatternShader(PdfObject pattern, bool stroke)
    {
        if (pattern is not PdfDict pd) return null;
        var m = Matrix.FromArray(_file.GetNumbers(pd, "Matrix")).Multiply(_patternBase);
        int type = _file.GetInt(pd, "PatternType", 1);
        if (type == 2)
        {
            var shading = _file.GetDict(pd, "Shading");
            return shading == null ? null : CreateShadingShader(shading, m, true);
        }
        if (pattern is not PdfStream ps) return null;
        uint rgb = stroke ? _gs.StrokeRgb : _gs.FillRgb;
        bool uncolored = _file.GetInt(pd, "PaintType", 1) == 2;
        var key = (pattern, Q4(m.A), Q4(m.B), Q4(m.C), Q4(m.D), uncolored ? rgb : 0u);
        // The translation matters too, but tiles are periodic: only its fractional phase would change the result.
        _tiles ??= new();
        if (_tiles.TryGetValue(key, out var cached) && cached != null && cached.SameOrigin(m)) return cached;
        var tile = BuildTile(ps, m, uncolored, rgb);
        _tiles[key] = tile;
        return tile;
    }

    private static int Q4(double v) => (int)Math.Round(v * 4096);

    private TileShader? BuildTile(PdfStream ps, in Matrix m, bool uncolored, uint rgb)
    {
        if (_depth >= MaxDepth - 2) return null;
        var bbox = _file.GetNumbers(ps, "BBox");
        if (bbox == null || bbox.Length < 4) return null;
        double bx0 = Math.Min(bbox[0], bbox[2]), by0 = Math.Min(bbox[1], bbox[3]);
        double bw = Math.Abs(bbox[2] - bbox[0]), bh = Math.Abs(bbox[3] - bbox[1]);
        double xs = Math.Abs(_file.GetNumber(ps, "XStep", bw)), ys = Math.Abs(_file.GetNumber(ps, "YStep", bh));
        if (xs < 1e-9) xs = bw;
        if (ys < 1e-9) ys = bh;
        if (xs < 1e-9 || ys < 1e-9) return null;
        double sx = Math.Sqrt(m.A * m.A + m.B * m.B), sy = Math.Sqrt(m.C * m.C + m.D * m.D);
        int tw = (int)Math.Ceiling(xs * sx), th = (int)Math.Ceiling(ys * sy);
        tw = Math.Clamp(tw, 1, MaxTileSize);
        th = Math.Clamp(th, 1, MaxTileSize);
        if (!m.TryInvert(out var inv)) return null;
        // Pattern space → tile pixels (y flipped so the tile looks like the device output).
        var toTile = new Matrix(tw / xs, 0, 0, -th / ys, -bx0 * tw / xs, (by0 + ys) * th / ys);

        var tileCanvas = new Canvas(tw, th, 0);
        var data = SafeDecode(ps);
        var res = _file.GetDict(ps, "Resources");
        var saved = _gs;
        var savedCanvas = _canvas;
        var savedBase = _patternBase;
        var savedPath = _path.IsEmpty ? null : _path.Clone();
        bool savedIgnore = _ignoreColor;
        int savedHidden = _hidden;
        try
        {
            _canvas = tileCanvas;
            _hidden = 0;
            // Copies shifted by whole steps cover content that overflows the cell.
            int nx = Math.Min(3, (int)Math.Ceiling(bw / xs - 1e-9)), ny = Math.Min(3, (int)Math.Ceiling(bh / ys - 1e-9));
            for (int iy = -Math.Max(0, ny - 1); iy <= 0; iy++)
                for (int ix = -Math.Max(0, nx - 1); ix <= 0; ix++)
                {
                    var ctm = Matrix.Translation(ix * xs, iy * ys).Multiply(toTile);
                    _gs = new GraphicsState { Clip = new ClipRegion(0, 0, tw, th), Ctm = ctm };
                    if (uncolored)
                    {
                        _gs.FillRgb = _gs.StrokeRgb = rgb;
                        _gs.FillSpace = _gs.StrokeSpace = ColorSpace.RgbSpace;
                        _ignoreColor = true;
                    }
                    else _ignoreColor = false;
                    _patternBase = ctm;
                    _path.Clear();
                    _path.Rect(bx0, by0, bw, bh);
                    ApplyClip(false);
                    _path.Clear();
                    RunContent(data, res);
                }
        }
        finally
        {
            _gs = saved;
            _canvas = savedCanvas;
            _patternBase = savedBase;
            _ignoreColor = savedIgnore;
            _hidden = savedHidden;
            _path.Clear();
            savedPath?.AppendTransformed(_path, Matrix.Identity);
        }
        return new TileShader(tileCanvas, inv, m, toTile, xs, ys, bx0, by0);
    }

    // ------------------------------------------------------------------ shadings

    private IShader? CreateShadingShader(PdfDict shading, in Matrix shadingToDevice, bool usePatternBackground)
    {
        int type = _file.GetInt(shading, "ShadingType", 0);
        var cs = ColorSpace.Parse(_file, shading["ColorSpace"] ?? shading["CS"], null);
        if (cs == null) return null;
        var fn = PdfFunction.Create(_file, _file.Resolve(shading["Function"]));
        if (!shadingToDevice.TryInvert(out var inv)) return null;
        uint background = 0;
        if (usePatternBackground && _file.GetNumbers(shading, "Background") is { } bg && bg.Length >= cs.Components)
            background = cs.ToRgb(bg.Select(x => (float)x).ToArray()) | 0xFF000000;
        try
        {
            switch (type)
            {
                case 1: return FunctionShader.Create(_file, shading, cs, fn, inv, background);
                case 2:
                case 3: return GradientShader.Create(_file, shading, type, cs, fn, inv, background);
                case 4: case 5: case 6: case 7:
                {
                    // The mesh is painted into a layer covering only the clip and the area being filled: a full-page
                    // layer for every filled shape made pages with many small shaded shapes allocate gigabytes.
                    var clip = _gs.Clip;
                    int x0 = Math.Max(0, clip.X0), y0 = Math.Max(0, clip.Y0);
                    int x1 = Math.Min(_canvas.Width, clip.X1), y1 = Math.Min(_canvas.Height, clip.Y1);
                    if (_fillArea is var (ax0, ay0, ax1, ay1))
                        (x0, y0, x1, y1) = (Math.Max(x0, ax0), Math.Max(y0, ay0), Math.Min(x1, ax1), Math.Min(y1, ay1));
                    if (x1 <= x0 || y1 <= y0) return null;
                    var layer = new Canvas(x1 - x0, y1 - y0, background);
                    MeshShading.Paint(_file, shading, type, cs, fn, shadingToDevice, layer, x0, y0, clip, _canvas.Width, _canvas.Height);
                    return new CanvasShader(layer, x0, y0, background);
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or InvalidDataException or PdfFormatException or ArgumentException or NotSupportedException)
        {
        }
        return null;
    }
}

/// <summary>Repeats a rendered pattern cell over device space.</summary>
internal sealed class TileShader : IShader
{
    private readonly Canvas _tile;
    private readonly Matrix _inv, _m;
    private readonly double _a, _b, _c, _d, _e, _f; // device → tile pixels (before wrapping)
    private readonly double _pw, _ph; // period in tile pixels

    public TileShader(Canvas tile, in Matrix deviceToPattern, in Matrix patternToDevice, in Matrix toTile, double xs, double ys, double bx0, double by0)
    {
        _tile = tile;
        _inv = deviceToPattern;
        _m = patternToDevice;
        var t = deviceToPattern.Multiply(toTile);
        _a = t.A; _b = t.B; _c = t.C; _d = t.D; _e = t.E; _f = t.F;
        _pw = tile.Width;
        _ph = tile.Height;
        _ = xs; _ = ys; _ = bx0; _ = by0;
    }

    public bool SameOrigin(in Matrix m) => Math.Abs(m.E - _m.E) < 1e-6 && Math.Abs(m.F - _m.F) < 1e-6;

    public void Shade(int y, int x0, Span<uint> colors)
    {
        int tw = _tile.Width, th = _tile.Height;
        var px = _tile.Pixels;
        double fx = x0 + 0.5, fy = y + 0.5;
        double u = _a * fx + _c * fy + _e, v = _b * fx + _d * fy + _f;
        for (int i = 0; i < colors.Length; i++, u += _a, v += _b)
        {
            double wu = u - Math.Floor(u / _pw) * _pw, wv = v - Math.Floor(v / _ph) * _ph;
            int iu = Math.Min(tw - 1, (int)wu), iv = Math.Min(th - 1, (int)wv);
            colors[i] = px[iv * tw + iu];
        }
    }
}

/// <summary>Converts shading function outputs (or direct components) to premultiplied colors.</summary>
internal static class ShadingColor
{
    public static uint Eval(ColorSpace cs, PdfFunction? fn, double t, Span<double> outBuf, Span<float> comps)
    {
        if (fn != null)
        {
            Span<double> input = stackalloc double[1];
            input[0] = t;
            fn.Eval(input, outBuf);
            for (int i = 0; i < comps.Length; i++) comps[i] = (float)(i < outBuf.Length ? outBuf[i] : 0);
        }
        return cs.ToRgb(comps) | 0xFF000000;
    }

    /// <summary>Samples a 1-in function over [t0, t1] into a color table.</summary>
    public static uint[] BuildLut(ColorSpace cs, PdfFunction? fn, double t0, double t1, int n)
    {
        var lut = new uint[n];
        Span<double> outBuf = stackalloc double[Math.Max(8, fn?.Outputs ?? 0)];
        Span<float> comps = stackalloc float[Math.Max(1, cs.Components)];
        Span<double> input = stackalloc double[1];
        for (int i = 0; i < n; i++)
        {
            double t = t0 + (t1 - t0) * i / (n - 1);
            if (fn != null)
            {
                input[0] = t;
                outBuf.Clear();
                fn.Eval(input, outBuf);
                for (int k = 0; k < comps.Length; k++) comps[k] = (float)outBuf[k];
            }
            lut[i] = cs.ToRgb(comps) | 0xFF000000;
        }
        return lut;
    }
}

/// <summary>Axial (type 2) and radial (type 3) shadings.</summary>
internal sealed class GradientShader : IShader
{
    private const int LutSize = 1024;
    private readonly uint[] _lut;
    private readonly Matrix _inv;
    private readonly bool _radial, _extend0, _extend1;
    private readonly double _x0, _y0, _r0, _x1, _y1, _r1;
    private readonly uint _background;

    private GradientShader(uint[] lut, in Matrix inv, bool radial, double[] coords, bool e0, bool e1, uint background)
    {
        _lut = lut;
        _inv = inv;
        _radial = radial;
        _extend0 = e0;
        _extend1 = e1;
        _background = background;
        if (radial) { _x0 = coords[0]; _y0 = coords[1]; _r0 = coords[2]; _x1 = coords[3]; _y1 = coords[4]; _r1 = coords[5]; }
        else { _x0 = coords[0]; _y0 = coords[1]; _x1 = coords[2]; _y1 = coords[3]; }
    }

    public static GradientShader? Create(PdfFile file, PdfDict d, int type, ColorSpace cs, PdfFunction? fn, in Matrix inv, uint background)
    {
        var coords = file.GetNumbers(d, "Coords");
        if (coords == null || coords.Length < (type == 2 ? 4 : 6) || fn == null) return null;
        var domain = file.GetNumbers(d, "Domain") ?? new double[] { 0, 1 };
        if (domain.Length < 2) domain = new double[] { 0, 1 };
        var ext = file.GetArray(d, "Extend");
        bool e0 = ext != null && ext.Count > 0 && file.Resolve(ext[0]) is PdfBool { Value: true };
        bool e1 = ext != null && ext.Count > 1 && file.Resolve(ext[1]) is PdfBool { Value: true };
        var lut = ShadingColor.BuildLut(cs, fn, domain[0], domain[1], LutSize);
        return new GradientShader(lut, inv, type == 3, coords, e0, e1, background);
    }

    public void Shade(int y, int x0, Span<uint> colors)
    {
        var m = _inv;
        double fx = x0 + 0.5, fy = y + 0.5;
        double px = m.A * fx + m.C * fy + m.E, py = m.B * fx + m.D * fy + m.F;
        if (!_radial)
        {
            double dx = _x1 - _x0, dy = _y1 - _y0, len2 = dx * dx + dy * dy;
            if (len2 < 1e-18) { colors.Fill(_background); return; }
            double t = ((px - _x0) * dx + (py - _y0) * dy) / len2;
            double dt = (m.A * dx + m.B * dy) / len2;
            for (int i = 0; i < colors.Length; i++, t += dt) colors[i] = Lookup(t);
            return;
        }
        double cdx = _x1 - _x0, cdy = _y1 - _y0, dr = _r1 - _r0;
        double a = cdx * cdx + cdy * cdy - dr * dr;
        for (int i = 0; i < colors.Length; i++, px += m.A, py += m.B)
        {
            double pdx = px - _x0, pdy = py - _y0;
            double b = pdx * cdx + pdy * cdy + _r0 * dr;
            double c = pdx * pdx + pdy * pdy - _r0 * _r0;
            double s;
            if (Math.Abs(a) < 1e-12)
            {
                if (Math.Abs(b) < 1e-12) { colors[i] = _background; continue; }
                s = c / (2 * b);
                if (_r0 + s * dr < 0) { colors[i] = _background; continue; }
                colors[i] = Lookup(s);
                continue;
            }
            double disc = b * b - a * c;
            if (disc < 0) { colors[i] = _background; continue; }
            double sq = Math.Sqrt(disc);
            double s1 = (b + sq) / a, s2 = (b - sq) / a;
            if (s1 < s2) (s1, s2) = (s2, s1);
            if (Valid(s1)) s = s1;
            else if (Valid(s2)) s = s2;
            else { colors[i] = _background; continue; }
            colors[i] = Lookup(s);
        }
    }

    private bool Valid(double s)
    {
        if (_r0 + s * (_r1 - _r0) < 0) return false;
        if (s < 0) return _extend0;
        if (s > 1) return _extend1;
        return true;
    }

    private uint Lookup(double t)
    {
        if (t < 0)
        {
            if (!_extend0) return _background;
            t = 0;
        }
        else if (t > 1)
        {
            if (!_extend1) return _background;
            t = 1;
        }
        else if (double.IsNaN(t)) return _background;
        return _lut[(int)(t * (LutSize - 1) + 0.5)];
    }
}

/// <summary>Function-based (type 1) shading.</summary>
internal sealed class FunctionShader : IShader
{
    private readonly PdfFunction _fn;
    private readonly ColorSpace _cs;
    private readonly Matrix _inv;
    private readonly double[] _domain;
    private readonly uint _background;
    private readonly double[] _out;
    private readonly float[] _comps;

    private FunctionShader(PdfFunction fn, ColorSpace cs, in Matrix inv, double[] domain, uint background)
    {
        _fn = fn;
        _cs = cs;
        _inv = inv;
        _domain = domain;
        _background = background;
        _out = new double[Math.Max(8, fn.Outputs)];
        _comps = new float[Math.Max(1, cs.Components)];
    }

    public static FunctionShader? Create(PdfFile file, PdfDict d, ColorSpace cs, PdfFunction? fn, in Matrix inv, uint background)
    {
        if (fn == null) return null;
        var domain = file.GetNumbers(d, "Domain") ?? new double[] { 0, 1, 0, 1 };
        if (domain.Length < 4) domain = new double[] { 0, 1, 0, 1 };
        var m = Matrix.FromArray(file.GetNumbers(d, "Matrix"));
        if (!m.TryInvert(out var mi)) return null;
        return new FunctionShader(fn, cs, inv.Multiply(mi), domain, background);
    }

    public void Shade(int y, int x0, Span<uint> colors)
    {
        Span<double> input = stackalloc double[2];
        var m = _inv;
        for (int i = 0; i < colors.Length; i++)
        {
            double fx = x0 + i + 0.5, fy = y + 0.5;
            double px = m.A * fx + m.C * fy + m.E, py = m.B * fx + m.D * fy + m.F;
            if (px < _domain[0] || px > _domain[1] || py < _domain[2] || py > _domain[3]) { colors[i] = _background; continue; }
            input[0] = px;
            input[1] = py;
            Array.Clear(_out);
            _fn.Eval(input, _out);
            for (int k = 0; k < _comps.Length; k++) _comps[k] = (float)_out[k];
            colors[i] = _cs.ToRgb(_comps) | 0xFF000000;
        }
    }
}
