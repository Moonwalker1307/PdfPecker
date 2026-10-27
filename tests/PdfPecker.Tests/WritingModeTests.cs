using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;
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

    [Theory]
    [InlineData("left", 10)]
    [InlineData("right", 200 - 50 - 10)]
    public void A_float_in_vertical_text_keeps_its_width_and_height_on_the_page(string side, float start)
    {
        // A float 40pt wide and 50pt tall goes to the top (left) or bottom (right) of vertical-rl lines, as in Chrome.
        // Inside the turn, its 50pt height runs along the lines and its top margin is before it there.
        var ops = Layout("<style>body{margin:0;font:12pt Helvetica}</style><div style='writing-mode:vertical-rl;height:200pt'>Text "
                         + $"<div style='float:{side};width:40pt;height:50pt;margin-top:10pt;margin-bottom:10pt;background:#ff0000'></div> more text</div>");
        var layer = All(ops).OfType<LayerOp>().Single(l => l.Transform != null);
        var box = Fill(layer.Ops, 0xFFFF0000);
        Assert.Equal(50, box.W, 1);
        Assert.Equal(40, box.H, 1);
        Assert.Equal(start, box.X, 1);
    }

    [Fact]
    public void A_vertical_label_without_a_height_is_as_long_as_its_text()
    {
        var ops = Layout("<style>body{margin:0;font:12pt Helvetica}</style><div style='writing-mode:vertical-rl;background:#ff0000'>Quarterly total</div>");
        var box = Fill(ops, 0xFFFF0000);
        Assert.True(box.W is > 60 and < 100, $"length {box.W}");
    }

    [Fact]
    public void A_right_margin_on_a_vertical_inline_block_is_counted_once()
    {
        var ops = Layout("<style>body{margin:0;font:12pt Helvetica}.v{display:inline-block;writing-mode:vertical-rl;height:40pt;vertical-align:top}</style>"
                         + "<div class='v' style='margin-right:30pt;background:#ff0000'>A</div><div class='v' style='background:#00ff00'>B</div>");
        var layers = All(ops).OfType<LayerOp>().Where(l => l.Transform != null).ToList();
        Assert.Equal(2, layers.Count);
        // Each layer turns about the middle of the box, whose depth is the height of the red or green fill inside it.
        float Left(LayerOp l, uint color) => l.OriginX - Fill(l.Ops, color).H / 2;
        float redLeft = Left(layers[0], 0xFFFF0000), redDepth = Fill(layers[0].Ops, 0xFFFF0000).H;
        // The green box starts where the red one ends plus its margin, not twice that.
        Assert.Equal(redLeft + redDepth + 30, Left(layers[1], 0xFF00FF00), 0.5f);
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

    [Theory]
    [InlineData("ltr")]
    [InlineData("rtl")]
    public void Vertical_lr_lines_run_from_left_to_right(string direction)
    {
        // The box is reflected in its diagonal, so the lines that follow each other down the horizontal layout follow each
        // other to the right on the page; each line is flipped back about its middle so that its glyphs are turned, not
        // mirrored.
        var ops = Layout("<style>body{margin:0;font:12pt Helvetica}</style>"
                         + $"<div style='writing-mode:vertical-lr;direction:{direction};height:60pt;width:100pt'>One two three four five six seven</div>");
        var layer = All(ops).OfType<LayerOp>().Single(l => l.Transform is { B: not 0 });
        Assert.Equal(new Matrix(0, 1, 1, 0, 0, 0), layer.Transform!.Value);
        var lines = All(layer.Ops).OfType<LayerOp>().Where(l => l.Transform is { A: 1, D: -1 }).ToList();
        Assert.True(lines.Count >= 3, $"{lines.Count} lines");
        for (int i = 1; i < lines.Count; i++) Assert.True(lines[i].OriginY > lines[i - 1].OriginY);
        Assert.All(lines, l => Assert.NotEmpty(l.Ops.OfType<TextOp>()));
    }

    [Fact]
    public void Vertical_lr_borders_keep_their_sides_on_the_page()
    {
        // Reflected in the diagonal, the left border is the top of the horizontal layout and the bottom one its right.
        var ops = Layout("<style>body{margin:0;font:12pt Helvetica}</style><div style='writing-mode:vertical-lr;height:60pt;"
                         + "border-left:3pt solid #ff0000;border-bottom:1.5pt solid #00ff00'>Text</div>");
        var layer = All(ops).OfType<LayerOp>().Single(l => l.Transform is { B: not 0 });
        var left = Fill(layer.Ops, 0xFFFF0000);
        var bottom = Fill(layer.Ops, 0xFF00FF00);
        Assert.Equal((0f, 3f), (left.Y, left.H));
        Assert.Equal(1.5f, bottom.W, 3);
        Assert.True(bottom.X > left.X, $"bottom border at {bottom.X}");
    }

    [Fact]
    public void Vertical_lr_text_clips_its_background_the_right_way_up()
    {
        var ops = Layout("<style>body{margin:0;font:20pt Helvetica}</style><div style='writing-mode:vertical-lr;"
                         + "background:linear-gradient(red,blue);background-clip:text;color:transparent'>Clip</div>");
        var text = All(ops).OfType<TextOp>().ToList();
        var drawn = text.Single(t => !t.UpsideDown);
        var copy = text.Single(t => t.UpsideDown);
        var line = All(ops).OfType<LayerOp>().Single(l => l.Transform is { A: 1, D: -1 });
        // The copy is the drawn text flipped about the middle of its line, where the line's layer draws it.
        Assert.Equal(2 * line.OriginY - drawn.Baseline, copy.Baseline, 3);
        AssertStructurallyValid(ConvertUncompressed("<div style='writing-mode:vertical-lr;background:linear-gradient(red,blue);"
                                                    + "background-clip:text;color:transparent'>Clip</div>"));
    }

    [Fact]
    public void Writing_mode_is_inherited_and_converts_to_pdf()
    {
        var pdf = ConvertUncompressed("<div style='writing-mode:vertical-rl'><p>Down</p><p style='writing-mode:horizontal-tb'>Across</p></div>");
        AssertStructurallyValid(pdf);
    }
}
