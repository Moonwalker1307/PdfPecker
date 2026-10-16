using System.Globalization;

namespace PdfPecker.Css;

internal enum BorderImageRepeat : byte { Stretch, Repeat, Round, Space }

/// <summary>
/// One side of border-image-slice, border-image-width or border-image-outset: a plain number, or a length or
/// percentage (auto for a border-image-width of auto). A number means image pixels in a slice and a multiple of the
/// border width in a width or an outset.
/// </summary>
internal readonly struct BorderImageValue
{
    public readonly float Number;
    public readonly Length Length;
    public readonly bool IsNumber;

    private BorderImageValue(float number, Length length, bool isNumber) { Number = number; Length = length; IsNumber = isNumber; }

    public static BorderImageValue Of(float number) => new(number, Length.Zero, true);
    public static BorderImageValue Of(Length length) => new(0, length, false);

    public bool IsAuto => !IsNumber && Length.IsAuto;
}

/// <summary>Parsing of the border-image longhands and of the border-image shorthand (CSS Backgrounds 3 §6).</summary>
internal static class CssBorderImage
{
    public static readonly string[] Longhands =
    {
        "border-image-source", "border-image-slice", "border-image-width", "border-image-outset", "border-image-repeat",
    };

    public static bool IsRepeatKeyword(string lower) => lower is "stretch" or "repeat" or "round" or "space";

    /// <summary>
    /// Expands <c>border-image: source || slice [/ width | / width? / outset]? || repeat</c> into its longhands, the parts
    /// left out set to their initial values. An invalid value adds nothing, so the declaration is dropped.
    /// </summary>
    public static void ExpandShorthand(string value, Action<string, string> add)
    {
        var lowerAll = value.Trim().ToLowerInvariant();
        if (lowerAll is "initial" or "unset" or "inherit")
        {
            foreach (var name in Longhands) add(name, lowerAll);
            return;
        }
        string? source = null, slice = null, width = null, outset = null, repeat = null;
        var tokens = CssParser.Tokens(CssBackground.SpaceSlashes(value));
        int i = 0;
        while (i < tokens.Count)
        {
            var tok = tokens[i];
            var lower = tok.ToLowerInvariant();
            if (lower == "none" || lower.StartsWith("url(", StringComparison.Ordinal) || lower.Contains("gradient("))
            {
                if (source != null) return;
                source = tok;
                i++;
            }
            else if (IsRepeatKeyword(lower))
            {
                if (repeat != null) return;
                repeat = lower;
                i++;
                if (i < tokens.Count && IsRepeatKeyword(tokens[i].ToLowerInvariant())) repeat += " " + tokens[i++].ToLowerInvariant();
            }
            else if (lower == "fill" || IsNumberOrPercent(lower))
            {
                if (slice != null) return;
                var parts = new List<string>();
                while (i < tokens.Count && (tokens[i].ToLowerInvariant() == "fill" || IsNumberOrPercent(tokens[i].ToLowerInvariant())))
                    parts.Add(tokens[i++].ToLowerInvariant());
                slice = string.Join(" ", parts);
                if (i < tokens.Count && tokens[i] == "/")
                {
                    i++;
                    var w = new List<string>();
                    while (i < tokens.Count && tokens[i] != "/" && IsWidthToken(tokens[i].ToLowerInvariant())) w.Add(tokens[i++].ToLowerInvariant());
                    if (w.Count > 0) width = string.Join(" ", w);
                    if (i < tokens.Count && tokens[i] == "/")
                    {
                        i++;
                        var o = new List<string>();
                        while (i < tokens.Count && tokens[i] != "/" && IsWidthToken(tokens[i].ToLowerInvariant())) o.Add(tokens[i++].ToLowerInvariant());
                        if (o.Count == 0) return;
                        outset = string.Join(" ", o);
                    }
                    else if (w.Count == 0) return;
                }
            }
            else return;
        }
        add("border-image-source", source ?? "none");
        add("border-image-slice", slice ?? "100%");
        add("border-image-width", width ?? "1");
        add("border-image-outset", outset ?? "0");
        add("border-image-repeat", repeat ?? "stretch");
    }

