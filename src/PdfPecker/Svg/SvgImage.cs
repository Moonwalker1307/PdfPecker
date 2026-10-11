using System.Globalization;
using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Svg;

/// <summary>One filled and/or stroked path of an SVG drawing, in the root element's user space.</summary>
internal sealed class SvgShape
{
    public const byte MoveTo = 0, LineTo = 1, CurveTo = 2, Close = 3;

    public byte[] Verbs = Array.Empty<byte>();
    public float[] Points = Array.Empty<float>(); // 2 per MoveTo/LineTo, 6 per CurveTo
    public uint? Fill;
    public bool EvenOdd;
    public uint? Stroke;
    public float StrokeWidth;
    public SvgLineCap LineCap;
    public SvgLineJoin LineJoin;
    public float MiterLimit = 4;
}

/// <summary>
/// An inline &lt;svg&gt; turned into vector paths: path, rect, circle, ellipse, line, polyline, polygon, g, use and
/// nested svg, with transforms, fill/stroke styling from attributes and CSS, and gradients drawn in a flat average
/// colour. Text, images, clipping, masks and filters are not drawn.
/// </summary>
internal sealed class SvgImage
{
    public float? Width, Height; // intrinsic size in points, when given
    public bool HasViewBox;
    public float ViewBoxX, ViewBoxY, ViewBoxWidth, ViewBoxHeight;
    public string Align = "xmidymid";
    public bool Slice;
    public readonly List<SvgShape> Shapes = new();

    private readonly Dictionary<string, Element> _ids = new(StringComparer.Ordinal);

    public static SvgImage Build(Element svg)
    {
        var img = new SvgImage();
        img.Width = IntrinsicLength(svg.GetAttribute("width"));
        img.Height = IntrinsicLength(svg.GetAttribute("height"));
        var vb = ParseNumbers(svg.GetAttribute("viewbox"));
        if (vb.Count == 4 && vb[2] > 0 && vb[3] > 0)
        {
            img.HasViewBox = true;
            img.ViewBoxX = vb[0];
            img.ViewBoxY = vb[1];
            img.ViewBoxWidth = vb[2];
            img.ViewBoxHeight = vb[3];
        }
        var par = svg.GetAttribute("preserveaspectratio");
        if (par != null)
        {
            var parts = par.Trim().ToLowerInvariant().Split(new[] { ' ', '\t', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0] == "defer") parts = parts.Skip(1).ToArray();
            if (parts.Length > 0) img.Align = parts[0];
            if (parts.Length > 1) img.Slice = parts[1] == "slice";
        }
        img.CollectIds(svg);
        foreach (var child in svg.Children)
            if (child is Element e) img.Render(e, Matrix.Identity, 1, 0);
        return img;
    }

