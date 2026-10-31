namespace PdfPecker.Css;

// Most boxes leave most properties at their initial values, and most children inherit their parent's values
// unchanged. The rarely set properties therefore live in groups that styles share: a child points at its parent's
// inherited groups, and at one initial instance of each group of properties that are not inherited. A group is copied
// the first time a style changes a value in it, so a style is a fraction of its full size and creating one copies
// little. This is the same split Chrome makes in its computed styles.
internal sealed partial class ComputedStyle
{
    private TypographyFields _typography = TypographyFields.Initial;
    private DecorationFields _decoration = DecorationFields.Initial;
    private SvgPaintFields _svgPaint = SvgPaintFields.Initial;
    private VisualFields _visual = VisualFields.Initial;
    private ContainersFields _containers = ContainersFields.Initial;
    private RareFields _rare = RareFields.Initial;

    // Marks the inherited groups as shared before a copy of the style takes them, so that the first change on either
    // side copies the group instead of changing it under the other.
    private void ShareInheritedGroups()
    {
        if (!_typography.Shared) _typography.Shared = true;
        if (!_decoration.Shared) _decoration.Shared = true;
        if (!_svgPaint.Shared) _svgPaint.Shared = true;
    }

    private void ShareGroups()
    {
        if (!_visual.Shared) _visual.Shared = true;
        if (!_containers.Shared) _containers.Shared = true;
        if (!_rare.Shared) _rare.Shared = true;
    }

    // A child starts with the initial values of the properties that are not inherited.
    private void ResetGroups()
    {
        _visual = VisualFields.Initial;
        _containers = ContainersFields.Initial;
        _rare = RareFields.Initial;
    }

    // ---- Inherited text and font properties that most text leaves at their initial values.

    public string? FontVariationSettings { get => _typography.FontVariationSettings; set => Typography.FontVariationSettings = value; }
    public bool FontOpticalSizingNone { get => _typography.FontOpticalSizingNone; set => Typography.FontOpticalSizingNone = value; }
    public TextAlign? TextAlignLast { get => _typography.TextAlignLast; set => Typography.TextAlignLast = value; }
    public TextJustify TextJustify { get => _typography.TextJustify; set => Typography.TextJustify = value; }
    public TextSpacingTrim TextSpacingTrim { get => _typography.TextSpacingTrim; set => Typography.TextSpacingTrim = value; }
    public TextAlign? BlockAlign { get => _typography.BlockAlign; set => Typography.BlockAlign = value; }
    public int TabSize { get => _typography.TabSize; set => Typography.TabSize = value; }
    public string[]? Quotes { get => _typography.Quotes; set => Typography.Quotes = value; }
    public string? HyphenateCharacter { get => _typography.HyphenateCharacter; set => Typography.HyphenateCharacter = value; }
    public string? BlockEllipsis { get => _typography.BlockEllipsis; set => Typography.BlockEllipsis = value; }
    public HyphenateLimits HyphenateLimitChars { get => _typography.HyphenateLimitChars; set => Typography.HyphenateLimitChars = value; }
    public LineBreak LineBreak { get => _typography.LineBreak; set => Typography.LineBreak = value; }
    public string? ListStyleString { get => _typography.ListStyleString; set => Typography.ListStyleString = value; }
    public CounterStyle? ListCounterStyle { get => _typography.ListCounterStyle; set => Typography.ListCounterStyle = value; }
    public string? ListStyleImage { get => _typography.ListStyleImage; set => Typography.ListStyleImage = value; }
    public float ListStyleImageResolution { get => _typography.ListStyleImageResolution; set => Typography.ListStyleImageResolution = value; }
    public CssGradient? ListStyleGradient { get => _typography.ListStyleGradient; set => Typography.ListStyleGradient = value; }
    public bool TextIndentHanging { get => _typography.TextIndentHanging; set => Typography.TextIndentHanging = value; }
    public bool TextIndentEachLine { get => _typography.TextIndentEachLine; set => Typography.TextIndentEachLine = value; }
    public FontSynthesis Synthesis { get => _typography.Synthesis; set => Typography.Synthesis = value; }
    public FontSizeAdjust SizeAdjust { get => _typography.SizeAdjust; set => Typography.SizeAdjust = value; }
    public string? FontVariantEastAsian { get => _typography.FontVariantEastAsian; set => Typography.FontVariantEastAsian = value; }
    public string? FontVariantPosition { get => _typography.FontVariantPosition; set => Typography.FontVariantPosition = value; }
    public string? FontVariantNumeric { get => _typography.FontVariantNumeric; set => Typography.FontVariantNumeric = value; }
    public string? FontFeatureSettings { get => _typography.FontFeatureSettings; set => Typography.FontFeatureSettings = value; }
    public bool CaptionBottom { get => _typography.CaptionBottom; set => Typography.CaptionBottom = value; }
    public bool EmptyCellsHide { get => _typography.EmptyCellsHide; set => Typography.EmptyCellsHide = value; }
    public bool ImageOrientationNone { get => _typography.ImageOrientationNone; set => Typography.ImageOrientationNone = value; }
    public TextEdgeOver TextEdgeOver { get => _typography.TextEdgeOver; set => Typography.TextEdgeOver = value; }
    public TextEdgeUnder TextEdgeUnder { get => _typography.TextEdgeUnder; set => Typography.TextEdgeUnder = value; }
    public int Orphans { get => _typography.Orphans; set => Typography.Orphans = value; }
    public int Widows { get => _typography.Widows; set => Typography.Widows = value; }

