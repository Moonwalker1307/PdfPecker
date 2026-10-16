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
    /// <summary>For a Percent track given as a math function (<c>calc(50% - 10px)</c>), the length to resolve.</summary>
    private readonly Length _calc;
    public TrackSize(TrackSizeKind kind, float value = 0) { Kind = kind; Value = value; }
    public TrackSize(Length calc) { Kind = TrackSizeKind.Percent; _calc = calc; }
    /// <summary>A Percent track's size against <paramref name="basis"/>.</summary>
    public float ResolvePercent(float basis) => _calc.Unit == LengthUnit.Calc ? _calc.Resolve(basis) : Value * basis / 100f;
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
    // Line names: (name, index of the line within Before or After, whether it is in After).
    public List<(string Name, int Line, bool InAfter)>? LineNames;
}

/// <summary>A grid-column-start/end or grid-row-start/end value: auto, a line number, a named line or a span.</summary>
internal readonly struct GridLine
{
    public readonly int Line; // 1-based; negative counts from the end; 0 = auto. With a name: the nth line of that name
    public readonly int Span; // > 0 for "span n"
    public readonly string? Name; // a line name or grid area name
    public readonly string? SpanName; // "span n name": spans to the nth line of that name
    public GridLine(int line, int span, string? name = null, string? spanName = null) { Line = line; Span = span; Name = name; SpanName = spanName; }
    public bool IsAuto => Line == 0 && Span == 0 && Name == null;
}

/// <summary>A grid-template-areas value: named rectangles of cells, as 0-based start lines and exclusive end lines.</summary>
internal sealed class GridAreas
{
    public int Rows, Columns;
    public readonly Dictionary<string, (int RowStart, int ColStart, int RowEnd, int ColEnd)> Areas = new(StringComparer.Ordinal);
}

internal enum SvgLineCap : byte { Butt, Round, Square }
internal enum SvgLineJoin : byte { Miter, Round, Bevel }

internal enum TextAlign : byte { Left, Right, Center, Justify, Start, End } // Start and End follow the direction
internal enum UnicodeBidi : byte { Normal, Embed, Isolate, BidiOverride, IsolateOverride, Plaintext }
internal enum WhiteSpace : byte { Normal, NoWrap, Pre, PreWrap, PreLine }
internal enum VerticalAlign : byte { Baseline, Sub, Super, Top, Middle, Bottom, TextTop, TextBottom }
internal enum OverflowWrap : byte { Normal, BreakWord, Anywhere }
internal enum Overflow : byte { Visible, Clip, Hidden } // Hidden also stands for auto and scroll, which clip alike on paper
internal enum WordBreak : byte { Normal, BreakAll, KeepAll }
internal enum TextTransform : byte { None, Uppercase, Lowercase, Capitalize }
internal enum BorderStyle : byte { None, Solid, Dashed, Dotted, Double, Groove, Ridge, Inset, Outset }
internal enum LineHeightKind : byte { Normal, Factor, Absolute }
internal enum ObjectFit : byte { Fill, Contain, Cover, None, ScaleDown }

/// <summary>Values of container-type: which axes of the box container queries can test.</summary>
internal enum ContainerType : byte { Normal, InlineSize, Size }

internal enum ListStyleType : byte
{
    Disc, Circle, Square, Decimal, DecimalLeadingZero, LowerAlpha, UpperAlpha, LowerRoman, UpperRoman, None,
    DisclosureOpen, DisclosureClosed,
    Other, // any other counter style: ListCounterStyle
}

[Flags]
internal enum TextDecoration : byte { None = 0, Underline = 1, LineThrough = 2, Overline = 4 }

internal enum DecorationStyle : byte { Solid, Double, Dotted, Dashed, Wavy }

/// <summary>Pt and Percent are plain values; Calc is a math function that depends on a percentage (see <see cref="CssMath"/>).</summary>
internal enum LengthUnit : byte { Pt, Percent, Auto, Calc }

internal enum Position : byte { Static, Relative, Absolute, Fixed }

internal enum FloatSide : byte { None, Left, Right }

/// <summary>One function of a CSS transform list. Translations are in points, or percentages of the border box when flagged.</summary>
internal readonly record struct TransformFn(TransformKind Kind, float A, float B = 0, float C = 0, float D = 0, float E = 0, float F = 0,
    bool PercentX = false, bool PercentY = false);

