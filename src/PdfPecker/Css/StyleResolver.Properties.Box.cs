using System.Globalization;

namespace PdfPecker.Css;

/// <summary>Box properties: display, floats, positioning, overflow, replaced content, containers and tables.</summary>
internal sealed partial class StyleResolver
{
    private static void ApplyDisplay(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        Display? display = d.Lower switch
        {
            "block" => Display.Block,
            "flow-root" => Display.Block,
            "flex" or "block flex" or "-webkit-flex" => Display.Flex,
            "grid" or "block grid" => Display.Grid,
            "inline-flex" or "inline flex" or "-webkit-inline-flex" => Display.InlineFlex,
            "inline-grid" or "inline grid" => Display.InlineGrid,
            "inline" or "contents" => Display.Inline,
            "inline-block" => Display.InlineBlock,
            // The legacy flexible box, which FinishLegacyBox turns into a flex container or a line-clamped block.
            "-webkit-box" => Display.Flex,
            "-webkit-inline-box" => Display.InlineFlex,
            "list-item" => Display.ListItem,
            "table" or "inline-table" => Display.Table,
            "table-row" => Display.TableRow,
            "table-cell" => Display.TableCell,
            "table-row-group" => Display.TableRowGroup,
            "table-header-group" => Display.TableHeaderGroup,
            "table-footer-group" => Display.TableFooterGroup,
            "table-caption" => Display.TableCaption,
            "table-column" => Display.TableColumn,
            "table-column-group" => Display.TableColumnGroup,
            "none" => Display.None,
            // display is not inherited, so unset is its initial value, inline.
            "initial" or "unset" => Display.Inline,
            "inherit" => parent.Display,
            _ => null,
        };
        if (display == null) return; // an invalid value is ignored
        s.Display = display.Value;
        if (d.Lower == "flow-root") s.FormattingRoot = true;
        s.InlineTable = s.Display == Display.Table && (d.Lower == "inline-table" || d.Lower == "inherit" && parent.InlineTable);
        s.Contents = d.Lower == "contents" || d.Lower == "inherit" && parent.Contents;
        s.LegacyBox = d.Lower is "-webkit-box" or "-webkit-inline-box" || d.Lower == "inherit" && parent.LegacyBox;
    }

    /// <summary>width: a length, or an intrinsic size keyword, which leaves the length auto for layout to size.</summary>
    private void ApplyWidth(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (IsStretch(d.Lower))
        {
            s.Width = Length.Auto;
            s.WidthKeyword = SizeKeyword.None;
            s.Stretched |= StretchSizes.Width;
            return;
        }
        var keyword = d.Lower switch
        {
            "min-content" or "-webkit-min-content" => SizeKeyword.MinContent,
            "max-content" or "-webkit-max-content" => SizeKeyword.MaxContent,
            "fit-content" or "-webkit-fit-content" or "-moz-fit-content" => SizeKeyword.FitContent,
            _ => SizeKeyword.None,
        };
        if (keyword != SizeKeyword.None)
        {
            s.Width = Length.Auto;
            s.WidthKeyword = keyword;
            s.Stretched &= ~StretchSizes.Width;
            return;
        }
        bool set = SetLength(ref s.Width, d.Lower, d.Em, d.Inherit ? parent.Width : null);
        if (d.Inherit) s.WidthKeyword = parent.WidthKeyword;
        else if (d.Lower == "auto" || !s.Width.IsAuto) s.WidthKeyword = SizeKeyword.None;
        if (d.Inherit) s.Stretched = s.Stretched & ~StretchSizes.Width | parent.Stretched & StretchSizes.Width;
        else if (set) s.Stretched &= ~StretchSizes.Width;
    }

    /// <summary>
    /// height, min-width, min-height, max-width or max-height (<paramref name="which"/>): a length, none for the maximums,
    /// or stretch, which <see cref="ResolveStretched"/> turns into a length once the margins are known.
    /// </summary>
    private void ApplySize(ref Length target, ComputedStyle s, ComputedStyle parent, in DeclaredValue d, StretchSizes which, Length? inherited)
    {
        if (IsStretch(d.Lower))
        {
            s.Stretched |= which;
            return;
        }
        bool max = which is StretchSizes.MaxWidth or StretchSizes.MaxHeight;
        bool set = SetLength(ref target, max && d.Lower == "none" ? "auto" : d.Lower, d.Em, inherited);
        if (d.Inherit && inherited != null) s.Stretched = s.Stretched & ~which | parent.Stretched & which;
        else if (set) s.Stretched &= ~which;
    }

