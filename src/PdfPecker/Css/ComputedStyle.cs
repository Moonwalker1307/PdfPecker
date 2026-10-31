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
    /// <summary>The length, percentage or fraction; for a MaxContent maximum from fit-content(), the cap (0: none).</summary>
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

    /// <summary>
    /// A <c>subgrid</c> value: the tracks are those of the parent grid that the item spans, and the list only names
    /// the subgrid's lines. <see cref="SubgridNames"/> and <see cref="SubgridNamesAfter"/> are the name lists before
    /// and after a <c>repeat(auto-fill, ...)</c> of <see cref="SubgridAutoFill"/>; without one, the second is empty.
    /// </summary>
    public bool Subgrid;
    public List<string[]> SubgridNames = new(), SubgridNamesAfter = new();
    public string[][]? SubgridAutoFill;

    /// <summary>How many lines a subgrid names outright, which decides its span when its placement leaves it automatic.</summary>
    public int SubgridNamedLines => SubgridNames.Count + SubgridNamesAfter.Count;

    /// <summary>
    /// The subgrid's own names for each of its <paramref name="lines"/> lines, in order: the auto-fill repetition is
    /// repeated as often as fits between the fixed lists, and lists beyond the last line are dropped.
    /// </summary>
    public string[]?[] SubgridLineNames(int lines)
    {
        var result = new string[]?[lines];
        int i = 0;
        foreach (var names in SubgridNames) { if (i >= lines) break; result[i++] = names; }
        if (SubgridAutoFill is { Length: > 0 } repeat)
        {
            int room = lines - SubgridNames.Count - SubgridNamesAfter.Count;
            for (int k = room / repeat.Length; k > 0; k--)
                foreach (var names in repeat) result[i++] = names;
        }
        foreach (var names in SubgridNamesAfter) { if (i >= lines) break; result[i++] = names; }
        return result;
    }
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

/// <summary>
/// A stroke-dasharray: dash and gap lengths in user units, or percentages of the viewport's normalised diagonal where
/// <see cref="Percent"/> says so. An odd-length list is kept as written; it repeats when the dashes are drawn.
/// </summary>
internal sealed class SvgDashArray
{
    /// <summary>The most values a dash array keeps; a longer list is ignored, so one property cannot blow up every stroke it reaches.</summary>
    public const int MaxValues = 256;

    public readonly float[] Values;
    public readonly bool[]? Percent; // null when no value is a percentage

    public SvgDashArray(float[] values, bool[]? percent) { Values = values; Percent = percent; }
}

internal enum TextAlign : byte { Left, Right, Center, Justify, Start, End } // Start and End follow the direction
// text-justify; distribute is inter-character's old name.
internal enum TextJustify : byte { Auto, None, InterWord, InterCharacter }
// text-spacing-trim: how fullwidth CJK punctuation sheds its blank half. normal trims it next to other punctuation and
// at the end of a line when that makes it fit; space-first also at the start of every line but a paragraph's first,
// trim-start at the start of every line, and space-all never.
internal enum TextSpacingTrim : byte { Normal, SpaceAll, SpaceFirst, TrimStart }
internal enum UnicodeBidi : byte { Normal, Embed, Isolate, BidiOverride, IsolateOverride, Plaintext }