internal enum TransformKind : byte { Translate, Scale, Rotate, Skew, Matrix }

/// <summary>A counter name with the value of a counter-reset, counter-set or counter-increment.</summary>
internal readonly record struct CounterOp(string Name, int Value);

/// <summary>
/// A computed length: absolute points, a percentage of the containing block, auto, or a <c>calc()</c>-style value that
/// mixes the two. A Calc length counts as a percentage (it needs the reference length), so code that only knows how to
/// handle absolute lengths treats it as it treats percentages; <see cref="Resolve"/> gives its value.
/// </summary>
internal readonly struct Length
{
    /// <summary>Points; for Percent the percentage; for a linear Calc the points part.</summary>
    public readonly float Value;
    public readonly LengthUnit Unit;
    /// <summary>For Calc only (kept in a separate object so ordinary lengths stay small).</summary>
    private readonly CalcParts? _calc;

    private sealed record CalcParts(float Pct, Func<float, float>? Fn);

    public Length(float value, LengthUnit unit) { Value = value; Unit = unit; }

    private Length(float value, CalcParts calc) { Value = value; Unit = LengthUnit.Calc; _calc = calc; }

    public static readonly Length Auto = new(0, LengthUnit.Auto);
    public static readonly Length Zero = new(0, LengthUnit.Pt);
    public static Length Pt(float v) => new(v, LengthUnit.Pt);
    /// <summary>A sum of points and a percentage, such as <c>calc(100% - 20pt)</c>.</summary>
    public static Length Calc(float pt, float pct) => new(pt, new CalcParts(pct, null));
    /// <summary>Any other value that depends on a percentage, such as <c>min(100%, 600pt)</c>: points as a function of the reference length.</summary>
    public static Length Calc(Func<float, float> fn) => new(0, new CalcParts(0, fn));

    public bool IsAuto => Unit == LengthUnit.Auto;
    /// <summary>True when the value depends on the reference length: a percentage, or a Calc containing one.</summary>
    public bool IsPercent => Unit is LengthUnit.Percent or LengthUnit.Calc;
    /// <summary>True for a Calc that is a plain sum of <see cref="Value"/> points and <see cref="Pct"/> percent.</summary>
    public bool IsLinear => _calc?.Fn == null;
    /// <summary>For a linear Calc, the percentage part (<c>Value + Pct% of the reference</c>).</summary>
    public float Pct => _calc?.Pct ?? 0;

    public float Resolve(float basis, float autoValue = 0)
        => Unit switch
        {
            LengthUnit.Pt => Value,
            LengthUnit.Percent => Value * basis / 100f,
            LengthUnit.Calc => _calc!.Fn == null ? Value + _calc.Pct * basis / 100f : Finite(_calc.Fn(basis)),
            _ => autoValue,
        };

    private static float Finite(float v) => float.IsFinite(v) ? v : 0;

    public override string ToString() => Unit switch
    {
        LengthUnit.Pt => Value + "pt",
        LengthUnit.Percent => Value + "%",
        LengthUnit.Calc => IsLinear ? $"calc({Pct}% + {Value}pt)" : "calc(...)",
        _ => "auto",
    };
}

