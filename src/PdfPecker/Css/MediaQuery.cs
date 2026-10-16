using System.Globalization;

namespace PdfPecker.Css;

/// <summary>
/// The printed page as a media query sees it: Chrome's print media, sized like the page area (the page minus its
/// margins) in CSS pixels.
/// </summary>
internal sealed class MediaContext
{
    /// <summary>Width and height of the page area in points.</summary>
    public float Width = 595.28f - 72, Height = 841.89f - 72;

    /// <summary>Set once a query has tested the page size, so a change of size (from @page) is known to matter.</summary>
    public bool UsedSize;
}

/// <summary>
/// Evaluates media query lists (Media Queries 4): media types, <c>not</c>/<c>and</c>/<c>or</c>, the range syntax
/// (<c>(width &gt;= 600px)</c>, <c>(400px &lt; width &lt;= 700px)</c>) and the min-/max- prefixes, for print.
/// </summary>
internal static class MediaQuery
{
    private const int MaxDepth = 16;

    /// <summary>Whether a comma-separated media query list matches; an empty list matches.</summary>
    public static bool Matches(string list, MediaContext context)
    {
        var text = list.Trim().ToLowerInvariant();
        if (text.Length == 0) return true;
        foreach (var query in CssParser.SplitTopLevel(text, ','))
            if (QueryMatches(query.Trim(), context)) return true;
        return false;
    }

    private static bool QueryMatches(string q, MediaContext context)
    {
        if (q.Length == 0) return false;
        if (q[0] == '(' || q.StartsWith("not (", StringComparison.Ordinal) || q.StartsWith("not(", StringComparison.Ordinal))
            return Condition(q, context, 0) == true;
        bool not = false;
        var tokens = q.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        int k = 0;
        if (tokens[k] == "not") { not = true; k++; }
        else if (tokens[k] == "only") k++;
        if (k >= tokens.Length) return false;
        // The media type, then optionally "and <condition>".
        var rest = string.Join(" ", tokens, k, tokens.Length - k);
        int sp = rest.IndexOf(' ');
        string type = sp < 0 ? rest : rest.Substring(0, sp);
        bool result = type is "print" or "all";
        if (sp >= 0)
        {
            var tail = rest.Substring(sp + 1).TrimStart();
            if (!tail.StartsWith("and", StringComparison.Ordinal)) return false;
            var cond = Condition(tail.Substring(3), context, 0);
            if (cond == null) return false; // malformed: the whole query is "not all"
            result &= cond.Value;
        }
        return not ? !result : result;
    }

