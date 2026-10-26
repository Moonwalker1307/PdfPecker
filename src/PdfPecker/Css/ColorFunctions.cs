using System.Globalization;

namespace PdfPecker.Css;

/// <summary>
/// The colour functions of CSS Color 4 and 5: <c>rgb()</c> and <c>hsl()</c> in both syntaxes, <c>hwb()</c>, <c>lab()</c>,
/// <c>lch()</c>, <c>oklab()</c>, <c>oklch()</c>, <c>color()</c> with the predefined colour spaces, <c>color-mix()</c> and
/// <c>light-dark()</c>, and the relative colour syntax of CSS Color 5 (<c>rgb(from &lt;color&gt; r g b / alpha)</c> and the
/// same for every function but <c>color-mix()</c>), whose channel keywords can be used in math functions. Colours are worked on as unclamped sRGB (so wide-gamut colours survive a mix) and clipped to
/// sRGB at the end, as Chrome does when it prints.
/// </summary>
internal static class ColorFunctions
{
    /// <summary>A colour as gamma-encoded sRGB components, unclamped, and alpha in 0..1.</summary>
    internal readonly record struct Rgba(double R, double G, double B, double A)
    {
        public uint ToArgb()
        {
            static uint C(double v) => (uint)Math.Round(Math.Clamp(double.IsNaN(v) ? 0 : v, 0, 1) * 255);
            return C(A) << 24 | C(R) << 16 | C(G) << 8 | C(B);
        }

        public static Rgba FromArgb(uint c)
            => new(((c >> 16) & 0xFF) / 255.0, ((c >> 8) & 0xFF) / 255.0, (c & 0xFF) / 255.0, (c >> 24) / 255.0);
    }

    private const int MaxDepth = 8;

    /// <summary>Parses a colour function call <paramref name="fn"/>(<paramref name="args"/>).</summary>
    public static bool TryParse(string fn, string args, uint current, out uint color)
    {
        color = 0;
        if (!TryParse(fn.ToLowerInvariant(), args, current, 0, out var c)) return false;
        color = c.ToArgb();
        return true;
    }

    private static bool TryParseAny(string value, uint current, int depth, out Rgba color)
    {
        color = default;
        var v = value.Trim();
        if (v.Length == 0 || depth > MaxDepth) return false;
        int paren = v.IndexOf('(');
        if (paren > 0 && v[^1] == ')')
            return TryParse(v.Substring(0, paren).Trim().ToLowerInvariant(), v.Substring(paren + 1, v.Length - paren - 2), current, depth, out color);
        if (!CssColor.TryParse(v, current, out uint c)) return false;
        color = Rgba.FromArgb(c);
        return true;
    }

    private static bool TryParse(string fn, string args, uint current, int depth, out Rgba color)
    {
        color = default;
        if (depth > MaxDepth) return false;
        if (fn is not ("color-mix" or "light-dark") && StartsWithWord(args, "from")) return Relative(fn, args, current, depth, out color);
        switch (fn)
        {
            case "rgb":
            case "rgba":
                return Rgb(args, out color);
            case "hsl":
            case "hsla":
                return Hsl(args, out color);
            case "hwb":
                return Hwb(args, out color);
            case "lab":
            case "lch":
            case "oklab":
            case "oklch":
                return LabLike(fn, args, out color);
            case "color":
                return ColorFn(args, out color);
            case "color-mix":
                return Mix(args, current, depth, out color);
            case "light-dark":
            {
                // Printed output uses the light scheme.
                var parts = CssParser.SplitTopLevel(args, ',');
                return parts.Count == 2 && TryParseAny(parts[0], current, depth + 1, out color);
            }
            default:
                return false;
        }
    }

    // ---------------------------------------------------------------- component parsing

    private enum Kind : byte { Number, Percent, Angle, None }

    private readonly record struct Component(double Value, Kind Kind);

    /// <summary>
    /// Splits the arguments into three components and an optional alpha. Accepts the legacy comma syntax (when
    /// <paramref name="legacy"/>) and the modern space syntax with "/ alpha". Relative colours ("from") are handled before.
    /// </summary>
    private static bool Components(string args, bool legacy, out Component[] comps, out Component? alpha)
    {
        comps = Array.Empty<Component>();
        alpha = null;
        var s = args.Trim();
        if (s.StartsWith("from ", StringComparison.OrdinalIgnoreCase)) return false;
        var list = new List<Component>(4);
        string main = s;
        string? alphaText = null;
        if (s.IndexOf(',') >= 0)
        {
            if (!legacy) return false;
            var parts = CssParser.SplitTopLevel(s, ',');
            if (parts.Count is < 3 or > 4) return false;
            foreach (var p in parts)
            {
                if (!TryComponent(p.Trim(), out var c) || c.Kind == Kind.None) return false;
                list.Add(c);
            }
            if (list.Count == 4) { alpha = list[3]; list.RemoveAt(3); }
            comps = list.ToArray();
            return true;
        }
        SplitAlpha(s, out main, out alphaText);
        foreach (var tok in CssParser.Tokens(main))
        {
            if (!TryComponent(tok, out var c)) return false;
            list.Add(c);
        }
        if (list.Count != 3) return false;
        if (alphaText != null)
        {
            if (!TryComponent(alphaText, out var a)) return false;
            alpha = a;
        }
        comps = list.ToArray();
        return true;
    }

