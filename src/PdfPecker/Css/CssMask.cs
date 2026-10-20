namespace PdfPecker.Css;

/// <summary>mask-mode: how a mask layer's image becomes mask values.</summary>
internal enum MaskMode : byte { MatchSource, Alpha, Luminance }

/// <summary>mask-composite: how a mask layer combines with the layers below it.</summary>
internal enum MaskComposite : byte { Add, Subtract, Intersect, Exclude }

/// <summary>Parsing of the mask shorthand and the mask longhands that differ from the background ones.</summary>
internal static class CssMask
{
    /// <summary>
    /// The -webkit- spellings of the mask properties, as the unprefixed properties they stand for. Chrome still takes
    /// -webkit-mask-image and its family, and most masks on the web are written that way.
    /// </summary>
    public static string? Unprefixed(string name) => name switch
    {
        "-webkit-mask-image" => "mask-image",
        "-webkit-mask-position" => "mask-position",
        "-webkit-mask-position-x" => "mask-position-x",
        "-webkit-mask-position-y" => "mask-position-y",
        "-webkit-mask-size" => "mask-size",
        "-webkit-mask-repeat" => "mask-repeat",
        "-webkit-mask-origin" => "mask-origin",
        "-webkit-mask-clip" => "mask-clip",
        "-webkit-mask-composite" => "mask-composite",
        "-webkit-mask-mode" => "mask-mode",
        "-webkit-backdrop-filter" => "backdrop-filter",
        _ => null,
    };

    public static MaskMode? Mode(string lower) => lower.Trim() switch
    {
        "match-source" => MaskMode.MatchSource,
        "alpha" => MaskMode.Alpha,
        "luminance" => MaskMode.Luminance,
        _ => null,
    };

    /// <summary>A mask-composite keyword, or one of the Porter-Duff names that -webkit-mask-composite takes.</summary>
    public static MaskComposite? Composite(string lower) => lower.Trim() switch
    {
        "add" or "source-over" => MaskComposite.Add,
        "subtract" or "source-out" => MaskComposite.Subtract,
        "intersect" or "source-in" => MaskComposite.Intersect,
        "exclude" or "xor" => MaskComposite.Exclude,
        _ => null,
    };

    /// <summary>
    /// A box of mask-origin or mask-clip. The SVG boxes stand for the CSS boxes an HTML element has: fill-box for its
    /// content box, stroke-box and view-box for its border box. no-clip, which lets a mask image reach past the box, is
    /// taken as the border box, past which an element rarely paints.
    /// </summary>
    public static BackgroundBox? Box(string lower) => lower.Trim() switch
    {
        "border-box" or "stroke-box" or "view-box" or "no-clip" => BackgroundBox.BorderBox,
        "padding-box" => BackgroundBox.PaddingBox,
        "content-box" or "fill-box" => BackgroundBox.ContentBox,
        _ => null,
    };

    private static bool IsBoxKeyword(string lower) => Box(lower) != null;

    /// <summary>Splits the layers of the mask shorthand into its longhands.</summary>
    public static void ExpandShorthand(string value, Action<string, string> add)
    {
        var images = new List<string>();
        var positions = new List<string>();
        var sizes = new List<string>();
        var repeats = new List<string>();
        var origins = new List<string>();
        var clips = new List<string>();
        var composites = new List<string>();
        var modes = new List<string>();
        foreach (var layer in CssParser.SplitTopLevel(value, ','))
        {
            string image = "none", composite = "add", mode = "match-source";
            var repeat = new List<string>();
            var pos = new List<string>();
            var size = new List<string>();
            var boxes = new List<string>();
            bool afterSlash = false;
            foreach (var tok in CssParser.Tokens(CssBackground.SpaceSlashes(layer)))
            {
                var lower = tok.ToLowerInvariant();
                if (tok == "/") { afterSlash = true; continue; }
                if (afterSlash && size.Count < 2 && (lower is "cover" or "contain" or "auto" || char.IsDigit(lower[0]) || lower[0] is '.' or '-' or '+'))
                {
                    size.Add(lower);
                    continue;
                }
                afterSlash = false;
                if (lower.Contains("gradient(") || lower.StartsWith("url(", StringComparison.Ordinal)) image = tok;
                else if (lower == "none") image = "none";
                else if (CssBackground.IsRepeatKeyword(lower)) repeat.Add(lower);
                else if (IsBoxKeyword(lower)) boxes.Add(lower);
                else if (Composite(lower) != null) composite = lower;
                else if (Mode(lower) != null) mode = lower;
                else if (CssBackground.IsPositionKeyword(lower) || char.IsDigit(lower[0]) || lower[0] is '.' or '-' or '+') pos.Add(lower);
            }
            images.Add(image);
            positions.Add(pos.Count > 0 ? string.Join(" ", pos) : "0% 0%");
            sizes.Add(size.Count > 0 ? string.Join(" ", size) : "auto");
            repeats.Add(repeat.Count > 0 ? string.Join(" ", repeat) : "repeat");
            origins.Add(boxes.Count > 0 ? boxes[0] : "border-box");
            clips.Add(boxes.Count > 1 ? boxes[1] : boxes.Count == 1 ? boxes[0] : "border-box");
            composites.Add(composite);
            modes.Add(mode);
        }
        add("mask-image", images.Count > 0 ? string.Join(", ", images) : "none");
        add("mask-position", string.Join(", ", positions));
        add("mask-size", string.Join(", ", sizes));
        add("mask-repeat", string.Join(", ", repeats));
        add("mask-origin", string.Join(", ", origins));
        add("mask-clip", string.Join(", ", clips));
        add("mask-composite", string.Join(", ", composites));
        add("mask-mode", string.Join(", ", modes));
    }
}