    // The group to change a value in, copied first when other styles share it.
    private TypographyFields Typography => _typography.Shared ? _typography = _typography.Copy() : _typography;

    private sealed class TypographyFields
    {
        public static readonly TypographyFields Initial = new() { Shared = true };

        public bool Shared;
        public string? FontVariationSettings;    // font-variation-settings, as written; null = normal
        public bool FontOpticalSizingNone;       // font-optical-sizing: none
        public TextAlign? TextAlignLast;         // null = auto
        public TextJustify TextJustify;
        public TextSpacingTrim TextSpacingTrim;
        public TextAlign? BlockAlign;            // -webkit-left, -webkit-center or -webkit-right: also aligns child blocks
        public int TabSize = 8;                  // in spaces
        public string[]? Quotes;                 // quotes: open and close strings of each nesting level; null = auto (by language), empty = none
        public string? HyphenateCharacter;       // hyphenate-character: "string"; null = auto (a hyphen-minus)
        public string? BlockEllipsis;            // block-ellipsis: the mark ending a clamped block; null = auto ("…"), "" = none
        public HyphenateLimits HyphenateLimitChars; // hyphenate-limit-chars; each value 0 = auto
        public LineBreak LineBreak;              // line-break: how strictly Chinese and Japanese text breaks
        public string? ListStyleString;          // list-style-type: "string": the marker text
        public CounterStyle? ListCounterStyle;   // the counter style of a text marker; null for the bullets, a string and none
        public string? ListStyleImage;           // list-style-image: the url
        public float ListStyleImageResolution = 1; // list-style-image: image pixels per CSS pixel, from image-set()
        public CssGradient? ListStyleGradient;   // list-style-image: a gradient
        public bool TextIndentHanging;      // text-indent: hanging, every line but the first indented
        public bool TextIndentEachLine;     // text-indent: each-line, lines after forced breaks too
        public FontSynthesis Synthesis = FontSynthesis.All; // font-synthesis: the faces the browser may make up
        public FontSizeAdjust SizeAdjust;    // font-size-adjust; none by default
        public string? FontVariantEastAsian; // font-variant-east-asian keywords; null = normal
        public string? FontVariantPosition;  // font-variant-position: sub or super; null = normal
        public string? FontVariantNumeric;   // font-variant-numeric keywords; null = normal
        public string? FontFeatureSettings;  // font-feature-settings as written; null = normal
        public bool CaptionBottom;    // caption-side: bottom (inherited)
        public bool EmptyCellsHide;   // empty-cells: hide (inherited): with separated borders, empty cells paint nothing
        public bool ImageOrientationNone; // image-orientation: none (inherited): photos are drawn as stored, ignoring EXIF
        public TextEdgeOver TextEdgeOver;   // text-box-edge, over the text (inherited)
        public TextEdgeUnder TextEdgeUnder; // text-box-edge, under the text (inherited)
        public int Orphans = 2, Widows = 2; // minimum lines of a paragraph left at the bottom / carried to the top of a page

        public TypographyFields Copy()
        {
            var c = (TypographyFields)MemberwiseClone();
            c.Shared = false;
            return c;
        }
    }

    // ---- Inherited decoration, emphasis, shadow and stroke properties of text.

    public uint? DecorationColor { get => _decoration.DecorationColor; set => Decoration.DecorationColor = value; }
    public DecorationStyle DecorationStyle { get => _decoration.DecorationStyle; set => Decoration.DecorationStyle = value; }
    public Length? DecorationThickness { get => _decoration.DecorationThickness; set => Decoration.DecorationThickness = value; }
    public Length? UnderlineOffset { get => _decoration.UnderlineOffset; set => Decoration.UnderlineOffset = value; }
    public SkipInk SkipInk { get => _decoration.SkipInk; set => Decoration.SkipInk = value; }
    public UnderlinePosition UnderlinePosition { get => _decoration.UnderlinePosition; set => Decoration.UnderlinePosition = value; }
    public EmphasisShape EmphasisShape { get => _decoration.EmphasisShape; set => Decoration.EmphasisShape = value; }
    public bool EmphasisOpen { get => _decoration.EmphasisOpen; set => Decoration.EmphasisOpen = value; }
    public string? EmphasisString { get => _decoration.EmphasisString; set => Decoration.EmphasisString = value; }
    public uint? EmphasisColor { get => _decoration.EmphasisColor; set => Decoration.EmphasisColor = value; }
    public bool EmphasisUnder { get => _decoration.EmphasisUnder; set => Decoration.EmphasisUnder = value; }
    public BoxShadow[]? TextShadows { get => _decoration.TextShadows; set => Decoration.TextShadows = value; }
    public float TextStrokeWidth { get => _decoration.TextStrokeWidth; set => Decoration.TextStrokeWidth = value; }
    public uint? TextFillColor { get => _decoration.TextFillColor; set => Decoration.TextFillColor = value; }
    public uint? TextStrokeColor { get => _decoration.TextStrokeColor; set => Decoration.TextStrokeColor = value; }
    public bool PaintStrokeFirst { get => _decoration.PaintStrokeFirst; set => Decoration.PaintStrokeFirst = value; }

