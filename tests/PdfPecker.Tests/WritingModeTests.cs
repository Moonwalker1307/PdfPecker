using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>writing-mode: vertical boxes laid out as horizontal text turned a quarter turn.</summary>
public class WritingModeTests
{
    private static List<DrawOp> Layout(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(800, new ImageLoader(null, null, false));
        engine.Layout(doc, 500);
        return engine.Ops;
    }

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) yield return c;
            foreach (var d in Find(c, name)) yield return d;
        }
    }

    private static IEnumerable<DrawOp> All(IEnumerable<DrawOp> ops)
    {
        foreach (var o in ops)
        {
            yield return o;
            if (o is GroupOp g) foreach (var inner in All(g.Ops)) yield return inner;
        }
    }

    private static RectOp Fill(IEnumerable<DrawOp> ops, uint color) => All(ops).OfType<RectOp>().Single(r => r.Color == color);

    [Theory]
    [InlineData("vertical-rl", 90)]
    [InlineData("sideways-lr", -90)]
    public void A_vertical_box_is_laid_out_horizontally_and_turned(string mode, int degrees)
    {
        var ops = Layout($"<style>body{{margin:0;font:12pt Helvetica}}</style><div style='writing-mode:{mode};height:90pt;background:#ff0000'>Label</div>"
                         + "<div style='background:#00ff00;height:10pt'></div>");
        var layer = All(ops).OfType<LayerOp>().Single(l => l.Transform != null);
        var turn = layer.Transform!.Value;
        Assert.Equal(degrees > 0 ? 1 : -1, turn.B, 3);
        // Inside the turn the box is as long as its height and as deep as one line; the next box starts below its height.
        var box = Fill(layer.Ops, 0xFFFF0000);
        Assert.Equal(90, box.W, 2);
        Assert.True(box.H is > 10 and < 20, $"depth {box.H}");
        Assert.Equal(90, Fill(ops, 0xFF00FF00).Y, 2);
    }

    [Fact]
    public void A_vertical_label_without_a_height_is_as_long_as_its_text()
    {
        var ops = Layout("<style>body{margin:0;font:12pt Helvetica}</style><div style='writing-mode:vertical-rl;background:#ff0000'>Quarterly total</div>");
        var box = Fill(ops, 0xFFFF0000);
        Assert.True(box.W is > 60 and < 100, $"length {box.W}");
    }

    [Fact]
    public void A_vertical_table_header_is_as_narrow_as_its_line()
    {
        var ops = Layout("<style>body{margin:0;font:12pt Helvetica} th{padding:0}</style><table style='border-spacing:0'><tr>"
                         + "<th style='writing-mode:vertical-rl;background:#ff0000'>February total</th><th style='background:#00ff00'>x</th></tr></table>");
        var cell = Fill(ops, 0xFFFF0000);
        Assert.True(cell.W < 20, $"cell width {cell.W}");
        Assert.True(cell.H > 60, $"cell height {cell.H}");
    }

    [Fact]
    public void Writing_mode_is_inherited_and_converts_to_pdf()
    {
        var pdf = ConvertUncompressed("<div style='writing-mode:vertical-rl'><p>Down</p><p style='writing-mode:horizontal-tb'>Across</p></div>");
        AssertStructurallyValid(pdf);
    }
}