/// <summary>Values of writing-mode. The vertical modes are laid out as horizontal text turned a quarter turn.</summary>
internal enum WritingMode : byte { HorizontalTb, VerticalRl, VerticalLr, SidewaysRl, SidewaysLr }
// PreLineNoWrap is pre-line's handling of white space with text-wrap-mode: nowrap, which no white-space keyword gives.
internal enum WhiteSpace : byte { Normal, NoWrap, Pre, PreWrap, PreLine, PreLineNoWrap }
internal enum TextWrapStyle : byte { Auto, Balance, Pretty } // text-wrap-style; stable lays out as auto on paper
internal enum VerticalAlign : byte { Baseline, Sub, Super, Top, Middle, Bottom, TextTop, TextBottom, Length }
internal enum OverflowWrap : byte { Normal, BreakWord, Anywhere }
internal enum Overflow : byte { Visible, Clip, Hidden } // Hidden also stands for auto and scroll, which clip alike on paper
internal enum WordBreak : byte { Normal, BreakAll, KeepAll }
internal enum LineBreak : byte { Auto, Loose, Normal, Strict, Anywhere }
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
// text-decoration-skip-ink: auto leaves gaps where underlines and overlines cross glyphs (not CJK ones); all also at CJK.
internal enum SkipInk : byte { Auto, None, All }
// text-underline-position: near the baseline as the UA likes, where the font says, or under the em box (clear of
// descenders). left and right only matter in vertical text, which draws them as auto.
internal enum UnderlinePosition : byte { Auto, FromFont, Under }
// text-emphasis-style: the shape of the mark drawn by each character. Auto is filled or open alone, a dot in horizontal
// text and sesame in vertical text, as in Chrome; String is a mark of the author's own.
internal enum EmphasisShape : byte { None, Auto, Dot, Circle, DoubleCircle, Triangle, Sesame, String }
// text-box-trim: whether a block's first line loses the space above its over edge and its last line the space below
// its under edge.
[Flags] internal enum TextBoxTrim : byte { None = 0, Start = 1, End = 2, Both = Start | End }
// text-box-edge: the edges lines are trimmed to, over and under the text. auto trims as text does, as in Chrome.
internal enum TextEdgeOver : byte { Text, Cap, Ex }
internal enum TextEdgeUnder : byte { Text, Alphabetic }

/// <summary>Pt and Percent are plain values; Calc is a math function that depends on a percentage (see <see cref="CssMath"/>).</summary>
internal enum LengthUnit : byte { Pt, Percent, Auto, Calc }

internal enum Position : byte { Static, Relative, Absolute, Fixed }

internal enum FloatSide : byte { None, Left, Right }

/// <summary>An intrinsic size keyword given for width (CSS Sizing 3): the box is as wide as its content says.</summary>
internal enum SizeKeyword : byte { None, MinContent, MaxContent, FitContent }

/// <summary>
/// The sizes given the stretch keyword (or Chrome's older -webkit-fill-available): the box fills its containing block,
/// less its margins, as a block with an auto width does. The length itself is the percentage that does that.
/// </summary>
[Flags]
internal enum StretchSizes : byte { None = 0, Width = 1, Height = 2, MinWidth = 4, MaxWidth = 8, MinHeight = 16, MaxHeight = 32 }

/// <summary>How a footnote sits in the footnote area (footnote-display); compact is laid out as inline.</summary>
internal enum FootnoteDisplay : byte { Block, Inline, Compact }

/// <summary>What happens when a footnote does not fit on the page of its call (footnote-policy).</summary>
internal enum FootnotePolicy : byte { Auto, Line, Block }

/// <summary>
/// One function of a CSS transform list. Translations are in points, or percentages of the border box when flagged; a
/// translation's C is its z offset. The 3D functions keep what they need in A to D (a scale's z, a rotation's axis and
/// angle, a perspective's distance) or, for matrix3d, in the sixteen values of M in CSS (column-major) order.
/// </summary>
internal readonly record struct TransformFn(TransformKind Kind, float A, float B = 0, float C = 0, float D = 0, float E = 0, float F = 0,
    bool PercentX = false, bool PercentY = false, float[]? M = null);

internal enum TransformKind : byte { Translate, Scale, Rotate, Skew, Matrix, Scale3d, Rotate3d, Perspective, Matrix3d }

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

    /// <summary>
    /// The largest magnitude, in points or percent, that a length keeps, and that a percentage resolves to. Like
    /// browsers, which clamp lengths to the range their layout units hold, larger values are clamped: a length near the
    /// float range overflows to infinity once layout adds or scales it (nested percentages multiply), and the NaN that
    /// follows from infinity minus infinity breaks every box built on it. About 380 km is still far more than the page
    /// limit holds, so absurd lengths keep ending in <see cref="PdfLimitException"/>, while sums and products of a few
    /// such lengths stay well inside the float range.
    /// </summary>
    public const float MaxMagnitude = 1 << 30;

    public Length(float value, LengthUnit unit) { Value = Clamp(value); Unit = unit; }

    private Length(float value, CalcParts calc) { Value = Clamp(value); Unit = LengthUnit.Calc; _calc = calc; }

    /// <summary>Clamps <paramref name="v"/> to <see cref="MaxMagnitude"/>, and NaN, which has no meaningful size, to zero.</summary>
    public static float Clamp(float v) => float.IsNaN(v) ? 0 : Math.Clamp(v, -MaxMagnitude, MaxMagnitude);

    public static readonly Length Auto = new(0, LengthUnit.Auto);
    public static readonly Length Zero = new(0, LengthUnit.Pt);
    public static Length Pt(float v) => new(v, LengthUnit.Pt);
    /// <summary>A sum of points and a percentage, such as <c>calc(100% - 20pt)</c>.</summary>
    public static Length Calc(float pt, float pct) => new(pt, new CalcParts(Clamp(pct), null));
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
            LengthUnit.Percent => Clamp(Value * basis / 100f),
            LengthUnit.Calc => _calc!.Fn == null ? Clamp(Value + _calc.Pct * basis / 100f) : Finite(_calc.Fn(basis)),
            _ => autoValue,
        };

    private static float Finite(float v) => float.IsFinite(v) ? Clamp(v) : 0;

    public override string ToString() => Unit switch
    {
        LengthUnit.Pt => Value + "pt",
        LengthUnit.Percent => Value + "%",
        LengthUnit.Calc => IsLinear ? $"calc({Pct}% + {Value}pt)" : "calc(...)",
        _ => "auto",
    };
}