/// <summary>The computed values of the supported CSS properties for one element. All lengths are in points.</summary>
internal sealed class ComputedStyle
{
    // ---- Inherited ----
    public uint Color = CssColor.Black;
    public FontFamily FontFamily = FontFamily.Times;
    public bool Bold;
    public int FontWeight = 400;
    public bool Italic;
    public float FontStretch = 100;          // font-stretch as a percentage of normal width
    public string? FontVariationSettings;    // font-variation-settings, as written; null = normal
    public bool FontOpticalSizingNone;       // font-optical-sizing: none
    public float FontSize = 12f; // 16px
    public float ParentFontSize = 12f; // the parent's font size, which sub and super shift by
    public LineHeightKind LineHeightKind = LineHeightKind.Normal;
    public float LineHeightValue;
    public TextAlign TextAlign = TextAlign.Start;
    public bool Rtl;                         // direction: rtl
    public TextAlign? TextAlignLast;         // null = auto
    public int TabSize = 8;                  // in spaces
    public bool NoHyphens;                   // hyphens: none (soft hyphens are no break opportunity)
    public bool HyphensAuto;                 // hyphens: auto (words also break where the language's patterns allow)
    public string? HyphenateCharacter;       // hyphenate-character: "string"; null = auto (a hyphen-minus)
    public string? Lang;                     // the content language: the nearest lang attribute, else PdfOptions.Language
    public IReadOnlyDictionary<string, HyphenationPatterns>? Hyphenation; // PdfOptions.Hyphenation, keys in lower case
    public byte SmallCaps;                   // font-variant-caps: 1 small-caps, 2 all-small-caps (synthesized)
    public WhiteSpace WhiteSpace;
    public OverflowWrap OverflowWrap;
    public WordBreak WordBreak;
    public ListStyleType ListStyleType;
    public string? ListStyleString;          // list-style-type: "string": the marker text
    public CounterStyle? ListCounterStyle;   // the counter style of a text marker; null for the bullets, a string and none
    public string? ListStyleImage;           // list-style-image: the url
    public bool ListStylePositionInside;
    public TextTransform TextTransform;
    public TextDecoration TextDecoration; // propagated to descendants, approximated as inherited
    // text-decoration-color, -style, -thickness and text-underline-offset, propagated with the decoration. The colour is
    // fixed to the decorating box's color when it sets the line, as Chrome draws a link's underline in the link colour.
    public uint? DecorationColor;          // null = currentColor
    public DecorationStyle DecorationStyle;
    public Length? DecorationThickness;    // null = auto; percentages of 1em
    public Length? UnderlineOffset;        // null = auto; from the alphabetic baseline
    public float TextIndent;
    public float LetterSpacing;
    public bool NoLigatures; // font-variant-ligatures: none or no-common-ligatures, or font-feature-settings: "liga" 0
    public string? FontVariantNumeric;   // font-variant-numeric keywords; null = normal
    public string? FontFeatureSettings;  // font-feature-settings as written; null = normal
    public float WordSpacing;
    public bool Visible = true;
    public bool CanvasBackground; // the background is painted on every page's canvas instead of on this box (html/body propagation)
    public bool BorderCollapse;
    public bool TableLayoutFixed; // table-layout: fixed
    public BoxShadow[]? BoxShadows;
    public BoxShadow[]? TextShadows; // text-shadow (inherited); spread and inset are not used
    public float BorderSpacingH = 1.5f, BorderSpacingV = 1.5f; // 2px
    public string? FontFamilyName;
    public FontResolver? Fonts; // the document's fonts; null when only the standard fonts exist
    public MissingFontReport? MissingFonts; // set when PdfOptions.MissingFont is
    public CssVarScope? CustomProperties; // custom properties (--name), shared with the parent until changed

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
    public bool SvgClipEvenOdd;              // clip-rule
    public byte SvgTextAnchor;               // 0 start, 1 middle, 2 end
    public byte SvgBaseline;                 // dominant-baseline: 0 alphabetic, 1 middle/central, 2 hanging, 3 text-after-edge
    public string? SvgMarkerStart, SvgMarkerMid, SvgMarkerEnd; // marker-start/mid/end: url(#id), as "#id"
    public bool SvgFilterSrgb;               // color-interpolation-filters: sRGB (else linearRGB)
    public int Orphans = 2, Widows = 2; // minimum lines of a paragraph left at the bottom / carried to the top of a page

