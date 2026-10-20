using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>calc(), min(), max() and clamp().</summary>
public class CssMathTests
{
    private const float Width = 500, PageHeight = 800;

    private static List<DrawOp> Layout(string html, float width = Width)
    {
        var doc = HtmlParser.Parse(html, impliedStructure: false);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, width);
        return engine.Ops;
    }

    private static Element Styled(string html)
    {
        var doc = HtmlParser.Parse(html, impliedStructure: false);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc;
    }

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) yield return c;
            foreach (var d in Find(c, name)) yield return d;
        }
    }

    private static Element ById(Element root, string id) => Descendants(root).First(e => e.Id == id);

    private static IEnumerable<Element> Descendants(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static RectOp Box(List<DrawOp> ops, string color)
    {
        Assert.True(CssColor.TryParse(color, 0xFF000000, out var c));
        return Flatten(ops).OfType<RectOp>().Single(r => r.Color == c);
    }

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var o in ops)
        {
            if (o is GroupOp g) { foreach (var inner in Flatten(g.Ops)) yield return inner; }
            else yield return o;
        }
    }

    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    private static float Len(string css, float basis = 0, float em = 12)
    {
        Assert.True(StyleResolver.TryParseLength(css, em, 12, out var len, 5, 8), css);
        return len.Resolve(basis);
    }

    [Theory]
    [InlineData("calc(10pt + 5pt)", 15)]
    [InlineData("calc(10pt - 2 * 3pt)", 4)]
    [InlineData("calc((10pt - 2pt) * 3)", 24)]
    [InlineData("calc(1in / 4)", 18)]
    [InlineData("calc(2em + 4px)", 27)]                // em = 12pt, 4px = 3pt
    [InlineData("calc(10vw - 1rem)", 38)]              // 1vw = 5pt here, 1rem = 12pt
    [InlineData("calc(calc(2pt + 3pt) * calc(4 / 2))", 10)]
    [InlineData("min(10pt, 20pt, 5pt)", 5)]
    [InlineData("max(10pt, 2em)", 24)]
    [InlineData("clamp(10pt, 50pt, 30pt)", 30)]
    [InlineData("clamp(10pt, 1pt, 30pt)", 10)]
    [InlineData("clamp(40pt, 1pt, 30pt)", 40)]         // the minimum wins over the maximum
    [InlineData("calc(min(10pt, 4pt) + max(1pt, 2pt))", 6)]
    [InlineData("CALC(1PT + 1PT)", 2)]
    [InlineData("-webkit-calc(1pt + 1pt)", 2)]
    [InlineData("calc(1.5e1pt - 5pt)", 10)]
    [InlineData("calc(-5pt + 10pt)", 5)]
    public void Absolute_expressions_evaluate_while_parsing(string css, float expected)
    {
        Assert.True(StyleResolver.TryParseLength(css, 12, 12, out var len, 5, 8), css);
        Assert.Equal(LengthUnit.Pt, len.Unit);
        Near(expected, len.Value);
    }

    [Theory]
    [InlineData("calc(100% - 20pt)", 400, 380)]
    [InlineData("calc(50% + 10pt)", 200, 110)]
    [InlineData("calc((100% - 30pt) / 3)", 330, 100)]
    [InlineData("min(100%, 300pt)", 500, 300)]
    [InlineData("min(100%, 300pt)", 200, 200)]
    [InlineData("max(50%, 120pt)", 200, 120)]
    [InlineData("clamp(100pt, 50%, 250pt)", 1000, 250)]
    [InlineData("clamp(100pt, 50%, 250pt)", 300, 150)]
    [InlineData("clamp(100pt, 50%, 250pt)", 100, 100)]
    [InlineData("calc(min(50%, 100pt) + 10pt)", 400, 110)]
    [InlineData("calc(2 * max(10%, 5pt))", 200, 40)]
    public void Percentages_resolve_against_the_reference_length(string css, float basis, float expected)
    {
        Assert.True(StyleResolver.TryParseLength(css, 12, 12, out var len, 5, 8), css);
        Assert.True(len.IsPercent);
        Near(expected, len.Resolve(basis));
    }

    [Fact]
    public void Pure_percentages_stay_plain_percentages()
    {
        Assert.True(StyleResolver.TryParseLength("calc(100% / 3)", 12, 12, out var len));
        Assert.Equal(LengthUnit.Percent, len.Unit);
        Near(33.333f, len.Value, 0.01f);
        Assert.True(StyleResolver.TryParseLength("min(40%, 60%)", 12, 12, out len));
        Assert.Equal(LengthUnit.Percent, len.Unit);
        Near(40, len.Value);
    }

    [Theory]
    [InlineData("calc(10pt + 5)")]        // a length plus a number
    [InlineData("calc(10pt * 5pt)")]      // a length times a length
    [InlineData("calc(10pt / 0)")]
    [InlineData("calc(10pt / 2pt)")]
    [InlineData("calc(10pt + )")]
    [InlineData("calc(10pt")]
    [InlineData("calc(5)")]               // a number where a length is needed
    [InlineData("min(10pt, 5)")]
    [InlineData("clamp(1pt, 2pt)")]
    [InlineData("calc(10foo)")]
    [InlineData("calc(10pt) extra")]
    [InlineData("sin(10pt)")]
    public void Invalid_expressions_are_rejected(string css)
        => Assert.False(StyleResolver.TryParseLength(css, 12, 12, out _), css);

    [Fact]
    public void Deep_nesting_is_rejected_instead_of_overflowing_the_stack()
    {
        string deep = string.Concat(Enumerable.Repeat("calc(", 5000)) + "1pt" + new string(')', 5000);
        Assert.False(StyleResolver.TryParseLength(deep, 12, 12, out _));
        string ok = string.Concat(Enumerable.Repeat("calc(", 10)) + "1pt" + new string(')', 10);
        Assert.True(StyleResolver.TryParseLength(ok, 12, 12, out _));
    }

    [Fact]
    public void Numbers_are_folded_for_number_properties()
    {
        Assert.Equal("0.5", CssMath.FoldNumbers("calc(1 / 2)"));
        Assert.Equal("rgb(128 0 0)", CssMath.FoldNumbers("rgb(calc(256 / 2) 0 0)"));
        Assert.Equal("calc(1pt + 2pt)", CssMath.FoldNumbers("calc(1pt + 2pt)"));
        Assert.Equal("\"calc(1)\"", CssMath.FoldNumbers("\"calc(1)\""));
        Assert.Equal("minmax(10pt, 1fr)", CssMath.FoldNumbers("minmax(10pt, 1fr)"));
        Assert.False(CssMath.HasMath("minmax(10pt, 1fr)"));

        var doc = Styled("""
            <div id=a style="opacity: calc(1 / 4); z-index: max(2, 5); flex-grow: clamp(0, 3, 2); line-height: calc(1 + 0.5)">t</div>
            """);
        var s = ById(doc, "a").Style;
        Near(0.25f, s.Opacity);
        Assert.Equal(5, s.ZIndex);
        Near(2, s.FlexGrow);
        Near(18, s.LineHeight); // 1.5 × 12pt
    }

    [Fact]
    public void Var_inside_math_functions_is_substituted_first()
    {
        var doc = Styled("""
            <style>:root { --gap: 10pt; --cols: 4 } #a { margin-left: calc(var(--gap) * 2); padding: calc(var(--gap) / 2) min(var(--gap), 3pt) }
            #b { width: calc((100% - var(--gap) * (var(--cols) - 1)) / var(--cols)) }</style>
            <div id=a>a</div><div id=b>b</div>
            """);
        var a = ById(doc, "a").Style;
        Near(20, a.MarginLeft.Resolve(0));
        Near(5, a.PaddingTop.Resolve(0));
        Near(3, a.PaddingRight.Resolve(0));
        Near(80, ById(doc, "b").Style.Width.Resolve(350)); // (350 - 30) / 4
    }

    [Fact]
    public void Font_size_percentages_use_the_parent_font_size()
    {
        var doc = Styled("""
            <div style="font-size: 20pt"><span id=a style="font-size: calc(50% + 2pt)">a</span><span id=b style="font-size: max(1em, 30pt)">b</span></div>
            """);
        Near(12, ById(doc, "a").Style.FontSize);
        Near(30, ById(doc, "b").Style.FontSize);
    }

    [Fact]
    public void Widths_use_the_containing_block()
    {
        var ops = Layout("""
            <div style="width: 400pt">
              <div style="width: calc(100% - 100pt); height: 10pt; background: #ff0000"></div>
              <div style="width: min(100%, 250pt); height: 10pt; background: #00ff00"></div>
              <div style="width: clamp(50pt, 10%, 300pt); height: 10pt; background: #0000ff"></div>
              <div style="margin-left: calc(25% + 10pt); height: 10pt; background: #ffff00"></div>
            </div>
            """);
        Near(300, Box(ops, "#ff0000").W);
        Near(250, Box(ops, "#00ff00").W);
        Near(50, Box(ops, "#0000ff").W);
        var y = Box(ops, "#ffff00");
        Near(110, y.X);
        Near(290, y.W);
    }

    [Fact]
    public void Flex_basis_and_grid_tracks_take_math_functions()
    {
        var ops = Layout("""
            <div style="display: flex; width: 400pt">
              <div style="flex: 0 0 calc(50% - 20pt); height: 10pt; background: #ff0000"></div>
              <div style="flex: 1; height: 10pt; background: #00ff00"></div>
            </div>
            <div style="display: grid; width: 400pt; grid-template-columns: calc(25% + 20pt) minmax(min(50pt, 10%), 1fr)">
              <div style="height: 10pt; background: #0000ff"></div>
              <div style="height: 10pt; background: #ffff00"></div>
            </div>
            """);
        Near(180, Box(ops, "#ff0000").W);
        Near(220, Box(ops, "#00ff00").W);
        Near(120, Box(ops, "#0000ff").W);
        Near(280, Box(ops, "#ffff00").W);
    }

    [Fact]
    public void Border_shorthand_and_translate_take_math_functions()
    {
        var doc = Styled("""
            <div id=a style="border: calc(1px + 2px) solid red; transform: translate(calc(50% + 10pt), max(2pt, 4pt))">a</div>
            """);
        var s = ById(doc, "a").Style;
        Near(2.25f, s.BorderTopWidth); // 3px
        Assert.NotNull(s.Transform);
        Assert.Equal(2, s.Transform!.Length);
        Assert.True(s.Transform[0].PercentX);
        Near(50, s.Transform[0].A);
        Near(10, s.Transform[1].A);
        Near(4, s.Transform[1].B);
    }

    [Fact]
    public void Math_functions_render_end_to_end()
    {
        var html = """
            <style>:root { --pad: 12pt } .card { width: min(100%, 300pt); padding: calc(var(--pad) / 2) clamp(4pt, 2vw, 20pt); border: 1px solid #333 }</style>
            <div class=card>Card</div>
            """;
        var pdf = HtmlToPdf.Convert(html);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }
}
