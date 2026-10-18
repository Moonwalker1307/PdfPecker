namespace PdfPecker.Css;

internal enum BackgroundRepeat : byte { Repeat, NoRepeat, Space, Round }

internal enum BackgroundBox : byte { BorderBox, PaddingBox, ContentBox, Text }

/// <summary>One background-image layer: a gradient or an image URL.</summary>
internal sealed class BackgroundImage
{
    public CssGradient? Gradient;
    public string? Url;
}

/// <summary>One axis of a background-position: an offset from the start edge, or from the end edge.</summary>
internal readonly struct BackgroundOffset
{
    public readonly Length Offset; // a percentage refers to the free space (positioning area minus image)
    public readonly bool FromEnd;
    public BackgroundOffset(Length offset, bool fromEnd) { Offset = offset; FromEnd = fromEnd; }

    public static readonly BackgroundOffset Start = new(new Length(0, LengthUnit.Percent), false);
    public static readonly BackgroundOffset Center = new(new Length(50, LengthUnit.Percent), false);

    /// <summary>The image's offset inside a positioning area with <paramref name="free"/> points left over.</summary>
    public float Resolve(float free)
    {
        float o = Offset.Resolve(free);
        return FromEnd ? free - o : o;
    }
}

internal enum BackgroundSizeKind : byte { Explicit, Cover, Contain }

internal readonly struct BackgroundSize
{
    public readonly BackgroundSizeKind Kind;
    public readonly Length Width, Height; // either may be auto
    public BackgroundSize(BackgroundSizeKind kind, Length width, Length height) { Kind = kind; Width = width; Height = height; }
    public static readonly BackgroundSize Auto = new(BackgroundSizeKind.Explicit, Length.Auto, Length.Auto);
}

/// <summary>Parsing of the background longhands (lists with one entry per layer).</summary>
internal static class CssBackground
{
    /// <summary>The URL inside <c>url(...)</c>, unquoted; null when the value is not a url() function.</summary>
    public static string? Url(string token)
    {
        var t = token.Trim();
        if (!t.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || !t.EndsWith(")", StringComparison.Ordinal)) return null;
        var inner = t.Substring(4, t.Length - 5).Trim();
        if (inner.Length >= 2 && (inner[0] == '"' || inner[0] == '\'') && inner[^1] == inner[0]) inner = inner.Substring(1, inner.Length - 2);
        return inner.Length > 0 ? inner : null;
    }

    public static bool IsRepeatKeyword(string lower) => lower is "repeat" or "repeat-x" or "repeat-y" or "no-repeat" or "space" or "round";

    public static bool IsBoxKeyword(string lower) => lower is "border-box" or "padding-box" or "content-box" or "text";

    public static bool IsPositionKeyword(string lower) => lower is "left" or "right" or "top" or "bottom" or "center";

    public static BackgroundBox? Box(string lower) => lower switch
    {
        "border-box" => BackgroundBox.BorderBox,
        "padding-box" => BackgroundBox.PaddingBox,
        "content-box" => BackgroundBox.ContentBox,
        "text" => BackgroundBox.Text,
        _ => null,
    };

    /// <summary>One layer of background-repeat: one or two keywords.</summary>
    public static (BackgroundRepeat X, BackgroundRepeat Y)? Repeat(string layer)
    {
        var t = CssParser.Tokens(layer.Trim().ToLowerInvariant());
        static BackgroundRepeat? One(string k) => k switch
        {
            "repeat" => BackgroundRepeat.Repeat,
            "no-repeat" => BackgroundRepeat.NoRepeat,
            "space" => BackgroundRepeat.Space,
            "round" => BackgroundRepeat.Round,
            _ => null,
        };
        if (t.Count == 1)
        {
            if (t[0] == "repeat-x") return (BackgroundRepeat.Repeat, BackgroundRepeat.NoRepeat);
            if (t[0] == "repeat-y") return (BackgroundRepeat.NoRepeat, BackgroundRepeat.Repeat);
            return One(t[0]) is { } r ? (r, r) : null;
        }
        if (t.Count == 2 && One(t[0]) is { } x && One(t[1]) is { } y) return (x, y);
        return null;
    }