    // ---- Not inherited ----
    public Display Display = Display.Inline;
    public UnicodeBidi UnicodeBidi;
    public uint BackgroundColor;
    // Background layers, topmost first. Each list repeats when it is shorter than BackgroundImages; null is the initial value.
    public BackgroundImage?[]? BackgroundImages; // null entries are "none" layers
    public (BackgroundRepeat X, BackgroundRepeat Y)[]? BackgroundRepeats;
    public BackgroundOffset[]? BackgroundPositionsX, BackgroundPositionsY;
    public BackgroundSize[]? BackgroundSizes;
    public BackgroundBox[]? BackgroundOrigins, BackgroundClips;
    public string? SvgClipPath;                 // clip-path: url(#id), as "#id"
    public string? SvgMask, SvgFilter;          // mask and filter: url(#id), as "#id"
    public bool SvgMaskAlpha;                   // mask-type: alpha (else luminance)
    public uint SvgFloodColor = CssColor.Black; // flood-color
    public float SvgFloodOpacity = 1;           // flood-opacity
    public uint SvgLightingColor = 0xFFFFFFFFu; // lighting-color
    public ClipShape? ClipShape;                // clip-path: a basic shape or a reference box, on HTML elements
    public string? BlendMode;                   // mix-blend-mode, as a PDF blend mode name; null = normal
    public string?[]? BackgroundBlendModes;     // background-blend-mode per layer, as PDF names (null = normal); null = all normal
    public bool Isolate;                        // isolation: isolate
    public FilterFunction[]? Filters;           // filter functions on HTML elements; null = none
    public Length MarginTop = Length.Zero, MarginRight = Length.Zero, MarginBottom = Length.Zero, MarginLeft = Length.Zero;
    public Length PaddingTop = Length.Zero, PaddingRight = Length.Zero, PaddingBottom = Length.Zero, PaddingLeft = Length.Zero;
    public float BorderTopWidth = 1.5f, BorderRightWidth = 1.5f, BorderBottomWidth = 1.5f, BorderLeftWidth = 1.5f; // medium = 3px
    public BorderStyle BorderTopStyle, BorderRightStyle, BorderBottomStyle, BorderLeftStyle;
    public uint? BorderTopColor, BorderRightColor, BorderBottomColor, BorderLeftColor; // null = currentColor
    // border-radius: horizontal and vertical radius of the top-left, top-right, bottom-right and bottom-left corner
    // (percentages of the border box's width and height); null when every corner is square.
    public Length[]? BorderRadii;
    // border-image. Null slices, widths and outsets are the initial values: 100%, 1 and 0 on every side.
    public BackgroundImage? BorderImageSource;
    public BorderImageValue[]? BorderImageSlice; // top, right, bottom, left
    public bool BorderImageFill;
    public BorderImageValue[]? BorderImageWidths, BorderImageOutsets;
    public BorderImageRepeat BorderImageRepeatX, BorderImageRepeatY;
    public BorderStyle OutlineStyle;
    public float OutlineWidth = 1.5f, OutlineOffset;
    public uint? OutlineColor; // null = currentColor
    public ObjectFit ObjectFit;
    public BackgroundOffset ObjectPositionX = BackgroundOffset.Center, ObjectPositionY = BackgroundOffset.Center;
    /// <summary>What replaces the cut end of an overflowing line (text-overflow): "…" for ellipsis, a custom string, or null for clip.</summary>
    public string? TextOverflow;
    public bool TextOverflowEllipsis => TextOverflow != null;
    public float AspectRatio;    // width / height; 0 = auto
    public bool AspectRatioAuto; // "auto <ratio>": a replaced element keeps its natural ratio
    public ContainerType ContainerType;
    public string[]? ContainerNames; // container-name; null for none
    public Length Width = Length.Auto, Height = Length.Auto;
    public Length MinWidth = Length.Auto, MaxWidth = Length.Auto, MinHeight = Length.Zero, MaxHeight = Length.Auto;
    public bool BorderBox;
    public VerticalAlign VerticalAlign;
    public bool PageBreakBefore, PageBreakAfter, AvoidBreakInside;
    public bool AvoidBreakBefore, AvoidBreakAfter; // break-before/after: avoid (keep with the next or previous box)
    public bool Floated;
    public FloatSide Float;
    public bool ClearLeft, ClearRight;
    public float Opacity = 1;
    // An inline box's opacity times that of the inline boxes around it, up to the block. Inline boxes get no layer of
    // their own, so their text, decorations and backgrounds are faded instead.
    public float InlineFade = 1;
    public Position Position;
    public Length Top = Length.Auto, Right = Length.Auto, Bottom = Length.Auto, Left = Length.Auto;
    public int? ZIndex;                         // null = auto
    public bool FormattingRoot;                 // overflow other than visible, or display: flow-root
    public Overflow OverflowX, OverflowY;

