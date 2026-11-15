using System.Globalization;
using System.Text;

namespace PdfPecker.Css;

/// <summary>
/// The anchor positioning properties of a box (CSS Anchor Positioning 1): the names it is an anchor under, the anchor
/// it is positioned against, its position-area, its fallbacks, and the declarations that use anchor() or
/// anchor-size(). Those depend on where the anchor is laid out, so they are kept as written and resolved by the layout.
/// </summary>
internal sealed class AnchorStyle
{
    /// <summary>anchor-name: the dashed idents this box is an anchor under; null for none.</summary>
    public string[]? Names;

    /// <summary>position-anchor: the default anchor's name; null for auto, which has none in a document without popovers.</summary>
    public string? Default;

    /// <summary>position-area; null for none.</summary>
    public PositionArea? Area;

    /// <summary>position-try-fallbacks: the options tried in turn when the box overflows; null for none.</summary>
    public TryOption[]? Fallbacks;

    /// <summary>justify-self and align-self are anchor-center: the box is centred on its anchor on that axis.</summary>
    public bool CenterX, CenterY;

    /// <summary>The declarations that use anchor() or anchor-size(), by their physical property name.</summary>
    public Dictionary<string, AnchorFunctionValue>? Functions;

    /// <summary>Whether the box is positioned against an anchor at all.</summary>
    public bool Positions => Area != null || Functions != null || CenterX || CenterY;

    public AnchorStyle Copy()
    {
        var c = (AnchorStyle)MemberwiseClone();
        if (Functions != null) c.Functions = new Dictionary<string, AnchorFunctionValue>(Functions, StringComparer.Ordinal);
        return c;
    }

    /// <summary>The properties whose values may use anchor() (the insets) or anchor-size() (these, sizes and margins).</summary>
    public static bool Takes(string property) => property is "top" or "right" or "bottom" or "left" or "width" or "height"
        or "min-width" or "min-height" or "max-width" or "max-height" or "margin-top" or "margin-right" or "margin-bottom" or "margin-left";

    /// <summary>Whether a value calls anchor() or anchor-size().</summary>
    public static bool UsesFunctions(string lower) => FindFunction(lower, 0, out _, out _) >= 0;

    /// <summary>
    /// The start of the next anchor() or anchor-size() call at or after <paramref name="from"/>, or -1; with where its
    /// arguments start and whether it is anchor-size().
    /// </summary>
    internal static int FindFunction(string s, int from, out int args, out bool size)
    {
        for (int i = s.IndexOf("anchor", from, StringComparison.Ordinal); i >= 0; i = s.IndexOf("anchor", i + 6, StringComparison.Ordinal))
        {
            // Part of a longer name (--my-anchor(), position-anchor) is not a call.
            if (i > 0 && (char.IsAsciiLetterOrDigit(s[i - 1]) || s[i - 1] is '-' or '_')) continue;
            int j = i + 6;
            size = s.AsSpan(j).StartsWith("-size(", StringComparison.Ordinal);
            if (size) j += 5;
            if (j < s.Length && s[j] == '(') { args = j + 1; return i; }
        }
        args = 0;
        size = false;
        return -1;
    }

    /// <summary>Parses anchor-name: none, or a list of dashed idents.</summary>
    public static bool TryParseNames(string lower, out string[]? names)
    {
        names = null;
        if (lower == "none") return true;
        var parts = CssParser.SplitTopLevel(lower, ',');
        var list = new string[parts.Count];
        for (int k = 0; k < parts.Count; k++)
        {
            var p = parts[k].Trim();
            if (!IsDashedIdent(p)) return false;
            list[k] = p;
        }
        names = list;
        return list.Length > 0;
    }

    /// <summary>Whether a token is a dashed ident: two hyphens and at least one more name character.</summary>
    public static bool IsDashedIdent(string s)
    {
        if (s.Length < 3 || !s.StartsWith("--", StringComparison.Ordinal)) return false;
        foreach (char c in s.AsSpan(2))
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || c > 0x7F)) return false;
        return true;
    }
}

