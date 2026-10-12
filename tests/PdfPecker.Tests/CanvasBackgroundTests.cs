using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// The html or body background painted on the canvas: it fills every page area (not the page margins), positioned
/// against the root element's box from the page area's top left corner, as Chrome prints it.
/// </summary>
public class CanvasBackgroundTests
{
    // A4 with 36pt margins: the page area is 36..559 x 36..806.
    private static PdfOptions Options => new() { PageSize = PageSize.A4, Margins = new PageMargins(36), Compress = false };

    private static string Lines(int n) => string.Concat(Enumerable.Repeat("<p>Lorem ipsum dolor sit amet</p>", n));

    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y, int page = 0)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(page, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Color(int r, int g, int b, (int R, int G, int B) actual, int tolerance = 40)
        => Assert.True(Math.Abs(actual.R - r) < tolerance && Math.Abs(actual.G - g) < tolerance && Math.Abs(actual.B - b) < tolerance,
            $"Expected ({r}, {g}, {b}), got {actual}");

    private static int PageCount(byte[] pdf) => PdfRenderer.Open(pdf).PageCount;

    [Fact]
    public void Body_colour_fills_every_page_area_but_not_the_margins()
    {
        var pdf = HtmlToPdf.Convert($"<body style='background:#00ff00'>{Lines(40)}</body>", Options);
        AssertStructurallyValid(pdf);
        Assert.Equal(2, PageCount(pdf));
        Color(0, 255, 0, Pixel(pdf, 550, 40));
        Color(0, 255, 0, Pixel(pdf, 550, 800, page: 1)); // below the end of the content
        Color(255, 255, 255, Pixel(pdf, 20, 400)); // the page margin
        Color(255, 255, 255, Pixel(pdf, 300, 820, page: 1));
    }

    [Fact]
    public void Translucent_body_colour_is_painted_once()
    {
        var pdf = HtmlToPdf.Convert("<body style='background:rgba(255,0,0,0.5)'><p>short</p></body>", Options);
        Color(255, 128, 128, Pixel(pdf, 300, 400), tolerance: 12);
        Color(255, 128, 128, Pixel(pdf, 40, 45), tolerance: 12); // inside the body box too
    }

    [Fact]
    public void Body_gradient_spans_the_whole_document_and_fills_later_pages()
    {
        // The positioning area is as tall as the root box: two pages minus a bit, so the gradient continues on page 2 and
        // repeats below the content.
        var pdf = HtmlToPdf.Convert($"<body style='margin:0; background:linear-gradient(#ff0000, #0000ff)'>{Lines(80)}</body>", Options);
        AssertStructurallyValid(pdf);
        var top = Pixel(pdf, 550, 38);
        var bottom1 = Pixel(pdf, 550, 803);
        Assert.True(top.R > 240 && top.B < 15, $"page 1 starts red: {top}");
        Assert.True(bottom1.R < top.R && bottom1.B > top.B, $"page 1 shades towards blue: {bottom1}");
        var top2 = Pixel(pdf, 550, 38, page: 1);
        Assert.True(top2.B > bottom1.B, $"page 2 continues the gradient: {top2}");
        Assert.NotEqual((255, 255, 255), Pixel(pdf, 550, 803, page: 1));
    }

    [Fact]
    public void Html_height_100_percent_restarts_the_gradient_on_every_page()
    {
        var html = $"<html style='height:100%'><body style='margin:0; height:100%; background:linear-gradient(#ff0000, #0000ff)'>{Lines(80)}</body></html>";
        var pdf = HtmlToPdf.Convert(html, Options);
        for (int page = 0; page < 2; page++)
        {
            Color(255, 0, 0, Pixel(pdf, 550, 37, page));
            Color(0, 0, 255, Pixel(pdf, 550, 805, page));
        }
    }

    [Fact]
    public void Html_background_wins_and_body_keeps_its_own_box()
    {
        var html = "<html style='background:#00ff00'><body style='margin:20pt; height:100pt; background:#ff0000'></body></html>";
        var pdf = HtmlToPdf.Convert(html, Options);
        Color(0, 255, 0, Pixel(pdf, 40, 40));
        Color(255, 0, 0, Pixel(pdf, 100, 100));
        Color(0, 255, 0, Pixel(pdf, 100, 400));
    }

    [Fact]
    public void Body_image_without_repeat_is_drawn_once()
    {
        var img = DataUri(SolidPng(40, 40, 255, 0, 0));
        var pdf = HtmlToPdf.Convert($"<body style='margin:0; background:#ffffff url({img}) no-repeat 10pt 10pt'>{Lines(80)}</body>", Options);
        Color(255, 0, 0, Pixel(pdf, 36 + 25, 36 + 25));
        Color(255, 255, 255, Pixel(pdf, 36 + 25, 36 + 25, page: 1));
    }

    [Fact]
    public void Repeated_body_image_tiles_every_page_from_the_page_area_corner()
    {
        // Opaque red tiles cover the blue colour on every page, below the content too, but not the margins.
        var img = DataUri(SolidPng(20, 20, 255, 0, 0));
        var html = $"<body style='margin:0; background:url({img}) 0 0 / 7.5pt 7.5pt, #0000ff'>{Lines(80)}</body>";
        var pdf = HtmlToPdf.Convert(html, Options);
        Color(255, 0, 0, Pixel(pdf, 40, 40));
        Color(255, 0, 0, Pixel(pdf, 550, 800, page: 1));
        Color(255, 255, 255, Pixel(pdf, 20, 400, page: 1));
    }

    [Fact]
    public void Page_rule_background_fills_the_whole_sheet()
    {
        var pdf = HtmlToPdf.Convert("<style>@page { background: #0000ff }</style><p>x</p>", Options);
        Color(0, 0, 255, Pixel(pdf, 5, 5));
        Color(0, 0, 255, Pixel(pdf, 300, 400));
    }

    [Fact]
    public void Root_margins_do_not_collapse_with_the_body()
    {
        var doc = HtmlParser.Parse("<html style='margin-top:30pt'><body style='margin-top:10pt'><p style='margin-top:12pt'>x</p></body></html>");
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(800, new ImageLoader(null, null, false)) { CanvasRoot = doc.FindFirst("html") };
        engine.Layout(doc, 500);
        var text = Flatten(engine.Ops).OfType<TextOp>().First();
        Assert.Equal(42, text.LineTop, 2); // 30 for html, then body and p collapse to 12
        Assert.Equal(30, engine.CanvasRootBox!.Value.Y, 2);
    }

    private static List<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        var list = new List<DrawOp>();
        foreach (var o in ops)
        {
            if (o is GroupOp g) list.AddRange(Flatten(g.Ops));
            else list.Add(o);
        }
        return list;
    }
}