    /// <summary>Whether the box clips its content horizontally and vertically (visible next to hidden computes to auto).</summary>
    public bool ClipsX => OverflowX == Overflow.Clip || OverflowX == Overflow.Hidden || OverflowX == Overflow.Visible && OverflowY == Overflow.Hidden;
    public bool ClipsY => OverflowY == Overflow.Clip || OverflowY == Overflow.Hidden || OverflowY == Overflow.Visible && OverflowX == Overflow.Hidden;
    public TransformFn[]? Transform;            // null = none; includes translate, rotate and scale once computed
    public TransformFn[]? Translate, Rotate, Scale; // the individual transform properties; null = none
    public Length TransformOriginX = new(50, LengthUnit.Percent), TransformOriginY = new(50, LengthUnit.Percent);
    public string? Content;                     // the content property as written; null = normal
    public string? StringSet;                   // string-set as written (named strings for running headers); null = none
    public string? PageName;                    // page: the named page this box goes on (inherited; null = auto)
    public CounterOp[]? CounterReset, CounterSet, CounterIncrement;

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
    public bool GridAutoFlowColumn, GridAutoFlowDense;
    public GridLine GridColumnStart, GridColumnEnd, GridRowStart, GridRowEnd;
    public GridAreas? GridTemplateAreas;
    public int ColumnCount;
    public Length ColumnWidth = Length.Auto;
    public BorderStyle ColumnRuleStyle;
    public float ColumnRuleWidth = 1.5f; // medium
    public uint? ColumnRuleColor;        // null = currentColor
    public bool ColumnSpanAll;           // column-span: all
    public int LineClamp;                // -webkit-line-clamp / line-clamp: lines shown, then an ellipsis; 0 = none

    private TextFont? _font;

    /// <summary>The font text is drawn with. Like browsers, letter-spaced text has no ligatures.</summary>
    public TextFont Font
    {
        get
        {
            var f = _font ??= Fonts == null ? StandardFor() : Fonts.Resolve(FontFamilyName, FontFamily, FontWeight, Italic,
                new FontVariation(FontStretch, FontOpticalSizingNone ? 0 : FontSize / 0.75f, FontVariationSettings));
            if ((FontVariantNumeric != null || FontFeatureSettings != null) && FontFeatures.Key(FontVariantNumeric, FontFeatureSettings) is { } features)
                f = f.WithFeatures(features);
            return NoLigatures || LetterSpacing != 0 ? f.WithoutLigatures() : f;
        }
    }

    private TextFont StandardFor()
    {
        MissingFonts?.CheckStandardOnly(FontFamilyName);
        return StandardFont.Get(FontFamily, Bold, Italic);
    }

    /// <summary>The standard PDF font for this style, used where embedded fonts are not supported (SVG text).</summary>
    public StandardFont StandardFont => StandardFont.Get(FontFamily, Bold, Italic);

    public void InvalidateFont() => _font = null;