    // The group to change a value in, copied first when other styles share it.
    private DecorationFields Decoration => _decoration.Shared ? _decoration = _decoration.Copy() : _decoration;

    private sealed class DecorationFields
    {
        public static readonly DecorationFields Initial = new() { Shared = true };

        public bool Shared;
        // text-decoration-color, -style, -thickness and text-underline-offset, propagated with the decoration. The colour is
        // fixed to the decorating box's color when it sets the line, as Chrome draws a link's underline in the link colour.
        public uint? DecorationColor;          // null = currentColor
        public DecorationStyle DecorationStyle;
        public Length? DecorationThickness;    // null = auto; percentages of 1em
        public Length? UnderlineOffset;        // null = auto; from the alphabetic baseline
        public SkipInk SkipInk;                // text-decoration-skip-ink, inherited
        public UnderlinePosition UnderlinePosition; // text-underline-position, inherited
        public EmphasisShape EmphasisShape;    // text-emphasis-style, inherited
        public bool EmphasisOpen;              // text-emphasis-style: open, hollow marks
        public string? EmphasisString;         // text-emphasis-style: a string, the mark itself
        public uint? EmphasisColor;            // text-emphasis-color; null = currentColor
        public bool EmphasisUnder;             // text-emphasis-position: under the line instead of over it
        public BoxShadow[]? TextShadows; // text-shadow (inherited); spread and inset are not used
        public float TextStrokeWidth;    // -webkit-text-stroke-width (inherited): the outline drawn on the glyphs, centred on their edge
        public uint? TextFillColor;      // -webkit-text-fill-color (inherited): the glyph fill instead of color; null = currentcolor
        public uint? TextStrokeColor;    // -webkit-text-stroke-color (inherited); null = currentcolor
        public bool PaintStrokeFirst;    // paint-order: stroke (inherited): the fill covers the inner half of the stroke

        public DecorationFields Copy()
        {
            var c = (DecorationFields)MemberwiseClone();
            c.Shared = false;
            return c;
        }
    }

    // ---- The inherited SVG presentation properties.

    public uint? SvgFill { get => _svgPaint.SvgFill; set => SvgPaint.SvgFill = value; }
    public string? SvgFillUrl { get => _svgPaint.SvgFillUrl; set => SvgPaint.SvgFillUrl = value; }
    public uint? SvgStroke { get => _svgPaint.SvgStroke; set => SvgPaint.SvgStroke = value; }
    public string? SvgStrokeUrl { get => _svgPaint.SvgStrokeUrl; set => SvgPaint.SvgStrokeUrl = value; }
    public float SvgStrokeWidth { get => _svgPaint.SvgStrokeWidth; set => SvgPaint.SvgStrokeWidth = value; }
    public float SvgFillOpacity { get => _svgPaint.SvgFillOpacity; set => SvgPaint.SvgFillOpacity = value; }
    public float SvgStrokeOpacity { get => _svgPaint.SvgStrokeOpacity; set => SvgPaint.SvgStrokeOpacity = value; }
    public bool SvgEvenOdd { get => _svgPaint.SvgEvenOdd; set => SvgPaint.SvgEvenOdd = value; }
    public SvgLineCap SvgLineCap { get => _svgPaint.SvgLineCap; set => SvgPaint.SvgLineCap = value; }
    public SvgLineJoin SvgLineJoin { get => _svgPaint.SvgLineJoin; set => SvgPaint.SvgLineJoin = value; }
    public float SvgMiterLimit { get => _svgPaint.SvgMiterLimit; set => SvgPaint.SvgMiterLimit = value; }
    public SvgDashArray? SvgDashArray { get => _svgPaint.SvgDashArray; set => SvgPaint.SvgDashArray = value; }
    public float SvgDashOffset { get => _svgPaint.SvgDashOffset; set => SvgPaint.SvgDashOffset = value; }
    public bool SvgDashOffsetPercent { get => _svgPaint.SvgDashOffsetPercent; set => SvgPaint.SvgDashOffsetPercent = value; }
    public bool SvgClipEvenOdd { get => _svgPaint.SvgClipEvenOdd; set => SvgPaint.SvgClipEvenOdd = value; }
    public byte SvgTextAnchor { get => _svgPaint.SvgTextAnchor; set => SvgPaint.SvgTextAnchor = value; }
    public byte SvgBaseline { get => _svgPaint.SvgBaseline; set => SvgPaint.SvgBaseline = value; }
    public string? SvgMarkerStart { get => _svgPaint.SvgMarkerStart; set => SvgPaint.SvgMarkerStart = value; }
    public string? SvgMarkerMid { get => _svgPaint.SvgMarkerMid; set => SvgPaint.SvgMarkerMid = value; }
    public string? SvgMarkerEnd { get => _svgPaint.SvgMarkerEnd; set => SvgPaint.SvgMarkerEnd = value; }
    public bool SvgFilterSrgb { get => _svgPaint.SvgFilterSrgb; set => SvgPaint.SvgFilterSrgb = value; }

