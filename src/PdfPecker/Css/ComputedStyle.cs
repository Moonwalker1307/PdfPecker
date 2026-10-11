using PdfPecker.Fonts;

namespace PdfPecker.Css;

internal enum Display : byte
{
    Inline, Block, ListItem, InlineBlock, Table, TableRow, TableCell, TableRowGroup, TableHeaderGroup,
    TableFooterGroup, TableCaption, TableColumn, TableColumnGroup, None, Flex, InlineFlex, Grid, InlineGrid,
}

internal enum FlexDirection : byte { Row, RowReverse, Column, ColumnReverse }
internal enum FlexWrap : byte { NoWrap, Wrap, WrapReverse }

/// <summary>Values of justify-content, align-items, align-self, align-content, justify-items and justify-self.</summary>
internal enum BoxAlign : byte { Normal, Start, End, Center, Stretch, Baseline, SpaceBetween, SpaceAround, SpaceEvenly, Auto }

internal enum TrackSizeKind : byte { Fixed, Percent, Fr, Auto, MinContent, MaxContent }

/// <summary>One sizing function of a grid track: a length, a percentage, a flexible fraction or a content keyword.</summary>
internal readonly struct TrackSize
{
    public readonly TrackSizeKind Kind;
    public readonly float Value;
    public TrackSize(TrackSizeKind kind, float value = 0) { Kind = kind; Value = value; }
    public bool IsIntrinsic => Kind is TrackSizeKind.Auto or TrackSizeKind.MinContent or TrackSizeKind.MaxContent;
}

/// <summary>A grid track: minmax(Min, Max). A plain size is minmax(size, size); an fr size is minmax(auto, fr).</summary>
internal readonly struct Track
{
    public readonly TrackSize Min, Max;
    public Track(TrackSize min, TrackSize max) { Min = min; Max = max; }
}

/// <summary>A grid-template-columns/rows value. With <see cref="AutoRepeat"/> set, those tracks repeat to fill the container.</summary>
internal sealed class TrackList
{
    public Track[] Before = Array.Empty<Track>();
    public Track[]? AutoRepeat;
    public bool AutoFit;
    public Track[] After = Array.Empty<Track>();
}

/// <summary>A grid-column-start/end or grid-row-start/end value: auto, a line number or a span.</summary>
internal readonly struct GridLine
{
    public readonly int Line; // 1-based; negative counts from the end; 0 = auto
    public readonly int Span; // > 0 for "span n"
    public GridLine(int line, int span) { Line = line; Span = span; }
    public bool IsAuto => Line == 0 && Span == 0;
}

internal enum SvgLineCap : byte { Butt, Round, Square }
internal enum SvgLineJoin : byte { Miter, Round, Bevel }

internal enum TextAlign : byte { Left, Right, Center, Justify }
internal enum WhiteSpace : byte { Normal, NoWrap, Pre, PreWrap, PreLine }
internal enum VerticalAlign : byte { Baseline, Sub, Super, Top, Middle, Bottom, TextTop, TextBottom }
internal enum TextTransform : byte { None, Uppercase, Lowercase, Capitalize }
internal enum BorderStyle : byte { None, Solid, Dashed, Dotted, Double }
internal enum LineHeightKind : byte { Normal, Factor, Absolute }

internal enum ListStyleType : byte
{
    Disc, Circle, Square, Decimal, DecimalLeadingZero, LowerAlpha, UpperAlpha, LowerRoman, UpperRoman, None,
}

[Flags]
internal enum TextDecoration : byte { None = 0, Underline = 1, LineThrough = 2, Overline = 4 }

internal enum LengthUnit : byte { Pt, Percent, Auto }

