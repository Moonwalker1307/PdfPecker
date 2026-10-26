using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// Images in the content of ::before and ::after, as Chrome draws them: a url() is an inline image at its own size
/// among the text, and a gradient that is the whole content paints the box.
/// </summary>
public class ContentImageTests
{
    // A 4 by 2 pixel PNG.
    private static readonly string Png = "data:image/png;base64," + Convert.ToBase64String(PngFile());

    private static byte[] PngFile()
    {
        var pixels = new uint[8];
        Array.Fill(pixels, 0xFF0000FFu);
        return Rendering.PngEncoder.Encode(pixels, 4, 2);
    }

    private static List<DrawOp> Layout(string css, string html)
    {
        var doc = HtmlParser.Parse("<body style='margin:0'>" + html + "</body>");
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet(css, sheet);
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

    [Fact]
    public void A_url_is_an_image_at_its_own_size_between_the_text()
    {
        var ops = Layout($"p::before {{ content: 'Note ' url({Png}) ' ' }}", "<p style='font: 16px Helvetica'>text</p>");
        var image = Assert.Single(ops.OfType<ImageOp>());
        Assert.Equal(3, image.W, 0.01f);   // 4px
        Assert.Equal(1.5f, image.H, 0.01f); // 2px
        var texts = ops.OfType<TextOp>().OrderBy(t => t.X).ToList();
        Assert.True(texts[0].X < image.X);
        Assert.True(texts[^1].X > image.X + image.W);
        // The image stands on the baseline.
        Assert.Equal(texts[0].Baseline, image.Y + image.H, 0.01f);
    }

    [Fact]
    public void The_box_size_does_not_size_the_image_and_alt_text_is_not_drawn()
    {
        var ops = Layout($"p::before {{ content: url({Png}) / 'alt'; display: block; width: 80px; height: 50px }}", "<p>text</p>");
        var image = Assert.Single(ops.OfType<ImageOp>());
        Assert.Equal(3, image.W, 0.01f);
        Assert.DoesNotContain(ops.OfType<TextOp>(), t => t.Text.Contains("alt", StringComparison.Ordinal));
    }

    [Fact]
    public void A_gradient_alone_paints_the_box()
    {
        var ops = Layout("p::before { content: linear-gradient(red, blue); display: block; width: 120px; height: 30px }", "<p>text</p>");
        var bg = Assert.Single(ops.OfType<BackgroundOp>(), b => b.Gradient != null);
        Assert.Equal(90, bg.ClipW, 0.01f);
        Assert.Equal(22.5f, bg.ClipH, 0.01f);
    }

    [Fact]
    public void A_gradient_keeps_the_box_background_under_it()
    {
        var ops = Layout("p::before { content: linear-gradient(red, transparent); background: linear-gradient(green, green); display: block; height: 30px }", "<p>text</p>");
        Assert.Equal(2, ops.OfType<BackgroundOp>().Count(b => b.Gradient != null));
    }

    [Fact]
    public void A_missing_image_is_left_out_and_the_text_stays()
    {
        var ops = Layout("p::before { content: url(missing.png) '(missing) ' }", "<p>text</p>");
        Assert.Empty(ops.OfType<ImageOp>());
        Assert.Contains(ops.OfType<TextOp>(), t => t.Text.Contains("(missing)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("url(a.png) nonsense")]
    [InlineData("linear-gradient(nonsense) 'x'")]
    public void Invalid_content_generates_no_box(string content)
    {
        var ops = Layout($"p::before {{ content: {content} }}", "<p>text</p>");
        Assert.Equal("text", Assert.Single(ops.OfType<TextOp>()).Text);
    }

    [Fact]
    public void A_pdf_ua_document_with_content_images_stays_valid()
    {
        var pdf = HtmlToPdf.Convert($"<html lang=en><title>T</title><style>p::before{{content:url({Png}) ' '}}</style><p>text</p></html>",
            new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 });
        PdfTestHelpers.AssertStructurallyValid(pdf);
    }
}