    // The group to change a value in, copied first when other styles share it.
    private SvgPaintFields SvgPaint => _svgPaint.Shared ? _svgPaint = _svgPaint.Copy() : _svgPaint;

    private sealed class SvgPaintFields
    {
        public static readonly SvgPaintFields Initial = new() { Shared = true };

        public bool Shared;
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
        public SvgDashArray? SvgDashArray;       // stroke-dasharray; null = none
        public float SvgDashOffset;              // stroke-dashoffset, in user units or a percentage
        public bool SvgDashOffsetPercent;
        public bool SvgClipEvenOdd;              // clip-rule
        public byte SvgTextAnchor;               // 0 start, 1 middle, 2 end
        public byte SvgBaseline;                 // dominant-baseline: 0 alphabetic, 1 middle/central, 2 hanging, 3 text-after-edge
        public string? SvgMarkerStart, SvgMarkerMid, SvgMarkerEnd; // marker-start/mid/end: url(#id), as "#id"
        public bool SvgFilterSrgb;               // color-interpolation-filters: sRGB (else linearRGB)

        public SvgPaintFields Copy()
        {
            var c = (SvgPaintFields)MemberwiseClone();
            c.Shared = false;
            return c;
        }
    }

    // ---- Background layers, masks, effects, border images, outlines and shadows: not inherited, and unset on most boxes.

    public BoxShadow[]? BoxShadows { get => _visual.BoxShadows; set => Visual.BoxShadows = value; }
    public BackgroundImage?[]? BackgroundImages { get => _visual.BackgroundImages; set => Visual.BackgroundImages = value; }
    public (BackgroundRepeat X, BackgroundRepeat Y)[]? BackgroundRepeats { get => _visual.BackgroundRepeats; set => Visual.BackgroundRepeats = value; }
    public BackgroundOffset[]? BackgroundPositionsX { get => _visual.BackgroundPositionsX; set => Visual.BackgroundPositionsX = value; }
    public BackgroundOffset[]? BackgroundPositionsY { get => _visual.BackgroundPositionsY; set => Visual.BackgroundPositionsY = value; }
    public BackgroundSize[]? BackgroundSizes { get => _visual.BackgroundSizes; set => Visual.BackgroundSizes = value; }
    public BackgroundBox[]? BackgroundOrigins { get => _visual.BackgroundOrigins; set => Visual.BackgroundOrigins = value; }
    public BackgroundBox[]? BackgroundClips { get => _visual.BackgroundClips; set => Visual.BackgroundClips = value; }
    public bool[]? BackgroundFixed { get => _visual.BackgroundFixed; set => Visual.BackgroundFixed = value; }
    public string? SvgClipPath { get => _visual.SvgClipPath; set => Visual.SvgClipPath = value; }
    public string? SvgMask { get => _visual.SvgMask; set => Visual.SvgMask = value; }
    public string? SvgFilter { get => _visual.SvgFilter; set => Visual.SvgFilter = value; }
    public bool SvgMaskAlpha { get => _visual.SvgMaskAlpha; set => Visual.SvgMaskAlpha = value; }
    public uint SvgFloodColor { get => _visual.SvgFloodColor; set => Visual.SvgFloodColor = value; }
    public float SvgFloodOpacity { get => _visual.SvgFloodOpacity; set => Visual.SvgFloodOpacity = value; }
    public uint SvgLightingColor { get => _visual.SvgLightingColor; set => Visual.SvgLightingColor = value; }
    public ClipShape? ClipShape { get => _visual.ClipShape; set => Visual.ClipShape = value; }
    public Length[]? ClipRect { get => _visual.ClipRect; set => Visual.ClipRect = value; }
    public string? BlendMode { get => _visual.BlendMode; set => Visual.BlendMode = value; }
    public string?[]? BackgroundBlendModes { get => _visual.BackgroundBlendModes; set => Visual.BackgroundBlendModes = value; }
    public bool Isolate { get => _visual.Isolate; set => Visual.Isolate = value; }
    public bool BoxDecorationClone { get => _visual.BoxDecorationClone; set => Visual.BoxDecorationClone = value; }
    public bool ScaleToFit { get => _visual.ScaleToFit; set => Visual.ScaleToFit = value; }
    public FilterFunction[]? Filters { get => _visual.Filters; set => Visual.Filters = value; }
    public FilterFunction[]? BackdropFilters { get => _visual.BackdropFilters; set => Visual.BackdropFilters = value; }
    public BackgroundImage?[]? MaskImages { get => _visual.MaskImages; set => Visual.MaskImages = value; }
    public (BackgroundRepeat X, BackgroundRepeat Y)[]? MaskRepeats { get => _visual.MaskRepeats; set => Visual.MaskRepeats = value; }
    public BackgroundOffset[]? MaskPositionsX { get => _visual.MaskPositionsX; set => Visual.MaskPositionsX = value; }
    public BackgroundOffset[]? MaskPositionsY { get => _visual.MaskPositionsY; set => Visual.MaskPositionsY = value; }
    public BackgroundSize[]? MaskSizes { get => _visual.MaskSizes; set => Visual.MaskSizes = value; }
    public BackgroundBox[]? MaskOrigins { get => _visual.MaskOrigins; set => Visual.MaskOrigins = value; }
    public BackgroundBox[]? MaskClips { get => _visual.MaskClips; set => Visual.MaskClips = value; }
    public MaskMode[]? MaskModes { get => _visual.MaskModes; set => Visual.MaskModes = value; }
    public MaskComposite[]? MaskComposites { get => _visual.MaskComposites; set => Visual.MaskComposites = value; }
    public BackgroundImage? BorderImageSource { get => _visual.BorderImageSource; set => Visual.BorderImageSource = value; }
    public BorderImageValue[]? BorderImageSlice { get => _visual.BorderImageSlice; set => Visual.BorderImageSlice = value; }
    public bool BorderImageFill { get => _visual.BorderImageFill; set => Visual.BorderImageFill = value; }
    public BorderImageValue[]? BorderImageWidths { get => _visual.BorderImageWidths; set => Visual.BorderImageWidths = value; }
    public BorderImageValue[]? BorderImageOutsets { get => _visual.BorderImageOutsets; set => Visual.BorderImageOutsets = value; }
    public BorderImageRepeat BorderImageRepeatX { get => _visual.BorderImageRepeatX; set => Visual.BorderImageRepeatX = value; }
    public BorderImageRepeat BorderImageRepeatY { get => _visual.BorderImageRepeatY; set => Visual.BorderImageRepeatY = value; }
    public BorderStyle OutlineStyle { get => _visual.OutlineStyle; set => Visual.OutlineStyle = value; }
    public bool OutlineAuto { get => _visual.OutlineAuto; set => Visual.OutlineAuto = value; }
    public float OutlineWidth { get => _visual.OutlineWidth; set => Visual.OutlineWidth = value; }
    public float OutlineOffset { get => _visual.OutlineOffset; set => Visual.OutlineOffset = value; }
    public uint? OutlineColor { get => _visual.OutlineColor; set => Visual.OutlineColor = value; }
    public ObjectFit ObjectFit { get => _visual.ObjectFit; set => Visual.ObjectFit = value; }
    public BackgroundOffset ObjectPositionX { get => _visual.ObjectPositionX; set => Visual.ObjectPositionX = value; }
    public BackgroundOffset ObjectPositionY { get => _visual.ObjectPositionY; set => Visual.ObjectPositionY = value; }

