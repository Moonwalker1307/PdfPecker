using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;

namespace PdfPecker.Tests;

/// <summary>Font-relative units from the font (ex, ch, cap, ic, lh), viewport variants and logical inset and size properties.</summary>
public class CssUnitTests
{
    private const float PageWidth = 500, PageHeight = 800;

    private static Element Styled(string html, FontResolver? fonts = null)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Descendants(doc).Where(e => e.Name == "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, PageWidth, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block, Fonts = fonts });
        return doc;
    }

    private static IEnumerable<Element> Descendants(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static ComputedStyle Of(Element root, string id) => Descendants(root).First(e => e.Id == id).Style!;

    private static float Width(string css, string extra = "") => WidthOf(Of(Styled($"<div id=a style=\"{css}\">x</div>{extra}"), "a"));

    private static float WidthOf(ComputedStyle s) => s.Width.Value;

    // The OS/2 x-height and cap height of Liberation Sans, which browsers draw Helvetica and Arial with.
    private const float SansEx = 1082 / 2048f, SansCap = 1409 / 2048f;

    [Fact]
    public void ExIsTheXHeightOfTheFont()
    {
        Assert.Equal(10 * SansEx * 12, Width("font: 16px Helvetica; width: 10ex"), 0.05);
        Assert.Equal(10 * 940 / 2048f * 12, Width("font: 16px Times; width: 10ex"), 0.05);
    }

    [Fact]
    public void ChIsTheAdvanceOfTheDigitZero()
    {
        Assert.Equal(10 * 0.556f * 12, Width("font: 16px Helvetica; width: 10ch"), 0.05);
        Assert.Equal(10 * 0.5f * 12, Width("font: 16px Times; width: 10ch"), 0.05);
        Assert.Equal(10 * 0.6f * 12, Width("font: 16px Courier; width: 10ch"), 0.05);
    }

    [Fact]
    public void CapAndIcFollowTheFont()
    {
        Assert.Equal(4 * SansCap * 12, Width("font: 16px Helvetica; width: 4cap"), 0.05);
        // A standard font has no ideographs, so the ic unit falls back to one em.
        Assert.Equal(3 * 12, Width("font: 16px Helvetica; width: 3ic"), 0.05);
    }

    [Fact]
    public void TheFontIsMeasuredWhateverTheDeclarationOrder()
    {
        float expected = 10 * 0.6f * 12;
        Assert.Equal(expected, Width("width: 10ch; font-size: 16px; font-family: Courier"), 0.05);
        Assert.Equal(expected, Width("font-family: Courier; width: 10ch; font-size: 16px"), 0.05);
        // font-weight changes the face, so a bold width is the bold advance.
        Assert.Equal(10 * 0.556f * 12, Width("width: 10ch; font-weight: bold; font: 16px Helvetica; font-weight: bold"), 0.05);
    }

    [Fact]
    public void UnitsWorkInsideMathFunctionsAndLists()
    {
        Assert.Equal(2 * 0.6f * 12 + 3, Width("font: 16px Courier; width: calc(2ch + 4px)"), 0.05);
        Assert.Equal(2 * SansEx * 12, Width("font: 16px Helvetica; width: max(1px, 2ex)"), 0.05);
        var s = Of(Styled("<div id=a style=\"font: 16px Courier; margin: 1ch 2ch 3ch 4ch\"></div>"), "a");
        Assert.Equal(7.2f, s.MarginTop.Value, 0.05);
        Assert.Equal(28.8f, s.MarginLeft.Value, 0.05);
    }

    [Fact]
    public void FontSizeMeasuresTheParentFont()
    {
        // A child's font-size in ex is the parent's x-height, not its own.
        var root = Styled("<div style=\"font: 20px Helvetica\"><p id=a style=\"font-size: 2ex; font-family: Times\">x</p></div>");
        Assert.Equal(2 * SansEx * 15, Of(root, "a").FontSize, 0.05);
    }

    [Fact]
    public void LhIsTheLineHeight()
    {
        Assert.Equal(3 * 1.5f * 12, Width("font: 16px/1.5 Helvetica; width: 3lh"), 0.05);
        Assert.Equal(3 * 24 * 0.75f, Width("font: 16px/24px Helvetica; width: 3lh"), 0.05);
        // line-height: normal is the font's own spacing.
        var normal = StandardFont.Get(PdfPecker.Fonts.FontFamily.Helvetica, false, false).NormalLineHeight(12);
        Assert.Equal(2 * normal, Width("font: 16px Helvetica; width: 2lh"), 0.05);
    }

    [Fact]
    public void LhInLineHeightMeasuresTheParent()
    {
        var root = Styled("<div style=\"font: 16px/2 Helvetica\"><p id=a style=\"font-size: 8px; line-height: 1.5lh\">x</p></div>");
        Assert.Equal(1.5f * 2 * 12, Of(root, "a").LineHeightValue, 0.05);
    }

    [Fact]
    public void RootUnitsMeasureTheHtmlElement()
    {
        var doc = Styled("<html style=\"font: 20px/1.5 Courier\"><body><div id=a style=\"font: 8px Helvetica; width: 2rlh\"></div>"
                         + "<div id=b style=\"font: 8px Helvetica; width: 10rch\"></div></body></html>");
        Assert.Equal(2 * 1.5f * 15, Of(doc, "a").Width.Value, 0.05);
        Assert.Equal(10 * 0.6f * 15, Of(doc, "b").Width.Value, 0.05);
    }

    [Fact]
    public void CustomPropertiesAreMeasuredWhereTheyAreUsed()
    {
        var doc = Styled("<div style=\"--w: 10ch; font: 16px Courier\"><p id=a style=\"font: 8px Helvetica; width: var(--w)\"></p></div>");
        Assert.Equal(10 * 0.556f * 6, Of(doc, "a").Width.Value, 0.05);
    }

    [Fact]
    public void UnitsInStringsAndUrlsAreLeftAlone()
    {
        Assert.False(CssUnits.HasMetricUnit("url(a/2ch.png) \"3ex\" h1 #1ex"));
        Assert.True(CssUnits.HasMetricUnit("1px 2ch"));
        Assert.True(CssUnits.HasMetricUnit("calc(-1.5EX + 1e1px)"));
        Assert.False(CssUnits.HasMetricUnit("2em 3e2px 1x"));
        Assert.Equal("url(2ch.png) 6pt", CssUnits.Rewrite("url(2ch.png) 2ch", _ => 3));
        Assert.Equal("calc(-4.5pt + 1px)", CssUnits.Rewrite("calc(-1.5ex + 1px)", _ => 3));
    }

    [Fact]
    public void MetricUnitsOutsideADeclarationUseHalfAnEm()
    {
        Assert.True(StyleResolver.TryParseLength("4ex", 12, 12, out var ex));
        Assert.Equal(24, ex.Value, 0.05);
        Assert.True(StyleResolver.TryParseLength("2lh", 10, 12, out var lh));
        Assert.True(lh.Value > 20);
    }

    [Fact]
    public void FontsGiveXHeightAndCapHeight()
    {
        var program = Assert.Single(FontProgram.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "WorkSans-Regular.ttf"))));
        Assert.InRange(program.XHeight / (float)program.UnitsPerEm, 0.3, 0.7);
        var font = new EmbeddedFont(program, false, false);
        Assert.Equal(program.XHeight / (float)program.UnitsPerEm, font.Metric(FontMetric.XHeight), 4);
        Assert.InRange(font.Metric(FontMetric.CapHeight), 0.5, 0.9);
        Assert.Equal(1082 / 2048f, StandardFont.Get(PdfPecker.Fonts.FontFamily.Courier, true, false).Metric(FontMetric.XHeight), 4);
        // A fallback chain measures its first font.
        Assert.Equal(font.Metric(FontMetric.XHeight), new FontChain(font, (_, _) => null).Metric(FontMetric.XHeight), 4);
    }

    [Fact]
    public void EmbeddedFontsAreMeasured()
    {
        var collection = new FontCollection();
        collection.Add(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "WorkSans-Regular.ttf")));
        var fonts = FontResolver.Create(new(), collection, _ => null);
        var program = Assert.Single(FontProgram.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "WorkSans-Regular.ttf"))));
        var doc = Styled("<div id=a style=\"font: 20px 'Work Sans'; width: 10ex\"></div><div id=b style=\"font: 20px 'Work Sans'; width: 10ch\"></div>", fonts);
        float ex = program.XHeight / (float)program.UnitsPerEm;
        float zero = program.Advance1000(program.GlyphId('0')) / 1000f;
        Assert.Equal(10 * ex * 15, Of(doc, "a").Width.Value, 0.02);
        Assert.Equal(10 * zero * 15, Of(doc, "b").Width.Value, 0.02);
    }

    [Theory]
    [InlineData("vw", "vi")]
    [InlineData("vh", "vb")]
    [InlineData("vw", "svw")]
    [InlineData("vw", "lvw")]
    [InlineData("vw", "dvw")]
    [InlineData("vw", "svi")]
    [InlineData("vh", "svh")]
    [InlineData("vh", "lvh")]
    [InlineData("vh", "dvh")]
    [InlineData("vh", "dvb")]
    [InlineData("vmin", "svmin")]
    [InlineData("vmax", "dvmax")]
    public void ViewportUnitVariantsEqualTheClassicOnes(string classic, string variant)
    {
        float expected = Width($"width: 7{classic}");
        Assert.True(expected > 0);
        Assert.Equal(expected, Width($"width: 7{variant}"), 0.001);
    }

    [Fact]
    public void ViewportBlockUnitsCountAsViewportHeightUnits()
    {
        var resolver = new StyleResolver(new Stylesheet(), PageWidth, PageHeight);
        var doc = HtmlParser.Parse("<div style=\"height: 50dvb\"></div>");
        resolver.ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        Assert.True(resolver.UsedViewportHeight);
    }

    [Fact]
    public void InsetInlineAndBlockMapToTheSides()
    {
        var s = Of(Styled("<div id=a style=\"position: absolute; inset-inline: 3px 5px; inset-block: 7px 9px\"></div>"), "a");
        Assert.Equal(2.25f, s.Left.Value, 0.001);
        Assert.Equal(3.75f, s.Right.Value, 0.001);
        Assert.Equal(5.25f, s.Top.Value, 0.001);
        Assert.Equal(6.75f, s.Bottom.Value, 0.001);
        var one = Of(Styled("<div id=a style=\"position: absolute; inset-inline: 4px; inset-block-start: 1px\"></div>"), "a");
        Assert.Equal(3, one.Left.Value, 0.001);
        Assert.Equal(3, one.Right.Value, 0.001);
        Assert.Equal(0.75f, one.Top.Value, 0.001);
        Assert.True(one.Bottom.IsAuto);
    }

    [Fact]
    public void InsetInlineFollowsTheDirection()
    {
        var s = Of(Styled("<div id=a dir=rtl style=\"position: absolute; inset-inline-start: 4px; inset-inline-end: 8px\"></div>"), "a");
        Assert.Equal(3, s.Right.Value, 0.001);
        Assert.Equal(6, s.Left.Value, 0.001);
    }

    [Fact]
    public void LogicalSizesMapToWidthAndHeight()
    {
        var s = Of(Styled("<div id=a style=\"inline-size: 100px; block-size: 20px; min-inline-size: 10px; max-block-size: 50px\"></div>"), "a");
        Assert.Equal(75, s.Width.Value, 0.001);
        Assert.Equal(15, s.Height.Value, 0.001);
        Assert.Equal(7.5f, s.MinWidth.Value, 0.001);
        Assert.Equal(37.5f, s.MaxHeight.Value, 0.001);
    }

    [Fact]
    public void LogicalPropertiesAreSupported()
    {
        Assert.True(CssSupports.Evaluate("(inset-inline: 0) and (inline-size: 1px) and (max-block-size: 1px)"));
    }
}