/// <summary>
/// hyphenate-limit-chars: the shortest word that is hyphenated and the fewest characters before and after a hyphen.
/// Each value is 0 for auto, which leaves it to the hyphenator's defaults.
/// </summary>
internal readonly record struct HyphenateLimits(int Word, int Before, int After)
{
    /// <summary>The largest value kept; a larger one is clamped to it, which no real word reaches either.</summary>
    public const int Max = 1000;
}

/// <summary>The computed values of the supported CSS properties for one element. All lengths are in points.</summary>
internal sealed partial class ComputedStyle
{
    // The properties few boxes set are in shared groups, in ComputedStyle.Groups.cs.

    // ---- Inherited ----
    public uint Color = CssColor.Black;
    public FontFamily FontFamily = FontFamily.Times;
    public bool Bold;
    public int FontWeight = 400;
    public bool Italic;
    public float FontStretch = 100;          // font-stretch as a percentage of normal width
    public float FontSize = 12f; // 16px
    public float ParentFontSize = 12f; // the parent's font size, which sub and super shift by
    public byte FontSizeKeyword = 4;   // the font-size keyword the size comes from (1 xx-small to 8 xxx-large); 0 = none
    public bool FontSizeAbsolute;      // the size comes from an absolute length, here or on an ancestor
    public float ParentBaselineShift;  // how far below the line's baseline the parent inline box's baseline sits
    public ComputedStyle? ParentStyle; // the parent's style, whose font text-top, text-bottom and middle align to
    public LineHeightKind LineHeightKind = LineHeightKind.Normal;
    public float LineHeightValue;
    public TextAlign TextAlign = TextAlign.Start;
    public bool Rtl;                         // direction: rtl
    public WritingMode WritingMode;          // writing-mode
    public bool NoHyphens;                   // hyphens: none (soft hyphens are no break opportunity)
    public bool HyphensAuto;                 // hyphens: auto (words also break where the language's patterns allow)
    public string? Lang;                     // the content language: the nearest lang attribute, else PdfOptions.Language
    public bool Quirks;                      // the document has no doctype, or an old one: quirks mode
    public IReadOnlyDictionary<string, HyphenationPatterns>? Hyphenation; // PdfOptions.Hyphenation, keys in lower case
    public byte SmallCaps;                   // font-variant-caps: 1 small-caps, 2 all-small-caps (synthesized)
    public WhiteSpace WhiteSpace;            // with text-wrap-mode, which it is a shorthand for
    public TextWrapStyle TextWrap;
    public OverflowWrap OverflowWrap;
    public WordBreak WordBreak;
    public ListStyleType ListStyleType;
    public bool ListStylePositionInside;
    public TextTransform TextTransform;
    public TextDecoration TextDecoration; // propagated to descendants, approximated as inherited
    public Length TextIndent;           // percentages of the block's width
    public float LetterSpacing;
    public bool NoLigatures; // font-variant-ligatures: none or no-common-ligatures, or font-feature-settings: "liga" 0
    public bool NoKerning;               // font-kerning: none, or font-feature-settings: "kern" 0
    public float WordSpacing;
    public bool Visible = true;
    public bool CanvasBackground; // the background is painted on every page's canvas instead of on this box (html/body propagation)
    public bool BorderCollapse;
    public float BorderSpacingH, BorderSpacingV; // initially 0; the user agent style gives <table> 2px
    public string? FontFamilyName;
    public FontResolver? Fonts; // the document's fonts; null when only the standard fonts exist
    public MissingFontReport? MissingFonts; // set when PdfOptions.MissingFont is
    public CssVarScope? CustomProperties; // custom properties (--name), shared with the parent until changed