    /// <summary>A media condition: true, false, or null when it does not parse.</summary>
    private static bool? Condition(string s, MediaContext context, int depth)
    {
        s = s.Trim();
        if (s.Length == 0 || depth > MaxDepth) return null;
        if (s.StartsWith("not", StringComparison.Ordinal) && s.Length > 3 && (s[3] == ' ' || s[3] == '('))
            return !Condition(s.Substring(3), context, depth + 1);
        bool? result = null;
        string? op = null;
        int i = 0;
        while (true)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length || s[i] != '(') return null;
            int close = MatchingParen(s, i);
            if (close < 0) return null;
            bool value = InParens(s.Substring(i + 1, close - i - 1).Trim(), context, depth + 1);
            result = result == null ? value : op == "or" ? result.Value || value : result.Value && value;
            i = close + 1;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) return result;
            // "and" or "or" between parts; mixing them without parentheses is invalid.
            int start = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            var word = s.Substring(start, i - start);
            if (word is not ("and" or "or") || op != null && op != word) return null;
            op = word;
        }
    }

    /// <summary>The inside of a pair of parentheses: a nested condition, a feature, or a range.</summary>
    private static bool InParens(string inner, MediaContext context, int depth)
    {
        if (inner.Length == 0) return false;
        if (inner[0] == '(' || inner.StartsWith("not ", StringComparison.Ordinal) || inner.StartsWith("not(", StringComparison.Ordinal))
            return Condition(inner, context, depth) == true;
        int colon = inner.IndexOf(':');
        if (colon >= 0)
        {
            var name = inner.Substring(0, colon).Trim();
            var value = inner.Substring(colon + 1).Trim();
            if (name.StartsWith("min-", StringComparison.Ordinal)) return Compare(name.Substring(4), ">=", value, context);
            if (name.StartsWith("max-", StringComparison.Ordinal)) return Compare(name.Substring(4), "<=", value, context);
            return Compare(name, "=", value, context);
        }
        if (inner.IndexOfAny(new[] { '<', '>', '=' }) >= 0) return Range(inner, context);
        return Boolean(inner.Trim(), context);
    }

    /// <summary>A feature in a boolean context, such as (color) or (hover).</summary>
    private static bool Boolean(string name, MediaContext context)
    {
        switch (name)
        {
            case "width":
            case "height":
                context.UsedSize = true;
                return (name == "width" ? context.Width : context.Height) > 0;
            case "color":
            case "aspect-ratio":
            case "orientation":
            case "resolution":
            case "prefers-color-scheme":
            case "overflow-block":
            case "display-mode":
                return true;
            default:
                // monochrome, grid, color-index, hover, pointer, update, scan, inverted-colors, forced-colors,
                // prefers-reduced-motion and the like are all "none" or 0 on paper.
                return false;
        }
    }

    /// <summary>The range syntax: "width &gt;= 600px", "600px &lt;= width", "400px &lt; width &lt;= 700px".</summary>
    private static bool Range(string s, MediaContext context)
    {
        var parts = new List<string>();
        var ops = new List<string>();
        int i = 0, start = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c is '<' or '>' or '=')
            {
                parts.Add(s.Substring(start, i - start).Trim());
                string op = c.ToString();
                if (c != '=' && i + 1 < s.Length && s[i + 1] == '=') { op += "="; i++; }
                ops.Add(op);
                start = i + 1;
            }
            i++;
        }
        parts.Add(s.Substring(start).Trim());
        if (parts.Count == 2)
        {
            if (IsFeatureName(parts[0])) return Compare(parts[0], ops[0], parts[1], context);
            if (IsFeatureName(parts[1])) return Compare(parts[1], Flip(ops[0]), parts[0], context);
            return false;
        }
        if (parts.Count == 3 && IsFeatureName(parts[1]) && ops[0][0] == ops[1][0] && ops[0][0] != '=')
            return Compare(parts[1], Flip(ops[0]), parts[0], context) && Compare(parts[1], ops[1], parts[2], context);
        return false;

        static string Flip(string op) => op switch { "<" => ">", "<=" => ">=", ">" => "<", ">=" => "<=", _ => op };
        static bool IsFeatureName(string t) => t.Length > 0 && (char.IsLetter(t[0]) || t[0] == '-') && !char.IsDigit(t[^1]) && t.All(ch => char.IsLetter(ch) || ch == '-');
    }

    /// <summary>Compares a feature's value on the page with <paramref name="value"/>: feature op value.</summary>
    private static bool Compare(string feature, string op, string value, MediaContext context)
    {
        double actual, wanted;
        switch (feature)
        {
            case "width":
            case "height":
            case "device-width":
            case "device-height":
                context.UsedSize = true;
                actual = (feature.EndsWith("width", StringComparison.Ordinal) ? context.Width : context.Height) / 0.75;
                if (!TryLengthPx(value, out wanted)) return false;
                break;
            case "aspect-ratio":
            case "device-aspect-ratio":
                context.UsedSize = true;
                actual = context.Width / Math.Max(0.001, context.Height);
                if (!TryRatio(value, out wanted)) return false;
                break;
            case "resolution":
                actual = 1; // dppx: CSS pixels on paper are 1/96 inch
                if (!TryResolution(value, out wanted)) return false;
                break;
            case "color":
                actual = 8;
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out wanted)) return false;
                break;
            case "color-index":
            case "monochrome":
            case "grid":
                actual = 0;
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out wanted)) return false;
                break;
            default:
                if (op != "=") return false;
                return Discrete(feature, value, context);
        }
        const double eps = 1e-6;
        return op switch
        {
            "=" => Math.Abs(actual - wanted) < eps,
            "<" => actual < wanted - eps,
            "<=" => actual <= wanted + eps,
            ">" => actual > wanted + eps,
            ">=" => actual >= wanted - eps,
            _ => false,
        };
    }

    private static bool Discrete(string feature, string value, MediaContext context)
    {
        switch (feature)
        {
            case "orientation":
                context.UsedSize = true;
                return value == (context.Height >= context.Width ? "portrait" : "landscape");
            case "prefers-color-scheme": return value == "light";
            case "prefers-reduced-motion":
            case "prefers-reduced-transparency":
            case "prefers-reduced-data":
            case "prefers-contrast":
                return value == "no-preference";
            case "forced-colors":
            case "inverted-colors":
            case "hover":
            case "any-hover":
            case "pointer":
            case "any-pointer":
            case "update":
            case "overflow-inline":
            case "scripting":
                return value == "none";
            case "overflow-block": return value == "paged";
            case "scan": return false;
            case "display-mode": return value == "browser";
            case "dynamic-range":
            case "video-dynamic-range":
                return value == "standard";
            case "color-gamut": return value == "srgb";
            default: return false;
        }
    }

    private static bool TryLengthPx(string v, out double px)
    {
        px = 0;
        v = v.Trim();
        int i = 0;
        while (i < v.Length && (char.IsDigit(v[i]) || v[i] is '.' or '-' or '+' || (v[i] is 'e' or 'E') && i + 1 < v.Length && char.IsDigit(v[i + 1]))) i++;
        if (!double.TryParse(v.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return false;
        double factor = v.Substring(i) switch
        {
            "px" => 1,
            "" => n == 0 ? 1 : double.NaN,
            "em" or "rem" => 16, // relative to the initial font size
            "ex" => 8,
            "ch" => 8,
            "pt" => 96.0 / 72,
            "pc" => 16,
            "in" => 96,
            "cm" => 96 / 2.54,
            "mm" => 96 / 25.4,
            "q" => 96 / 101.6,
            _ => double.NaN,
        };
        if (double.IsNaN(factor)) return false;
        px = n * factor;
        return true;
    }

    internal static bool TryRatio(string v, out double ratio)
    {
        ratio = 0;
        var parts = v.Split('/');
        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double a)) return false;
        double b = 1;
        if (parts.Length == 2 && !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return false;
        if (parts.Length > 2 || b == 0) return false;
        ratio = a / b;
        return true;
    }

    private static bool TryResolution(string v, out double dppx)
    {
        dppx = 0;
        v = v.Trim();
        (string Unit, double Factor)[] units = { ("dppx", 1), ("dpcm", 2.54 / 96), ("dpi", 1.0 / 96), ("x", 1) };
        foreach (var (unit, factor) in units)
        {
            if (!v.EndsWith(unit, StringComparison.Ordinal)) continue;
            if (!double.TryParse(v.AsSpan(0, v.Length - unit.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return false;
            dppx = n * factor;
            return true;
        }
        return false;
    }

    internal static int MatchingParen(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }
}