    public float LineHeight
        => LineHeightKind switch
        {
            LineHeightKind.Factor => LineHeightValue * FontSize,
            LineHeightKind.Absolute => LineHeightValue,
            _ => Font.NormalLineHeight(FontSize),
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

    /// <summary>text-align with start and end resolved for a paragraph of the given direction.</summary>
    public static TextAlign Resolve(TextAlign align, bool rtl) => align switch
    {
        TextAlign.Start => rtl ? TextAlign.Right : TextAlign.Left,
        TextAlign.End => rtl ? TextAlign.Left : TextAlign.Right,
        _ => align,
    };

    /// <summary>Absolutely positioned (absolute or fixed): taken out of the flow and placed against a containing block.</summary>
    public bool OutOfFlow => Position is Position.Absolute or Position.Fixed;

    /// <summary>Positioned (anything but static): a containing block for absolutely positioned descendants.</summary>
    public bool Positioned => Position != Position.Static;

    /// <summary>Painted in the positioned layer: positioned, transformed or translucent.</summary>
    public bool Layered => Position != Position.Static || Transform != null || Opacity < 1 || HasEffects;

    /// <summary>Clipped, blended, isolated or filtered: painted as a layer of its own, which is a stacking context.</summary>
    public bool HasEffects => ClipShape != null || SvgClipPath != null || BlendMode != null || Isolate || Filters != null;

    /// <summary>Creates the starting style for a child: inherited properties copied, the rest reset.</summary>
    public ComputedStyle CreateChild()
    {
        var c = (ComputedStyle)MemberwiseClone();
        c.ParentFontSize = FontSize;
        c.Display = Display.Inline;
        c.BackgroundColor = 0;
        c.BackgroundImages = null;
        c.CanvasBackground = false;
        c.BackgroundRepeats = null;
        c.BackgroundPositionsX = c.BackgroundPositionsY = null;
        c.BackgroundSizes = null;
        c.BackgroundOrigins = c.BackgroundClips = null;
        c.SvgClipPath = null;
        c.SvgMask = c.SvgFilter = null;
        c.SvgMaskAlpha = false;
        c.SvgFloodColor = CssColor.Black;
        c.SvgFloodOpacity = 1;
        c.SvgLightingColor = 0xFFFFFFFFu;
        c.ClipShape = null;
        c.BlendMode = null;
        c.BackgroundBlendModes = null;
        c.Isolate = false;
        c.Filters = null;
        c.MarginTop = c.MarginRight = c.MarginBottom = c.MarginLeft = Length.Zero;
        c.PaddingTop = c.PaddingRight = c.PaddingBottom = c.PaddingLeft = Length.Zero;
        c.BorderTopWidth = c.BorderRightWidth = c.BorderBottomWidth = c.BorderLeftWidth = 1.5f;
        c.BorderTopStyle = c.BorderRightStyle = c.BorderBottomStyle = c.BorderLeftStyle = BorderStyle.None;
        c.BorderTopColor = c.BorderRightColor = c.BorderBottomColor = c.BorderLeftColor = null;
        c.BorderRadii = null;
        c.BorderImageSource = null;
        c.BorderImageSlice = c.BorderImageWidths = c.BorderImageOutsets = null;
        c.BorderImageFill = false;
        c.BorderImageRepeatX = c.BorderImageRepeatY = BorderImageRepeat.Stretch;
        c.OutlineStyle = BorderStyle.None;
        c.OutlineWidth = 1.5f;
        c.OutlineOffset = 0;
        c.OutlineColor = null;
        c.ObjectFit = ObjectFit.Fill;
        c.ObjectPositionX = c.ObjectPositionY = BackgroundOffset.Center;
        c.TextOverflow = null;
        c.AspectRatio = 0;
        c.AspectRatioAuto = false;
        c.ContainerType = ContainerType.Normal;
        c.ContainerNames = null;
        c.Width = c.Height = Length.Auto;
        c.MinWidth = Length.Auto; // min-width: auto, which differs from 0 for boxes with aspect-ratio
        c.MinHeight = Length.Zero;
        c.MaxWidth = c.MaxHeight = Length.Auto;
        c.BorderBox = false;
        c.VerticalAlign = VerticalAlign.Baseline;
        c.PageBreakBefore = c.PageBreakAfter = c.AvoidBreakInside = false;
        c.AvoidBreakBefore = c.AvoidBreakAfter = false;
        c.Floated = false;
        c.Float = FloatSide.None;
        c.ClearLeft = c.ClearRight = false;
        c.Opacity = 1;
        c.Position = Position.Static;
        c.Top = c.Right = c.Bottom = c.Left = Length.Auto;
        c.ZIndex = null;
        c.FormattingRoot = false;
        c.OverflowX = c.OverflowY = Overflow.Visible;
        c.Transform = null;
        c.Translate = c.Rotate = c.Scale = null;
        c.TransformOriginX = c.TransformOriginY = new Length(50, LengthUnit.Percent);
        c.Content = null;
        c.CounterReset = c.CounterSet = c.CounterIncrement = null;
        c.FlexDirection = FlexDirection.Row;
        c.UnicodeBidi = UnicodeBidi.Normal;
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
        c.GridAutoFlowColumn = c.GridAutoFlowDense = false;
        c.GridColumnStart = c.GridColumnEnd = c.GridRowStart = c.GridRowEnd = default;
        c.GridTemplateAreas = null;
        c.ColumnCount = 0;
        c.ColumnWidth = Length.Auto;
        c.ColumnRuleStyle = BorderStyle.None;
        c.ColumnRuleWidth = 1.5f;
        c.ColumnRuleColor = null;
        c.ColumnSpanAll = false;
        c.LineClamp = 0;
        c.StringSet = null;
        c.TableLayoutFixed = false;
        c.BoxShadows = null;
        return c;
    }

    public ComputedStyle Clone() => (ComputedStyle)MemberwiseClone();
}