    // ---- Not inherited ----
    public Display Display = Display.Inline;
    public bool InlineTable;                    // display: inline-table, a table that sits on a line like an inline-block
    public bool Contents;                       // display: contents, an element without a box whose children take its place
    public UnicodeBidi UnicodeBidi;
    public uint BackgroundColor;
    public Length MarginTop = Length.Zero, MarginRight = Length.Zero, MarginBottom = Length.Zero, MarginLeft = Length.Zero;
    public Length PaddingTop = Length.Zero, PaddingRight = Length.Zero, PaddingBottom = Length.Zero, PaddingLeft = Length.Zero;
    public float BorderTopWidth = 1.5f, BorderRightWidth = 1.5f, BorderBottomWidth = 1.5f, BorderLeftWidth = 1.5f; // medium = 3px
    public BorderStyle BorderTopStyle, BorderRightStyle, BorderBottomStyle, BorderLeftStyle;
    // The sides whose border-style is hidden (top 1, right 2, bottom 4, left 8): drawn as none, but in a table with
    // collapsed borders a hidden side also hides the borders it meets (CSS 2.1 §17.6.2.1).
    public byte BorderHiddenSides;
    public uint? BorderTopColor, BorderRightColor, BorderBottomColor, BorderLeftColor; // null = currentColor
    // border-radius: horizontal and vertical radius of the top-left, top-right, bottom-right and bottom-left corner
    // (percentages of the border box's width and height); null when every corner is square.
    public Length[]? BorderRadii;
    // corner-shape: the superellipse parameter of the same four corners (1 round, 0 bevel, ...); null when all are round.
    public float[]? CornerShapes;
    public bool TextOverflowEllipsis => TextOverflow != null;
    public Length Width = Length.Auto, Height = Length.Auto;
    public Length MinWidth = Length.Auto, MaxWidth = Length.Auto, MinHeight = Length.Zero, MaxHeight = Length.Auto;
    public bool BorderBox;
    public VerticalAlign VerticalAlign;
    public Length VerticalAlignLength = Length.Zero; // the raise of vertical-align: <length-percentage>
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
    public bool FormattingRoot;                 // overflow other than visible, or display: flow-root
    public Overflow OverflowX, OverflowY;

    /// <summary>Whether the box clips its content horizontally and vertically (visible next to hidden computes to auto).</summary>
    public bool ClipsX => OverflowX == Overflow.Clip || OverflowX == Overflow.Hidden || OverflowX == Overflow.Visible && OverflowY == Overflow.Hidden;
    public bool ClipsY => OverflowY == Overflow.Clip || OverflowY == Overflow.Hidden || OverflowY == Overflow.Visible && OverflowX == Overflow.Hidden;
    public string? PageName;                    // page: the named page this box goes on (inherited; null = auto)

    public bool BoxReverse; // -webkit-box-direction: reverse, which is inherited

    private TextFont? _font;

    /// <summary>The font text is drawn with. Like browsers, letter-spaced text has no ligatures.</summary>
    public TextFont Font
    {
        get
        {
            var f = BaseFont;
            if ((FontVariantNumeric != null || FontFeatureSettings != null || FontVariantEastAsian != null || FontVariantPosition != null || SmallCaps != 0)
                && FontFeatures.Key(FontVariantNumeric, FontFeatureSettings, FontVariantEastAsian, FontVariantPosition, SmallCaps) is { } features)
                f = f.WithFeatures(features);
            if (NoKerning) f = f.WithoutKerning();
            return NoLigatures || LetterSpacing != 0 ? f.WithoutLigatures() : f;
        }
    }

    // The font before OpenType features, ligatures and kerning are applied.
    private TextFont BaseFont => _font ??= Fonts == null ? StandardFor() : Fonts.Resolve(FontFamilyName, FontFamily, FontWeight, Italic,
        new FontVariation(FontStretch, FontOpticalSizingNone ? 0 : FontSize / 0.75f, FontVariationSettings, Synthesis & (FontSynthesis.Weight | FontSynthesis.Style)));