    /// <summary>One layer of background-size: cover, contain, or one or two lengths (auto allowed).</summary>
    public static BackgroundSize? Size(string layer, float em, float rem, float vw, float vh)
    {
        var t = CssParser.Tokens(layer.Trim().ToLowerInvariant());
        if (t.Count == 1 && t[0] == "cover") return new BackgroundSize(BackgroundSizeKind.Cover, Length.Auto, Length.Auto);
        if (t.Count == 1 && t[0] == "contain") return new BackgroundSize(BackgroundSizeKind.Contain, Length.Auto, Length.Auto);
        if (t.Count is < 1 or > 2) return null;
        if (!StyleResolver.TryParseLength(t[0], em, rem, out var w, vw, vh) || w.Value < 0) return null;
        var h = Length.Auto;
        if (t.Count == 2 && (!StyleResolver.TryParseLength(t[1], em, rem, out h, vw, vh) || h.Value < 0)) return null;
        return new BackgroundSize(BackgroundSizeKind.Explicit, w, h);
    }

    /// <summary>One layer of background-position: one to four keywords and offsets.</summary>
    public static (BackgroundOffset X, BackgroundOffset Y)? Position(string layer, float em, float rem, float vw, float vh)
    {
        var t = CssParser.Tokens(layer.Trim().ToLowerInvariant());
        if (t.Count is < 1 or > 4) return null;
        var pct50 = new Length(50, LengthUnit.Percent);
        bool Len(string s, out Length l) => StyleResolver.TryParseLength(s, em, rem, out l, vw, vh) && !l.IsAuto;
        BackgroundOffset? Keyword(string k) => k switch
        {
            "left" or "top" => BackgroundOffset.Start,
            "right" or "bottom" => new BackgroundOffset(new Length(0, LengthUnit.Percent), true),
            "center" => new BackgroundOffset(pct50, false),
            _ => null,
        };

        if (t.Count <= 2)
        {
            string a = t[0], b = t.Count == 2 ? t[1] : "center";
            // A vertical keyword first, or a horizontal one second, means the pair is written y x.
            if (a is "top" or "bottom" || b is "left" or "right") (a, b) = (b, a);
            if (a is "top" or "bottom" || b is "left" or "right") return null;
            BackgroundOffset? x = Keyword(a), y = Keyword(b);
            if (x == null) { if (!Len(a, out var lx)) return null; x = new BackgroundOffset(lx, false); }
            if (y == null) { if (!Len(b, out var ly)) return null; y = new BackgroundOffset(ly, false); }
            return (x.Value, y.Value);
        }

        // Three or four values: each keyword may be followed by an offset from that edge.
        BackgroundOffset? hx = null, vy = null;
        bool centerPending = false;
        for (int i = 0; i < t.Count; i++)
        {
            var k = t[i];
            if (!IsPositionKeyword(k)) return null;
            Length off = new(0, LengthUnit.Percent);
            bool hasOffset = i + 1 < t.Count && Len(t[i + 1], out off);
            if (hasOffset) i++;
            if (k == "center")
            {
                if (hasOffset || centerPending) return null;
                centerPending = true;
                continue;
            }
            bool horizontal = k is "left" or "right";
            var value = new BackgroundOffset(hasOffset ? off : new Length(0, LengthUnit.Percent), k is "right" or "bottom");
            if (horizontal) { if (hx != null) return null; hx = value; }
            else { if (vy != null) return null; vy = value; }
        }
        if (centerPending)
        {
            if (hx == null) hx = new BackgroundOffset(pct50, false);
            else if (vy == null) vy = new BackgroundOffset(pct50, false);
            else return null;
        }
        if (hx == null || vy == null) return null;
        return (hx.Value, vy.Value);
    }