/// <summary>A declaration that uses anchor() or anchor-size(), with what its other units resolve against.</summary>
internal readonly record struct AnchorFunctionValue(string Value, float Em, float Rem, float Vw, float Vh);

/// <summary>
/// A position-area: the columns and rows of the grid that the anchor's edges make in the containing block, from 0 (left
/// or top: before the anchor) through 1 (the anchor) to 2 (right or bottom: after it), in physical terms.
/// </summary>
internal readonly record struct PositionArea(byte Col0, byte Col1, byte Row0, byte Row1)
{
    /// <summary>The area mirrored left to right (flip-inline) or top to bottom (flip-block).</summary>
    public PositionArea Flip(bool inline, bool block) => new(
        inline ? (byte)(2 - Col1) : Col0, inline ? (byte)(2 - Col0) : Col1,
        block ? (byte)(2 - Row1) : Row0, block ? (byte)(2 - Row0) : Row1);

    private enum Axis : byte { Either, X, Y, InlineX }

    /// <summary>Parses position-area: one or two keywords, with directions taken from <paramref name="rtl"/>.</summary>
    public static bool TryParse(string lower, bool rtl, out PositionArea? area)
    {
        area = null;
        var tokens = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 1 && tokens[0] == "none") return true;
        if (tokens.Length is 0 or > 2) return false;
        if (!Keyword(tokens[0], out var a0, out int f0, out int t0, out int family0)) return false;
        Axis a1;
        int f1, t1;
        if (tokens.Length == 2)
        {
            // Physical, logical and start/end keywords do not mix; center and span-all go with any.
            if (!Keyword(tokens[1], out a1, out f1, out t1, out int family1) || family0 != 0 && family1 != 0 && family0 != family1) return false;
        }
        // One keyword: the same keyword on both axes when it says no axis, otherwise span-all on the other.
        else if (a0 == Axis.Either) (a1, f1, t1) = (Axis.Either, f0, t0);
        else (a1, f1, t1) = (Axis.Either, 0, 2);
        // An unaxed keyword takes the other keyword's axis; two of them are block then inline.
        if (a0 == Axis.Either && a1 == Axis.Either) (a0, a1) = (Axis.Y, Axis.InlineX);
        else if (a0 == Axis.Either) a0 = a1 == Axis.Y ? Axis.InlineX : Axis.Y;
        else if (a1 == Axis.Either) a1 = a0 == Axis.Y ? Axis.InlineX : Axis.Y;
        if ((a0 == Axis.Y) == (a1 == Axis.Y)) return false;
        if (a0 == Axis.Y) (a0, f0, t0, a1, f1, t1) = (a1, f1, t1, a0, f0, t0);
        // Now the first is horizontal. Logical keywords run from the right in a right-to-left box.
        if (a0 == Axis.InlineX && rtl) (f0, t0) = (2 - t0, 2 - f0);
        area = new PositionArea((byte)f0, (byte)t0, (byte)f1, (byte)t1);
        return true;
    }

    /// <summary>A position-area keyword: its axis, and the first and last track it covers counted from the start.</summary>
    private static bool Keyword(string k, out Axis axis, out int from, out int to, out int family)
    {
        axis = Axis.Either;
        (from, to) = (0, 0);
        family = 0;
        // The self- forms refer to the box's own writing mode, which every box here shares with its containing block.
        var t = k.Replace("self-", "", StringComparison.Ordinal).Replace("-self", "", StringComparison.Ordinal);
        bool span = t.StartsWith("span-", StringComparison.Ordinal);
        if (span) t = t.Substring(5);
        if (t == "all") { if (!span) return false; (from, to) = (0, 2); return true; }
        if (t == "center") { if (span) return false; (from, to) = (1, 1); return true; }
        int side;
        family = t.StartsWith("inline", StringComparison.Ordinal) || t.StartsWith("block", StringComparison.Ordinal) ? 2 : t is "start" or "end" ? 3 : 1;
        switch (t)
        {
            case "left": axis = Axis.X; side = 0; break;
            case "right": axis = Axis.X; side = 2; break;
            case "top": axis = Axis.Y; side = 0; break;
            case "bottom": axis = Axis.Y; side = 2; break;
            case "x-start" or "inline-start": axis = Axis.InlineX; side = 0; break;
            case "x-end" or "inline-end": axis = Axis.InlineX; side = 2; break;
            case "y-start" or "block-start": axis = Axis.Y; side = 0; break;
            case "y-end" or "block-end": axis = Axis.Y; side = 2; break;
            case "start": side = 0; break;
            case "end": side = 2; break;
            default: return false;
        }
        (from, to) = span ? side == 0 ? (0, 1) : (1, 2) : (side, side);
        return true;
    }
}