    /// <summary>
    /// Whether font-variant-caps takes the font's own small capitals (smcp, and c2sc for all-small-caps), as browsers do
    /// when the font has them.
    /// </summary>
    public bool HasRealSmallCaps => SmallCaps != 0 && BaseFont.Supports("smcp") && (SmallCaps != 2 || BaseFont.Supports("c2sc"));

    /// <summary>Whether font-variant-caps makes small capitals from smaller capitals: the font has none and font-synthesis allows it.</summary>
    public bool SynthesizesSmallCaps => SmallCaps != 0 && (Synthesis & FontSynthesis.SmallCaps) != 0 && !HasRealSmallCaps;

    private StandardFont StandardFor()
    {
        MissingFonts?.CheckStandardOnly(FontFamilyName);
        return StandardFont.Get(FontFamily, Bold, Italic);
    }

    /// <summary>The standard PDF font for this style, used where embedded fonts are not supported (SVG text).</summary>
    public StandardFont StandardFont => StandardFont.Get(FontFamily, Bold, Italic);

    public void InvalidateFont() => _font = null;

    /// <summary>The size glyphs are drawn and measured at: the font size, changed by font-size-adjust when it is set.</summary>
    public float TextSize => SizeAdjust.IsNone ? FontSize : SizeAdjust.Apply(FontSize, Font);

    /// <summary>The font the ex, ch and cap units measure. The style may still change, so the font is not kept.</summary>
    public TextFont UnitFont()
    {
        var font = Font;
        _font = null;
        return font;
    }

    /// <summary>
    /// The used line height. A huge value is capped (as Chrome caps its layout units) so that sums of line heights stay
    /// finite; an infinite height makes the box geometry NaN.
    /// </summary>
    /// <remarks>
    /// Chrome keeps a line height in layout units of 1/64 pixel, and lines like 11px at 1.45 drift a pixel from it over
    /// a page when the exact value is used. A length is rounded to the nearest unit. A number is kept as a percentage,
    /// which takes the font size rounded to a unit and rounds the product down.
    /// </remarks>
    public float LineHeight
        => Math.Min(MaxLineHeight, LineHeightKind switch
        {
            LineHeightKind.Factor => FactorLineHeight(LineHeightValue, FontSize),
            LineHeightKind.Absolute => MathF.Round(LineHeightValue * LayoutUnitsPerPoint, MidpointRounding.AwayFromZero) / LayoutUnitsPerPoint,
            _ => Font.NormalLineHeight(TextSize),
        });

    // Chrome's layout unit is 1/64 of a CSS pixel, which is 3/4 of a point.
    private const float LayoutUnitsPerPoint = 64 / 0.75f, PointsPerPixel = 0.75f;

    /// <summary>
    /// A number line height in points, computed in single precision in the order Chrome uses, so that 1.15 times 20px
    /// stays 23px.
    /// </summary>
    private static float FactorLineHeight(float factor, float fontSize)
    {
        float percent = (float)(factor * 100.0);
        float fontPx = MathF.Round(fontSize * LayoutUnitsPerPoint, MidpointRounding.AwayFromZero) / 64;
        return MathF.Floor(fontPx * percent / 100f * 64) / 64 * PointsPerPixel;
    }

    private const float MaxLineHeight = 1e7f;

    /// <summary>
    /// How far below the line's baseline this box's baseline sits, in points: its own sub, super or length shift added
    /// to the shift of the inline box it is in, so that a superscript inside a superscript is raised again.
    /// </summary>
    public float BaselineShift => ParentBaselineShift + OwnBaselineShift + TextEdgeShift;

    /// <summary>
    /// <see cref="BaselineShift"/> for an image or inline-block: middle, text-top and text-bottom place those by their
    /// own height on the line, not by the font.
    /// </summary>
    public float AtomicBaselineShift => ParentBaselineShift + OwnBaselineShift;

