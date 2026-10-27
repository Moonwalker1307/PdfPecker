using System.Text;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Pdf;

namespace PdfPecker.Tests;

/// <summary>
/// The used size of img elements in boxes narrower or shorter than the image. The expected sizes are Chrome's, in CSS
/// pixels, on a page 666.67 px (500 pt) wide.
/// </summary>
public class ImageSizingTests
{
    private const float Width = 500, PageHeight = 750;

    private static SvgOp Image(string html)
    {
        var doc = HtmlParser.Parse("<!DOCTYPE html><html><body style='margin:0'>" + html + "</body></html>");
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(new PageGrid(new PageSetup { Width = Width, Height = PageHeight }), new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        return Assert.Single(All(engine.Ops).OfType<SvgOp>());

        static IEnumerable<DrawOp> All(IEnumerable<DrawOp> ops)
        {
            foreach (var o in ops)
            {
                yield return o;
                if (o is GroupOp g)
                    foreach (var c in All(g.Ops)) yield return c;
            }
        }
    }

    /// <summary>An SVG file of the given natural size in pixels, as an img src.</summary>
    private static string Src(int w, int h)
        => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"<svg xmlns='http://www.w3.org/2000/svg' width='{w}' height='{h}'><rect width='{w}' height='{h}' fill='red'/></svg>"));

    private static void AssertSize(float w, float h, SvgOp image)
    {
        Assert.Equal(w * 0.75f, image.W, 0.1f);
        Assert.Equal(h * 0.75f, image.H, 0.1f);
    }

    [Theory]
    [InlineData("<div style='width:100px'><img src='{0}'></div>", 400, 250)]
    [InlineData("<div style='width:100px'><img src='{0}' style='display:block'></div>", 400, 250)]
    [InlineData("<div style='width:100px'><img src='{0}' style='width:300px'></div>", 300, 187.5f)]
    [InlineData("<div style='width:100px'><img src='{0}' style='max-width:100%'></div>", 100, 62.5f)]
    public void An_image_wider_than_its_box_overflows_it(string html, float w, float h)
        => AssertSize(w, h, Image(string.Format(html, Src(400, 250))));

    [Fact]
    public void An_image_wider_than_the_page_is_scaled_to_fit_it()
        => AssertSize(666.67f, 166.67f, Image($"<div style='width:100px'><img src='{Src(2000, 500)}'></div>"));

    [Theory]
    [InlineData("", 96, 48)]
    [InlineData("justify-self:stretch", 200, 100)]
    [InlineData("justify-self:stretch;height:30px", 200, 30)]
    [InlineData("justify-self:stretch;max-width:150px", 150, 75)]
    [InlineData("justify-self:stretch;margin:0 20px;border:3px solid;padding:2px", 150, 75)]
    [InlineData("justify-self:stretch;align-self:stretch", 200, 150)]
    public void An_image_in_a_grid_stretches_only_when_asked(string style, float w, float h)
        => AssertSize(w, h, Image("<div style='display:grid;grid-template-columns:200px 200px'>" +
                                  $"<img src='{Src(96, 48)}' style='display:block;{style}'><div style='height:150px'></div></div>"));

    [Theory]
    [InlineData("", "", 200, 100)]
    [InlineData("", "max-width:120px", 120, 60)]
    [InlineData("", "height:30px", 200, 30)]
    [InlineData("height:200px", "flex:1", 200, 200)]
    [InlineData("align-items:start", "", 96, 48)]
    public void An_image_in_a_flex_column_stretches_across_it(string container, string style, float w, float h)
        => AssertSize(w, h, Image($"<div style='display:flex;flex-direction:column;width:200px;{container}'><img src='{Src(96, 48)}' style='{style}'></div>"));

    [Theory]
    [InlineData("", 96, 80)]
    [InlineData("width:60px", 60, 80)]
    [InlineData("align-self:start", 96, 48)]
    public void An_image_in_a_flex_row_stretches_to_the_line(string style, float w, float h)
        => AssertSize(w, h, Image($"<div style='display:flex'><img src='{Src(96, 48)}' style='{style}'><div style='height:80px;width:50px'></div></div>"));

    [Theory]
    [InlineData("max-height:100%", 128, 80)]
    [InlineData("height:100%", 128, 80)]
    [InlineData("max-height:50%", 64, 40)]
    public void A_percentage_height_limits_an_image_in_a_flex_box_of_fixed_height(string style, float w, float h)
        => AssertSize(w, h, Image($"<div style='display:flex;width:300px;height:80px'><img src='{Src(400, 250)}' style='{style}'></div>"));

    [Theory]
    [InlineData("width:100%;border:5px solid;margin:8px")]
    [InlineData("width:100%;padding:6px")]
    [InlineData("max-width:100%;border:5px solid;margin:0 20px")]
    public void A_percentage_width_is_of_the_box_so_the_frame_and_margins_overflow_it(string style)
        => AssertSize(200, 125, Image($"<div style='width:200px'><img src='{Src(400, 250)}' style='{style}'></div>"));

    [Fact]
    public void A_border_box_image_of_full_width_keeps_its_border_inside_the_box()
        => AssertSize(190, 118.75f, Image($"<div style='width:200px'><img src='{Src(400, 250)}' style='width:100%;border:5px solid;box-sizing:border-box'></div>"));

    [Theory]
    [InlineData("img")]
    [InlineData("svg")]
    public void Vertical_margins_of_an_inline_image_or_svg_move_it_down_and_the_line_after_it(string element)
    {
        // The line starts 50px down, so the 20px margin puts the image at 70px, whatever else is on the line.
        var drawn = element == "img"
            ? $"<img src='{Src(100, 40)}' style='margin:20px 0'>"
            : "<svg width='100' height='40' style='margin:20px 0'><rect width='100' height='40'/></svg>";
        var image = Image($"<div style='height:50px'></div>{drawn}");
        Assert.Equal(70 * 0.75f, image.Y, 0.1f);
    }

    /// <summary>A PNG of the given size in pixels, all one opaque colour.</summary>
    private static string Png(int w, int h)
        => "data:image/png;base64," + Convert.ToBase64String(PdfPecker.Rendering.PngEncoder.Encode(Enumerable.Repeat(0xFF2050A0u, w * h).ToArray(), w, h));

    [Theory]
    [InlineData("png")]
    [InlineData("svg")]
    public void An_image_larger_than_its_box_is_drawn_on_a_later_page(string format)
    {
        // object-fit: none centres the 400 x 250 image in a 100 x 100 box 20px down page 2, so the image starts 55px above
        // the box, on page 1.
        var src = format == "png" ? Png(400, 250) : Src(400, 250);
        var pdf = HtmlToPdf.Convert($"<style>@page {{ size: 600px 600px; margin: 0 }}</style><body style='margin:0'><div style='height:620px'></div><img src='{src}' style='width:100px;height:100px;object-fit:none'></body>");
        var rgba = PdfRenderer.Open(pdf).RenderPage(1, new ImageOptions { Dpi = 72 }).Rgba;
        int ink = 0;
        for (int i = 0; i < rgba.Length; i += 4)
            if (rgba[i] + rgba[i + 1] + rgba[i + 2] < 600) ink++;
        // 100 x 100 CSS px is 75 x 75 points, which are pixels at 72 dpi.
        Assert.InRange(ink, 75 * 75 - 300, 75 * 75 + 300);
    }
}