/// <summary>A computed length: absolute points, a percentage of the containing block, or auto.</summary>
internal readonly struct Length
{
    public readonly float Value;
    public readonly LengthUnit Unit;

    public Length(float value, LengthUnit unit) { Value = value; Unit = unit; }

    public static readonly Length Auto = new(0, LengthUnit.Auto);
    public static readonly Length Zero = new(0, LengthUnit.Pt);
    public static Length Pt(float v) => new(v, LengthUnit.Pt);

    public bool IsAuto => Unit == LengthUnit.Auto;
    public bool IsPercent => Unit == LengthUnit.Percent;

    public float Resolve(float basis, float autoValue = 0)
        => Unit switch
        {
            LengthUnit.Pt => Value,
            LengthUnit.Percent => Value * basis / 100f,
            _ => autoValue,
        };

    public override string ToString() => Unit switch { LengthUnit.Pt => Value + "pt", LengthUnit.Percent => Value + "%", _ => "auto" };
}

/// <summary>The computed values of the supported CSS properties for one element. All lengths are in points.</summary>
internal sealed class ComputedStyle
{
    // ---- Inherited ----
    public uint Color = CssColor.Black;
    public FontFamily FontFamily = FontFamily.Times;
    public bool Bold;
    public bool Italic;
    public float FontSize = 12f; // 16px
    public LineHeightKind LineHeightKind = LineHeightKind.Normal;
    public float LineHeightValue;
    public TextAlign TextAlign;
    public WhiteSpace WhiteSpace;
    public ListStyleType ListStyleType;
    public bool ListStylePositionInside;
    public TextTransform TextTransform;
    public TextDecoration TextDecoration; // propagated to descendants, approximated as inherited
    public float TextIndent;
    public float LetterSpacing;
    public float WordSpacing;
    public bool Visible = true;
    public bool BorderCollapse;
    public float BorderSpacingH = 1.5f, BorderSpacingV = 1.5f; // 2px
    public string? FontFamilyName;
    public Dictionary<string, string>? CustomProperties; // custom properties (--name), shared with the parent until changed

    // SVG presentation properties (inherited).
    public uint? SvgFill = CssColor.Black;  // null = none
    public string? SvgFillUrl;               // fill: url(#id)
    public uint? SvgStroke;
    public string? SvgStrokeUrl;
    public float SvgStrokeWidth = 1;         // user units
    public float SvgFillOpacity = 1, SvgStrokeOpacity = 1;
    public bool SvgEvenOdd;
    public SvgLineCap SvgLineCap;
    public SvgLineJoin SvgLineJoin;
    public float SvgMiterLimit = 4;

    // ---- Not inherited ----
    public Display Display = Display.Inline;
    public uint BackgroundColor;
    public Length MarginTop = Length.Zero, MarginRight = Length.Zero, MarginBottom = Length.Zero, MarginLeft = Length.Zero;
    public Length PaddingTop = Length.Zero, PaddingRight = Length.Zero, PaddingBottom = Length.Zero, PaddingLeft = Length.Zero;
    public float BorderTopWidth = 1.5f, BorderRightWidth = 1.5f, BorderBottomWidth = 1.5f, BorderLeftWidth = 1.5f; // medium = 3px
    public BorderStyle BorderTopStyle, BorderRightStyle, BorderBottomStyle, BorderLeftStyle;
    public uint? BorderTopColor, BorderRightColor, BorderBottomColor, BorderLeftColor; // null = currentColor
    public Length Width = Length.Auto, Height = Length.Auto;
    public Length MinWidth = Length.Zero, MaxWidth = Length.Auto, MinHeight = Length.Zero, MaxHeight = Length.Auto;
    public bool BorderBox;
    public VerticalAlign VerticalAlign;
    public bool PageBreakBefore, PageBreakAfter, AvoidBreakInside;
    public bool Floated;
    public bool ClearBoth;
    public float Opacity = 1;

    // Flexbox, grid and multi-column.
    public FlexDirection FlexDirection;
    public FlexWrap FlexWrap;
    public float FlexGrow, FlexShrink = 1;
    public Length FlexBasis = Length.Auto;
    public int Order;
    public BoxAlign JustifyContent, AlignItems, AlignContent, JustifyItems;
    public BoxAlign AlignSelf = BoxAlign.Auto, JustifySelf = BoxAlign.Auto;
    public Length? RowGap, ColumnGap; // null = normal
    public TrackList? GridTemplateColumns, GridTemplateRows;
    public Track[]? GridAutoColumns, GridAutoRows;
    public bool GridAutoFlowColumn;
    public GridLine GridColumnStart, GridColumnEnd, GridRowStart, GridRowEnd;
    public int ColumnCount;
    public Length ColumnWidth = Length.Auto;