    /// <summary>
    /// The stretch keyword of CSS Sizing 4, and -webkit-fill-available, the name Chrome gave it first. Chrome takes both
    /// and nothing else (not Firefox's -moz-available), so a page that lists all three gets Chrome's choice.
    /// </summary>
    private static bool IsStretch(string lower) => lower is "stretch" or "-webkit-fill-available";

    /// <summary>
    /// Turns the sizes set to stretch into lengths: 100% of the containing block less the box's margins, and less its
    /// padding and border unless box-sizing is border-box, with auto margins as zero (CSS Sizing 4 §6.1). An absolutely
    /// positioned box also leaves out its insets, so that it reaches from its left offset to its right one. Horizontal
    /// percentages are of the containing block's width, as the result is, so they stay exact; vertical margins and
    /// padding in percent refer to the width, which a height cannot know, and count as zero.
    /// </summary>
    private static void ResolveStretched(ComputedStyle s)
    {
        var st = s.Stretched;
        if ((st & StretchSizes.Width) != 0) s.Width = StretchLength(s, horizontal: true);
        if ((st & StretchSizes.MinWidth) != 0) s.MinWidth = StretchLength(s, horizontal: true);
        if ((st & StretchSizes.MaxWidth) != 0) s.MaxWidth = StretchLength(s, horizontal: true);
        if ((st & StretchSizes.Height) != 0) s.Height = StretchLength(s, horizontal: false);
        if ((st & StretchSizes.MinHeight) != 0) s.MinHeight = StretchLength(s, horizontal: false);
        if ((st & StretchSizes.MaxHeight) != 0) s.MaxHeight = StretchLength(s, horizontal: false);
    }

    private static Length StretchLength(ComputedStyle s, bool horizontal)
    {
        float pt = 0, pct = 100;
        Func<float, float>? rest = null;
        void Less(Length l)
        {
            switch (l.Unit)
            {
                case LengthUnit.Pt: pt -= l.Value; break;
                case LengthUnit.Percent: if (horizontal) pct -= l.Value; break;
                case LengthUnit.Calc when l.IsLinear: pt -= l.Value; if (horizontal) pct -= l.Pct; break;
                case LengthUnit.Calc when horizontal:
                    var before = rest;
                    rest = before == null ? b => l.Resolve(b) : b => before(b) + l.Resolve(b);
                    break;
            }
        }
        if (horizontal) { Less(s.MarginLeft); Less(s.MarginRight); }
        else { Less(s.MarginTop); Less(s.MarginBottom); }
        if (!s.BorderBox)
        {
            if (horizontal) { Less(s.PaddingLeft); Less(s.PaddingRight); pt -= s.BorderLeft + s.BorderRight; }
            else { Less(s.PaddingTop); Less(s.PaddingBottom); pt -= s.BorderTop + s.BorderBottom; }
        }
        if (s.OutOfFlow)
        {
            if (horizontal) { Less(s.Left); Less(s.Right); }
            else { Less(s.Top); Less(s.Bottom); }
        }
        if (rest == null) return Length.Calc(pt, pct);
        var others = rest;
        return Length.Calc(b => pt + pct * b / 100f - others(b));
    }