    // The group to change a value in, copied first when other styles share it.
    private VisualFields Visual => _visual.Shared ? _visual = _visual.Copy() : _visual;

    private sealed class VisualFields
    {
        public static readonly VisualFields Initial = new() { Shared = true };

        public bool Shared;
        public BoxShadow[]? BoxShadows;
        // Background layers, topmost first. Each list repeats when it is shorter than BackgroundImages; null is the initial value.
        public BackgroundImage?[]? BackgroundImages; // null entries are "none" layers
        public (BackgroundRepeat X, BackgroundRepeat Y)[]? BackgroundRepeats;
        public BackgroundOffset[]? BackgroundPositionsX, BackgroundPositionsY;
        public BackgroundSize[]? BackgroundSizes;
        public BackgroundBox[]? BackgroundOrigins, BackgroundClips;
        public bool[]? BackgroundFixed; // background-attachment: fixed per layer; null when every layer scrolls
        public string? SvgClipPath;                 // clip-path: url(#id), as "#id"
        public string? SvgMask, SvgFilter;          // mask and filter: url(#id), as "#id"
        public bool SvgMaskAlpha;                   // mask-type: alpha (else luminance)
        public uint SvgFloodColor = CssColor.Black; // flood-color
        public float SvgFloodOpacity = 1;           // flood-opacity
        public uint SvgLightingColor = 0xFFFFFFFFu; // lighting-color
        public ClipShape? ClipShape;                // clip-path: a basic shape or a reference box, on HTML elements
        public Length[]? ClipRect;                  // clip: rect(top, right, bottom, left) from the border box's corner, auto sides as auto
        public string? BlendMode;                   // mix-blend-mode, as a PDF blend mode name; null = normal
        public string?[]? BackgroundBlendModes;     // background-blend-mode per layer, as PDF names (null = normal); null = all normal
        public bool Isolate;                        // isolation: isolate
        public bool BoxDecorationClone;             // box-decoration-break: clone (else slice)
        public bool ScaleToFit;                     // -pdfpecker-scale-to-fit: shrink
        public FilterFunction[]? Filters;           // filter functions on HTML elements; null = none
        public FilterFunction[]? BackdropFilters;   // backdrop-filter functions; null = none
        // Mask layers on HTML elements, topmost first, repeated like the background lists; null MaskImages is mask-image: none.
        public BackgroundImage?[]? MaskImages;      // null entries are "none" layers
        public (BackgroundRepeat X, BackgroundRepeat Y)[]? MaskRepeats;
        public BackgroundOffset[]? MaskPositionsX, MaskPositionsY;
        public BackgroundSize[]? MaskSizes;
        public BackgroundBox[]? MaskOrigins, MaskClips; // null = border-box
        public MaskMode[]? MaskModes;
        public MaskComposite[]? MaskComposites;
        // border-image. Null slices, widths and outsets are the initial values: 100%, 1 and 0 on every side.
        public BackgroundImage? BorderImageSource;
        public BorderImageValue[]? BorderImageSlice; // top, right, bottom, left
        public bool BorderImageFill;
        public BorderImageValue[]? BorderImageWidths, BorderImageOutsets;
        public BorderImageRepeat BorderImageRepeatX, BorderImageRepeatY;
        public BorderStyle OutlineStyle;
        /// <summary>outline-style: auto, drawn as a solid focus ring of its own width.</summary>
        public bool OutlineAuto;
        public float OutlineWidth = 1.5f, OutlineOffset;
        public uint? OutlineColor; // null = currentColor
        public ObjectFit ObjectFit;
        public BackgroundOffset ObjectPositionX = BackgroundOffset.Center, ObjectPositionY = BackgroundOffset.Center;

