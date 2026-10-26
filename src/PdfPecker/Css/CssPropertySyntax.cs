using System.Globalization;

namespace PdfPecker.Css;

/// <summary>
/// The syntax descriptor of an @property rule and whether its initial value matches it (css-properties-values-api
/// §3.1, §5). A rule whose syntax uses a type the spec does not list, or whose initial value does not parse as the
/// syntax or depends on the element (font-relative lengths), is invalid and ignored, as Chrome ignores it; a custom
/// property used with a var() fallback then falls back instead of taking a wrong initial value.
/// </summary>
internal static class CssPropertySyntax
{
    /// <summary>
    /// Whether <paramref name="initial"/> is a valid initial value for <paramref name="syntax"/>; false also when the
    /// syntax itself is not valid. The universal syntax "*" takes any value.
    /// </summary>
    public static bool Accepts(string syntax, string? initial)
    {
        if (syntax == "*") return true;
        var value = initial?.Trim() ?? "";
        if (value.Length == 0) return false;
        bool valid = false;
        foreach (var raw in syntax.Split('|'))
        {
            var part = raw.Trim();
            if (part.Length == 0) return false;
            char multiplier = part[^1] is '+' or '#' ? part[^1] : '\0';
            if (multiplier != '\0') part = part.Substring(0, part.Length - 1);
            if (part.StartsWith('<'))
            {
                if (!part.EndsWith('>') || !IsKnownType(part[1..^1])) return false;
            }
            else if (multiplier != '\0' || !IsIdent(part) || CssParser.IsCssWideKeyword(part)) return false;
            if (!valid) valid = Matches(part, multiplier, value);
        }
        return valid;
    }

    private static bool IsKnownType(string name) => name is "length" or "number" or "percentage" or "length-percentage" or "color"
        or "image" or "url" or "integer" or "angle" or "time" or "resolution" or "transform-function" or "transform-list"
        or "custom-ident" or "string";

    private static bool Matches(string component, char multiplier, string value)
    {
        if (multiplier == '\0') return MatchesOne(component, value);
        var items = multiplier == '#' ? CssParser.SplitTopLevel(value, ',') : SplitSpaces(value);
        if (items.Count == 0) return false;
        foreach (var item in items)
            if (!MatchesOne(component, item.Trim())) return false;
        return true;
    }

    private static bool MatchesOne(string component, string value)
    {
        if (value.Length == 0) return false;
        if (!component.StartsWith('<')) return string.Equals(component, value, StringComparison.Ordinal);
        string type = component[1..^1];
        // Maths functions are checked by the property that uses the value, not here.
        bool math = IsMathFunction(value);
        switch (type)
        {
            case "length": return math || IsZero(value) || IsDimension(value, IsAbsoluteLength);
            case "number": return math || IsNumber(value, out _);
            case "integer": return math || IsNumber(value, out var n) && n == Math.Floor(n);
            case "percentage": return math || IsPercentage(value);
            case "length-percentage": return math || IsZero(value) || IsPercentage(value) || IsDimension(value, IsAbsoluteLength);
            case "angle": return math || IsDimension(value, static u => u is "deg" or "rad" or "grad" or "turn");
            case "time": return math || IsDimension(value, static u => u is "s" or "ms");
            case "resolution": return math || IsDimension(value, static u => u is "dpi" or "dpcm" or "dppx" or "x");
            case "color": return value.Equals("currentcolor", StringComparison.OrdinalIgnoreCase) || CssColor.TryParse(value, 0, out _);
            case "url": return IsFunction(value, "url");
            case "image": return IsFunction(value, "url") || value.Contains("gradient(", StringComparison.OrdinalIgnoreCase)
                                 || IsFunction(value, "image-set") || IsFunction(value, "-webkit-image-set");
            case "string": return value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0];
            case "custom-ident": return IsIdent(value) && !CssParser.IsCssWideKeyword(value) && !value.Equals("default", StringComparison.OrdinalIgnoreCase);
            case "transform-function": return value.EndsWith(')') && value.IndexOf('(') > 0 && SplitSpaces(value).Count == 1;
            case "transform-list": return value.EndsWith(')') && value.IndexOf('(') > 0;
            default: return false;
        }
    }

    private static bool IsMathFunction(string value)
    {
        int open = value.IndexOf('(');
        return open > 0 && value.EndsWith(')')
               && value.AsSpan(0, open).ToString().ToLowerInvariant() is "calc" or "min" or "max" or "clamp" or "-webkit-calc";
    }

    private static bool IsFunction(string value, string name)
        => value.Length > name.Length + 1 && value.EndsWith(')') && value.StartsWith(name, StringComparison.OrdinalIgnoreCase)
           && value[name.Length] == '(';

    private static bool IsZero(string value) => IsNumber(value, out var n) && n == 0;

    private static bool IsPercentage(string value) => value.EndsWith('%') && IsNumber(value[..^1], out _);

    // Font-relative lengths depend on the element, so they are not computationally independent and may not be initial values.
    private static bool IsAbsoluteLength(string unit) => unit is "px" or "cm" or "mm" or "q" or "in" or "pt" or "pc"
        or "vw" or "vh" or "vmin" or "vmax" or "svw" or "svh" or "lvw" or "lvh" or "dvw" or "dvh" or "vi" or "vb";

    private static bool IsDimension(string value, Func<string, bool> unit)
    {
        int end = value.Length;
        while (end > 0 && char.IsAsciiLetter(value[end - 1])) end--;
        return end > 0 && end < value.Length && unit(value[end..].ToLowerInvariant()) && IsNumber(value[..end], out _);
    }

    private static bool IsNumber(string value, out double number)
    {
        number = 0;
        if (value.Length == 0 || value[^1] == '.' || !char.IsAsciiDigit(value[^1])) return false;
        foreach (char c in value)
            if (!char.IsAsciiDigit(c) && c is not ('.' or '-' or '+' or 'e' or 'E')) return false;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static bool IsIdent(string value)
    {
        if (value.Length == 0 || char.IsAsciiDigit(value[0]) || value[0] == '-' && value.Length > 1 && char.IsAsciiDigit(value[1])) return false;
        foreach (char c in value)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_') && c < 0x80) return false;
        return true;
    }

    private static List<string> SplitSpaces(string value)
    {
        var parts = new List<string>();
        int depth = 0, start = -1;
        for (int i = 0; i <= value.Length; i++)
        {
            char c = i < value.Length ? value[i] : ' ';
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (char.IsWhiteSpace(c) && depth <= 0)
            {
                if (start >= 0) parts.Add(value[start..i]);
                start = -1;
            }
            else if (start < 0) start = i;
        }
        return parts;
    }
}