    /// <summary>One axis of background-position-x / -y: a keyword, an offset, or a keyword and an offset.</summary>
    public static BackgroundOffset? Axis(string layer, bool horizontal, float em, float rem, float vw, float vh)
    {
        var t = CssParser.Tokens(layer.Trim().ToLowerInvariant());
        if (t.Count is < 1 or > 2) return null;
        string start = horizontal ? "left" : "top", end = horizontal ? "right" : "bottom";
        Length off;
        if (t.Count == 1)
        {
            if (t[0] == "center") return new BackgroundOffset(new Length(50, LengthUnit.Percent), false);
            if (t[0] == start) return BackgroundOffset.Start;
            if (t[0] == end) return new BackgroundOffset(new Length(0, LengthUnit.Percent), true);
            return StyleResolver.TryParseLength(t[0], em, rem, out off, vw, vh) && !off.IsAuto ? new BackgroundOffset(off, false) : null;
        }
        if ((t[0] == start || t[0] == end) && StyleResolver.TryParseLength(t[1], em, rem, out off, vw, vh) && !off.IsAuto)
            return new BackgroundOffset(off, t[0] == end);
        return null;
    }

    /// <summary>Splits the layers of the background shorthand into its longhands.</summary>
    public static void ExpandShorthand(string value, Action<string, string> add)
    {
        string color = "transparent";
        var images = new List<string>();
        var positions = new List<string>();
        var sizes = new List<string>();
        var repeats = new List<string>();
        var origins = new List<string>();
        var clips = new List<string>();
        foreach (var layer in CssParser.SplitTopLevel(value, ','))
        {
            string image = "none";
            var repeat = new List<string>();
            var pos = new List<string>();
            var size = new List<string>();
            var boxes = new List<string>();
            bool afterSlash = false;
            foreach (var tok in CssParser.Tokens(SpaceSlashes(layer)))
            {
                var lower = tok.ToLowerInvariant();
                if (tok == "/") { afterSlash = true; continue; }
                if (afterSlash && (size.Count < 2) && (lower is "cover" or "contain" or "auto" || char.IsDigit(lower[0]) || lower[0] is '.' or '-' or '+'))
                {
                    size.Add(lower);
                    continue;
                }
                afterSlash = false;
                if (lower.Contains("gradient(") || lower.StartsWith("url(", StringComparison.Ordinal)) image = tok;
                else if (lower == "none") image = "none";
                else if (IsRepeatKeyword(lower)) repeat.Add(lower);
                else if (IsBoxKeyword(lower)) boxes.Add(lower);
                else if (lower is "scroll" or "fixed" or "local") { } // background-attachment: paged media has no scrolling
                else if (IsPositionKeyword(lower) || char.IsDigit(lower[0]) || lower[0] is '.' or '-' or '+') pos.Add(lower);
                else if (CssColor.TryParse(tok, CssColor.Black, out _)) color = tok;
            }
            images.Add(image);
            positions.Add(pos.Count > 0 ? string.Join(" ", pos) : "0% 0%");
            sizes.Add(size.Count > 0 ? string.Join(" ", size) : "auto");
            repeats.Add(repeat.Count > 0 ? string.Join(" ", repeat) : "repeat");
            origins.Add(boxes.Count > 0 ? boxes[0] : "padding-box");
            clips.Add(boxes.Count > 1 ? boxes[1] : boxes.Count == 1 ? boxes[0] : "border-box");
        }
        add("background-color", color);
        add("background-image", images.Count > 0 ? string.Join(", ", images) : "none");
        add("background-position", string.Join(", ", positions));
        add("background-size", string.Join(", ", sizes));
        add("background-repeat", string.Join(", ", repeats));
        add("background-origin", string.Join(", ", origins));
        add("background-clip", string.Join(", ", clips));
    }

    /// <summary>Puts spaces around slashes outside functions and strings so "center/cover" splits into tokens.</summary>
    internal static string SpaceSlashes(string s)
    {
        if (s.IndexOf('/') < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length + 4);
        int depth = 0;
        char quote = '\0';
        foreach (char c in s)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; sb.Append(c); continue; }
            if (c == '"' || c == '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            if (c == '/' && depth == 0) sb.Append(" / ");
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
