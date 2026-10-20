using System.Globalization;
using PdfPecker.Svg;

namespace PdfPecker.Css;

/// <summary>Parses the transform and transform-origin properties and turns a transform list into a matrix.</summary>
internal static class CssTransform
{
    // Real transform lists hold a handful of functions; the limit keeps hostile stylesheets cheap.
    private const int MaxFunctions = 32;

    public static bool TryParse(string v, float em, float rem, float vw, float vh, out TransformFn[] result)
    {
        result = Array.Empty<TransformFn>();
        var list = new List<TransformFn>();
        int i = 0;
        while (i < v.Length)
        {
            while (i < v.Length && char.IsWhiteSpace(v[i])) i++;
            if (i >= v.Length) break;
            int open = v.IndexOf('(', i);
            if (open < 0) return false;
            int close = MatchingParen(v, open);
            if (close < 0) return false;
            string name = v.Substring(i, open - i).Trim();
            // Arguments may hold calc() and friends, so split outside parentheses only.
            var inner = v.Substring(open + 1, close - open - 1);
            var args = CssParser.SplitTopLevel(inner, ',').Select(a => a.Trim()).ToArray();
            if (args.Length == 1) args = CssParser.Tokens(args[0]).ToArray();
            i = close + 1;
            if (list.Count == MaxFunctions || !TryFunction(name, args, em, rem, vw, vh, list)) return false;
        }
        if (list.Count == 0) return false;
        result = list.ToArray();
        return true;
    }

    private static bool TryFunction(string name, string[] a, float em, float rem, float vw, float vh, List<TransformFn> list)
    {
        switch (name)
        {
            case "translate":
            case "translatex":
            case "translatey":
            {
                if (a.Length is < 1 or > 2 || name != "translate" && a.Length != 1) return false;
                if (!TryLength(a[0], em, rem, vw, vh, out var first)) return false;
                var second = Length.Zero;
                if (a.Length == 2 && !TryLength(a[1], em, rem, vw, vh, out second)) return false;
                var (x, y) = name == "translatey" ? (Length.Zero, first) : (first, second);
                if (x.Unit != LengthUnit.Calc && y.Unit != LengthUnit.Calc)
                {
                    list.Add(new TransformFn(TransformKind.Translate, x.Value, y.Value, PercentX: x.IsPercent, PercentY: y.IsPercent));
                    return true;
                }
                // calc(50% + 10px): translations add up, so move by the percentage part and then by the points part.
                if (!x.IsLinear || !y.IsLinear) return false;
                list.Add(new TransformFn(TransformKind.Translate, PctPart(x), PctPart(y), PercentX: true, PercentY: true));
                list.Add(new TransformFn(TransformKind.Translate, x.Unit == LengthUnit.Percent ? 0 : x.Value, y.Unit == LengthUnit.Percent ? 0 : y.Value));
                return true;
            }
            case "scale":
            case "scalex":
            case "scaley":
            {
                if (a.Length is < 1 or > 2 || name != "scale" && a.Length != 1) return false;
                if (!TryNumber(a[0], out float sx)) return false;
                float sy = sx;
                if (a.Length == 2 && !TryNumber(a[1], out sy)) return false;
                if (name == "scalex") sy = 1;
                else if (name == "scaley") { sy = sx; sx = 1; }
                list.Add(new TransformFn(TransformKind.Scale, sx, sy));
                return true;
            }
            case "rotate":
            case "rotatez":
                if (a.Length != 1 || !TryAngle(a[0], out float deg)) return false;
                list.Add(new TransformFn(TransformKind.Rotate, deg));
                return true;
            case "skew":
            case "skewx":
            case "skewy":
            {
                if (a.Length is < 1 or > 2 || name != "skew" && a.Length != 1) return false;
                if (!TryAngle(a[0], out float ax)) return false;
                float ay = 0;
                if (a.Length == 2 && !TryAngle(a[1], out ay)) return false;
                if (name == "skewy") (ax, ay) = (0, ax);
                list.Add(new TransformFn(TransformKind.Skew, ax, ay));
                return true;
            }
            case "matrix":
            {
                if (a.Length != 6) return false;
                var m = new float[6];
                for (int k = 0; k < 6; k++)
                    if (!TryNumber(a[k], out m[k])) return false;
                list.Add(new TransformFn(TransformKind.Matrix, m[0], m[1], m[2], m[3], m[4] * 0.75f, m[5] * 0.75f));
                return true;
            }
            case "translate3d":
            {
                if (a.Length != 3 || !TryFunction("translate", new[] { a[0], a[1] }, em, rem, vw, vh, list)) return false;
                return TryFunction("translatez", new[] { a[2] }, em, rem, vw, vh, list);
            }
            case "translatez":
            {
                // A z offset is a length; a percentage has no depth to refer to.
                if (a.Length != 1 || !TryLength(a[0], em, rem, vw, vh, out var z) || z.Unit != LengthUnit.Pt) return false;
                list.Add(new TransformFn(TransformKind.Translate, 0, 0, z.Value));
                return true;
            }
            case "scale3d":
            {
                if (a.Length != 3 || !TryNumber(a[0], out float x3) || !TryNumber(a[1], out float y3) || !TryNumber(a[2], out float z3)) return false;
                list.Add(new TransformFn(TransformKind.Scale3d, x3, y3, z3));
                return true;
            }
            case "scalez":
            {
                if (a.Length != 1 || !TryNumber(a[0], out float sz)) return false;
                list.Add(new TransformFn(TransformKind.Scale3d, 1, 1, sz));
                return true;
            }
            case "rotatex":
            case "rotatey":
                if (a.Length != 1 || !TryAngle(a[0], out float axisAngle)) return false;
                list.Add(new TransformFn(TransformKind.Rotate3d, name == "rotatex" ? 1 : 0, name == "rotatey" ? 1 : 0, 0, axisAngle));
                return true;
            case "rotate3d":
            {
                if (a.Length != 4 || !TryPlainNumber(a[0], out float rx) || !TryPlainNumber(a[1], out float ry) || !TryPlainNumber(a[2], out float rz)
                    || !TryAngle(a[3], out float ra)) return false;
                if (rx == 0 && ry == 0) list.Add(new TransformFn(TransformKind.Rotate, rz == 0 ? 0 : rz > 0 ? ra : -ra)); // a turn about the z axis is 2D
                else list.Add(new TransformFn(TransformKind.Rotate3d, rx, ry, rz, ra));
                return true;
            }
            case "perspective":
            {
                // The distance to the viewer; a closer one than 1px is taken as 1px, as browsers do.
                if (a.Length != 1 || !TryLength(a[0], em, rem, vw, vh, out var d) || d.Unit != LengthUnit.Pt || d.Value < 0) return false;
                list.Add(new TransformFn(TransformKind.Perspective, MathF.Max(d.Value, 0.75f)));
                return true;
            }
            case "matrix3d":
            {
                if (a.Length != 16) return false;
                var m = new float[16];
                for (int k = 0; k < 16; k++)
                    if (!TryPlainNumber(a[k], out m[k])) return false;
                m[12] *= 0.75f; m[13] *= 0.75f; m[14] *= 0.75f;
                list.Add(new TransformFn(TransformKind.Matrix3d, 0, M: m));
                return true;
            }
            default:
                return false; // unknown names make the whole value invalid
        }
    }