    private static float? IntrinsicLength(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim().ToLowerInvariant();
        if (v.EndsWith("%", StringComparison.Ordinal)) return null;
        if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float px)) return px > 0 ? px * 0.75f : null;
        return StyleResolver.TryParseLength(v, 12, 12, out var len) && !len.IsAuto && !len.IsPercent && len.Value > 0 ? len.Value : null;
    }

    private void CollectIds(Element e)
    {
        foreach (var c in e.Children)
        {
            if (c is not Element ce) continue;
            var id = ce.GetAttribute("id");
            if (id != null && !_ids.ContainsKey(id)) _ids[id] = ce;
            CollectIds(ce);
        }
    }

    // ------------------------------------------------------------------ tree walk

    private void Render(Element e, Matrix ctm, float opacity, int depth)
    {
        if (depth > 32) return;
        var s = e.Style;
        if (s == null || s.Display == Display.None) return;
        if (e.GetAttribute("transform") is { } tf) ctm = ParseTransform(tf).Then(ctm);
        float op = opacity * s.Opacity;

        switch (e.Name)
        {
            case "g":
            case "a":
            case "switch":
                foreach (var c in e.Children) if (c is Element ce) Render(ce, ctm, op, depth + 1);
                return;
            case "svg":
            {
                float x = Num(e, "x"), y = Num(e, "y");
                var inner = Matrix.Translate(x, y).Then(ctm);
                var vb = ParseNumbers(e.GetAttribute("viewbox"));
                float w = Num(e, "width", float.NaN), h = Num(e, "height", float.NaN);
                if (vb.Count == 4 && vb[2] > 0 && vb[3] > 0 && !float.IsNaN(w) && !float.IsNaN(h))
                {
                    float sc = Math.Min(w / vb[2], h / vb[3]);
                    inner = Matrix.Translate(-vb[0], -vb[1]).Then(Matrix.Scale(sc, sc)).Then(inner);
                }
                foreach (var c in e.Children) if (c is Element ce) Render(ce, inner, op, depth + 1);
                return;
            }
            case "use":
            {
                var href = e.GetAttribute("href") ?? e.GetAttribute("xlink:href");
                if (href == null || !href.StartsWith("#", StringComparison.Ordinal) || !_ids.TryGetValue(href.Substring(1), out var target)) return;
                var m = Matrix.Translate(Num(e, "x"), Num(e, "y")).Then(ctm);
                if (target.Name == "symbol")
                {
                    foreach (var c in target.Children) if (c is Element ce) Render(ce, m, op, depth + 1);
                }
                else RenderReferenced(target, e, m, op, depth + 1);
                return;
            }
            case "path":
                Emit(e, s, ctm, op, PathParser.Parse(e.GetAttribute("d")));
                return;
            case "rect":
                Emit(e, s, ctm, op, RectPath(e));
                return;
            case "circle":
            {
                float r = Num(e, "r");
                if (r > 0) Emit(e, s, ctm, op, EllipsePath(Num(e, "cx"), Num(e, "cy"), r, r));
                return;
            }
            case "ellipse":
            {
                float rx = Num(e, "rx", float.NaN), ry = Num(e, "ry", float.NaN);
                if (float.IsNaN(rx)) rx = ry;
                if (float.IsNaN(ry)) ry = rx;
                if (rx > 0 && ry > 0) Emit(e, s, ctm, op, EllipsePath(Num(e, "cx"), Num(e, "cy"), rx, ry));
                return;
            }
            case "line":
            {
                var p = new PathBuilder();
                p.MoveTo(Num(e, "x1"), Num(e, "y1"));
                p.LineTo(Num(e, "x2"), Num(e, "y2"));
                Emit(e, s, ctm, op, p, fillable: false);
                return;
            }
            case "polyline":
            case "polygon":
            {
                var pts = ParseNumbers(e.GetAttribute("points"));
                if (pts.Count < 4) return;
                var p = new PathBuilder();
                p.MoveTo(pts[0], pts[1]);
                for (int i = 2; i + 1 < pts.Count; i += 2) p.LineTo(pts[i], pts[i + 1]);
                if (e.Name == "polygon") p.Close();
                Emit(e, s, ctm, op, p);
                return;
            }
            // Not drawn: defs, symbol (only through use), gradients, clipPath, mask, pattern, marker, filter, text, image, style...
        }
    }

    /// <summary>Renders an element referenced by &lt;use&gt;; the use element's own presentation styles were already
    /// inherited by the target through the cascade only when the target is its descendant, so they are applied here.</summary>
    private void RenderReferenced(Element target, Element use, Matrix m, float opacity, int depth)
        => Render(target, m, opacity * (use.Style?.Opacity ?? 1), depth);

    private void Emit(Element e, ComputedStyle s, Matrix ctm, float opacity, PathBuilder? path, bool fillable = true)
    {
        if (path == null || path.Verbs.Count == 0 || !s.Visible) return;
        uint? fill = fillable ? Paint(s.SvgFill, s.SvgFillUrl, s.SvgFillOpacity * opacity) : null;
        uint? stroke = s.SvgStrokeWidth > 0 ? Paint(s.SvgStroke, s.SvgStrokeUrl, s.SvgStrokeOpacity * opacity) : null;
        if (fill == null && stroke == null) return;
        var shape = new SvgShape
        {
            Verbs = path.Verbs.ToArray(),
            Points = path.Points.ToArray(),
            Fill = fill,
            EvenOdd = s.SvgEvenOdd,
            Stroke = stroke,
            StrokeWidth = s.SvgStrokeWidth * ctm.ScaleFactor,
            LineCap = s.SvgLineCap,
            LineJoin = s.SvgLineJoin,
            MiterLimit = s.SvgMiterLimit,
        };
        var pts = shape.Points;
        for (int i = 0; i + 1 < pts.Length; i += 2) ctm.Apply(ref pts[i], ref pts[i + 1]);
        Shapes.Add(shape);
        _ = e;
    }

    private uint? Paint(uint? color, string? url, float opacity)
    {
        uint? c = color;
        if (url != null && url.StartsWith("#", StringComparison.Ordinal) && _ids.TryGetValue(url.Substring(1), out var server))
            c = GradientColor(server) ?? color;
        if (c == null) return null;
        uint v = c.Value;
        float alpha = (v >> 24) / 255f * Math.Clamp(opacity, 0, 1);
        if (alpha <= 0.002f) return null;
        return CssColor.FlattenOnWhite(((uint)Math.Round(alpha * 255) << 24) | (v & 0xFFFFFF));
    }

    /// <summary>A gradient drawn as a flat colour: the average of its stops.</summary>
    private uint? GradientColor(Element gradient, int depth = 0)
    {
        if (gradient.Name is not ("lineargradient" or "radialgradient") || depth > 8) return null;
        float r = 0, g = 0, b = 0, a = 0;
        int count = 0;
        foreach (var c in gradient.Children)
        {
            if (c is not Element stop || stop.Name != "stop") continue;
            string? color = stop.GetAttribute("stop-color");
            string? opacity = stop.GetAttribute("stop-opacity");
            var style = stop.GetAttribute("style");
            if (style != null)
            {
                foreach (var decl in style.Split(';'))
                {
                    int colon = decl.IndexOf(':');
                    if (colon <= 0) continue;
                    var name = decl.Substring(0, colon).Trim().ToLowerInvariant();
                    if (name == "stop-color") color = decl.Substring(colon + 1).Trim();
                    else if (name == "stop-opacity") opacity = decl.Substring(colon + 1).Trim();
                }
            }
            if (!CssColor.TryParse(color ?? "black", CssColor.Black, out uint col)) col = CssColor.Black;
            float alpha = (col >> 24) / 255f;
            if (opacity != null && float.TryParse(opacity, NumberStyles.Float, CultureInfo.InvariantCulture, out float o)) alpha *= Math.Clamp(o, 0, 1);
            r += ((col >> 16) & 0xFF) * alpha;
            g += ((col >> 8) & 0xFF) * alpha;
            b += (col & 0xFF) * alpha;
            a += alpha;
            count++;
        }
        if (count == 0)
        {
            var href = gradient.GetAttribute("href") ?? gradient.GetAttribute("xlink:href");
            if (href != null && href.StartsWith("#", StringComparison.Ordinal) && _ids.TryGetValue(href.Substring(1), out var parent))
                return GradientColor(parent, depth + 1);
            return null;
        }
        if (a <= 0) return 0;
        uint R = (uint)Math.Clamp(Math.Round(r / a), 0, 255), G = (uint)Math.Clamp(Math.Round(g / a), 0, 255), B = (uint)Math.Clamp(Math.Round(b / a), 0, 255);
        uint A = (uint)Math.Clamp(Math.Round(a / count * 255), 0, 255);
        return (A << 24) | (R << 16) | (G << 8) | B;
    }

    // ------------------------------------------------------------------ basic shapes

    private static PathBuilder? RectPath(Element e)
    {
        float x = Num(e, "x"), y = Num(e, "y"), w = Num(e, "width"), h = Num(e, "height");
        if (w <= 0 || h <= 0) return null;
        float rx = Num(e, "rx", float.NaN), ry = Num(e, "ry", float.NaN);
        if (float.IsNaN(rx)) rx = float.IsNaN(ry) ? 0 : ry;
        if (float.IsNaN(ry)) ry = rx;
        rx = Math.Clamp(rx, 0, w / 2);
        ry = Math.Clamp(ry, 0, h / 2);
        var p = new PathBuilder();
        if (rx <= 0 || ry <= 0)
        {
            p.MoveTo(x, y);
            p.LineTo(x + w, y);
            p.LineTo(x + w, y + h);
            p.LineTo(x, y + h);
            p.Close();
            return p;
        }
        const float k = 0.5522848f;
        p.MoveTo(x + rx, y);
        p.LineTo(x + w - rx, y);
        p.CurveTo(x + w - rx + rx * k, y, x + w, y + ry - ry * k, x + w, y + ry);
        p.LineTo(x + w, y + h - ry);
        p.CurveTo(x + w, y + h - ry + ry * k, x + w - rx + rx * k, y + h, x + w - rx, y + h);
        p.LineTo(x + rx, y + h);
        p.CurveTo(x + rx - rx * k, y + h, x, y + h - ry + ry * k, x, y + h - ry);
        p.LineTo(x, y + ry);
        p.CurveTo(x, y + ry - ry * k, x + rx - rx * k, y, x + rx, y);
        p.Close();
        return p;
    }

    private static PathBuilder EllipsePath(float cx, float cy, float rx, float ry)
    {
        const float k = 0.5522848f;
        var p = new PathBuilder();
        p.MoveTo(cx + rx, cy);
        p.CurveTo(cx + rx, cy + ry * k, cx + rx * k, cy + ry, cx, cy + ry);
        p.CurveTo(cx - rx * k, cy + ry, cx - rx, cy + ry * k, cx - rx, cy);
        p.CurveTo(cx - rx, cy - ry * k, cx - rx * k, cy - ry, cx, cy - ry);
        p.CurveTo(cx + rx * k, cy - ry, cx + rx, cy - ry * k, cx + rx, cy);
        p.Close();
        return p;
    }

    // ------------------------------------------------------------------ attribute helpers

    private static float Num(Element e, string name, float fallback = 0)
    {
        var v = e.GetAttribute(name);
        if (v == null) return fallback;
        v = v.Trim();
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase)) v = v.Substring(0, v.Length - 2);
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
    }

    internal static List<float> ParseNumbers(string? s)
    {
        var list = new List<float>();
        if (s == null) return list;
        var lexer = new NumberLexer(s, 0);
        while (lexer.TryNumber(out float f)) list.Add(f);
        return list;
    }

    internal static Matrix ParseTransform(string s)
    {
        var m = Matrix.Identity;
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
            int nameStart = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            if (i == nameStart) break;
            string name = s.Substring(nameStart, i - nameStart).ToLowerInvariant();
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length || s[i] != '(') break;
            int close = s.IndexOf(')', i);
            if (close < 0) break;
            var a = ParseNumbers(s.Substring(i + 1, close - i - 1));
            i = close + 1;
            Matrix t;
            switch (name)
            {
                case "matrix" when a.Count == 6: t = new Matrix(a[0], a[1], a[2], a[3], a[4], a[5]); break;
                case "translate" when a.Count >= 1: t = Matrix.Translate(a[0], a.Count > 1 ? a[1] : 0); break;
                case "scale" when a.Count >= 1: t = Matrix.Scale(a[0], a.Count > 1 ? a[1] : a[0]); break;
                case "rotate" when a.Count >= 1:
                    t = Matrix.Rotate(a[0]);
                    if (a.Count >= 3) t = Matrix.Translate(-a[1], -a[2]).Then(t).Then(Matrix.Translate(a[1], a[2]));
                    break;
                case "skewx" when a.Count >= 1: t = new Matrix(1, 0, (float)Math.Tan(a[0] * Math.PI / 180), 1, 0, 0); break;
                case "skewy" when a.Count >= 1: t = new Matrix(1, (float)Math.Tan(a[0] * Math.PI / 180), 0, 1, 0, 0); break;
                default: continue;
            }
            // The rightmost transform applies first.
            m = t.Then(m);
        }
        return m;
    }
}