    private static bool IsNumberOrPercent(string lower)
        => lower.Length > 0 && (char.IsDigit(lower[0]) || lower[0] is '.' or '+' or '-')
           && (float.TryParse(lower.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out _));

    private static bool IsWidthToken(string lower)
        => lower == "auto" || lower.Length > 0 && (char.IsDigit(lower[0]) || lower[0] is '.' or '+' or '-') || CssMath.IsMathFunction(lower);

    /// <summary>The four sides of a one-to-four value list (top, right, bottom, left), as margin expands them.</summary>
    private static T[] FourSides<T>(List<T> v)
        => v.Count switch
        {
            1 => new[] { v[0], v[0], v[0], v[0] },
            2 => new[] { v[0], v[1], v[0], v[1] },
            3 => new[] { v[0], v[1], v[2], v[1] },
            _ => new[] { v[0], v[1], v[2], v[3] },
        };

    /// <summary>Parses border-image-slice: one to four non-negative numbers or percentages, and optionally fill.</summary>
    public static (BorderImageValue[] Slice, bool Fill)? Slice(string lower)
    {
        bool fill = false, closed = false;
        var values = new List<BorderImageValue>(4);
        foreach (var tok in CssParser.Tokens(lower))
        {
            if (tok == "fill")
            {
                // fill comes before or after the numbers, never between them.
                if (fill) return null;
                fill = true;
                closed = values.Count > 0;
                continue;
            }
            if (closed || values.Count == 4) return null;
            if (tok.EndsWith("%", StringComparison.Ordinal))
            {
                if (!float.TryParse(tok.AsSpan(0, tok.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float p) || p < 0 || !float.IsFinite(p)) return null;
                values.Add(BorderImageValue.Of(new Length(p, LengthUnit.Percent)));
            }
            else if (Number(tok) is { } n && n >= 0) values.Add(BorderImageValue.Of(n));
            else return null;
        }
        if (values.Count == 0) return null;
        return (FourSides(values), fill);
    }

    /// <summary>
    /// Parses border-image-width (<paramref name="outset"/> false: numbers, lengths, percentages or auto) or
    /// border-image-outset (numbers or lengths). Negative values are invalid.
    /// </summary>
    public static BorderImageValue[]? Widths(string lower, bool outset, float em, float rem, float vw, float vh)
    {
        var tokens = CssParser.Tokens(lower);
        if (tokens.Count is 0 or > 4) return null;
        var values = new List<BorderImageValue>(4);
        foreach (var tok in tokens)
        {
            if (Number(tok) is { } n)
            {
                if (n < 0) return null;
                values.Add(BorderImageValue.Of(n));
                continue;
            }
            if (tok == "auto")
            {
                if (outset) return null;
                values.Add(BorderImageValue.Of(Length.Auto));
                continue;
            }
            if (!StyleResolver.TryParseLength(tok, em, rem, out var len, vw, vh) || len.IsAuto) return null;
            if (outset && len.IsPercent) return null;
            if (len.Unit == LengthUnit.Pt && len.Value < 0 || len.Unit == LengthUnit.Percent && len.Value < 0) return null;
            values.Add(BorderImageValue.Of(len));
        }
        return FourSides(values);
    }

    /// <summary>Parses border-image-repeat: one keyword for both axes, or the horizontal and the vertical one.</summary>
    public static (BorderImageRepeat X, BorderImageRepeat Y)? Repeat(string lower)
    {
        var tokens = CssParser.Tokens(lower);
        if (tokens.Count is 0 or > 2) return null;
        if (Keyword(tokens[0]) is not { } x) return null;
        if (tokens.Count == 1) return (x, x);
        if (Keyword(tokens[1]) is not { } y) return null;
        return (x, y);
    }

    private static BorderImageRepeat? Keyword(string t) => t switch
    {
        "stretch" => BorderImageRepeat.Stretch,
        "repeat" => BorderImageRepeat.Repeat,
        "round" => BorderImageRepeat.Round,
        "space" => BorderImageRepeat.Space,
        _ => null,
    };

    private static float? Number(string tok)
    {
        if (float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out float n) && float.IsFinite(n)) return n;
        if (CssMath.IsMathFunction(tok) && CssMath.TryParseNumber(tok, out n)) return n;
        return null;
    }
}
