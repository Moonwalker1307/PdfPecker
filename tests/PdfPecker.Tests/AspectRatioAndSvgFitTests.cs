using System.Text;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// aspect-ratio in intrinsic sizing, with min/max sizes and on tables, max-height on blocks, and object-fit for SVG files
/// in img elements. The expected sizes are Chrome's, measured in CSS pixels.
/// </summary>
public class AspectRatioAndSvgFitTests
{
    private const float Width = 500, PageHeight = 750; // 666.67 x 1000 px
    private const uint Red = 0xFFFF0000;

    private static List<DrawOp> Ops(string html)
    {
        var doc = HtmlParser.Parse("<!DOCTYPE html><html><body style='margin:0'>" + html + "</body></html>");
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        var list = new List<DrawOp>();
        void Add(IEnumerable<DrawOp> ops)
        {
            foreach (var o in ops)
            {
                list.Add(o);
                if (o is GroupOp g) Add(g.Ops);
            }
        }
        Add(engine.Ops);
        return list;

        static IEnumerable<Element> Find(Element e, string name)
        {
            foreach (var c in e.Children.OfType<Element>())
            {
                if (c.Name == name) yield return c;
                foreach (var d in Find(c, name)) yield return d;
            }
        }
    }

    /// <summary>The size in CSS pixels of the red background.</summary>
    private static (float W, float H) RedBox(string html)
    {
        var r = Ops(html).OfType<RectOp>().First(o => o.Color == Red);
        return (r.W / 0.75f, r.H / 0.75f);
    }

    private static void AssertSize(float w, float h, (float W, float H) actual)
    {
        Assert.True(Math.Abs(w - actual.W) < 0.2f && Math.Abs(h - actual.H) < 0.2f, $"Expected {w} x {h} but got {actual.W} x {actual.H}");
    }

    // ---- aspect-ratio in shrink-to-fit and intrinsic sizes ----

    [Theory]
    [InlineData("<div style='float:left;height:100px;aspect-ratio:2;background:red'></div>")]
    [InlineData("<div style='display:inline-block;height:100px;aspect-ratio:2;background:red'></div>")]
    [InlineData("<div style='position:absolute;height:100px;aspect-ratio:2;background:red'></div>")]
    [InlineData("<div style='float:left;display:flex;background:red'><div style='height:100px;aspect-ratio:2'></div></div>")]
    [InlineData("<div style='display:inline-grid;background:red'><div style='height:100px;aspect-ratio:2'></div></div>")]
    [InlineData("<div style='display:grid;grid-template-columns:min-content'><div style='height:100px;aspect-ratio:2;background:red'></div></div>")]
    [InlineData("<table style='border-spacing:0'><tr><td style='padding:0;background:red'><div style='height:100px;aspect-ratio:2'></div></td><td>x</td></tr></table>")]
    [InlineData("<div style='float:left;min-height:100px;aspect-ratio:2;background:red'></div>")]
    [InlineData("<div style='display:flex'><div style='min-height:100px;aspect-ratio:2;background:red'></div></div>")]
    [InlineData("<div style='position:absolute;min-height:100px;aspect-ratio:2;background:red'></div>")]
    public void A_ratio_box_contributes_the_width_its_height_gives(string html)
    {
        AssertSize(200, 100, RedBox(html));
    }

    [Theory]
    // min-height and max-height limit a fixed height before the ratio turns it into a width.
    [InlineData("float:left;height:100px;max-height:50px", 100, 50)]
    [InlineData("float:left;height:30px;min-height:50px", 100, 50)]
    [InlineData("float:left;height:100px;max-height:60px;min-height:80px", 160, 80)]
    [InlineData("height:100px;max-height:50px", 100, 50)]
    // Without a fixed height they carry over to the width as limits (transferred min and max sizes).
    [InlineData("max-height:20px", 40, 20)]
    [InlineData("min-height:400px", 800, 400)]
    [InlineData("max-height:50px;min-width:150px", 150, 50)]
    [InlineData("max-height:50px;box-sizing:border-box;padding:10px", 100, 50)]
    [InlineData("max-height:50px;padding:10px", 120, 70)]
    [InlineData("float:left;box-sizing:border-box;padding:10px;height:100px", 200, 100)]
    [InlineData("float:left;padding:10px;height:100px", 220, 120)]
    [InlineData("float:left;height:100px;max-width:150px", 150, 100)]
    [InlineData("float:left;height:100px;min-width:300px", 300, 100)]
    public void Min_and_max_heights_go_through_the_ratio(string style, float w, float h)
    {
        AssertSize(w, h, RedBox($"<div style='{style};aspect-ratio:2;background:red'></div>"));
    }