/// <summary>An affine transform [a c e; b d f; 0 0 1] mapping (x, y) to (a x + c y + e, b x + d y + f).</summary>
internal readonly struct Matrix
{
    public readonly float A, B, C, D, E, F;
    public Matrix(float a, float b, float c, float d, float e, float f) { A = a; B = b; C = c; D = d; E = e; F = f; }

    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);
    public static Matrix Translate(float x, float y) => new(1, 0, 0, 1, x, y);
    public static Matrix Scale(float x, float y) => new(x, 0, 0, y, 0, 0);

    public static Matrix Rotate(float degrees)
    {
        double r = degrees * Math.PI / 180;
        float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
        return new Matrix(c, s, -s, c, 0, 0);
    }

    /// <summary>This transform followed by <paramref name="o"/>.</summary>
    public Matrix Then(Matrix o)
        => new(o.A * A + o.C * B, o.B * A + o.D * B, o.A * C + o.C * D, o.B * C + o.D * D, o.A * E + o.C * F + o.E, o.B * E + o.D * F + o.F);

    public void Apply(ref float x, ref float y)
    {
        float nx = A * x + C * y + E;
        y = B * x + D * y + F;
        x = nx;
    }

    /// <summary>Average scale, used for stroke widths.</summary>
    public float ScaleFactor => (float)Math.Sqrt(Math.Abs(A * D - B * C));
}

