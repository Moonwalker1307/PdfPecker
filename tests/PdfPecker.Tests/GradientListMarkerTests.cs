using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// list-style-image with a gradient: as in Chrome, the marker is a square of half the font's ascent, in whole pixels,
/// standing on the baseline 7px before the content, painted with the gradient.
/// </summary>
public class GradientListMarkerTests
{
    private static List<DrawOp> Layout(string html, string? css = null)
    {
        var doc = HtmlParser.Parse("<body style='margin:0'>" + html + "</body>");
        var sheet = new Stylesheet();
        if (css != null) CssParser.ParseStylesheet(css, sheet);
        new StyleResolver(sheet, 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(800, new ImageLoader(null, null, false));
        engine.Layout(doc, 500);
        return Flatten(engine.Ops).ToList();

        static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
        {
            foreach (var o in ops)
            {
                yield return o;
                if (o is GroupOp g)
                    foreach (var c in Flatten(g.Ops)) yield return c;
            }
        }
    }

    private static List<BackgroundOp> Gradients(List<DrawOp> ops) => ops.OfType<BackgroundOp>().Where(b => b.Gradient != null).ToList();

    [Theory]
    [InlineData("linear-gradient(red, blue)")]
    [InlineData("radial-gradient(yellow, green)")]
    [InlineData("repeating-conic-gradient(red, blue 25%)")]
    public void A_gradient_marker_is_a_square_before_the_text(string gradient)
    {
        var ops = Layout($"<ul style='margin:0;padding-left:40px;font:16px Helvetica'><li style='list-style-image:{gradient}'>Item</li></ul>");
        var marker = Assert.Single(Gradients(ops));
        var text = ops.OfType<TextOp>().Single(t => t.Text == "Item");
        // Half of Helvetica's 14px ascent (rounded) at 16px is 7px.
        Assert.Equal(7 * 0.75f, marker.ClipW, 0.01f);
        Assert.Equal(marker.ClipW, marker.ClipH, 0.01f);
        Assert.Equal(text.X - 7 * 0.75f, marker.ClipX + marker.ClipW, 0.01f);
        Assert.Equal(text.Baseline, marker.ClipY + marker.ClipH, 0.01f);
        Assert.DoesNotContain(ops.OfType<TextOp>(), t => t.Text.Contains('•', StringComparison.Ordinal));
    }

    [Fact]
    public void The_list_style_shorthand_takes_a_gradient()
        => Assert.Single(Gradients(Layout("<ul><li style='list-style: square linear-gradient(90deg, #000, #f80)'>Item</li></ul>")));

    [Fact]
    public void A_gradient_that_does_not_parse_leaves_the_type_to_show()
    {
        var ops = Layout("<ul><li style='list-style-image: linear-gradient(nonsense)'>Item</li></ul>");
        Assert.Empty(Gradients(ops));
        Assert.Single(ops.OfType<CircleOp>()); // the disc
    }

    [Fact]
    public void Marker_content_wins_over_the_gradient()
        => Assert.Empty(Gradients(Layout("<ul><li style='list-style-image:linear-gradient(red,blue)'>Item</li></ul>", "li::marker { content: '> ' }")));

    [Fact]
    public void A_marker_image_taller_than_the_line_moves_the_line_down()
    {
        // As in Chrome, the image stands on the first line's baseline and the line's ascent grows to hold it, outside
        // and inside alike; the descent stays the text's.
        var png = PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(16, 40, 200, 0, 0));
        foreach (var position in new[] { "outside", "inside" })
        {
            var ops = Layout($"<ul style=\"font:12pt Helvetica;list-style:url('{png}') {position}\"><li>a</li><li>b</li></ul>");
            var image = ops.OfType<ImageOp>().First();
            var a = ops.OfType<TextOp>().First(t => t.Text == "a");
            var b = ops.OfType<TextOp>().First(t => t.Text == "b");
            Assert.Equal(30, image.H, 2);
            Assert.True(image.Y >= -0.01f, position);
            Assert.Equal(a.Baseline, image.Y + image.H, 2);
            Assert.True(b.Baseline - a.Baseline > 30, position);
        }
    }
}