    /// <summary>
    /// The shift vertical-align gives this box against its parent's baseline. Chrome raises a superscript by a third of
    /// the parent's font size plus one pixel and lowers a subscript by a fifth plus one pixel, kept in layout units of
    /// 1/64 pixel; a length raises by that much, and a percentage by that share of the element's own line height.
    /// </summary>
    private float OwnBaselineShift
    {
        get
        {
            if (VerticalAlign == VerticalAlign.Length) return -VerticalAlignLength.Resolve(LineHeight);
            if (VerticalAlign is not (VerticalAlign.Super or VerticalAlign.Sub)) return 0;
            float px = ParentFontSize / PointsPerPixel;
            float shift = VerticalAlign == VerticalAlign.Super ? -(px / 3 + 1) : px / 5 + 1;
            return MathF.Truncate(shift * 64) / 64 * PointsPerPixel;
        }
    }

    /// <summary>
    /// The shift that vertical-align: middle, text-top or text-bottom gives an inline box against its parent's baseline,
    /// measured on the box's line height as Chrome does: middle centres it on the parent's baseline raised by half the
    /// parent's x-height, and text-top and text-bottom line it up with the top or bottom of the parent's font.
    /// </summary>
    private float TextEdgeShift
    {
        get
        {
            if (Display != Display.Inline || VerticalAlign is not (VerticalAlign.Middle or VerticalAlign.TextTop or VerticalAlign.TextBottom)
                || ParentStyle is not { } parent)
                return 0;
            Strut(out float a, out float d);
            var font = parent.Font;
            float size = parent.TextSize;
            if (VerticalAlign == VerticalAlign.Middle)
            {
                float xHeight = font.Metric(FontMetric.XHeight) is > 0 and var x ? x * size : size / 2;
                return (a - d - xHeight) / 2;
            }
            font.LineMetrics(size, out float ascent, out float descent);
            return VerticalAlign == VerticalAlign.TextTop ? a - ascent : descent - d;
        }
    }

    /// <summary>
    /// The ascent and descent of this style's strut: its font and line height on its own baseline. Chrome gives the top
    /// half of the leading whole CSS pixels and the rest to the bottom.
    /// </summary>
    /// <summary>The text-emphasis mark drawn by each character, or null for none.</summary>
    public string? EmphasisMark => EmphasisShape switch
    {
        EmphasisShape.None => null,
        EmphasisShape.String => EmphasisString is { Length: > 0 } mark ? mark : null,
        EmphasisShape.Auto => ShapeMark(WritingMode == WritingMode.HorizontalTb ? EmphasisShape.Dot : EmphasisShape.Sesame),
        _ => ShapeMark(EmphasisShape),
    };

    /// <summary>The character Chrome draws for a shape, filled or open.</summary>
    private string ShapeMark(EmphasisShape shape) => shape switch
    {
        EmphasisShape.Dot => EmphasisOpen ? "\u25E6" : "\u2022",
        EmphasisShape.Circle => EmphasisOpen ? "\u25CB" : "\u25CF",
        EmphasisShape.DoubleCircle => EmphasisOpen ? "\u25CE" : "\u25C9",
        EmphasisShape.Triangle => EmphasisOpen ? "\u25B3" : "\u25B2",
        _ => EmphasisOpen ? "\uFE46" : "\uFE45",
    };

    public void Strut(out float ascent, out float descent)
    {
        Font.LineMetrics(TextSize, out float asc, out float desc);
        float lineH = LineHeight;
        float half = MathF.Floor((lineH - (asc + desc)) / 2 / PointsPerPixel + HalfLeadingSlack) * PointsPerPixel;
        ascent = asc + half;
        descent = lineH - ascent;
    }

    /// <summary>
    /// How far above and below its own baseline this style's text-box-edge lies: the font's ascent and descent for
    /// text, its cap height or x-height over the text, and the baseline itself for alphabetic. Lines are trimmed to
    /// these edges, as Chrome trims them.
    /// </summary>
    public void TextBoxEdges(out float over, out float under)
    {
        var font = Font;
        font.LineMetrics(TextSize, out float ascent, out float descent);
        over = TextEdgeOver switch
        {
            TextEdgeOver.Cap => font.Metric(FontMetric.CapHeight) is > 0 and var cap ? cap * TextSize : ascent,
            TextEdgeOver.Ex => font.Metric(FontMetric.XHeight) is > 0 and var ex ? ex * TextSize : ascent,
            _ => ascent,
        };
        under = TextEdgeUnder == TextEdgeUnder.Alphabetic ? 0 : descent;
    }

    // The half-leading is rounded down to whole pixels, with a little slack against rounding error.
    private const float HalfLeadingSlack = 0.001f;