internal sealed class PathBuilder
{
    public readonly List<byte> Verbs = new();
    public readonly List<float> Points = new();

    public void MoveTo(float x, float y) { Verbs.Add(SvgShape.MoveTo); Points.Add(x); Points.Add(y); }
    public void LineTo(float x, float y) { Verbs.Add(SvgShape.LineTo); Points.Add(x); Points.Add(y); }

    public void CurveTo(float x1, float y1, float x2, float y2, float x, float y)
    {
        Verbs.Add(SvgShape.CurveTo);
        Points.Add(x1); Points.Add(y1); Points.Add(x2); Points.Add(y2); Points.Add(x); Points.Add(y);
    }

    public void Close() => Verbs.Add(SvgShape.Close);
}

/// <summary>Reads SVG numbers: "1.5.5" is two numbers, "-1-2" is two, exponents are allowed, separators are optional.</summary>
internal struct NumberLexer
{
    private readonly string _s;
    public int Pos;

    public NumberLexer(string s, int pos) { _s = s; Pos = pos; }

    public void SkipSeparators()
    {
        while (Pos < _s.Length && (char.IsWhiteSpace(_s[Pos]) || _s[Pos] == ',')) Pos++;
    }

    public bool AtNumber()
    {
        SkipSeparators();
        if (Pos >= _s.Length) return false;
        char c = _s[Pos];
        return char.IsDigit(c) || c == '-' || c == '+' || c == '.';
    }