        public VisualFields Copy()
        {
            var c = (VisualFields)MemberwiseClone();
            c.Shared = false;
            return c;
        }
    }

    // ---- Flexbox, grid, multi-column and legacy box properties: not inherited, and unset on most boxes.

    public FlexDirection FlexDirection { get => _containers.FlexDirection; set => Containers.FlexDirection = value; }
    public FlexWrap FlexWrap { get => _containers.FlexWrap; set => Containers.FlexWrap = value; }
    public float FlexGrow { get => _containers.FlexGrow; set => Containers.FlexGrow = value; }
    public float FlexShrink { get => _containers.FlexShrink; set => Containers.FlexShrink = value; }
    public Length FlexBasis { get => _containers.FlexBasis; set => Containers.FlexBasis = value; }
    public int Order { get => _containers.Order; set => Containers.Order = value; }
    public BoxAlign JustifyContent { get => _containers.JustifyContent; set => Containers.JustifyContent = value; }
    public BoxAlign AlignItems { get => _containers.AlignItems; set => Containers.AlignItems = value; }
    public BoxAlign AlignContent { get => _containers.AlignContent; set => Containers.AlignContent = value; }
    public BoxAlign JustifyItems { get => _containers.JustifyItems; set => Containers.JustifyItems = value; }
    public BoxAlign AlignSelf { get => _containers.AlignSelf; set => Containers.AlignSelf = value; }
    public BoxAlign JustifySelf { get => _containers.JustifySelf; set => Containers.JustifySelf = value; }
    public Length? RowGap { get => _containers.RowGap; set => Containers.RowGap = value; }
    public Length? ColumnGap { get => _containers.ColumnGap; set => Containers.ColumnGap = value; }
    public TrackList? GridTemplateColumns { get => _containers.GridTemplateColumns; set => Containers.GridTemplateColumns = value; }
    public TrackList? GridTemplateRows { get => _containers.GridTemplateRows; set => Containers.GridTemplateRows = value; }
    public Track[]? GridAutoColumns { get => _containers.GridAutoColumns; set => Containers.GridAutoColumns = value; }
    public Track[]? GridAutoRows { get => _containers.GridAutoRows; set => Containers.GridAutoRows = value; }
    public bool GridAutoFlowColumn { get => _containers.GridAutoFlowColumn; set => Containers.GridAutoFlowColumn = value; }
    public bool GridAutoFlowDense { get => _containers.GridAutoFlowDense; set => Containers.GridAutoFlowDense = value; }
    public GridLine GridColumnStart { get => _containers.GridColumnStart; set => Containers.GridColumnStart = value; }
    public GridLine GridColumnEnd { get => _containers.GridColumnEnd; set => Containers.GridColumnEnd = value; }
    public GridLine GridRowStart { get => _containers.GridRowStart; set => Containers.GridRowStart = value; }
    public GridLine GridRowEnd { get => _containers.GridRowEnd; set => Containers.GridRowEnd = value; }
    public GridAreas? GridTemplateAreas { get => _containers.GridTemplateAreas; set => Containers.GridTemplateAreas = value; }
    public int ColumnCount { get => _containers.ColumnCount; set => Containers.ColumnCount = value; }
    public Length ColumnWidth { get => _containers.ColumnWidth; set => Containers.ColumnWidth = value; }
    public BorderStyle ColumnRuleStyle { get => _containers.ColumnRuleStyle; set => Containers.ColumnRuleStyle = value; }
    public float ColumnRuleWidth { get => _containers.ColumnRuleWidth; set => Containers.ColumnRuleWidth = value; }
    public uint? ColumnRuleColor { get => _containers.ColumnRuleColor; set => Containers.ColumnRuleColor = value; }
    public bool ColumnSpanAll { get => _containers.ColumnSpanAll; set => Containers.ColumnSpanAll = value; }
    public bool ColumnFillAuto { get => _containers.ColumnFillAuto; set => Containers.ColumnFillAuto = value; }
    public int LineClamp { get => _containers.LineClamp; set => Containers.LineClamp = value; }
    public bool LegacyBox { get => _containers.LegacyBox; set => Containers.LegacyBox = value; }
    public bool BoxVertical { get => _containers.BoxVertical; set => Containers.BoxVertical = value; }
    public BoxAlign BoxPack { get => _containers.BoxPack; set => Containers.BoxPack = value; }
    public BoxAlign BoxAlignItems { get => _containers.BoxAlignItems; set => Containers.BoxAlignItems = value; }
    public float BoxFlex { get => _containers.BoxFlex; set => Containers.BoxFlex = value; }
    public int BoxOrdinalGroup { get => _containers.BoxOrdinalGroup; set => Containers.BoxOrdinalGroup = value; }