    private StandardFont? _font;

    public StandardFont Font => _font ??= StandardFont.Get(FontFamily, Bold, Italic);

    public void InvalidateFont() => _font = null;

    public float LineHeight
        => LineHeightKind switch
        {
            LineHeightKind.Factor => LineHeightValue * FontSize,
            LineHeightKind.Absolute => LineHeightValue,
            _ => FontSize * 1.2f,
        };

    // Effective border widths: zero when the style is none.
    public float BorderTop => BorderTopStyle == BorderStyle.None ? 0 : BorderTopWidth;
    public float BorderRight => BorderRightStyle == BorderStyle.None ? 0 : BorderRightWidth;
    public float BorderBottom => BorderBottomStyle == BorderStyle.None ? 0 : BorderBottomWidth;
    public float BorderLeft => BorderLeftStyle == BorderStyle.None ? 0 : BorderLeftWidth;

    public bool HasBorder => BorderTopStyle != BorderStyle.None || BorderRightStyle != BorderStyle.None
                             || BorderBottomStyle != BorderStyle.None || BorderLeftStyle != BorderStyle.None;

    public bool IsBlockLevel => Display is not (Display.Inline or Display.InlineBlock or Display.InlineFlex or Display.InlineGrid);

    public bool IsFlex => Display is Display.Flex or Display.InlineFlex;
    public bool IsGrid => Display is Display.Grid or Display.InlineGrid;
    public bool IsMultiColumn => ColumnCount > 1 || !ColumnWidth.IsAuto;
    public bool IsRow => FlexDirection is FlexDirection.Row or FlexDirection.RowReverse;

    /// <summary>Creates the starting style for a child: inherited properties copied, the rest reset.</summary>
    public ComputedStyle CreateChild()
    {
        var c = (ComputedStyle)MemberwiseClone();
        c.Display = Display.Inline;
        c.BackgroundColor = 0;
        c.MarginTop = c.MarginRight = c.MarginBottom = c.MarginLeft = Length.Zero;
        c.PaddingTop = c.PaddingRight = c.PaddingBottom = c.PaddingLeft = Length.Zero;
        c.BorderTopWidth = c.BorderRightWidth = c.BorderBottomWidth = c.BorderLeftWidth = 1.5f;
        c.BorderTopStyle = c.BorderRightStyle = c.BorderBottomStyle = c.BorderLeftStyle = BorderStyle.None;
        c.BorderTopColor = c.BorderRightColor = c.BorderBottomColor = c.BorderLeftColor = null;
        c.Width = c.Height = Length.Auto;
        c.MinWidth = c.MinHeight = Length.Zero;
        c.MaxWidth = c.MaxHeight = Length.Auto;
        c.BorderBox = false;
        c.VerticalAlign = VerticalAlign.Baseline;
        c.PageBreakBefore = c.PageBreakAfter = c.AvoidBreakInside = false;
        c.Floated = false;
        c.ClearBoth = false;
        c.Opacity = 1;
        c.FlexDirection = FlexDirection.Row;
        c.FlexWrap = FlexWrap.NoWrap;
        c.FlexGrow = 0;
        c.FlexShrink = 1;
        c.FlexBasis = Length.Auto;
        c.Order = 0;
        c.JustifyContent = c.AlignItems = c.AlignContent = c.JustifyItems = BoxAlign.Normal;
        c.AlignSelf = c.JustifySelf = BoxAlign.Auto;
        c.RowGap = c.ColumnGap = null;
        c.GridTemplateColumns = c.GridTemplateRows = null;
        c.GridAutoColumns = c.GridAutoRows = null;
        c.GridAutoFlowColumn = false;
        c.GridColumnStart = c.GridColumnEnd = c.GridRowStart = c.GridRowEnd = default;
        c.ColumnCount = 0;
        c.ColumnWidth = Length.Auto;
        return c;
    }

    public ComputedStyle Clone() => (ComputedStyle)MemberwiseClone();
}