    public bool TryNumber(out float value)
    {
        value = 0;
        if (!AtNumber()) return false;
        int start = Pos;
        if (_s[Pos] == '+' || _s[Pos] == '-') Pos++;
        bool dot = false, digits = false;
        while (Pos < _s.Length)
        {
            char c = _s[Pos];
            if (char.IsDigit(c)) { digits = true; Pos++; }
            else if (c == '.' && !dot) { dot = true; Pos++; }
            else break;
        }
        if (digits && Pos < _s.Length && (_s[Pos] == 'e' || _s[Pos] == 'E'))
        {
            int save = Pos;
            Pos++;
            if (Pos < _s.Length && (_s[Pos] == '+' || _s[Pos] == '-')) Pos++;
            if (Pos < _s.Length && char.IsDigit(_s[Pos])) { while (Pos < _s.Length && char.IsDigit(_s[Pos])) Pos++; }
            else Pos = save;
        }
        if (!digits) { Pos = start + 1; return false; }
        return Parse(_s.AsSpan(start, Pos - start), out value);
    }

    private static readonly double[] Pow10 = { 1, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15 };

    /// <summary>Fast path for plain decimals (by far the most common in path data); anything else goes to float.TryParse.</summary>
    private static bool Parse(ReadOnlySpan<char> text, out float value)
    {
        int i = 0;
        bool negative = false;
        if (text[0] == '-' || text[0] == '+') { negative = text[0] == '-'; i++; }
        long mantissa = 0;
        int digits = 0, fraction = -1;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '.') { fraction = 0; continue; }
            if (c < '0' || c > '9' || digits >= 15)
                return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            mantissa = mantissa * 10 + (c - '0');
            if (mantissa != 0) digits++;
            if (fraction >= 0) fraction++;
        }
        double v = fraction > 0 ? mantissa / Pow10[Math.Min(fraction, 15)] : mantissa;
        value = (float)(negative ? -v : v);
        return true;
    }

    /// <summary>An arc flag: a single 0 or 1, which may be followed directly by the next number.</summary>
    public bool TryFlag(out bool flag)
    {
        flag = false;
        SkipSeparators();
        if (Pos >= _s.Length || _s[Pos] is not ('0' or '1')) return false;
        flag = _s[Pos] == '1';
        Pos++;
        return true;
    }
}