    // The group to change a value in, copied first when other styles share it.
    private ContainersFields Containers => _containers.Shared ? _containers = _containers.Copy() : _containers;

    private sealed class ContainersFields
    {
        public static readonly ContainersFields Initial = new() { Shared = true };

        public bool Shared;
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
        public bool ColumnFillAuto;          // column-fill: auto (fill each column in turn) rather than balance
        public int LineClamp;                // -webkit-line-clamp / line-clamp: lines shown, then an ellipsis; 0 = none
        // The legacy flexible box (display: -webkit-box) and its -webkit-box- properties, which Chrome lays out as a flex
        // container in place of the flex properties. -webkit-box-direction is inherited, the others are not.
        public bool LegacyBox;
        public bool BoxVertical;
        public BoxAlign BoxPack = BoxAlign.Start, BoxAlignItems = BoxAlign.Stretch;
        public float BoxFlex;
        public int BoxOrdinalGroup = 1;

        public ContainersFields Copy()
        {
            var c = (ContainersFields)MemberwiseClone();
            c.Shared = false;
            return c;
        }
    }

    // ---- Other properties that are not inherited and that few boxes set: transforms, generated content, counters, bookmarks, shapes, initial letters, footnotes and container queries.

    public bool TableLayoutFixed { get => _rare.TableLayoutFixed; set => Rare.TableLayoutFixed = value; }
    public string? TextOverflow { get => _rare.TextOverflow; set => Rare.TextOverflow = value; }
    public TextBoxTrim TextBoxTrim { get => _rare.TextBoxTrim; set => Rare.TextBoxTrim = value; }
    public float AspectRatio { get => _rare.AspectRatio; set => Rare.AspectRatio = value; }
    public bool AspectRatioAuto { get => _rare.AspectRatioAuto; set => Rare.AspectRatioAuto = value; }
    public ContainerType ContainerType { get => _rare.ContainerType; set => Rare.ContainerType = value; }
    public string[]? ContainerNames { get => _rare.ContainerNames; set => Rare.ContainerNames = value; }
    public Length ControlWidth { get => _rare.ControlWidth; set => Rare.ControlWidth = value; }
    public SizeKeyword WidthKeyword { get => _rare.WidthKeyword; set => Rare.WidthKeyword = value; }
    public StretchSizes Stretched { get => _rare.Stretched; set => Rare.Stretched = value; }
    public bool ColumnBreakBefore { get => _rare.ColumnBreakBefore; set => Rare.ColumnBreakBefore = value; }
    public bool ColumnBreakAfter { get => _rare.ColumnBreakAfter; set => Rare.ColumnBreakAfter = value; }
    public bool AvoidColumnBreakBefore { get => _rare.AvoidColumnBreakBefore; set => Rare.AvoidColumnBreakBefore = value; }
    public bool AvoidColumnBreakAfter { get => _rare.AvoidColumnBreakAfter; set => Rare.AvoidColumnBreakAfter = value; }
    public bool AvoidColumnBreakInside { get => _rare.AvoidColumnBreakInside; set => Rare.AvoidColumnBreakInside = value; }
    public ShapeOutside? ShapeOutside { get => _rare.ShapeOutside; set => Rare.ShapeOutside = value; }
    public Length ShapeMargin { get => _rare.ShapeMargin; set => Rare.ShapeMargin = value; }
    public float ShapeImageThreshold { get => _rare.ShapeImageThreshold; set => Rare.ShapeImageThreshold = value; }
    public ShapeBox OverflowClipBox { get => _rare.OverflowClipBox; set => Rare.OverflowClipBox = value; }
    public float OverflowClipMargin { get => _rare.OverflowClipMargin; set => Rare.OverflowClipMargin = value; }
    public float InitialLetterSize { get => _rare.InitialLetterSize; set => Rare.InitialLetterSize = value; }
    public int InitialLetterSink { get => _rare.InitialLetterSink; set => Rare.InitialLetterSink = value; }
    public bool InitialLetterSunk { get => _rare.InitialLetterSunk; set => Rare.InitialLetterSunk = value; }
    public bool Footnote { get => _rare.Footnote; set => Rare.Footnote = value; }
    public FootnoteDisplay FootnoteDisplay { get => _rare.FootnoteDisplay; set => Rare.FootnoteDisplay = value; }
    public FootnotePolicy FootnotePolicy { get => _rare.FootnotePolicy; set => Rare.FootnotePolicy = value; }
    public int? ZIndex { get => _rare.ZIndex; set => Rare.ZIndex = value; }
    public TransformFn[]? Transform { get => _rare.Transform; set => Rare.Transform = value; }
    public TransformFn[]? Translate { get => _rare.Translate; set => Rare.Translate = value; }
    public TransformFn[]? Rotate { get => _rare.Rotate; set => Rare.Rotate = value; }
    public TransformFn[]? Scale { get => _rare.Scale; set => Rare.Scale = value; }
    public Length TransformOriginX { get => _rare.TransformOriginX; set => Rare.TransformOriginX = value; }
    public Length TransformOriginY { get => _rare.TransformOriginY; set => Rare.TransformOriginY = value; }
    public float? Perspective { get => _rare.Perspective; set => Rare.Perspective = value; }
    public Length PerspectiveOriginX { get => _rare.PerspectiveOriginX; set => Rare.PerspectiveOriginX = value; }
    public Length PerspectiveOriginY { get => _rare.PerspectiveOriginY; set => Rare.PerspectiveOriginY = value; }
    public string? Content { get => _rare.Content; set => Rare.Content = value; }
    public bool ContentNone { get => _rare.ContentNone; set => Rare.ContentNone = value; }
    public string? StringSet { get => _rare.StringSet; set => Rare.StringSet = value; }
    public int? BookmarkLevel { get => _rare.BookmarkLevel; set => Rare.BookmarkLevel = value; }
    public string? BookmarkLabel { get => _rare.BookmarkLabel; set => Rare.BookmarkLabel = value; }
    public bool BookmarkClosed { get => _rare.BookmarkClosed; set => Rare.BookmarkClosed = value; }
    public CounterOp[]? CounterReset { get => _rare.CounterReset; set => Rare.CounterReset = value; }
    public CounterOp[]? CounterSet { get => _rare.CounterSet; set => Rare.CounterSet = value; }
    public CounterOp[]? CounterIncrement { get => _rare.CounterIncrement; set => Rare.CounterIncrement = value; }

