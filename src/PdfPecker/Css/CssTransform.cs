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
            default:
                return false; // 3D functions and unknown names make the whole value invalid
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

    /// <summary>
    /// The transform as a matrix about the origin of a <paramref name="w"/> x <paramref name="h"/> border box, in
    /// y-down coordinates. The functions apply right to left, as CSS composes them.
    /// </summary>
    public static Matrix ToMatrix(TransformFn[] fns, float w, float h)
    {
        var m = Matrix.Identity;
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
            m = m.Then(step);
        }
        return m;
    }
}
