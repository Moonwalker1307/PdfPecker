namespace PdfPecker.Css;

// Most boxes leave most properties at their initial values, and most children inherit their parent's values
// unchanged. The rarely set properties therefore live in groups that styles share: a child points at its parent's
// inherited groups, and at one initial instance of each group of properties that are not inherited. A group is copied
// the first time a style changes a value in it, so a style is a fraction of its full size and creating one copies
// little. This is the same split Chrome makes in its computed styles.
// A setter that is given the value the group already has leaves the group alone: a declaration that repeats the initial
// or inherited value (display on every element sets LegacyBox, width its keyword) would otherwise copy a whole group
// to change nothing. The SVG paint group is the exception, because sharing it with the parent is what tells an element
// that sets no paint of its own (see InheritsSvgPaint).
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

    public string? FontVariationSettings { get => _typography.FontVariationSettings; set { if (_typography.FontVariationSettings != value) Typography.FontVariationSettings = value; } }
    public bool FontOpticalSizingNone { get => _typography.FontOpticalSizingNone; set { if (_typography.FontOpticalSizingNone != value) Typography.FontOpticalSizingNone = value; } }
    public TextAlign? TextAlignLast { get => _typography.TextAlignLast; set { if (_typography.TextAlignLast != value) Typography.TextAlignLast = value; } }
    public TextJustify TextJustify { get => _typography.TextJustify; set { if (_typography.TextJustify != value) Typography.TextJustify = value; } }
    public TextSpacingTrim TextSpacingTrim { get => _typography.TextSpacingTrim; set { if (_typography.TextSpacingTrim != value) Typography.TextSpacingTrim = value; } }
    public TextAutospace TextAutospace { get => _typography.TextAutospace; set { if (_typography.TextAutospace != value) Typography.TextAutospace = value; } }
    public TextOrientation TextOrientation { get => _typography.TextOrientation; set { if (_typography.TextOrientation != value) Typography.TextOrientation = value; } }
    public bool TextCombineUpright { get => _typography.TextCombineUpright; set { if (_typography.TextCombineUpright != value) Typography.TextCombineUpright = value; } }
    public TextAlign? BlockAlign { get => _typography.BlockAlign; set { if (_typography.BlockAlign != value) Typography.BlockAlign = value; } }
    public int TabSize { get => _typography.TabSize; set { if (_typography.TabSize != value) Typography.TabSize = value; } }
    public string[]? Quotes { get => _typography.Quotes; set { if (_typography.Quotes != value) Typography.Quotes = value; } }
    public string? HyphenateCharacter { get => _typography.HyphenateCharacter; set { if (_typography.HyphenateCharacter != value) Typography.HyphenateCharacter = value; } }
    public string? BlockEllipsis { get => _typography.BlockEllipsis; set { if (_typography.BlockEllipsis != value) Typography.BlockEllipsis = value; } }
    public HyphenateLimits HyphenateLimitChars { get => _typography.HyphenateLimitChars; set => Typography.HyphenateLimitChars = value; }
    public int HyphenateLimitLines { get => _typography.HyphenateLimitLines; set { if (_typography.HyphenateLimitLines != value) Typography.HyphenateLimitLines = value; } }
    public LineBreak LineBreak { get => _typography.LineBreak; set { if (_typography.LineBreak != value) Typography.LineBreak = value; } }
    public string? ListStyleString { get => _typography.ListStyleString; set { if (_typography.ListStyleString != value) Typography.ListStyleString = value; } }
    public CounterStyle? ListCounterStyle { get => _typography.ListCounterStyle; set { if (_typography.ListCounterStyle != value) Typography.ListCounterStyle = value; } }
    public string? ListStyleImage { get => _typography.ListStyleImage; set { if (_typography.ListStyleImage != value) Typography.ListStyleImage = value; } }
    public float ListStyleImageResolution { get => _typography.ListStyleImageResolution; set => Typography.ListStyleImageResolution = value; }
    public CssGradient? ListStyleGradient { get => _typography.ListStyleGradient; set { if (_typography.ListStyleGradient != value) Typography.ListStyleGradient = value; } }
    public bool TextIndentHanging { get => _typography.TextIndentHanging; set { if (_typography.TextIndentHanging != value) Typography.TextIndentHanging = value; } }
    public bool TextIndentEachLine { get => _typography.TextIndentEachLine; set { if (_typography.TextIndentEachLine != value) Typography.TextIndentEachLine = value; } }
    public FontSynthesis Synthesis { get => _typography.Synthesis; set { if (_typography.Synthesis != value) Typography.Synthesis = value; } }
    public FontSizeAdjust SizeAdjust { get => _typography.SizeAdjust; set => Typography.SizeAdjust = value; }
    public string? FontVariantEastAsian { get => _typography.FontVariantEastAsian; set { if (_typography.FontVariantEastAsian != value) Typography.FontVariantEastAsian = value; } }
    public string? FontVariantPosition { get => _typography.FontVariantPosition; set { if (_typography.FontVariantPosition != value) Typography.FontVariantPosition = value; } }
    public FontAlternates? FontVariantAlternates { get => _typography.FontVariantAlternates; set { if (_typography.FontVariantAlternates != value) Typography.FontVariantAlternates = value; } }
    public FontPalette? FontPalette { get => _typography.FontPalette; set { if (_typography.FontPalette != value) Typography.FontPalette = value; } }
    public string? FontVariantNumeric { get => _typography.FontVariantNumeric; set { if (_typography.FontVariantNumeric != value) Typography.FontVariantNumeric = value; } }
    public string? FontFeatureSettings { get => _typography.FontFeatureSettings; set { if (_typography.FontFeatureSettings != value) Typography.FontFeatureSettings = value; } }
    public bool CaptionBottom { get => _typography.CaptionBottom; set { if (_typography.CaptionBottom != value) Typography.CaptionBottom = value; } }
    public bool EmptyCellsHide { get => _typography.EmptyCellsHide; set { if (_typography.EmptyCellsHide != value) Typography.EmptyCellsHide = value; } }
    public bool ImageOrientationNone { get => _typography.ImageOrientationNone; set { if (_typography.ImageOrientationNone != value) Typography.ImageOrientationNone = value; } }
    public bool ImagePixelated { get => _typography.ImagePixelated; set { if (_typography.ImagePixelated != value) Typography.ImagePixelated = value; } }
    public int ImageQuality { get => _typography.ImageQuality; set { if (_typography.ImageQuality != value) Typography.ImageQuality = value; } }
    public RubyAlign RubyAlign { get => _typography.RubyAlign; set { if (_typography.RubyAlign != value) Typography.RubyAlign = value; } }
    public bool RubyUnder { get => _typography.RubyUnder; set { if (_typography.RubyUnder != value) Typography.RubyUnder = value; } }
    public float ImageMaxDpi { get => _typography.ImageMaxDpi; set => Typography.ImageMaxDpi = value; }
    public uint AccentColor { get => _typography.AccentColor; set { if (_typography.AccentColor != value) Typography.AccentColor = value; } }
    public TextEdgeOver TextEdgeOver { get => _typography.TextEdgeOver; set { if (_typography.TextEdgeOver != value) Typography.TextEdgeOver = value; } }
    public TextEdgeUnder TextEdgeUnder { get => _typography.TextEdgeUnder; set { if (_typography.TextEdgeUnder != value) Typography.TextEdgeUnder = value; } }
    public int Orphans { get => _typography.Orphans; set { if (_typography.Orphans != value) Typography.Orphans = value; } }
    public int Widows { get => _typography.Widows; set { if (_typography.Widows != value) Typography.Widows = value; } }

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
        public TextAutospace TextAutospace;
        public TextOrientation TextOrientation;  // text-orientation: how characters stand in vertical lines
        public bool TextCombineUpright;          // text-combine-upright: all, the text set across one vertical em
        public TextAlign? BlockAlign;            // -webkit-left, -webkit-center or -webkit-right: also aligns child blocks
        public int TabSize = 8;                  // in spaces
        public string[]? Quotes;                 // quotes: open and close strings of each nesting level; null = auto (by language), empty = none
        public string? HyphenateCharacter;       // hyphenate-character: "string"; null = auto (a hyphen-minus)
        public string? BlockEllipsis;            // block-ellipsis: the mark ending a clamped block; null = auto ("…"), "" = none
        public HyphenateLimits HyphenateLimitChars; // hyphenate-limit-chars; each value 0 = auto
        public int HyphenateLimitLines = -1;     // hyphenate-limit-lines: the most consecutive lines that may end in a hyphen; -1 = no-limit
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
        public FontAlternates? FontVariantAlternates; // font-variant-alternates; null = normal
        public FontPalette? FontPalette;     // font-palette: the colour font palette; null = normal
        public string? FontVariantNumeric;   // font-variant-numeric keywords; null = normal
        public string? FontFeatureSettings;  // font-feature-settings as written; null = normal
        public bool CaptionBottom;    // caption-side: bottom (inherited)
        public bool EmptyCellsHide;   // empty-cells: hide (inherited): with separated borders, empty cells paint nothing
        public bool ImageOrientationNone; // image-orientation: none (inherited): photos are drawn as stored, ignoring EXIF
        public bool ImagePixelated;   // image-rendering: pixelated or crisp-edges (inherited): enlarged images keep hard pixel edges
        public RubyAlign RubyAlign;   // ruby-align (inherited): how a ruby base or annotation narrower than the other fills it
        public bool RubyUnder;        // ruby-position: under (inherited): annotations below their base (left of it in vertical text)
        public int ImageQuality;      // -pdfpecker-image-quality (inherited): JPEG quality 1 to 100 images are written at; 0 = none
        public float ImageMaxDpi;     // -pdfpecker-image-max-resolution (inherited), in dpi: images drawn denser are downsampled; 0 = none
        public uint AccentColor;      // accent-color (inherited), the colour of checked check boxes and radio buttons; 0 = auto
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

    public uint? DecorationColor { get => _decoration.DecorationColor; set { if (_decoration.DecorationColor != value) Decoration.DecorationColor = value; } }
    public DecorationStyle DecorationStyle { get => _decoration.DecorationStyle; set { if (_decoration.DecorationStyle != value) Decoration.DecorationStyle = value; } }
    public Length? DecorationThickness { get => _decoration.DecorationThickness; set => Decoration.DecorationThickness = value; }
    public Length? UnderlineOffset { get => _decoration.UnderlineOffset; set => Decoration.UnderlineOffset = value; }
    public SkipInk SkipInk { get => _decoration.SkipInk; set { if (_decoration.SkipInk != value) Decoration.SkipInk = value; } }
    public UnderlinePosition UnderlinePosition { get => _decoration.UnderlinePosition; set { if (_decoration.UnderlinePosition != value) Decoration.UnderlinePosition = value; } }
    public EmphasisShape EmphasisShape { get => _decoration.EmphasisShape; set { if (_decoration.EmphasisShape != value) Decoration.EmphasisShape = value; } }
    public bool EmphasisOpen { get => _decoration.EmphasisOpen; set { if (_decoration.EmphasisOpen != value) Decoration.EmphasisOpen = value; } }
    public string? EmphasisString { get => _decoration.EmphasisString; set { if (_decoration.EmphasisString != value) Decoration.EmphasisString = value; } }
    public uint? EmphasisColor { get => _decoration.EmphasisColor; set { if (_decoration.EmphasisColor != value) Decoration.EmphasisColor = value; } }
    public bool EmphasisUnder { get => _decoration.EmphasisUnder; set { if (_decoration.EmphasisUnder != value) Decoration.EmphasisUnder = value; } }
    public BoxShadow[]? TextShadows { get => _decoration.TextShadows; set { if (_decoration.TextShadows != value) Decoration.TextShadows = value; } }
    public float TextStrokeWidth { get => _decoration.TextStrokeWidth; set => Decoration.TextStrokeWidth = value; }
    public uint? TextFillColor { get => _decoration.TextFillColor; set { if (_decoration.TextFillColor != value) Decoration.TextFillColor = value; } }
    public uint? TextStrokeColor { get => _decoration.TextStrokeColor; set { if (_decoration.TextStrokeColor != value) Decoration.TextStrokeColor = value; } }
    public bool PaintStrokeFirst { get => _decoration.PaintStrokeFirst; set { if (_decoration.PaintStrokeFirst != value) Decoration.PaintStrokeFirst = value; } }

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
    public bool SvgFillCurrent { get => _svgPaint.SvgFillCurrent; set => SvgPaint.SvgFillCurrent = value; }
    public bool SvgStrokeCurrent { get => _svgPaint.SvgStrokeCurrent; set => SvgPaint.SvgStrokeCurrent = value; }
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

    /// <summary>
    /// The fill colour to paint with: currentColor is resolved now, against this style's colour, since currentColor is
    /// inherited as the keyword and an element drawn by a use element takes its colour from there.
    /// </summary>
    public uint? SvgFillColor => _svgPaint.SvgFillCurrent ? CurrentPaint(Color) : _svgPaint.SvgFill;

    /// <summary>The stroke colour to paint with, currentColor resolved against this style's colour.</summary>
    public uint? SvgStrokeColor => _svgPaint.SvgStrokeCurrent ? CurrentPaint(Color) : _svgPaint.SvgStroke;

    // A transparent colour paints nothing, as a transparent fill or stroke does.
    private static uint? CurrentPaint(uint color) => CssColor.IsVisible(color) ? color : null;

    /// <summary>
    /// Whether this style took every SVG paint property from <paramref name="parent"/> unchanged. A child shares its
    /// parent's group until it sets a value in it, so this tells an element that sets no paint of its own.
    /// </summary>
    internal bool InheritsSvgPaint(ComputedStyle parent) => ReferenceEquals(_svgPaint, parent._svgPaint);

    /// <summary>A copy of this style with the SVG paint properties of <paramref name="from"/>.</summary>
    internal ComputedStyle WithSvgPaintOf(ComputedStyle from)
    {
        var c = Clone();
        from._svgPaint.Shared = true;
        c._svgPaint = from._svgPaint;
        return c;
    }

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
        public bool SvgFillCurrent, SvgStrokeCurrent; // fill or stroke: currentColor (alone or as a url() fallback)
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

    public BoxShadow[]? BoxShadows { get => _visual.BoxShadows; set { if (_visual.BoxShadows != value) Visual.BoxShadows = value; } }
    public BackgroundImage?[]? BackgroundImages { get => _visual.BackgroundImages; set { if (_visual.BackgroundImages != value) Visual.BackgroundImages = value; } }
    public (BackgroundRepeat X, BackgroundRepeat Y)[]? BackgroundRepeats { get => _visual.BackgroundRepeats; set => Visual.BackgroundRepeats = value; }
    public BackgroundOffset[]? BackgroundPositionsX { get => _visual.BackgroundPositionsX; set { if (_visual.BackgroundPositionsX != value) Visual.BackgroundPositionsX = value; } }
    public BackgroundOffset[]? BackgroundPositionsY { get => _visual.BackgroundPositionsY; set { if (_visual.BackgroundPositionsY != value) Visual.BackgroundPositionsY = value; } }
    public BackgroundSize[]? BackgroundSizes { get => _visual.BackgroundSizes; set { if (_visual.BackgroundSizes != value) Visual.BackgroundSizes = value; } }
    public BackgroundBox[]? BackgroundOrigins { get => _visual.BackgroundOrigins; set { if (_visual.BackgroundOrigins != value) Visual.BackgroundOrigins = value; } }
    public BackgroundBox[]? BackgroundClips { get => _visual.BackgroundClips; set { if (_visual.BackgroundClips != value) Visual.BackgroundClips = value; } }
    public bool[]? BackgroundFixed { get => _visual.BackgroundFixed; set { if (_visual.BackgroundFixed != value) Visual.BackgroundFixed = value; } }
    public string? SvgClipPath { get => _visual.SvgClipPath; set { if (_visual.SvgClipPath != value) Visual.SvgClipPath = value; } }
    public string? SvgMask { get => _visual.SvgMask; set { if (_visual.SvgMask != value) Visual.SvgMask = value; } }
    public string? SvgFilter { get => _visual.SvgFilter; set { if (_visual.SvgFilter != value) Visual.SvgFilter = value; } }
    public SvgGeometry? SvgGeometry { get => _visual.SvgGeometry; set { if (_visual.SvgGeometry != value) Visual.SvgGeometry = value; } }
    public bool SvgMaskAlpha { get => _visual.SvgMaskAlpha; set { if (_visual.SvgMaskAlpha != value) Visual.SvgMaskAlpha = value; } }
    public bool SvgNonScalingStroke { get => _visual.SvgNonScalingStroke; set { if (_visual.SvgNonScalingStroke != value) Visual.SvgNonScalingStroke = value; } }
    public uint SvgFloodColor { get => _visual.SvgFloodColor; set { if (_visual.SvgFloodColor != value) Visual.SvgFloodColor = value; } }
    public float SvgFloodOpacity { get => _visual.SvgFloodOpacity; set => Visual.SvgFloodOpacity = value; }
    public uint SvgLightingColor { get => _visual.SvgLightingColor; set { if (_visual.SvgLightingColor != value) Visual.SvgLightingColor = value; } }
    public ClipShape? ClipShape { get => _visual.ClipShape; set { if (_visual.ClipShape != value) Visual.ClipShape = value; } }
    public Length[]? ClipRect { get => _visual.ClipRect; set { if (_visual.ClipRect != value) Visual.ClipRect = value; } }
    public string? BlendMode { get => _visual.BlendMode; set { if (_visual.BlendMode != value) Visual.BlendMode = value; } }
    public string?[]? BackgroundBlendModes { get => _visual.BackgroundBlendModes; set { if (_visual.BackgroundBlendModes != value) Visual.BackgroundBlendModes = value; } }
    public bool Isolate { get => _visual.Isolate; set { if (_visual.Isolate != value) Visual.Isolate = value; } }
    public bool BoxDecorationClone { get => _visual.BoxDecorationClone; set { if (_visual.BoxDecorationClone != value) Visual.BoxDecorationClone = value; } }
    public bool ScaleToFit { get => _visual.ScaleToFit; set { if (_visual.ScaleToFit != value) Visual.ScaleToFit = value; } }
    public bool RepeatOnPages { get => _visual.RepeatOnPages; set { if (_visual.RepeatOnPages != value) Visual.RepeatOnPages = value; } }
    public bool ContinueDiscard { get => _visual.ContinueDiscard; set { if (_visual.ContinueDiscard != value) Visual.ContinueDiscard = value; } }
    public FilterFunction[]? Filters { get => _visual.Filters; set { if (_visual.Filters != value) Visual.Filters = value; } }
    public FilterFunction[]? BackdropFilters { get => _visual.BackdropFilters; set { if (_visual.BackdropFilters != value) Visual.BackdropFilters = value; } }
    public BackgroundImage?[]? MaskImages { get => _visual.MaskImages; set { if (_visual.MaskImages != value) Visual.MaskImages = value; } }
    public (BackgroundRepeat X, BackgroundRepeat Y)[]? MaskRepeats { get => _visual.MaskRepeats; set => Visual.MaskRepeats = value; }
    public BackgroundOffset[]? MaskPositionsX { get => _visual.MaskPositionsX; set { if (_visual.MaskPositionsX != value) Visual.MaskPositionsX = value; } }
    public BackgroundOffset[]? MaskPositionsY { get => _visual.MaskPositionsY; set { if (_visual.MaskPositionsY != value) Visual.MaskPositionsY = value; } }
    public BackgroundSize[]? MaskSizes { get => _visual.MaskSizes; set { if (_visual.MaskSizes != value) Visual.MaskSizes = value; } }
    public BackgroundBox[]? MaskOrigins { get => _visual.MaskOrigins; set { if (_visual.MaskOrigins != value) Visual.MaskOrigins = value; } }
    public BackgroundBox[]? MaskClips { get => _visual.MaskClips; set { if (_visual.MaskClips != value) Visual.MaskClips = value; } }
    public MaskMode[]? MaskModes { get => _visual.MaskModes; set { if (_visual.MaskModes != value) Visual.MaskModes = value; } }
    public MaskComposite[]? MaskComposites { get => _visual.MaskComposites; set { if (_visual.MaskComposites != value) Visual.MaskComposites = value; } }
    public BackgroundImage? BorderImageSource { get => _visual.BorderImageSource; set { if (_visual.BorderImageSource != value) Visual.BorderImageSource = value; } }
    public BorderImageValue[]? BorderImageSlice { get => _visual.BorderImageSlice; set { if (_visual.BorderImageSlice != value) Visual.BorderImageSlice = value; } }
    public bool BorderImageFill { get => _visual.BorderImageFill; set { if (_visual.BorderImageFill != value) Visual.BorderImageFill = value; } }
    public BorderImageValue[]? BorderImageWidths { get => _visual.BorderImageWidths; set { if (_visual.BorderImageWidths != value) Visual.BorderImageWidths = value; } }
    public BorderImageValue[]? BorderImageOutsets { get => _visual.BorderImageOutsets; set { if (_visual.BorderImageOutsets != value) Visual.BorderImageOutsets = value; } }
    public BorderImageRepeat BorderImageRepeatX { get => _visual.BorderImageRepeatX; set { if (_visual.BorderImageRepeatX != value) Visual.BorderImageRepeatX = value; } }
    public BorderImageRepeat BorderImageRepeatY { get => _visual.BorderImageRepeatY; set { if (_visual.BorderImageRepeatY != value) Visual.BorderImageRepeatY = value; } }
    public BorderStyle OutlineStyle { get => _visual.OutlineStyle; set { if (_visual.OutlineStyle != value) Visual.OutlineStyle = value; } }
    public bool OutlineAuto { get => _visual.OutlineAuto; set { if (_visual.OutlineAuto != value) Visual.OutlineAuto = value; } }
    public float OutlineWidth { get => _visual.OutlineWidth; set => Visual.OutlineWidth = value; }
    public float OutlineOffset { get => _visual.OutlineOffset; set => Visual.OutlineOffset = value; }
    public uint? OutlineColor { get => _visual.OutlineColor; set { if (_visual.OutlineColor != value) Visual.OutlineColor = value; } }
    public ObjectFit ObjectFit { get => _visual.ObjectFit; set { if (_visual.ObjectFit != value) Visual.ObjectFit = value; } }
    public BackgroundOffset ObjectPositionX { get => _visual.ObjectPositionX; set => Visual.ObjectPositionX = value; }
    public BackgroundOffset ObjectPositionY { get => _visual.ObjectPositionY; set => Visual.ObjectPositionY = value; }
    /// <summary>object-view-box: the part of an image (an inset(), rect() or xywh() of its natural size) shown as the whole of it, or null.</summary>
    public ClipShape? ObjectViewBox { get => _visual.ObjectViewBox; set { if (_visual.ObjectViewBox != value) Visual.ObjectViewBox = value; } }

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
        public SvgGeometry? SvgGeometry;            // cx, cy, r, rx, ry, x, y, d, stop-color and stop-opacity set by rules
        public bool SvgMaskAlpha;                   // mask-type: alpha (else luminance)
        public bool SvgNonScalingStroke;            // vector-effect: non-scaling-stroke (not inherited)
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
        public bool RepeatOnPages;                  // -pdfpecker-repeat: each-page
        public bool ContinueDiscard;                // continue: discard
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
        public ClipShape? ObjectViewBox;

        public VisualFields Copy()
        {
            var c = (VisualFields)MemberwiseClone();
            c.Shared = false;
            return c;
        }
    }

    // ---- Flexbox, grid, multi-column and legacy box properties: not inherited, and unset on most boxes.

    public FlexDirection FlexDirection { get => _containers.FlexDirection; set { if (_containers.FlexDirection != value) Containers.FlexDirection = value; } }
    public FlexWrap FlexWrap { get => _containers.FlexWrap; set { if (_containers.FlexWrap != value) Containers.FlexWrap = value; } }
    public float FlexGrow { get => _containers.FlexGrow; set => Containers.FlexGrow = value; }
    public float FlexShrink { get => _containers.FlexShrink; set => Containers.FlexShrink = value; }
    public Length FlexBasis { get => _containers.FlexBasis; set => Containers.FlexBasis = value; }
    public int Order { get => _containers.Order; set { if (_containers.Order != value) Containers.Order = value; } }
    public BoxAlign JustifyContent { get => _containers.JustifyContent; set { if (_containers.JustifyContent != value) Containers.JustifyContent = value; } }
    public BoxAlign AlignItems { get => _containers.AlignItems; set { if (_containers.AlignItems != value) Containers.AlignItems = value; } }
    public BoxAlign AlignContent { get => _containers.AlignContent; set { if (_containers.AlignContent != value) Containers.AlignContent = value; } }
    public BoxAlign JustifyItems { get => _containers.JustifyItems; set { if (_containers.JustifyItems != value) Containers.JustifyItems = value; } }
    public BoxAlign AlignSelf { get => _containers.AlignSelf; set { if (_containers.AlignSelf != value) Containers.AlignSelf = value; } }
    public BoxAlign JustifySelf { get => _containers.JustifySelf; set { if (_containers.JustifySelf != value) Containers.JustifySelf = value; } }
    public Length? RowGap { get => _containers.RowGap; set => Containers.RowGap = value; }
    public Length? ColumnGap { get => _containers.ColumnGap; set => Containers.ColumnGap = value; }
    public TrackList? GridTemplateColumns { get => _containers.GridTemplateColumns; set { if (_containers.GridTemplateColumns != value) Containers.GridTemplateColumns = value; } }
    public TrackList? GridTemplateRows { get => _containers.GridTemplateRows; set { if (_containers.GridTemplateRows != value) Containers.GridTemplateRows = value; } }
    public Track[]? GridAutoColumns { get => _containers.GridAutoColumns; set { if (_containers.GridAutoColumns != value) Containers.GridAutoColumns = value; } }
    public Track[]? GridAutoRows { get => _containers.GridAutoRows; set { if (_containers.GridAutoRows != value) Containers.GridAutoRows = value; } }
    public bool GridAutoFlowColumn { get => _containers.GridAutoFlowColumn; set { if (_containers.GridAutoFlowColumn != value) Containers.GridAutoFlowColumn = value; } }
    public bool GridAutoFlowDense { get => _containers.GridAutoFlowDense; set { if (_containers.GridAutoFlowDense != value) Containers.GridAutoFlowDense = value; } }
    public GridLine GridColumnStart { get => _containers.GridColumnStart; set => Containers.GridColumnStart = value; }
    public GridLine GridColumnEnd { get => _containers.GridColumnEnd; set => Containers.GridColumnEnd = value; }
    public GridLine GridRowStart { get => _containers.GridRowStart; set => Containers.GridRowStart = value; }
    public GridLine GridRowEnd { get => _containers.GridRowEnd; set => Containers.GridRowEnd = value; }
    public GridAreas? GridTemplateAreas { get => _containers.GridTemplateAreas; set { if (_containers.GridTemplateAreas != value) Containers.GridTemplateAreas = value; } }
    public int ColumnCount { get => _containers.ColumnCount; set { if (_containers.ColumnCount != value) Containers.ColumnCount = value; } }
    public Length ColumnWidth { get => _containers.ColumnWidth; set => Containers.ColumnWidth = value; }
    public BorderStyle ColumnRuleStyle { get => _containers.ColumnRuleStyle; set { if (_containers.ColumnRuleStyle != value) Containers.ColumnRuleStyle = value; } }
    public float ColumnRuleWidth { get => _containers.ColumnRuleWidth; set => Containers.ColumnRuleWidth = value; }
    public uint? ColumnRuleColor { get => _containers.ColumnRuleColor; set { if (_containers.ColumnRuleColor != value) Containers.ColumnRuleColor = value; } }
    public bool ColumnSpanAll { get => _containers.ColumnSpanAll; set { if (_containers.ColumnSpanAll != value) Containers.ColumnSpanAll = value; } }
    public bool ColumnFillAuto { get => _containers.ColumnFillAuto; set { if (_containers.ColumnFillAuto != value) Containers.ColumnFillAuto = value; } }
    public int LineClamp { get => _containers.LineClamp; set { if (_containers.LineClamp != value) Containers.LineClamp = value; } }
    public bool LegacyBox { get => _containers.LegacyBox; set { if (_containers.LegacyBox != value) Containers.LegacyBox = value; } }
    public bool BoxVertical { get => _containers.BoxVertical; set { if (_containers.BoxVertical != value) Containers.BoxVertical = value; } }
    public BoxAlign BoxPack { get => _containers.BoxPack; set { if (_containers.BoxPack != value) Containers.BoxPack = value; } }
    public BoxAlign BoxAlignItems { get => _containers.BoxAlignItems; set { if (_containers.BoxAlignItems != value) Containers.BoxAlignItems = value; } }
    public float BoxFlex { get => _containers.BoxFlex; set => Containers.BoxFlex = value; }
    public int BoxOrdinalGroup { get => _containers.BoxOrdinalGroup; set { if (_containers.BoxOrdinalGroup != value) Containers.BoxOrdinalGroup = value; } }

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

    // ---- Other properties that are not inherited and that few boxes set: transforms, generated content, counters, bookmarks, shapes, initial letters, footnotes, container queries and gap decorations.

    public bool TableLayoutFixed { get => _rare.TableLayoutFixed; set { if (_rare.TableLayoutFixed != value) Rare.TableLayoutFixed = value; } }
    public string? TextOverflow { get => _rare.TextOverflow; set { if (_rare.TextOverflow != value) Rare.TextOverflow = value; } }
    public TextBoxTrim TextBoxTrim { get => _rare.TextBoxTrim; set { if (_rare.TextBoxTrim != value) Rare.TextBoxTrim = value; } }
    public float AspectRatio { get => _rare.AspectRatio; set => Rare.AspectRatio = value; }
    public bool AspectRatioAuto { get => _rare.AspectRatioAuto; set { if (_rare.AspectRatioAuto != value) Rare.AspectRatioAuto = value; } }
    public ContainerType ContainerType { get => _rare.ContainerType; set { if (_rare.ContainerType != value) Rare.ContainerType = value; } }
    public string[]? ContainerNames { get => _rare.ContainerNames; set { if (_rare.ContainerNames != value) Rare.ContainerNames = value; } }
    public Length ControlWidth { get => _rare.ControlWidth; set => Rare.ControlWidth = value; }
    public SizeKeyword WidthKeyword { get => _rare.WidthKeyword; set { if (_rare.WidthKeyword != value) Rare.WidthKeyword = value; } }
    public StretchSizes Stretched { get => _rare.Stretched; set { if (_rare.Stretched != value) Rare.Stretched = value; } }
    public bool ColumnBreakBefore { get => _rare.ColumnBreakBefore; set { if (_rare.ColumnBreakBefore != value) Rare.ColumnBreakBefore = value; } }
    public bool ColumnBreakAfter { get => _rare.ColumnBreakAfter; set { if (_rare.ColumnBreakAfter != value) Rare.ColumnBreakAfter = value; } }
    public bool AvoidColumnBreakBefore { get => _rare.AvoidColumnBreakBefore; set { if (_rare.AvoidColumnBreakBefore != value) Rare.AvoidColumnBreakBefore = value; } }
    public bool AvoidColumnBreakAfter { get => _rare.AvoidColumnBreakAfter; set { if (_rare.AvoidColumnBreakAfter != value) Rare.AvoidColumnBreakAfter = value; } }
    public bool AvoidColumnBreakInside { get => _rare.AvoidColumnBreakInside; set { if (_rare.AvoidColumnBreakInside != value) Rare.AvoidColumnBreakInside = value; } }
    public ShapeOutside? ShapeOutside { get => _rare.ShapeOutside; set { if (_rare.ShapeOutside != value) Rare.ShapeOutside = value; } }
    public bool FieldSizingContent { get => _rare.FieldSizingContent; set { if (_rare.FieldSizingContent != value) Rare.FieldSizingContent = value; } }
    public Length ShapeMargin { get => _rare.ShapeMargin; set => Rare.ShapeMargin = value; }
    public float ShapeImageThreshold { get => _rare.ShapeImageThreshold; set => Rare.ShapeImageThreshold = value; }
    public ShapeBox OverflowClipBox { get => _rare.OverflowClipBox; set { if (_rare.OverflowClipBox != value) Rare.OverflowClipBox = value; } }
    public bool OverflowAuto { get => _rare.OverflowAuto; set { if (_rare.OverflowAuto != value) Rare.OverflowAuto = value; } }
    public float OverflowClipMargin { get => _rare.OverflowClipMargin; set => Rare.OverflowClipMargin = value; }
    public float InitialLetterSize { get => _rare.InitialLetterSize; set => Rare.InitialLetterSize = value; }
    public int InitialLetterSink { get => _rare.InitialLetterSink; set { if (_rare.InitialLetterSink != value) Rare.InitialLetterSink = value; } }
    public bool InitialLetterSunk { get => _rare.InitialLetterSunk; set { if (_rare.InitialLetterSunk != value) Rare.InitialLetterSunk = value; } }
    public bool Footnote { get => _rare.Footnote; set { if (_rare.Footnote != value) Rare.Footnote = value; } }
    public FootnoteDisplay FootnoteDisplay { get => _rare.FootnoteDisplay; set { if (_rare.FootnoteDisplay != value) Rare.FootnoteDisplay = value; } }
    public FootnotePolicy FootnotePolicy { get => _rare.FootnotePolicy; set { if (_rare.FootnotePolicy != value) Rare.FootnotePolicy = value; } }
    public int? ZIndex { get => _rare.ZIndex; set { if (_rare.ZIndex != value) Rare.ZIndex = value; } }
    public TransformFn[]? Transform { get => _rare.Transform; set { if (_rare.Transform != value) Rare.Transform = value; } }
    public TransformFn[]? Translate { get => _rare.Translate; set { if (_rare.Translate != value) Rare.Translate = value; } }
    public TransformFn[]? Rotate { get => _rare.Rotate; set { if (_rare.Rotate != value) Rare.Rotate = value; } }
    public TransformFn[]? Scale { get => _rare.Scale; set { if (_rare.Scale != value) Rare.Scale = value; } }
    public Length TransformOriginX { get => _rare.TransformOriginX; set => Rare.TransformOriginX = value; }
    public Length TransformOriginY { get => _rare.TransformOriginY; set => Rare.TransformOriginY = value; }
    public bool TransformOriginSet { get => _rare.TransformOriginSet; set { if (_rare.TransformOriginSet != value) Rare.TransformOriginSet = value; } }
    public TransformBox TransformBox { get => _rare.TransformBox; set { if (_rare.TransformBox != value) Rare.TransformBox = value; } }
    public float? Perspective { get => _rare.Perspective; set => Rare.Perspective = value; }
    public Length PerspectiveOriginX { get => _rare.PerspectiveOriginX; set => Rare.PerspectiveOriginX = value; }
    public Length PerspectiveOriginY { get => _rare.PerspectiveOriginY; set => Rare.PerspectiveOriginY = value; }
    public string? Content { get => _rare.Content; set { if (_rare.Content != value) Rare.Content = value; } }
    public bool ContentNone { get => _rare.ContentNone; set { if (_rare.ContentNone != value) Rare.ContentNone = value; } }
    public string? StringSet { get => _rare.StringSet; set { if (_rare.StringSet != value) Rare.StringSet = value; } }
    public string? RunningName { get => _rare.RunningName; set { if (_rare.RunningName != value) Rare.RunningName = value; } }
    public bool AppearanceNone { get => _rare.AppearanceNone; set { if (_rare.AppearanceNone != value) Rare.AppearanceNone = value; } }
    public int? BookmarkLevel { get => _rare.BookmarkLevel; set { if (_rare.BookmarkLevel != value) Rare.BookmarkLevel = value; } }
    public string? BookmarkLabel { get => _rare.BookmarkLabel; set { if (_rare.BookmarkLabel != value) Rare.BookmarkLabel = value; } }
    public bool BookmarkClosed { get => _rare.BookmarkClosed; set { if (_rare.BookmarkClosed != value) Rare.BookmarkClosed = value; } }
    public CounterOp[]? CounterReset { get => _rare.CounterReset; set { if (_rare.CounterReset != value) Rare.CounterReset = value; } }
    public CounterOp[]? CounterSet { get => _rare.CounterSet; set { if (_rare.CounterSet != value) Rare.CounterSet = value; } }
    public CounterOp[]? CounterIncrement { get => _rare.CounterIncrement; set { if (_rare.CounterIncrement != value) Rare.CounterIncrement = value; } }
    /// <summary>The gap decoration properties other than a single column rule, or null when none is set.</summary>
    public GapRules? GapRules => _rare.GapRules;

    /// <summary>The gap decoration properties to change one in: a copy, since styles share them.</summary>
    public GapRules ChangeGapRules() => Rare.GapRules = _rare.GapRules?.Copy() ?? new GapRules();

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
        public bool FieldSizingContent;             // field-sizing: content: a form control takes the size of its value
        public SizeKeyword WidthKeyword;            // width: min-content, max-content or fit-content; Width is then auto
        public StretchSizes Stretched;              // the sizes set to stretch, which become lengths once the box's margins are known
        public bool ColumnBreakBefore, ColumnBreakAfter; // break-before/after: column, which only breaks inside a multicol
        public bool AvoidColumnBreakBefore, AvoidColumnBreakAfter, AvoidColumnBreakInside; // avoid-column: only inside a multicol
        public ShapeOutside? ShapeOutside;          // shape-outside on a float; null = none
        public Length ShapeMargin = Length.Zero;    // shape-margin; a percentage is of the containing block's width
        public float ShapeImageThreshold;           // shape-image-threshold, from 0 to 1
        public ShapeBox OverflowClipBox = ShapeBox.PaddingBox; // overflow-clip-margin: the box that overflow: clip clips to
        public float OverflowClipMargin;            // overflow-clip-margin: how far past that box, in points
        // overflow-x is auto or overlay, which OverflowX stores as hidden because they clip alike on paper. An SVG
        // viewport does not clip for auto, so the drawing code needs to tell them apart.
        public bool OverflowAuto;
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
        public bool TransformOriginSet;             // transform-origin was declared: an SVG element otherwise turns about 0 0
        public TransformBox TransformBox;           // transform-box: the box the origin and percentages refer to
        public float? Perspective;                  // points; null = none. Applies to the 3D transforms of the children
        public Length PerspectiveOriginX = new(50, LengthUnit.Percent), PerspectiveOriginY = new(50, LengthUnit.Percent);
        public string? Content;                     // the content property as written; null = normal
        public bool ContentNone;                    // content: none, which on ::marker removes the marker (normal keeps it)
        public string? StringSet;                   // string-set as written (named strings for running headers); null = none
        public string? RunningName;                 // position: running(name): taken out of the flow for element() in margin boxes
        public bool AppearanceNone;                 // appearance: none: a form control drawn only as its CSS box, without its native look
        public int? BookmarkLevel;                  // bookmark-level: null = unset (h1-h6 take their own level), 0 = none
        public string? BookmarkLabel;               // bookmark-label as written; null = content(text)
        public bool BookmarkClosed;                 // bookmark-state: closed
        public CounterOp[]? CounterReset, CounterSet, CounterIncrement;
        public GapRules? GapRules;                  // row rules, rule lists, breaks, insets, visibility and overlap; null = all initial

        public RareFields Copy()
        {
            var c = (RareFields)MemberwiseClone();
            c.Shared = false;
            return c;
        }
    }
}