    private static float PctPart(Length l) => l.Unit switch { LengthUnit.Percent => l.Value, LengthUnit.Calc => l.Pct, _ => 0 };

    private static int MatchingParen(string v, int open)
    {
        int depth = 0;
        for (int i = open; i < v.Length; i++)
        {
            if (v[i] == '(') depth++;
            else if (v[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static bool TryLength(string s, float em, float rem, float vw, float vh, out Length len)
    {
        if (s == "0") { len = Length.Zero; return true; }
        return StyleResolver.TryParseLength(s, em, rem, out len, vw, vh) && !len.IsAuto;
    }

    private static bool TryNumber(string s, out float n)
    {
        if (s.EndsWith('%'))
        {
            bool ok = float.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out n);
            n /= 100;
            return ok && float.IsFinite(n);
        }
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && float.IsFinite(n);
    }

    private static bool TryPlainNumber(string s, out float n)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n) && float.IsFinite(n);

    private static bool TryAngle(string s, out float degrees)
    {
        degrees = 0;
        if (s == "0") return true;
        (string Unit, float Factor)[] units = { ("deg", 1), ("grad", 0.9f), ("rad", 180 / MathF.PI), ("turn", 360) };
        foreach (var (unit, factor) in units)
        {
            if (!s.EndsWith(unit, StringComparison.Ordinal)) continue;
            if (!float.TryParse(s.AsSpan(0, s.Length - unit.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out float n) || !float.IsFinite(n)) return false;
            degrees = n * factor;
            return true;
        }
        return false;
    }

    /// <summary>Parses transform-origin: one to three values (the third, a z offset, is ignored).</summary>
    public static bool TryParseOrigin(string v, float em, float rem, float vw, float vh, out Length x, out Length y)
    {
        var center = new Length(50, LengthUnit.Percent);
        x = y = center;
        var t = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length is < 1 or > 3) return false;
        if (t.Length == 1)
        {
            if (t[0] is "top" or "bottom") { y = Keyword(t[0])!.Value; return true; }
            if (Keyword(t[0]) is { } k) { x = k; return true; }
            return TryLength(t[0], em, rem, vw, vh, out x);
        }
        string a = t[0], b = t[1];
        if (a is "top" or "bottom" || b is "left" or "right") (a, b) = (b, a);
        return TryOriginValue(a, em, rem, vw, vh, out x) && TryOriginValue(b, em, rem, vw, vh, out y);
    }

    private static bool TryOriginValue(string s, float em, float rem, float vw, float vh, out Length len)
    {
        if (Keyword(s) is { } k) { len = k; return true; }
        return TryLength(s, em, rem, vw, vh, out len);
    }

    private static Length? Keyword(string s) => s switch
    {
        "left" or "top" => new Length(0, LengthUnit.Percent),
        "center" => new Length(50, LengthUnit.Percent),
        "right" or "bottom" => new Length(100, LengthUnit.Percent),
        _ => null,
    };

    /// <summary>Whether the list turns, scales or moves things in depth, or has a perspective: it needs the origin-aware <c>ToMatrix</c>.</summary>
    public static bool Has3D(TransformFn[] fns)
    {
        foreach (var f in fns)
            if (f.Kind is TransformKind.Scale3d or TransformKind.Rotate3d or TransformKind.Perspective or TransformKind.Matrix3d
                || f.Kind == TransformKind.Translate && f.C != 0) return true;
        return false;
    }

    /// <summary>
    /// The transform as a matrix about the origin of a <paramref name="w"/> x <paramref name="h"/> border box, in
    /// y-down coordinates. The functions apply right to left, as CSS composes them. A 3D transform is flattened as if
    /// the box's own centre is the point that matters.
    /// </summary>
    public static Matrix ToMatrix(TransformFn[] fns, float w, float h)
        => ToMatrix(fns, w, h, w * 0.5f, h * 0.5f, null) ?? Matrix.Scale(0, 0);

    /// <summary>
    /// The transform as a matrix about the transform origin (<paramref name="originX"/>, <paramref name="originY"/>) of a
    /// <paramref name="w"/> x <paramref name="h"/> border box, in y-down coordinates, or null when the box is turned out
    /// of sight (behind the viewer). The PDF has only affine matrices, so a 3D transform is flattened: the projection of
    /// the box (with the perspective of the parent, as <paramref name="perspective"/>, a distance and the perspective
    /// origin relative to the transform origin, and of any perspective() function) is replaced by the affine map that
    /// agrees with it at the centre of the box. Without perspective that is exactly the 2D part of the transform.
    /// </summary>
    public static Matrix? ToMatrix(TransformFn[] fns, float w, float h, float originX, float originY, (float Distance, float X, float Y)? perspective)
    {
        if (perspective == null && !Has3D(fns))
        {
            var m2 = Matrix.Identity;
            for (int i = fns.Length - 1; i >= 0; i--)
            {
                var f = fns[i];
                Matrix step = f.Kind switch
                {
                    TransformKind.Translate => Matrix.Translate(f.PercentX ? f.A * w / 100 : f.A, f.PercentY ? f.B * h / 100 : f.B),
                    TransformKind.Scale => Matrix.Scale(f.A, f.B),
                    TransformKind.Rotate => Matrix.Rotate(f.A),
                    TransformKind.Skew => new Matrix(1, MathF.Tan(f.B * MathF.PI / 180), MathF.Tan(f.A * MathF.PI / 180), 1, 0, 0),
                    _ => new Matrix(f.A, f.B, f.C, f.D, f.E, f.F),
                };
                m2 = m2.Then(step);
            }
            return m2;
        }

        // The parent's perspective acts on the box as it is after its own transform, with the viewer above the
        // perspective origin: translate there, project, translate back.
        var m = Identity4();
        if (perspective is { } p)
        {
            m = Translate4(p.X, p.Y, 0);
            var proj = Identity4();
            proj[11] = -1 / p.Distance;
            m = Multiply(m, proj);
            m = Multiply(m, Translate4(-p.X, -p.Y, 0));
        }
        foreach (var f in fns) m = Multiply(m, Step4(f, w, h));
        return Flatten(m, w * 0.5f - originX, h * 0.5f - originY);
    }

    // The 4x4 matrices below are in the order CSS gives matrix3d(): column by column, so that v[column * 4 + row].
    private static double[] Identity4() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    private static double[] Translate4(double x, double y, double z)
    {
        var m = Identity4();
        m[12] = x; m[13] = y; m[14] = z;
        return m;
    }

    /// <summary>a followed by b is <c>Multiply(b, a)</c>: the product applies its right factor to a point first.</summary>
    private static double[] Multiply(double[] a, double[] b)
    {
        var r = new double[16];
        for (int col = 0; col < 4; col++)
            for (int row = 0; row < 4; row++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++) sum += a[k * 4 + row] * b[col * 4 + k];
                r[col * 4 + row] = sum;
            }
        return r;
    }

    private static double[] Step4(TransformFn f, float w, float h)
    {
        var m = Identity4();
        switch (f.Kind)
        {
            case TransformKind.Translate:
                m[12] = f.PercentX ? f.A * w / 100 : f.A;
                m[13] = f.PercentY ? f.B * h / 100 : f.B;
                m[14] = f.C;
                break;
            case TransformKind.Scale: m[0] = f.A; m[5] = f.B; break;
            case TransformKind.Scale3d: m[0] = f.A; m[5] = f.B; m[10] = f.C; break;
            case TransformKind.Rotate:
            {
                double r = f.A * Math.PI / 180;
                m[0] = m[5] = Math.Cos(r);
                m[1] = Math.Sin(r);
                m[4] = -m[1];
                break;
            }
            case TransformKind.Skew:
                m[1] = Math.Tan(f.B * Math.PI / 180);
                m[4] = Math.Tan(f.A * Math.PI / 180);
                break;
            case TransformKind.Matrix:
                m[0] = f.A; m[1] = f.B; m[4] = f.C; m[5] = f.D; m[12] = f.E; m[13] = f.F;
                break;
            case TransformKind.Perspective: m[11] = -1 / f.A; break;
            case TransformKind.Matrix3d:
                for (int i = 0; i < 16; i++) m[i] = f.M![i];
                break;
            case TransformKind.Rotate3d:
            {
                // The rotation about the axis (x, y, z) from the CSS Transforms spec.
                double x = f.A, y = f.B, z = f.C, len = Math.Sqrt(x * x + y * y + z * z);
                if (len == 0) break;
                x /= len; y /= len; z /= len;
                double half = f.D * Math.PI / 360, sc = Math.Sin(half) * Math.Cos(half), sq = Math.Sin(half) * Math.Sin(half);
                m[0] = 1 - 2 * (y * y + z * z) * sq;
                m[1] = 2 * (x * y * sq + z * sc);
                m[2] = 2 * (x * z * sq - y * sc);
                m[4] = 2 * (x * y * sq - z * sc);
                m[5] = 1 - 2 * (x * x + z * z) * sq;
                m[6] = 2 * (y * z * sq + x * sc);
                m[8] = 2 * (x * z * sq + y * sc);
                m[9] = 2 * (y * z * sq - x * sc);
                m[10] = 1 - 2 * (x * x + y * y) * sq;
                break;
            }
        }
        return m;
    }

    /// <summary>
    /// The affine map that agrees with the projection of the z = 0 plane by <paramref name="m"/> at (cx, cy): the point
    /// goes where the projection sends it, and the plane around it is scaled and sheared by the projection's slope
    /// there. Null when the point is behind the viewer or the result is not a usable number.
    /// </summary>
    private static Matrix? Flatten(double[] m, double cx, double cy)
    {
        double x = m[0] * cx + m[4] * cy + m[12], y = m[1] * cx + m[5] * cy + m[13], w = m[3] * cx + m[7] * cy + m[15];
        if (!(w > 1e-3)) return null;
        double px = x / w, py = y / w;
        double a = (m[0] - px * m[3]) / w, c = (m[4] - px * m[7]) / w;
        double b = (m[1] - py * m[3]) / w, d = (m[5] - py * m[7]) / w;
        double e = px - a * cx - c * cy, f = py - b * cx - d * cy;
        // A box blown up past 10,000 times is a viewer inside it: nothing sensible to draw.
        if (Math.Abs(a) + Math.Abs(b) + Math.Abs(c) + Math.Abs(d) > 1e4 || !double.IsFinite(e + f)) return null;
        // A box turned edge-on (rotateX(90deg)) is a line with no area; browsers draw nothing for it.
        if (Math.Abs(a * d - b * c) < 1e-6) return null;
        return new Matrix((float)a, (float)b, (float)c, (float)d, (float)e, (float)f);
    }
}