    [Theory]
    // min-width: auto lets wider content widen a box whose width comes from its height; min-width: 0 and clipped
    // overflow do not.
    [InlineData("float:left;height:100px", 300)]
    [InlineData("height:100px", 300)]
    [InlineData("float:left;height:100px;min-width:0", 200)]
    [InlineData("float:left;height:100px;overflow:hidden", 200)]
    [InlineData("height:100px;overflow:hidden", 200)]
    // A max-height limit is not widened by the content.
    [InlineData("float:left;max-height:50px", 100)]
    public void Content_widens_a_ratio_box_only_with_an_automatic_minimum(string style, float w)
    {
        var size = RedBox($"<div style='{style};aspect-ratio:2;background:red'><div style='width:300px;height:10px'></div></div>");
        Assert.Equal(w, size.W, 0.2f);
    }

    [Fact]
    public void A_flex_container_keeps_its_ratio_height_when_content_is_taller()
    {
        // A block container grows with its content; a flex container lets it overflow, as Chrome does.
        AssertSize(100, 10, RedBox("<div style='display:flex;width:100px;aspect-ratio:10;background:red'><span style='font-size:16px'>hello</span></div>"));
        Assert.True(RedBox("<div style='width:100px;aspect-ratio:10;background:red'><span style='font-size:16px'>hello</span></div>").H > 15);
    }

    // ---- max-height on blocks ----

    [Fact]
    public void Max_height_limits_a_block_and_its_content_overflows()
    {
        var ops = Ops("<div style='height:100px;max-height:50px;width:100px;background:red'>a<br>b<br>c<br>d<br>e</div>"
                      + "<div style='height:10px;background:#00f'></div>");
        var red = ops.OfType<RectOp>().First(o => o.Color == Red);
        var blue = ops.OfType<RectOp>().First(o => o.Color == 0xFF0000FF);
        Assert.Equal(37.5f, red.H, 0.01f);
        Assert.Equal(red.Y + 37.5f, blue.Y, 0.01f);
        // min-height wins over max-height.
        AssertSize(100, 80, RedBox("<div style='width:100px;max-height:50px;min-height:80px;background:red'></div>"));
        AssertSize(100, 40, RedBox("<div style='width:100px;max-height:40px;background:red'>a<br>b<br>c<br>d</div>"));
    }

    // ---- aspect-ratio on tables ----

    [Theory]
    [InlineData("width:200px", 200, 100)]
    [InlineData("height:100px", 200, 100)]
    [InlineData("width:100%;aspect-ratio:5", 666.7f, 133.3f)]
    // Chrome divides the table's content width by the ratio, and adds the frame only when the ratio sizes the content box.
    [InlineData("width:200px;border:10px solid #0f0;padding:5px", 200, 85)]
    [InlineData("width:200px;border:20px solid #0f0", 200, 80)]
    [InlineData("width:200px;border-top:20px solid #0f0", 200, 100)]
    [InlineData("width:200px;border:10px solid #0f0;box-sizing:content-box", 220, 110)]
    [InlineData("width:200px;border-left:20px solid #0f0;box-sizing:content-box", 220, 90)]
    [InlineData("width:200px;border-top:20px solid #0f0;box-sizing:content-box", 200, 120)]
    [InlineData("width:200px;border:10px solid #0f0;aspect-ratio:auto 2", 200, 100)]
    [InlineData("width:200px;border-collapse:collapse;border:10px solid #0f0", 200, 95)]
    // From the height the width follows the usual box-sizing rules.
    [InlineData("height:100px;border:20px solid #0f0", 200, 100)]
    [InlineData("height:100px;border:20px solid #0f0;box-sizing:content-box", 240, 140)]
    [InlineData("height:100px;border:10px solid #0f0;padding:5px;box-sizing:content-box", 230, 130)]
    // min-height and max-height limit both.
    [InlineData("height:100px;max-height:50px", 100, 50)]
    [InlineData("width:200px;max-height:50px", 200, 50)]
    [InlineData("width:200px;min-height:150px", 200, 150)]
    [InlineData("min-height:100px", 200, 100)]
    [InlineData("width:200px;max-width:100px", 100, 50)]
    public void Aspect_ratio_sizes_a_table(string style, float w, float h)
    {
        var ratio = style.Contains("aspect-ratio") ? "" : ";aspect-ratio:2";
        AssertSize(w, h, RedBox($"<table style='{style}{ratio};background:red'><tr><td>x</td></tr></table>"));
    }