    // The group to change a value in, copied first when other styles share it.
    private RareFields Rare => _rare.Shared ? _rare = _rare.Copy() : _rare;

    private sealed class RareFields
    {
        public static readonly RareFields Initial = new() { Shared = true };

        public bool Shared;
        public bool TableLayoutFixed; // table-layout: fixed
        /// <summary>What replaces the cut end of an overflowing line (text-overflow): "…" for ellipsis, a custom string, or null for clip.</summary>
        public string? TextOverflow;
        public TextBoxTrim TextBoxTrim; // text-box-trim
        public float AspectRatio;    // width / height; 0 = auto
        public bool AspectRatioAuto; // "auto <ratio>": a replaced element keeps its natural ratio
        public ContainerType ContainerType;
        public string[]? ContainerNames; // container-name; null for none
        public Length ControlWidth = Length.Auto;   // a text field's own width, used while width is auto and not stretched
        public SizeKeyword WidthKeyword;            // width: min-content, max-content or fit-content; Width is then auto
        public StretchSizes Stretched;              // the sizes set to stretch, which become lengths once the box's margins are known
        public bool ColumnBreakBefore, ColumnBreakAfter; // break-before/after: column, which only breaks inside a multicol
        public bool AvoidColumnBreakBefore, AvoidColumnBreakAfter, AvoidColumnBreakInside; // avoid-column: only inside a multicol
        public ShapeOutside? ShapeOutside;          // shape-outside on a float; null = none
        public Length ShapeMargin = Length.Zero;    // shape-margin; a percentage is of the containing block's width
        public float ShapeImageThreshold;           // shape-image-threshold, from 0 to 1
        public ShapeBox OverflowClipBox = ShapeBox.PaddingBox; // overflow-clip-margin: the box that overflow: clip clips to
        public float OverflowClipMargin;            // overflow-clip-margin: how far past that box, in points
        /// <summary>
        /// initial-letter on a ::first-letter: how many lines the letter is tall, or 0 for normal, and how many lines it sinks
        /// (0 for drop, which sinks it by its size rounded up; raise is 1). InitialLetterSunk is false for a letter its author
        /// floats, which keeps its float instead of sinking into the lines.
        /// </summary>
        public float InitialLetterSize;
        public int InitialLetterSink;
        public bool InitialLetterSunk;
        /// <summary>float: footnote: the box leaves the flow for the footnote area of the page its call lands on.</summary>
        public bool Footnote;
        public FootnoteDisplay FootnoteDisplay;
        public FootnotePolicy FootnotePolicy;
        public int? ZIndex;                         // null = auto
        public TransformFn[]? Transform;            // null = none; includes translate, rotate and scale once computed
        public TransformFn[]? Translate, Rotate, Scale; // the individual transform properties; null = none
        public Length TransformOriginX = new(50, LengthUnit.Percent), TransformOriginY = new(50, LengthUnit.Percent);
        public float? Perspective;                  // points; null = none. Applies to the 3D transforms of the children
        public Length PerspectiveOriginX = new(50, LengthUnit.Percent), PerspectiveOriginY = new(50, LengthUnit.Percent);
        public string? Content;                     // the content property as written; null = normal
        public bool ContentNone;                    // content: none, which on ::marker removes the marker (normal keeps it)
        public string? StringSet;                   // string-set as written (named strings for running headers); null = none
        public int? BookmarkLevel;                  // bookmark-level: null = unset (h1-h6 take their own level), 0 = none
        public string? BookmarkLabel;               // bookmark-label as written; null = content(text)
        public bool BookmarkClosed;                 // bookmark-state: closed
        public CounterOp[]? CounterReset, CounterSet, CounterIncrement;

        public RareFields Copy()
        {
            var c = (RareFields)MemberwiseClone();
            c.Shared = false;
            return c;
        }
    }
}
