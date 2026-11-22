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

/// <summary>
/// mask-border (CSS Masking 1 §7): a border image used as a mask. Null slices and widths are the initial values of
/// mask-border, 0 and auto on every side, unlike border-image's 100% and 1. Fill is read and, as in Chrome, not used:
/// the middle of a mask border is always drawn.
/// </summary>
internal sealed record MaskBorderStyle(
    BackgroundImage? Source, BorderImageValue[]? Slice, bool Fill, BorderImageValue[]? Widths, BorderImageValue[]? Outsets,
    BorderImageRepeat RepeatX, BorderImageRepeat RepeatY, bool Luminance)
{
    public static readonly MaskBorderStyle Initial = new(null, null, false, null, null, BorderImageRepeat.Stretch, BorderImageRepeat.Stretch, false);

    private static readonly BorderImageValue[] ZeroSlice = [BorderImageValue.Of(0f), BorderImageValue.Of(0f), BorderImageValue.Of(0f), BorderImageValue.Of(0f)];
    private static readonly BorderImageValue[] AutoWidths = [BorderImageValue.Of(Length.Auto), BorderImageValue.Of(Length.Auto), BorderImageValue.Of(Length.Auto), BorderImageValue.Of(Length.Auto)];

    /// <summary>The slices as border-image values, where null would mean 100%.</summary>
    public BorderImageValue[] SliceOrInitial => Slice ?? ZeroSlice;

    /// <summary>The widths as border-image values, where null would mean the border widths.</summary>
    public BorderImageValue[] WidthsOrInitial => Widths ?? AutoWidths;
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
    public static void ExpandShorthand(string value, Action<string, string> add) => ExpandShorthand(value, add, mask: false);

    /// <summary>
    /// Expands border-image, or with <paramref name="mask"/> mask-border (and -webkit-mask-box-image), which has the
    /// same syntax and other initial values: a slice of 0 and widths of auto.
    /// </summary>
    public static void ExpandShorthand(string value, Action<string, string> add, bool mask)
    {
        string prefix = mask ? "mask-border" : "border-image";
        var lowerAll = value.Trim().ToLowerInvariant();
        if (lowerAll is "initial" or "unset" or "inherit")
        {
            foreach (var name in Longhands) add(prefix + name.AsSpan("border-image".Length).ToString(), lowerAll);
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
                while (i < tokens.Count && (string.Equals(tokens[i], "fill", StringComparison.OrdinalIgnoreCase) || IsNumberOrPercent(tokens[i].ToLowerInvariant())))
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
        add(prefix + "-source", source ?? "none");
        add(prefix + "-slice", slice ?? (mask ? "0" : "100%"));
        add(prefix + "-width", width ?? (mask ? "auto" : "1"));
        add(prefix + "-outset", outset ?? "0");
        add(prefix + "-repeat", repeat ?? "stretch");
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
            if (tok.EndsWith('%'))
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