    /// <summary>
    /// The baseline shift this box hands to its children. Only an inline box passes its shift on: an inline-block,
    /// table cell or block lays its content out on lines of its own.
    /// </summary>
    public float ChildBaselineShift => Display == Display.Inline ? BaselineShift : 0;

    // Effective border widths: zero when the style is none.
    public float BorderTop => BorderTopStyle == BorderStyle.None ? 0 : BorderTopWidth;
    public float BorderRight => BorderRightStyle == BorderStyle.None ? 0 : BorderRightWidth;
    public float BorderBottom => BorderBottomStyle == BorderStyle.None ? 0 : BorderBottomWidth;
    public float BorderLeft => BorderLeftStyle == BorderStyle.None ? 0 : BorderLeftWidth;

    public bool HasBorder => BorderTopStyle != BorderStyle.None || BorderRightStyle != BorderStyle.None
                             || BorderBottomStyle != BorderStyle.None || BorderLeftStyle != BorderStyle.None;

    public bool IsBlockLevel => Display is not (Display.Inline or Display.InlineBlock or Display.InlineFlex or Display.InlineGrid) && !IsInlineTable;

    /// <summary>An inline-level box laid out as a whole on its line: inline-block, inline-flex, inline-grid or inline-table.</summary>
    public bool IsAtomicInline => Display is Display.InlineBlock or Display.InlineFlex or Display.InlineGrid || IsInlineTable;

    public bool IsInlineTable => InlineTable && Display == Display.Table;

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
    /// <summary>The clip rectangle applies only to absolutely positioned boxes.</summary>
    public bool Clipped => ClipRect != null && OutOfFlow;

    /// <summary>Positioned (anything but static): a containing block for absolutely positioned descendants.</summary>
    public bool Positioned => Position != Position.Static;

    /// <summary>Painted in the positioned layer: positioned, transformed or translucent.</summary>
    public bool Layered => Position != Position.Static || Transform != null || Perspective != null || Opacity < 1 || HasEffects;

    /// <summary>Clipped, masked, blended, isolated or filtered: painted as a layer of its own, which is a stacking context.</summary>
    public bool HasEffects => ClipShape != null || SvgClipPath != null || BlendMode != null || Isolate || Filters != null || MaskImages != null
                              || BackdropFilters != null;

    /// <summary>
    /// The quarter turn, in degrees clockwise, that turns horizontal layout into this box's writing mode: 90 for the
    /// vertical modes and sideways-rl, whose text runs down, and -90 for sideways-lr, whose text runs up.
    /// </summary>
    public int WritingModeTurn => WritingMode switch
    {
        WritingMode.HorizontalTb => 0,
        WritingMode.SidewaysLr => -90,
        _ => 90,
    };

    /// <summary>Creates the starting style for a child: inherited properties copied, the rest reset.</summary>
    public ComputedStyle CreateChild()
    {
        ShareInheritedGroups();
        var c = (ComputedStyle)MemberwiseClone();
        c.ResetGroups();
        c.ParentFontSize = FontSize;
        c.ParentBaselineShift = ChildBaselineShift;
        c.ParentStyle = this;
        c.Display = Display.Inline;
        c.InlineTable = false;
        c.Contents = false;
        c.BackgroundColor = 0;
        c.CanvasBackground = false;
        c.MarginTop = c.MarginRight = c.MarginBottom = c.MarginLeft = Length.Zero;
        c.PaddingTop = c.PaddingRight = c.PaddingBottom = c.PaddingLeft = Length.Zero;
        c.BorderTopWidth = c.BorderRightWidth = c.BorderBottomWidth = c.BorderLeftWidth = 1.5f;
        c.BorderTopStyle = c.BorderRightStyle = c.BorderBottomStyle = c.BorderLeftStyle = BorderStyle.None;
        c.BorderHiddenSides = 0;
        c.BorderTopColor = c.BorderRightColor = c.BorderBottomColor = c.BorderLeftColor = null;
        c.BorderRadii = null;
        c.CornerShapes = null;
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
        c.FormattingRoot = false;
        c.OverflowX = c.OverflowY = Overflow.Visible;
        c.UnicodeBidi = UnicodeBidi.Normal;
        return c;
    }

    public ComputedStyle Clone()
    {
        ShareInheritedGroups();
        ShareGroups();
        return (ComputedStyle)MemberwiseClone();
    }
}