    [Fact]
    public void A_ratio_table_is_never_narrower_than_its_columns()
    {
        // Columns of 306 px (a 300 px block, 1 px padding, 2 px spacing): the table keeps them, and an auto height
        // follows the wider width.
        const string cell = "<tr><td><div style='width:300px;height:5px'></div></td></tr>";
        AssertSize(306, 153, RedBox($"<table style='aspect-ratio:2;background:red'>{cell}</table>"));
        Assert.Equal(306, RedBox($"<table style='height:20px;aspect-ratio:2;background:red'>{cell}</table>").W, 0.2f);
        Assert.Equal(306, RedBox($"<table style='max-height:20px;aspect-ratio:2;background:red'>{cell}</table>").W, 0.2f);
    }

    [Fact]
    public void A_ratio_table_gives_its_width_to_a_shrink_to_fit_parent()
    {
        var ops = Ops("<div style='float:left;background:#00f'><table style='height:100px;aspect-ratio:2;background:red'><tr><td>x</td></tr></table></div>");
        Assert.Equal(150f, ops.OfType<RectOp>().First(o => o.Color == 0xFF0000FF).W, 0.1f);
        Assert.Equal(150f, ops.OfType<RectOp>().First(o => o.Color == Red).W, 0.1f);
    }

    [Theory]
    [InlineData("height:100px;max-height:50px", 50)]
    [InlineData("min-height:100px", 100)]
    [InlineData("height:10px;min-height:100px", 100)]
    public void Tables_take_min_and_max_height(string style, float h)
    {
        Assert.Equal(h, RedBox($"<table style='{style};background:red'><tr><td>x</td></tr></table>").H, 0.2f);
    }

    // ---- object-fit and object-position for SVG files in img ----