/// <summary>Parses SVG path data into absolute move/line/cubic commands. Quadratic curves and arcs become cubics.</summary>
internal static class PathParser
{
    public static PathBuilder? Parse(string? d)
    {
        if (string.IsNullOrWhiteSpace(d)) return null;
        var p = new PathBuilder();
        var lx = new NumberLexer(d!, 0);
        float cx = 0, cy = 0, sx = 0, sy = 0;  // current point, subpath start
        float lcx = 0, lcy = 0;                // last control point (for S/T)
        char prev = ' ';
        char cmd = ' ';
        bool hasCurrent = false;
        while (true)
        {
            lx.SkipSeparators();
            if (lx.Pos >= d!.Length) break;
            char c = d[lx.Pos];
            if (char.IsLetter(c))
            {
                cmd = c;
                lx.Pos++;
            }
            else if (cmd == ' ' || !lx.AtNumber()) break;
            else if (cmd == 'M') cmd = 'L'; // extra coordinate pairs after a moveto are linetos
            else if (cmd == 'm') cmd = 'l';

            bool rel = char.IsLower(cmd);
            float ox = rel ? cx : 0, oy = rel ? cy : 0;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    if (!lx.TryNumber(out float x) || !lx.TryNumber(out float y)) return Finish(p);
                    cx = sx = ox + x; cy = sy = oy + y;
                    p.MoveTo(cx, cy);
                    hasCurrent = true;
                    lcx = cx; lcy = cy;
                    break;
                }
                case 'Z':
                    if (hasCurrent) p.Close();
                    cx = sx; cy = sy;
                    lcx = cx; lcy = cy;
                    break;
                case 'L':
                {
                    if (!lx.TryNumber(out float x) || !lx.TryNumber(out float y)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    cx = ox + x; cy = oy + y;
                    p.LineTo(cx, cy);
                    lcx = cx; lcy = cy;
                    break;
                }
                case 'H':
                {
                    if (!lx.TryNumber(out float x)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    cx = (rel ? cx : 0) + x;
                    p.LineTo(cx, cy);
                    lcx = cx; lcy = cy;
                    break;
                }
                case 'V':
                {
                    if (!lx.TryNumber(out float y)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    cy = (rel ? cy : 0) + y;
                    p.LineTo(cx, cy);
                    lcx = cx; lcy = cy;
                    break;
                }
                case 'C':
                {
                    if (!lx.TryNumber(out float x1) || !lx.TryNumber(out float y1) || !lx.TryNumber(out float x2) || !lx.TryNumber(out float y2)
                        || !lx.TryNumber(out float x) || !lx.TryNumber(out float y)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    p.CurveTo(ox + x1, oy + y1, ox + x2, oy + y2, ox + x, oy + y);
                    lcx = ox + x2; lcy = oy + y2;
                    cx = ox + x; cy = oy + y;
                    break;
                }
                case 'S':
                {
                    if (!lx.TryNumber(out float x2) || !lx.TryNumber(out float y2) || !lx.TryNumber(out float x) || !lx.TryNumber(out float y)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    bool smooth = "CcSs".IndexOf(prev) >= 0;
                    float x1 = smooth ? 2 * cx - lcx : cx, y1 = smooth ? 2 * cy - lcy : cy;
                    p.CurveTo(x1, y1, ox + x2, oy + y2, ox + x, oy + y);
                    lcx = ox + x2; lcy = oy + y2;
                    cx = ox + x; cy = oy + y;
                    break;
                }
                case 'Q':
                {
                    if (!lx.TryNumber(out float qx) || !lx.TryNumber(out float qy) || !lx.TryNumber(out float x) || !lx.TryNumber(out float y)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    Quad(p, cx, cy, ox + qx, oy + qy, ox + x, oy + y);
                    lcx = ox + qx; lcy = oy + qy;
                    cx = ox + x; cy = oy + y;
                    break;
                }
                case 'T':
                {
                    if (!lx.TryNumber(out float x) || !lx.TryNumber(out float y)) return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    bool smooth = "QqTt".IndexOf(prev) >= 0;
                    float qx = smooth ? 2 * cx - lcx : cx, qy = smooth ? 2 * cy - lcy : cy;
                    Quad(p, cx, cy, qx, qy, ox + x, oy + y);
                    lcx = qx; lcy = qy;
                    cx = ox + x; cy = oy + y;
                    break;
                }
                case 'A':
                {
                    if (!lx.TryNumber(out float rx) || !lx.TryNumber(out float ry) || !lx.TryNumber(out float rot)
                        || !lx.TryFlag(out bool large) || !lx.TryFlag(out bool sweep) || !lx.TryNumber(out float x) || !lx.TryNumber(out float y))
                        return Finish(p);
                    EnsureStart(p, ref hasCurrent, cx, cy);
                    Arc(p, cx, cy, rx, ry, rot, large, sweep, ox + x, oy + y);
                    cx = ox + x; cy = oy + y;
                    lcx = cx; lcy = cy;
                    break;
                }
                default:
                    return Finish(p);
            }
            prev = cmd;
        }
        return Finish(p);
    }

    private static PathBuilder? Finish(PathBuilder p) => p.Verbs.Count > 0 ? p : null;

    private static void EnsureStart(PathBuilder p, ref bool hasCurrent, float x, float y)
    {
        if (hasCurrent) return;
        p.MoveTo(x, y);
        hasCurrent = true;
    }

    private static void Quad(PathBuilder p, float x0, float y0, float qx, float qy, float x, float y)
        => p.CurveTo(x0 + 2f / 3 * (qx - x0), y0 + 2f / 3 * (qy - y0), x + 2f / 3 * (qx - x), y + 2f / 3 * (qy - y), x, y);

    /// <summary>Converts an endpoint-parameterised elliptical arc (SVG 1.1 F.6) into cubic Béziers.</summary>
    private static void Arc(PathBuilder p, float x1, float y1, float rx, float ry, float angle, bool large, bool sweep, float x2, float y2)
    {
        if (x1 == x2 && y1 == y2) return;
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0)
        {
            p.LineTo(x2, y2);
            return;
        }
        double phi = angle * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (x1 - x2) / 2.0, dy = (y1 - y2) / 2.0;
        double x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;
        double lambda = x1p * x1p / (rx * (double)rx) + y1p * y1p / (ry * (double)ry);
        double Rx = rx, Ry = ry;
        if (lambda > 1)
        {
            double sq = Math.Sqrt(lambda);
            Rx *= sq;
            Ry *= sq;
        }
        double num = Rx * Rx * Ry * Ry - Rx * Rx * y1p * y1p - Ry * Ry * x1p * x1p;
        double den = Rx * Rx * y1p * y1p + Ry * Ry * x1p * x1p;
        double coef = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den));
        if (large == sweep) coef = -coef;
        double cxp = coef * Rx * y1p / Ry, cyp = -coef * Ry * x1p / Rx;
        double cx = cos * cxp - sin * cyp + (x1 + x2) / 2.0, cy = sin * cxp + cos * cyp + (y1 + y2) / 2.0;

        static double AngleOf(double ux, double uy, double vx, double vy)
        {
            double a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            return a;
        }
        double theta1 = AngleOf(1, 0, (x1p - cxp) / Rx, (y1p - cyp) / Ry);
        double delta = AngleOf((x1p - cxp) / Rx, (y1p - cyp) / Ry, (-x1p - cxp) / Rx, (-y1p - cyp) / Ry);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-6));
        double step = delta / segments;
        double t = 4.0 / 3.0 * Math.Tan(step / 4);
        double a0 = theta1;
        for (int i = 0; i < segments; i++)
        {
            double a1 = a0 + step;
            double c0 = Math.Cos(a0), s0 = Math.Sin(a0), c1 = Math.Cos(a1), s1 = Math.Sin(a1);
            // Unit-circle control points, then scale, rotate and translate.
            double px1 = c0 - t * s0, py1 = s0 + t * c0;
            double px2 = c1 + t * s1, py2 = s1 - t * c1;
            (double, double) Map(double ux, double uy)
            {
                double ex = ux * Rx, ey = uy * Ry;
                return (cos * ex - sin * ey + cx, sin * ex + cos * ey + cy);
            }
            var (ax1, ay1) = Map(px1, py1);
            var (ax2, ay2) = Map(px2, py2);
            var (ax, ay) = i == segments - 1 ? (x2, y2) : Map(c1, s1);
            p.CurveTo((float)ax1, (float)ay1, (float)ax2, (float)ay2, (float)ax, (float)ay);
            a0 = a1;
        }
    }
}