/// <summary>One position-try-fallbacks option: flips of the box's own positioning, or another position-area.</summary>
internal readonly record struct TryOption(bool FlipBlock, bool FlipInline, PositionArea? Area)
{
    /// <summary>
    /// Parses position-try-fallbacks: none, or options of flip-block, flip-inline and position-area values. Named
    /// @position-try rules are not read, so an option naming one is left out.
    /// </summary>
    public static bool TryParse(string lower, bool rtl, out TryOption[]? options)
    {
        options = null;
        if (lower == "none") return true;
        var list = new List<TryOption>();
        foreach (var part in CssParser.SplitTopLevel(lower, ','))
        {
            var p = part.Trim();
            if (p.StartsWith("position-area(", StringComparison.Ordinal) && p.EndsWith(')')) p = p.Substring(14, p.Length - 15).Trim();
            bool block = false, inline = false, named = false, ok = true;
            foreach (var t in p.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (t == "flip-block") block = true;
                else if (t == "flip-inline") inline = true;
                else if (AnchorStyle.IsDashedIdent(t)) named = true;
                else ok = false;
            }
            if (ok)
            {
                if (!named && (block || inline)) list.Add(new TryOption(block, inline, null));
                continue;
            }
            if (!PositionArea.TryParse(p, rtl, out var area) || area == null) return false;
            list.Add(new TryOption(false, false, area));
        }
        options = list.Count > 0 ? list.ToArray() : null;
        return true;
    }
}

/// <summary>The border box of an anchor and the containing block that its anchor() values are measured in.</summary>
internal readonly record struct AnchorRect(float X, float Y, float W, float H);

/// <summary>Replaces the anchor() and anchor-size() calls of a value with the lengths they stand for.</summary>
internal static class AnchorFunctions
{
    private const int MaxCalls = 64;

    /// <summary>
    /// The value of <paramref name="property"/> with each call replaced by points, or null when a call has no anchor
    /// and no fallback, which makes the declaration invalid. <paramref name="find"/> returns the anchor of a name (null
    /// for the default anchor), <paramref name="cb"/> is the containing block, and <paramref name="flipBlock"/> and
    /// <paramref name="flipInline"/> swap the sides that calls name, as a position-try-fallbacks flip does.
    /// </summary>
    public static string? Substitute(string value, string property, Func<string?, AnchorRect?> find, AnchorRect cb, bool rtl,
        bool flipBlock, bool flipInline, int depth = 0)
    {
        if (depth > 4) return null;
        var sb = new StringBuilder(value.Length);
        int at = 0, calls = 0;
        while (AnchorStyle.FindFunction(value, at, out int args, out bool size) is int start and >= 0)
        {
            if (++calls > MaxCalls) return null;
            int close = Close(value, args);
            if (close < 0) return null;
            sb.Append(value, at, start - at);
            var inner = value.Substring(args, close - args);
            var parts = CssParser.SplitTopLevel(inner, ',');
            if (parts.Count == 0) return null;
            string? fallback = parts.Count > 1 ? inner.Substring(inner.IndexOf(',', StringComparison.Ordinal) + 1).Trim() : null;
            float? v = size ? Size(parts[0], property, find) : Side(parts[0], property, find, cb, rtl, flipBlock, flipInline);
            if (v is float points) sb.Append(points.ToString("0.####", CultureInfo.InvariantCulture)).Append("pt");
            else if (fallback != null)
            {
                var resolved = Substitute(fallback, property, find, cb, rtl, flipBlock, flipInline, depth + 1);
                if (resolved == null) return null;
                // The fallback stands for one value, so a sum in it keeps its terms together.
                sb.Append("calc(").Append(resolved).Append(')');
            }
            else return null;
            at = close + 1;
        }
        sb.Append(value, at, value.Length - at);
        return sb.ToString();
    }