    private static string SvgUri(string attrs, string content = "<rect width='100' height='50' fill='red'/>")
        => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes($"<svg xmlns='http://www.w3.org/2000/svg' {attrs}>{content}</svg>"));

    private const string Natural = "width='100' height='50' viewBox='0 0 100 50'";

    [Theory]
    [InlineData(Natural, "", 0, 0, 200, 200)]
    [InlineData(Natural, "object-fit:contain", 0, 50, 200, 100)]
    [InlineData(Natural, "object-fit:cover", -100, 0, 400, 200)]
    [InlineData(Natural, "object-fit:none", 50, 75, 100, 50)]
    [InlineData(Natural, "object-fit:scale-down", 50, 75, 100, 50)]
    [InlineData(Natural, "object-fit:none;object-position:right 10px bottom 20px", 90, 130, 100, 50)]
    [InlineData(Natural, "object-fit:contain;object-position:0 0", 0, 0, 200, 100)]
    // The natural ratio comes from width and height before the viewBox.
    [InlineData("width='100' height='100' viewBox='0 0 100 50'", "object-fit:contain;height:100px", 50, 0, 100, 100)]
    // A viewBox alone gives a ratio but no size: none then fits it like contain.
    [InlineData("viewBox='0 0 100 50'", "object-fit:none", 0, 50, 200, 100)]
    [InlineData("viewBox='0 0 100 50'", "object-fit:cover", -100, 0, 400, 200)]
    [InlineData("width='100' viewBox='0 0 100 50'", "object-fit:none", 50, 75, 100, 50)]
    // Without a ratio, contain and cover fill the box.
    [InlineData("", "object-fit:contain", 0, 0, 200, 200)]
    [InlineData("width='100' height='50'", "object-fit:none", 50, 75, 100, 50)]
    public void Object_fit_sizes_the_viewport_of_an_svg_image(string attrs, string style, float x, float y, float w, float h)
    {
        var ops = Ops($"<img src=\"{SvgUri(attrs)}\" style='display:block;width:200px;height:200px;{style}'>");
        var svg = Assert.Single(ops.OfType<SvgOp>());
        Assert.Equal(x * 0.75f, svg.X, 0.05f);
        Assert.Equal(y * 0.75f, svg.Y, 0.05f);
        Assert.Equal(w * 0.75f, svg.W, 0.05f);
        Assert.Equal(h * 0.75f, svg.H, 0.05f);
        // A viewport reaching outside the content box is clipped to it.
        bool outside = x < 0 || y < 0 || x + w > 200 || y + h > 200;
        Assert.Equal(outside, ops.OfType<ClipOp>().Any(c => c.Ops.Contains(svg)));
    }

    [Fact]
    public void Object_fit_works_for_inline_svg_images_and_scale_down_picks_the_smaller()
    {
        var ops = Ops($"<p><img src=\"{SvgUri(Natural)}\" style='width:200px;height:100px;object-fit:cover;object-position:right'></p>");
        var svg = Assert.Single(ops.OfType<SvgOp>());
        Assert.Equal(200 * 0.75f, svg.W, 0.05f);
        ops = Ops($"<img src=\"{SvgUri(Natural)}\" style='display:block;width:60px;height:60px;object-fit:scale-down'>");
        svg = Assert.Single(ops.OfType<SvgOp>());
        Assert.Equal(15 * 0.75f, svg.Y, 0.05f);
        Assert.Equal(60 * 0.75f, svg.W, 0.05f);
    }

    /// <summary>The bounding box in CSS pixels of the red pixels of the rendered page.</summary>
    private static (int X, int Y, int W, int H) RenderedRed(string html)
    {
        var pdf = HtmlToPdf.Convert("<!DOCTYPE html><html><body style='margin:0'>" + html + "</body></html>", new PdfOptions { Margins = new PageMargins(0) });
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 96 });
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 0; y < Math.Min(img.Height, 400); y++)
            for (int x = 0; x < Math.Min(img.Width, 400); x++)
            {
                int k = (y * img.Width + x) * 4;
                if (img.Rgba[k] <= 200 || img.Rgba[k + 1] >= 80 || img.Rgba[k + 2] >= 80) continue;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
        return x1 < 0 ? (0, 0, 0, 0) : (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    [Theory]
    // The file's preserveAspectRatio fits the viewBox inside the viewport object-fit gives.
    [InlineData(Natural, "object-fit:cover", 0, 0, 200, 200)]
    [InlineData("width='100' height='50' viewBox='0 0 100 50' preserveAspectRatio='none'", "object-fit:contain", 0, 50, 200, 100)]
    [InlineData("width='100' height='50' viewBox='0 0 100 50' preserveAspectRatio='none'", "", 0, 0, 200, 200)]
    [InlineData("width='100' height='100' viewBox='0 0 100 50'", "object-fit:contain;height:100px", 50, 25, 100, 50)]
    [InlineData("width='100' height='100' viewBox='0 0 100 50' preserveAspectRatio='xMinYMin slice'", "object-fit:contain;height:100px", 50, 0, 100, 100)]
    // Without a viewBox, Chrome stretches the drawing's natural size over the viewport on each axis.
    [InlineData("width='100' height='50'", "", 0, 0, 200, 200)]
    [InlineData("width='100' height='50'", "object-fit:contain", 0, 50, 200, 100)]
    [InlineData("width='100'", "", 0, 0, 200, 50)]
    // Without a natural size it is drawn at its own scale.
    [InlineData("", "object-fit:contain", 0, 0, 100, 50)]
    public void Svg_images_render_like_chrome(string attrs, string style, int x, int y, int w, int h)
    {
        var red = RenderedRed($"<img src=\"{SvgUri(attrs)}\" style='display:block;width:200px;height:200px;{style}'>");
        Assert.True(Math.Abs(red.X - x) <= 1 && Math.Abs(red.Y - y) <= 1 && Math.Abs(red.W - w) <= 1 && Math.Abs(red.H - h) <= 1,
            $"Expected {x},{y} {w}x{h} but got {red.X},{red.Y} {red.W}x{red.H}");
    }

    [Fact]
    public void Svg_images_are_clipped_to_rounded_corners()
    {
        var ops = Ops($"<img src=\"{SvgUri(Natural)}\" style='display:block;width:200px;height:200px;border-radius:20px'>");
        var clip = Assert.Single(ops.OfType<ClipOp>());
        Assert.NotNull(clip.Radii);
        Assert.Contains(ops.OfType<SvgOp>().Single(), clip.Ops);
    }
}
