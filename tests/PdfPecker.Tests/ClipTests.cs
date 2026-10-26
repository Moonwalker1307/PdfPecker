using PdfPecker.Css;

namespace PdfPecker.Tests;

/// <summary>
/// clip: rect(), the deprecated clip of absolutely positioned boxes, which Chrome still draws: the box and everything in
/// it show only inside the rectangle, measured from the border box's top-left corner.
/// </summary>
public class ClipTests
{
    private static RenderedImage Render(string box)
    {
        var pdf = HtmlToPdf.Convert(
            $"<body style='margin:0'><div style='width:100px;height:100px;background:blue;{box}'>"
            + "<div style='position:absolute;left:80px;top:80px;width:60px;height:60px;background:lime'></div></div></body>",
            new PdfOptions { Margins = new PageMargins(0) });
        return PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
    }

    /// <summary>The colour at a point given in CSS pixels: 'b' blue, 'g' lime, 'w' white, '?' anything else.</summary>
    private static char At(RenderedImage img, float cssX, float cssY)
    {
        int i = ((int)(cssY * 0.75f) * img.Width + (int)(cssX * 0.75f)) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]) switch
        {
            ( < 20, < 20, > 235) => 'b',
            ( < 20, > 235, < 20) => 'g',
            ( > 235, > 235, > 235) => 'w',
            _ => '?',
        };
    }

    [Fact]
    public void An_absolutely_positioned_box_shows_only_inside_its_clip_rectangle()
    {
        var img = Render("position:absolute;top:0;left:0;clip:rect(10px, 90px, 70px, 20px)");
        Assert.Equal('b', At(img, 50, 40));
        Assert.Equal('w', At(img, 15, 40));  // left of the rectangle
        Assert.Equal('w', At(img, 50, 5));   // above it
        Assert.Equal('w', At(img, 50, 80));  // below it
        Assert.Equal('w', At(img, 95, 40));  // right of it
        Assert.Equal('w', At(img, 100, 100)); // the overflowing child is clipped too
    }

    [Theory]
    [InlineData("rect(auto, auto, auto, auto)")]
    [InlineData("rect(auto auto auto auto)")]
    public void Auto_sides_are_the_border_box_edges(string clip)
    {
        var img = Render($"position:absolute;top:0;left:0;clip:{clip}");
        Assert.Equal('b', At(img, 5, 5));
        Assert.Equal('g', At(img, 90, 90));
        Assert.Equal('w', At(img, 120, 120)); // the child's overflow is cut at the border box
    }

    [Theory]
    [InlineData("position:absolute;top:0;left:0;clip:auto")]
    [InlineData("position:absolute;top:0;left:0;clip:rect(10%, 90px, 70px, 20px)")]
    [InlineData("position:absolute;top:0;left:0;clip:rect(10px, 90px, 70px)")]
    [InlineData("position:relative;clip:rect(0, 20px, 20px, 0)")]
    [InlineData("clip:rect(0, 20px, 20px, 0)")]
    public void Auto_invalid_values_and_boxes_that_are_not_absolutely_positioned_clip_nothing(string box)
    {
        var img = Render(box);
        Assert.Equal('b', At(img, 50, 50));
        Assert.Equal('g', At(img, 120, 120));
    }

    [Fact]
    public void A_fixed_box_is_clipped_too()
    {
        var img = Render("position:fixed;top:0;left:0;clip:rect(0, 50px, 50px, 0)");
        Assert.Equal('b', At(img, 25, 25));
        Assert.Equal('w', At(img, 75, 25));
        Assert.Equal('w', At(img, 120, 120));
    }

    [Fact]
    public void Supports_knows_clip()
        => Assert.True(CssSupports.Evaluate("(clip: rect(0, 10px, 10px, 0))"));
}