    /// <summary>The index of the parenthesis that closes the one before <paramref name="from"/>, or -1.</summary>
    private static int Close(string s, int from)
    {
        int depth = 1;
        for (int i = from; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static bool Horizontal(string property) => property is "left" or "right" or "width" or "min-width" or "max-width" or "margin-left" or "margin-right";

    /// <summary>anchor(): the place of an anchor's side as the property's inset, in points; null when it has none.</summary>
    private static float? Side(string args, string property, Func<string?, AnchorRect?> find, AnchorRect cb, bool rtl, bool flipBlock, bool flipInline)
    {
        if (property is not ("top" or "right" or "bottom" or "left")) return null;
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is 0 or > 2) return null;
        string? name = null;
        string side = tokens[^1];
        if (tokens.Length == 2)
        {
            if (!AnchorStyle.IsDashedIdent(tokens[0])) return null;
            name = tokens[0];
        }
        if (find(name) is not { } a) return null;
        bool horizontal = Horizontal(property);
        bool startEdge = property is "left" or "top";
        // A flip swaps the sides a call names on the flipped axis, as it swaps the insets themselves.
        bool flip = horizontal ? flipInline : flipBlock;
        float fraction;
        switch (side)
        {
            case "inside": fraction = startEdge ? 0 : 1; flip = false; break;
            case "outside": fraction = startEdge ? 1 : 0; flip = false; break;
            case "center": fraction = 0.5f; break;
            case "left" or "right" when horizontal: fraction = side == "left" ? 0 : 1; break;
            case "top" or "bottom" when !horizontal: fraction = side == "top" ? 0 : 1; break;
            case "start" or "self-start" or "end" or "self-end":
                fraction = side.EndsWith("start", StringComparison.Ordinal) ? 0 : 1;
                if (horizontal && rtl) fraction = 1 - fraction;
                break;
            default:
                if (!side.EndsWith('%') || !float.TryParse(side.AsSpan(0, side.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct)
                    || !float.IsFinite(pct)) return null;
                fraction = pct / 100f;
                if (horizontal && rtl) fraction = 1 - fraction;
                break;
        }
        if (flip) fraction = 1 - fraction;
        float at = horizontal ? a.X + a.W * fraction : a.Y + a.H * fraction;
        return property switch
        {
            "left" => at - cb.X,
            "right" => cb.X + cb.W - at,
            "top" => at - cb.Y,
            _ => cb.Y + cb.H - at,
        };
    }

    /// <summary>anchor-size(): an anchor's width or height, in points; null when it has none.</summary>
    private static float? Size(string args, string property, Func<string?, AnchorRect?> find)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 2) return null;
        string? name = null, size = null;
        foreach (var t in tokens)
        {
            if (AnchorStyle.IsDashedIdent(t) && name == null && size == null) name = t;
            else if (t is "width" or "height" or "block" or "inline" or "self-block" or "self-inline" && size == null) size = t;
            else return null;
        }
        if (find(name) is not { } a) return null;
        // Without a size, the anchor's size on the property's own axis.
        bool width = size switch
        {
            "width" or "inline" or "self-inline" => true,
            "height" or "block" or "self-block" => false,
            _ => Horizontal(property),
        };
        return width ? a.W : a.H;
    }
}