    /// <summary>Splits "c1 c2 c3 / alpha" at the slash that is not inside a function.</summary>
    private static void SplitAlpha(string s, out string main, out string? alpha)
    {
        main = s;
        alpha = null;
        for (int i = 0, depth = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (s[i] == '/' && depth == 0)
            {
                main = s.Substring(0, i);
                alpha = s.Substring(i + 1).Trim();
                return;
            }
        }
    }

    private static bool StartsWithWord(string s, string word)
    {
        var t = s.AsSpan().TrimStart();
        return t.Length > word.Length && t.StartsWith(word, StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(t[word.Length]);
    }

    // ---------------------------------------------------------------- relative colours (CSS Color 5)

    /// <summary>
    /// A relative colour: "from &lt;origin&gt;" then the channels, where the function's channel keywords (r g b, h s l,
    /// l c h and so on, and alpha) stand for the origin colour's values in that function's colour space, as plain
    /// numbers. The keywords are replaced by their values and the result is parsed as an ordinary colour, so they work
    /// on their own and inside calc() and the other math functions. Without "/ alpha" the origin's alpha is kept.
    /// </summary>
    private static bool Relative(string fn, string args, uint current, int depth, out Rgba color)
    {
        color = default;
        var s = args.TrimStart().Substring(4).Trim();
        SplitAlpha(s, out var main, out var alphaText);
        var tokens = CssParser.Tokens(main);
        if (tokens.Count < 1 || !TryParseAny(tokens[0], current, depth + 1, out var origin)) return false;
        int first = 1;
        string? space = null;
        if (fn == "color")
        {
            if (tokens.Count < 2) return false;
            space = tokens[1].ToLowerInvariant();
            first = 2;
        }
        if (tokens.Count - first != 3 || !Channels(fn, space, origin, out var names, out var values)) return false;
        var sb = new System.Text.StringBuilder();
        if (space != null) sb.Append(space).Append(' ');
        for (int i = 0; i < 3; i++)
        {
            var channel = Substitute(tokens[first + i], names, values);
            if (channel == null) return false;
            sb.Append(channel).Append(' ');
        }
        sb.Append("/ ");
        if (alphaText == null) sb.Append(Format(origin.A));
        else if (Substitute(alphaText, names, values) is { } a) sb.Append(a);
        else return false;
        return TryParse(fn, sb.ToString(), current, depth + 1, out color);
    }

    /// <summary>The channel keywords of a colour function and the origin colour's value for each, the last being alpha.</summary>
    private static bool Channels(string fn, string? space, Rgba origin, out string[] names, out double[] values)
    {
        names = Array.Empty<string>();
        values = Array.Empty<double>();
        double[] v;
        switch (fn)
        {
            case "rgb":
            case "rgba":
                names = new[] { "r", "g", "b" };
                v = new[] { origin.R * 255, origin.G * 255, origin.B * 255 };
                break;
            case "hsl":
            case "hsla":
            {
                // Saturation and lightness are numbers on the percentage scale: s is 100 for a saturated colour.
                var (h, sat, l) = RgbToHsl(origin.R, origin.G, origin.B);
                names = new[] { "h", "s", "l" };
                v = new[] { h, sat * 100, l * 100 };
                break;
            }
            case "hwb":
            {
                if (!TryToSpace("hwb", origin, out var w, out _)) return false;
                names = new[] { "h", "w", "b" };
                v = new[] { w[2], w[0] * 100, w[1] * 100 };
                break;
            }
            case "lab":
            case "oklab":
                if (!TryToSpace(fn, origin, out v, out _)) return false;
                names = new[] { "l", "a", "b" };
                break;
            case "lch":
            case "oklch":
                if (!TryToSpace(fn, origin, out v, out _)) return false;
                names = new[] { "l", "c", "h" };
                break;
            case "color":
                if (space == null || !TryToSpace(space, origin, out v, out _)) return false;
                names = space.StartsWith("xyz", StringComparison.Ordinal) ? new[] { "x", "y", "z" } : new[] { "r", "g", "b" };
                break;
            default:
                return false;
        }
        names = new[] { names[0], names[1], names[2], "alpha" };
        // A missing hue (an achromatic origin) is 0 when a keyword refers to it.
        values = new[] { Finite(v[0]), Finite(v[1]), Finite(v[2]), origin.A };
        return true;

        static double Finite(double d) => double.IsFinite(d) ? d : 0;
    }

    /// <summary>
    /// Replaces the channel keywords in a component (an identifier on its own, not a function name or a unit) with
    /// their values; null when an identifier remains that is neither none nor a math keyword.
    /// </summary>
    private static string? Substitute(string component, string[] names, double[] values)
    {
        var sb = new System.Text.StringBuilder(component.Length + 8);
        int i = 0, n = component.Length;
        while (i < n)
        {
            char c = component[i];
            bool startsIdent = char.IsLetter(c) || c == '-' && i + 1 < n && char.IsLetter(component[i + 1]);
            bool afterNumber = i > 0 && (char.IsLetterOrDigit(component[i - 1]) || component[i - 1] is '.' or '_' or '-' or '%');
            if (!startsIdent || afterNumber)
            {
                sb.Append(c);
                i++;
                continue;
            }
            int start = i;
            while (i < n && (char.IsLetterOrDigit(component[i]) || component[i] is '-' or '_')) i++;
            var ident = component.Substring(start, i - start);
            if (i < n && component[i] == '(') { sb.Append(ident); continue; }
            int k = Array.FindIndex(names, x => x.Equals(ident, StringComparison.OrdinalIgnoreCase));
            if (k >= 0)
            {
                // In brackets, so that "calc(10 - a)" with a negative a stays a valid expression.
                bool alone = start == 0 && i == n;
                var value = Format(values[k]);
                sb.Append(alone || values[k] >= 0 ? value : "(" + value + ")");
                continue;
            }
            var lower = ident.ToLowerInvariant();
            if (lower is not ("none" or "pi" or "e" or "infinity" or "-infinity" or "nearest" or "up" or "down" or "to-zero")) return null;
            sb.Append(ident);
        }
        return sb.ToString();
    }

    private static string Format(double v) => v.ToString("0.##########", CultureInfo.InvariantCulture);

    private static bool TryComponent(string tok, out Component c)
    {
        c = default;
        var t = tok.Trim().ToLowerInvariant();
        if (t.Length == 0) return false;
        if (t == "none") { c = new Component(0, Kind.None); return true; }
        if (CssMath.HasMath(t))
        {
            if (CssMath.TryParseAngle(t, out float deg)) { c = new Component(deg, Kind.Angle); return true; }
            if (CssMath.TryParseNumber(t, out float n)) { c = new Component(n, Kind.Number); return true; }
            return false;
        }
        if (t.EndsWith('%'))
        {
            if (!double.TryParse(t.AsSpan(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double p)) return false;
            c = new Component(p, Kind.Percent);
            return true;
        }
        if (TryAngle(t, out double angle)) { c = new Component(angle, Kind.Angle); return true; }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return false;
        c = new Component(v, Kind.Number);
        return true;
    }

    /// <summary>Parses an angle with a unit (deg, rad, grad, turn) into degrees.</summary>
    internal static bool TryAngle(string t, out double degrees)
    {
        degrees = 0;
        (string Unit, double Factor)[] units = { ("deg", 1), ("grad", 0.9), ("rad", 180 / Math.PI), ("turn", 360) };
        foreach (var (unit, factor) in units)
        {
            if (!t.EndsWith(unit, StringComparison.Ordinal)) continue;
            if (!double.TryParse(t.AsSpan(0, t.Length - unit.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return false;
            degrees = v * factor;
            return true;
        }
        return false;
    }

    private static double Alpha(Component? a)
        => a is not { } c ? 1 : c.Kind switch { Kind.Percent => c.Value / 100, Kind.None => 0, _ => c.Value };

    /// <summary>A number, or a percentage of <paramref name="hundredPercent"/>.</summary>
    private static double Num(Component c, double hundredPercent) => c.Kind == Kind.Percent ? c.Value / 100 * hundredPercent : c.Value;

    private static double Hue(Component c) => c.Kind is Kind.Angle or Kind.Number or Kind.None ? c.Value : double.NaN;

    // ---------------------------------------------------------------- the functions

    private static bool Rgb(string args, out Rgba color)
    {
        color = default;
        if (!Components(args, legacy: true, out var c, out var a)) return false;
        color = new Rgba(Num(c[0], 255) / 255, Num(c[1], 255) / 255, Num(c[2], 255) / 255, Math.Clamp(Alpha(a), 0, 1));
        return true;
    }

    private static bool Hsl(string args, out Rgba color)
    {
        color = default;
        if (!Components(args, legacy: true, out var c, out var a)) return false;
        double h = Hue(c[0]);
        if (double.IsNaN(h)) return false;
        double s = Num(c[1], 100) / 100, l = Num(c[2], 100) / 100;
        var (r, g, b) = HslToRgb(h, s, l);
        color = new Rgba(r, g, b, Math.Clamp(Alpha(a), 0, 1));
        return true;
    }

    private static (double R, double G, double B) HslToRgb(double h, double s, double l)
    {
        h = (h % 360 + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        double F(double n)
        {
            double k = (n + h / 30) % 12;
            double a = s * Math.Min(l, 1 - l);
            return l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1));
        }
        return (F(0), F(8), F(4));
    }

    private static (double H, double S, double L) RgbToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = double.NaN, s = 0, l = (min + max) / 2, d = max - min;
        if (d != 0)
        {
            s = l is 0 or 1 ? 0 : (max - l) / Math.Min(l, 1 - l);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    private static bool Hwb(string args, out Rgba color)
    {
        color = default;
        if (!Components(args, legacy: false, out var c, out var a)) return false;
        double h = Hue(c[0]);
        if (double.IsNaN(h)) return false;
        var (r, g, b) = HwbToRgb(h, Num(c[1], 100) / 100, Num(c[2], 100) / 100);
        color = new Rgba(r, g, b, Math.Clamp(Alpha(a), 0, 1));
        return true;
    }

    private static (double R, double G, double B) HwbToRgb(double h, double w, double bl)
    {
        if (w + bl >= 1)
        {
            double gray = w / (w + bl);
            return (gray, gray, gray);
        }
        var (r, g, b) = HslToRgb(h, 1, 0.5);
        double k = 1 - w - bl;
        return (r * k + w, g * k + w, b * k + w);
    }

    private static bool LabLike(string fn, string args, out Rgba color)
    {
        color = default;
        if (!Components(args, legacy: false, out var c, out var a)) return false;
        double alpha = Math.Clamp(Alpha(a), 0, 1);
        (double R, double G, double B) rgb;
        switch (fn)
        {
            case "lab":
            {
                double l = Math.Clamp(Num(c[0], 100), 0, 100);
                rgb = FromXyzD50(LabToXyzD50(l, Num(c[1], 125), Num(c[2], 125)));
                break;
            }
            case "lch":
            {
                double l = Math.Clamp(Num(c[0], 100), 0, 100), ch = Math.Max(0, Num(c[1], 150)), h = Hue(c[2]);
                if (double.IsNaN(h)) return false;
                var (la, lb) = Polar(ch, h);
                rgb = FromXyzD50(LabToXyzD50(l, la, lb));
                break;
            }
            case "oklab":
            {
                double l = Math.Clamp(Num(c[0], 1), 0, 1);
                rgb = Gamma(OklabToLinear(l, Num(c[1], 0.4), Num(c[2], 0.4)));
                break;
            }
            default: // oklch
            {
                double l = Math.Clamp(Num(c[0], 1), 0, 1), ch = Math.Max(0, Num(c[1], 0.4)), h = Hue(c[2]);
                if (double.IsNaN(h)) return false;
                var (oa, ob) = Polar(ch, h);
                rgb = Gamma(OklabToLinear(l, oa, ob));
                break;
            }
        }
        color = new Rgba(rgb.R, rgb.G, rgb.B, alpha);
        return true;
    }

    private static (double A, double B) Polar(double chroma, double hueDeg)
    {
        double rad = hueDeg * Math.PI / 180;
        return (chroma * Math.Cos(rad), chroma * Math.Sin(rad));
    }

    private static bool ColorFn(string args, out Rgba color)
    {
        color = default;
        var s = args.Trim();
        int sp = 0;
        while (sp < s.Length && !char.IsWhiteSpace(s[sp])) sp++;
        var space = s.Substring(0, sp).ToLowerInvariant();
        var rest = s.Substring(sp);
        if (!Components(rest, legacy: false, out var c, out var a)) return false;
        double x = Num(c[0], 1), y = Num(c[1], 1), z = Num(c[2], 1);
        if (!TryFromSpace(space, (x, y, z), out var rgb)) return false;
        color = new Rgba(rgb.R, rgb.G, rgb.B, Math.Clamp(Alpha(a), 0, 1));
        return true;
    }

    // ---------------------------------------------------------------- color-mix()

    private static bool Mix(string args, uint current, int depth, out Rgba color)
    {
        color = default;
        var parts = CssParser.SplitTopLevel(args, ',');
        if (parts.Count != 3) return false;
        var head = CssParser.Tokens(parts[0].Trim().ToLowerInvariant());
        if (head.Count < 2 || head[0] != "in") return false;
        string space = head[1];
        string hueMethod = "shorter";
        if (head.Count == 4 && head[3] == "hue") hueMethod = head[2];
        else if (head.Count != 2) return false;

        if (!MixArg(parts[1], current, depth, out var c1, out double? p1) || !MixArg(parts[2], current, depth, out var c2, out double? p2))
            return false;
        // Percentages: both missing is 50/50; one missing is the rest of 100%; a sum other than 100% is normalised,
        // and a sum below 100% also scales the result's alpha.
        if (p1 == null && p2 == null) { p1 = 50; p2 = 50; }
        else if (p1 == null) p1 = 100 - p2;
        else if (p2 == null) p2 = 100 - p1;
        double w1 = p1!.Value, w2 = p2!.Value;
        if (w1 < 0 || w2 < 0) return false;
        double sum = w1 + w2;
        if (sum <= 0) return false;
        double alphaScale = sum < 100 ? sum / 100 : 1;
        double t = w2 / sum;

        if (!TryInterpolate(c1, c2, t, space, hueMethod, out color)) return false;
        color = color with { A = Math.Clamp(color.A * alphaScale, 0, 1) };
        return true;
    }

    /// <summary>
    /// Whether colours can be interpolated in <paramref name="space"/> (a color-mix() or gradient interpolation space),
    /// and whether it is polar, so that a hue interpolation method may follow it.
    /// </summary>
    internal static bool IsInterpolationSpace(string space, out bool polar) => TryToSpace(space, default, out _, out polar);

    /// <summary>
    /// The colour a fraction <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>,
    /// interpolated in <paramref name="space"/> with the hue method <paramref name="hueMethod"/>, as gradients with a
    /// colour interpolation method mix their stops.
    /// </summary>
    internal static uint Interpolate(uint from, uint to, double t, string space, string hueMethod)
        => TryInterpolate(Rgba.FromArgb(from), Rgba.FromArgb(to), t, space, hueMethod, out var c) ? c.ToArgb() : t < 0.5 ? from : to;

    /// <summary>Interpolates two colours in a space, premultiplied by alpha (the hue of a polar space is not).</summary>
    private static bool TryInterpolate(Rgba c1, Rgba c2, double t, string space, string hueMethod, out Rgba color)
    {
        color = default;
        if (!TryToSpace(space, c1, out var v1, out bool polar) || !TryToSpace(space, c2, out var v2, out _)) return false;
        double a = c1.A * (1 - t) + c2.A * t;
        double[] r = new double[3];
        for (int k = 0; k < 3; k++)
        {
            if (polar && k == 2)
            {
                r[k] = MixHue(v1[k], v2[k], t, hueMethod);
                continue;
            }
            double x1 = v1[k] * c1.A, x2 = v2[k] * c2.A;
            double mixed = x1 * (1 - t) + x2 * t;
            r[k] = a > 0 ? mixed / a : 0;
        }
        if (!TryFromSpaceVector(space, r, out var rgb)) return false;
        color = new Rgba(rgb.R, rgb.G, rgb.B, Math.Clamp(a, 0, 1));
        return true;
    }

    private static bool MixArg(string part, uint current, int depth, out Rgba color, out double? percent)
    {
        color = default;
        percent = null;
        var tokens = CssParser.Tokens(part.Trim());
        string? colorText = null;
        foreach (var tok in tokens)
        {
            if (tok.EndsWith('%') && percent == null
                && double.TryParse(tok.AsSpan(0, tok.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
            {
                percent = p;
                continue;
            }
            if (CssMath.HasMath(tok) && percent == null && CssMath.TryParseLength(tok, 12, 12, 0, 0, out var len) && len.Unit == LengthUnit.Percent)
            {
                percent = len.Value;
                continue;
            }
            if (colorText != null) return false;
            colorText = tok;
        }
        return colorText != null && (percent is null or (>= 0 and <= 100)) && TryParseAny(colorText, current, depth + 1, out color);
    }

    private static double MixHue(double h1, double h2, double t, string method)
    {
        if (double.IsNaN(h1)) return h2;
        if (double.IsNaN(h2)) return h1;
        h1 = (h1 % 360 + 360) % 360;
        h2 = (h2 % 360 + 360) % 360;
        double d = h2 - h1;
        switch (method)
        {
            case "longer":
                if (d > 0 && d < 180) h1 += 360;
                else if (d > -180 && d <= 0) h2 += 360;
                break;
            case "increasing":
                if (d < 0) h2 += 360;
                break;
            case "decreasing":
                if (d > 0) h1 += 360;
                break;
            default: // shorter
                if (d > 180) h1 += 360;
                else if (d < -180) h2 += 360;
                break;
        }
        return h1 * (1 - t) + h2 * t;
    }

    /// <summary>The colour's coordinates in an interpolation space. Polar spaces put the hue last.</summary>
    private static bool TryToSpace(string space, Rgba c, out double[] v, out bool polar)
    {
        polar = false;
        v = Array.Empty<double>();
        var lin = (Linear(c.R), Linear(c.G), Linear(c.B));
        switch (space)
        {
            case "srgb":
                v = new[] { c.R, c.G, c.B };
                return true;
            case "srgb-linear":
                v = new[] { lin.Item1, lin.Item2, lin.Item3 };
                return true;
            case "display-p3":
            {
                var p = MulM(XyzToLinP3, MulM(LinSrgbToXyz, lin));
                v = new[] { Encode(p.Item1), Encode(p.Item2), Encode(p.Item3) };
                return true;
            }
            case "a98-rgb":
            {
                static double Enc(double v) => Math.Sign(v) * Math.Pow(Math.Abs(v), 256.0 / 563);
                var p = MulM(XyzToLinA98, MulM(LinSrgbToXyz, lin));
                v = new[] { Enc(p.Item1), Enc(p.Item2), Enc(p.Item3) };
                return true;
            }
            case "prophoto-rgb":
            {
                static double Enc(double v) => Math.Abs(v) >= 1.0 / 512 ? Math.Sign(v) * Math.Pow(Math.Abs(v), 1 / 1.8) : 16 * v;
                var p = MulM(XyzD50ToLinProPhoto, MulM(D65ToD50, MulM(LinSrgbToXyz, lin)));
                v = new[] { Enc(p.Item1), Enc(p.Item2), Enc(p.Item3) };
                return true;
            }
            case "rec2020":
            {
                static double Enc(double v)
                {
                    const double alpha = 1.09929682680944, beta = 0.018053968510807;
                    double abs = Math.Abs(v);
                    return abs > beta ? Math.Sign(v) * (alpha * Math.Pow(abs, 0.45) - (alpha - 1)) : 4.5 * v;
                }
                var p = MulM(XyzToLinRec2020, MulM(LinSrgbToXyz, lin));
                v = new[] { Enc(p.Item1), Enc(p.Item2), Enc(p.Item3) };
                return true;
            }
            case "xyz":
            case "xyz-d65":
            {
                var x = MulM(LinSrgbToXyz, lin);
                v = new[] { x.Item1, x.Item2, x.Item3 };
                return true;
            }
            case "xyz-d50":
            {
                var x = MulM(D65ToD50, MulM(LinSrgbToXyz, lin));
                v = new[] { x.Item1, x.Item2, x.Item3 };
                return true;
            }
            case "lab":
            case "lch":
            {
                var (l, a, b) = XyzD50ToLab(MulM(D65ToD50, MulM(LinSrgbToXyz, lin)));
                if (space == "lab") v = new[] { l, a, b };
                else { polar = true; v = ToPolar(l, a, b, 0.0015); }
                return true;
            }
            case "oklab":
            case "oklch":
            {
                var (l, a, b) = LinearToOklab(lin);
                if (space == "oklab") v = new[] { l, a, b };
                else { polar = true; v = ToPolar(l, a, b, 0.000004); }
                return true;
            }
            case "hsl":
            {
                var (h, s, l) = RgbToHsl(c.R, c.G, c.B);
                polar = true;
                v = new[] { s, l, h };
                return true;
            }
            case "hwb":
            {
                var (h, _, _) = RgbToHsl(c.R, c.G, c.B);
                double w = Math.Min(c.R, Math.Min(c.G, c.B)), bl = 1 - Math.Max(c.R, Math.Max(c.G, c.B));
                polar = true;
                v = new[] { w, bl, w + bl >= 1 ? double.NaN : h };
                return true;
            }
            default:
                return false;
        }
    }

    private static double[] ToPolar(double l, double a, double b, double achromatic)
    {
        double ch = Math.Sqrt(a * a + b * b);
        double h = ch <= achromatic ? double.NaN : (Math.Atan2(b, a) * 180 / Math.PI + 360) % 360;
        return new[] { l, ch, h };
    }

    private static bool TryFromSpaceVector(string space, double[] v, out (double R, double G, double B) rgb)
    {
        rgb = default;
        switch (space)
        {
            case "lab":
                rgb = FromXyzD50(LabToXyzD50(v[0], v[1], v[2]));
                return true;
            case "lch":
            {
                var (a, b) = double.IsNaN(v[2]) ? (0, 0) : Polar(v[1], v[2]);
                rgb = FromXyzD50(LabToXyzD50(v[0], a, b));
                return true;
            }
            case "oklab":
                rgb = Gamma(OklabToLinear(v[0], v[1], v[2]));
                return true;
            case "oklch":
            {
                var (a, b) = double.IsNaN(v[2]) ? (0, 0) : Polar(v[1], v[2]);
                rgb = Gamma(OklabToLinear(v[0], a, b));
                return true;
            }
            case "hsl":
                rgb = HslToRgb(double.IsNaN(v[2]) ? 0 : v[2], v[0], v[1]);
                return true;
            case "hwb":
                rgb = HwbToRgb(double.IsNaN(v[2]) ? 0 : v[2], v[0], v[1]);
                return true;
            default:
                return TryFromSpace(space, (v[0], v[1], v[2]), out rgb);
        }
    }

    /// <summary>Converts coordinates of a predefined RGB or XYZ space (as used by color()) to gamma-encoded sRGB.</summary>
    private static bool TryFromSpace(string space, (double, double, double) c, out (double R, double G, double B) rgb)
    {
        rgb = default;
        switch (space)
        {
            case "srgb":
                rgb = c;
                return true;
            case "srgb-linear":
                rgb = Gamma(c);
                return true;
            case "display-p3":
                rgb = FromXyzD65(MulM(LinP3ToXyz, (Linear(c.Item1), Linear(c.Item2), Linear(c.Item3))));
                return true;
            case "a98-rgb":
            {
                static double Lin(double v) => Math.Sign(v) * Math.Pow(Math.Abs(v), 563.0 / 256);
                rgb = FromXyzD65(MulM(LinA98ToXyz, (Lin(c.Item1), Lin(c.Item2), Lin(c.Item3))));
                return true;
            }
            case "prophoto-rgb":
            {
                static double Lin(double v) => Math.Abs(v) <= 16.0 / 512 ? v / 16 : Math.Sign(v) * Math.Pow(Math.Abs(v), 1.8);
                rgb = FromXyzD50(MulM(LinProPhotoToXyzD50, (Lin(c.Item1), Lin(c.Item2), Lin(c.Item3))));
                return true;
            }
            case "rec2020":
            {
                static double Lin(double v)
                {
                    const double alpha = 1.09929682680944, beta = 0.018053968510807;
                    double abs = Math.Abs(v);
                    return abs < beta * 4.5 ? v / 4.5 : Math.Sign(v) * Math.Pow((abs + alpha - 1) / alpha, 1 / 0.45);
                }
                rgb = FromXyzD65(MulM(LinRec2020ToXyz, (Lin(c.Item1), Lin(c.Item2), Lin(c.Item3))));
                return true;
            }
            case "xyz":
            case "xyz-d65":
                rgb = FromXyzD65(c);
                return true;
            case "xyz-d50":
                rgb = FromXyzD50(c);
                return true;
            default:
                return false;
        }
    }

    // ---------------------------------------------------------------- conversions (CSS Color 4, section 18)

    private static double Linear(double v)
    {
        double abs = Math.Abs(v);
        return abs <= 0.04045 ? v / 12.92 : Math.Sign(v) * Math.Pow((abs + 0.055) / 1.055, 2.4);
    }

    private static double Encode(double v)
    {
        double abs = Math.Abs(v);
        return abs > 0.0031308 ? Math.Sign(v) * (1.055 * Math.Pow(abs, 1 / 2.4) - 0.055) : 12.92 * v;
    }

    private static (double, double, double) Gamma((double, double, double) lin) => (Encode(lin.Item1), Encode(lin.Item2), Encode(lin.Item3));

    private static (double, double, double) FromXyzD65((double, double, double) xyz) => Gamma(MulM(XyzToLinSrgb, xyz));

    private static (double, double, double) FromXyzD50((double, double, double) xyz) => FromXyzD65(MulM(D50ToD65, xyz));

    private static (double, double, double) MulM(double[] m, (double X, double Y, double Z) v)
        => (m[0] * v.X + m[1] * v.Y + m[2] * v.Z, m[3] * v.X + m[4] * v.Y + m[5] * v.Z, m[6] * v.X + m[7] * v.Y + m[8] * v.Z);

    private const double Kappa = 24389.0 / 27, Epsilon = 216.0 / 24389;
    private static readonly double[] D50White = { 0.3457 / 0.3585, 1.0, (1.0 - 0.3457 - 0.3585) / 0.3585 };

    private static (double, double, double) LabToXyzD50(double l, double a, double b)
    {
        double f1 = (l + 16) / 116, f0 = a / 500 + f1, f2 = f1 - b / 200;
        double x = Math.Pow(f0, 3) > Epsilon ? Math.Pow(f0, 3) : (116 * f0 - 16) / Kappa;
        double y = l > Kappa * Epsilon ? Math.Pow((l + 16) / 116, 3) : l / Kappa;
        double z = Math.Pow(f2, 3) > Epsilon ? Math.Pow(f2, 3) : (116 * f2 - 16) / Kappa;
        return (x * D50White[0], y * D50White[1], z * D50White[2]);
    }

    private static (double L, double A, double B) XyzD50ToLab((double X, double Y, double Z) xyz)
    {
        static double F(double v) => v > Epsilon ? Math.Cbrt(v) : (Kappa * v + 16) / 116;
        double fx = F(xyz.X / D50White[0]), fy = F(xyz.Y / D50White[1]), fz = F(xyz.Z / D50White[2]);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static (double, double, double) OklabToLinear(double l, double a, double b)
    {
        double l_ = l + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = l - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = l - 0.0894841775 * a - 1.2914855480 * b;
        double L = l_ * l_ * l_, M = m_ * m_ * m_, S = s_ * s_ * s_;
        return (4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S,
                -1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S,
                -0.0041960863 * L - 0.7034186147 * M + 1.7076147010 * S);
    }

    private static (double L, double A, double B) LinearToOklab((double R, double G, double B) c)
    {
        double l = Math.Cbrt(0.4122214708 * c.R + 0.5363325363 * c.G + 0.0514459929 * c.B);
        double m = Math.Cbrt(0.2119034982 * c.R + 0.6806995451 * c.G + 0.1073969566 * c.B);
        double s = Math.Cbrt(0.0883024619 * c.R + 0.2817188376 * c.G + 0.6299787005 * c.B);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static readonly double[] LinSrgbToXyz =
    {
        0.41239079926595934, 0.357584339383878, 0.1804807884018343,
        0.21263900587151027, 0.715168678767756, 0.07219231536073371,
        0.01933081871559182, 0.11919477979462598, 0.9505321522496607,
    };

    private static readonly double[] XyzToLinSrgb =
    {
        3.2409699419045226, -1.537383177570094, -0.4986107602930034,
        -0.9692436362808796, 1.8759675015077202, 0.04155505740717559,
        0.05563007969699366, -0.20397695888897652, 1.0569715142428786,
    };

    private static readonly double[] LinP3ToXyz =
    {
        0.4865709486482162, 0.26566769316909306, 0.1982172852343625,
        0.2289745640697488, 0.6917385218365064, 0.079286914093745,
        0.0, 0.04511338185890264, 1.043944368900976,
    };

    private static readonly double[] XyzToLinP3 = Invert(LinP3ToXyz);

    private static readonly double[] LinA98ToXyz =
    {
        0.5766690429101305, 0.1855582379065463, 0.1882286462349947,
        0.29734497525053605, 0.6273635662554661, 0.07529145849399788,
        0.02703136138641234, 0.07068885253582723, 0.9913375368376388,
    };

    private static readonly double[] XyzToLinA98 = Invert(LinA98ToXyz);

    private static readonly double[] LinRec2020ToXyz =
    {
        0.6369580483012914, 0.14461690358620832, 0.1688809751641721,
        0.2627002120112671, 0.6779980715188708, 0.05930171646986196,
        0.0, 0.028072693049087428, 1.060985057710791,
    };

    private static readonly double[] LinProPhotoToXyzD50 =
    {
        0.7977604896723027, 0.13518583717574031, 0.0313493495815248,
        0.2880711282292934, 0.7118432178101014, 0.00008565396060525902,
        0.0, 0.0, 0.8251046025104601,
    };

    private static readonly double[] XyzToLinRec2020 = Invert(LinRec2020ToXyz);

    private static readonly double[] XyzD50ToLinProPhoto = Invert(LinProPhotoToXyzD50);

    private static readonly double[] D50ToD65 =
    {
        0.955473421488075, -0.02309845494876471, 0.06325924320057072,
        -0.0283697093338637, 1.0099953980813041, 0.021041441191917323,
        0.012314014864481998, -0.020507649298898964, 1.330365926242124,
    };

    private static readonly double[] D65ToD50 = Invert(D50ToD65);

    private static double[] Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        double det = a * A + b * B + c * C;
        return new[]
        {
            A / det, -(b * i - c * h) / det, (b * f - c * e) / det,
            B / det, (a * i - c * g) / det, -(a * f - c * d) / det,
            C / det, -(a * h - b * g) / det, (a * e - b * d) / det,
        };
    }
}