    private static void ApplyInitialLetter(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Lower == "inherit") (s.InitialLetterSize, s.InitialLetterSink) = (parent.InitialLetterSize, parent.InitialLetterSink);
        else if (d.Lower is "normal" or "initial" or "unset") (s.InitialLetterSize, s.InitialLetterSink) = (0, 0);
        else if (ParseInitialLetter(d.Lower) is { } letter) (s.InitialLetterSize, s.InitialLetterSink) = letter;
    }

    /// <summary>
    /// Parses initial-letter: a size of at least 1 followed by a whole sink of at least 1, or by drop or raise (raise
    /// sinks by one line, drop and nothing by the size rounded up, given as 0). Null when the value is not valid.
    /// </summary>
    internal static (float Size, int Sink)? ParseInitialLetter(string lower)
    {
        var parts = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > 2) return null;
        int sizeAt = parts.Length == 2 && parts[0] is "drop" or "raise" ? 1 : 0;
        if (!float.TryParse(parts[sizeAt], NumberStyles.Float, CultureInfo.InvariantCulture, out float size) || !float.IsFinite(size) || size < 1)
            return null;
        if (parts.Length == 1) return (size, 0);
        string sink = parts[1 - sizeAt];
        if (sink == "drop") return (size, 0);
        if (sink == "raise") return (size, 1);
        if (sizeAt == 1 || !Invariant.TryParseInt(sink, out int n) || n < 1) return null;
        return (size, n);
    }

    private static void ApplyFloat(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        string lower = d.Lower;
        s.Float = lower switch
        {
            "left" or "inline-start" => FloatSide.Left,
            "right" or "inline-end" => FloatSide.Right,
            "none" or "footnote" => FloatSide.None,
            "inherit" => parent.Float,
            _ => s.Float,
        };
        // Only a valid value decides whether the element is a footnote (float: footnote).
        if (lower is "left" or "inline-start" or "right" or "inline-end" or "none" or "footnote" or "inherit")
            s.Footnote = lower == "footnote" || d.Inherit && parent.Footnote;
        s.Floated = s.Float != FloatSide.None;
    }

    private static void ApplyClear(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.ClearLeft = parent.ClearLeft; s.ClearRight = parent.ClearRight; return; }
        s.ClearLeft = d.Lower is "left" or "both" or "inline-start";
        s.ClearRight = d.Lower is "right" or "both" or "inline-end";
    }

    /// <summary>shape-outside: none, a basic shape and/or a reference box, or an image or gradient.</summary>
    private void ApplyShapeOutside(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.ShapeOutside = parent.ShapeOutside;
        else if (d.Lower is "none" or "initial" or "unset") s.ShapeOutside = null;
        else if (CssParser.Tokens(d.Lower).Count == 1 && ParseBackgroundImage(d.Value, s) is [{ } image]) s.ShapeOutside = new ShapeOutside { Image = image };
        else if (ClipShape.Parse(d.Value, d.Em, _rootFontSize, _vw, _vh, shapeOutside: true) is { } shape) s.ShapeOutside = new ShapeOutside { Shape = shape };
    }

    /// <summary>shape-margin: a length or percentage that is not negative.</summary>
    private void ApplyShapeMargin(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.ShapeMargin = parent.ShapeMargin;
        else if (d.Lower is "initial" or "unset") s.ShapeMargin = Length.Zero;
        else if (TryParseLength(d.Lower, d.Em, _rootFontSize, out var len, _vw, _vh) && !len.IsAuto && !(len.Unit != LengthUnit.Calc && len.Value < 0))
            s.ShapeMargin = len;
    }

    /// <summary>shape-image-threshold: the alpha above which an image's pixels are in the shape, clamped to 0 to 1.</summary>
    private static void ApplyShapeImageThreshold(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.ShapeImageThreshold = parent.ShapeImageThreshold;
        else if (d.Lower is "initial" or "unset") s.ShapeImageThreshold = 0;
        else if (TryParseNumberOrPercent(d.Lower, out float t)) s.ShapeImageThreshold = Math.Clamp(t, 0, 1);
    }

    private static void ApplyPosition(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.Position = d.Lower switch
        {
            "relative" or "sticky" => Position.Relative,
            "absolute" => Position.Absolute,
            "fixed" => Position.Fixed,
            "static" => Position.Static,
            "inherit" => parent.Position,
            _ => s.Position,
        };

    private static void ApplyZIndex(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Lower == "auto") s.ZIndex = null;
        else if (d.Inherit) s.ZIndex = parent.ZIndex;
        else if (int.TryParse(d.Lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z)) s.ZIndex = z;
    }

    /// <summary>overflow sets both axes (one value for both, or x then y); overflow-x and overflow-y set one.</summary>
    private static void ApplyOverflow(ComputedStyle s, ComputedStyle parent, string prop, in DeclaredValue d)
    {
        // Any value but visible and clip makes the box a block formatting context root, which contains its floats.
        var parts = d.Lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in parts)
            if (t is "hidden" or "auto" or "scroll" or "overlay") s.FormattingRoot = true;
        if (parts.Length == 0) return;
        static Overflow Parse(string t, Overflow inherited) => t switch
        {
            "visible" => Overflow.Visible,
            "clip" => Overflow.Clip,
            "hidden" or "auto" or "scroll" or "overlay" => Overflow.Hidden,
            "inherit" => inherited,
            _ => Overflow.Visible,
        };
        if (prop != "overflow-y") s.OverflowX = Parse(parts[0], parent.OverflowX);
        if (prop != "overflow-x") s.OverflowY = Parse(parts[prop == "overflow" && parts.Length > 1 ? 1 : 0], parent.OverflowY);
    }

    /// <summary>
    /// overflow-clip-margin: a visual box (padding-box when left out) and a length. Chrome 153 also takes a negative
    /// length, which pulls the clip inside the box, so this does too.
    /// </summary>
    private void ApplyOverflowClipMargin(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.OverflowClipBox = parent.OverflowClipBox; s.OverflowClipMargin = parent.OverflowClipMargin; return; }
        if (d.Lower is "initial" or "unset") { s.OverflowClipBox = ShapeBox.PaddingBox; s.OverflowClipMargin = 0; return; }
        var tokens = CssParser.Tokens(d.Lower);
        if (tokens.Count is 0 or > 2) return;
        ShapeBox? box = null;
        float? margin = null;
        foreach (var t in tokens)
        {
            var b = t switch { "border-box" => ShapeBox.BorderBox, "padding-box" => ShapeBox.PaddingBox, "content-box" => ShapeBox.ContentBox, _ => (ShapeBox?)null };
            if (b != null)
            {
                if (box != null) return;
                box = b;
            }
            else if (margin == null && TryParseLength(t, d.Em, _rootFontSize, out var len, _vw, _vh) && !len.IsAuto && !len.IsPercent
                     && len.Resolve(0) is var pt && float.IsFinite(pt)) margin = pt;
            else return;
        }
        s.OverflowClipBox = box ?? ShapeBox.PaddingBox;
        s.OverflowClipMargin = margin ?? 0;
    }

    private static void ApplyAspectRatio(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.AspectRatio = parent.AspectRatio; s.AspectRatioAuto = parent.AspectRatioAuto; }
        else if (ParseAspectRatio(d.Lower, out float ratio, out bool ratioAuto)) { s.AspectRatio = ratio; s.AspectRatioAuto = ratioAuto; }
    }

    private static void ApplyObjectFit(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.ObjectFit = d.Lower switch
        {
            "fill" => ObjectFit.Fill,
            "contain" => ObjectFit.Contain,
            "cover" => ObjectFit.Cover,
            "none" => ObjectFit.None,
            "scale-down" => ObjectFit.ScaleDown,
            "inherit" => parent.ObjectFit,
            _ => s.ObjectFit,
        };

    private void ApplyObjectPosition(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) { s.ObjectPositionX = parent.ObjectPositionX; s.ObjectPositionY = parent.ObjectPositionY; }
        else if (CssBackground.Position(d.Lower, d.Em, _rootFontSize, _vw, _vh) is { } opos) { s.ObjectPositionX = opos.X; s.ObjectPositionY = opos.Y; }
    }

    private static void ApplyContainerType(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
        => s.ContainerType = d.Lower switch
        {
            "inline-size" => ContainerType.InlineSize,
            "size" => ContainerType.Size,
            "normal" or "initial" or "unset" or "revert" or "revert-layer" => ContainerType.Normal,
            "inherit" => parent.ContainerType,
            _ => s.ContainerType,
        };

    private static void ApplyContainerName(ComputedStyle s, ComputedStyle parent, in DeclaredValue d)
    {
        if (d.Inherit) s.ContainerNames = parent.ContainerNames;
        else if (d.Lower is "none" or "initial" or "unset" or "revert" or "revert-layer") s.ContainerNames = null;
        else if (CssParser.ContainerNamesValid(d.Value)) s.ContainerNames = d.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>border-spacing-h and border-spacing-v, the two halves the border-spacing shorthand expands to.</summary>
    private void ApplyBorderSpacing(ref float target, in DeclaredValue d)
    {
        if (TryParseLength(d.Lower, d.Em, _rootFontSize, out var spacing, _vw, _vh)) target = ToLayoutUnits(spacing.Resolve(0));
    }
}
