using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// Gradients and tiled images inside transformed boxes, blurred boxes and scaled pages. A PDF pattern is placed in the
/// default space of its stream, not in the space where it is used, so each of these has to carry the transforms around it.
/// </summary>
public class TransformedPatternTests
{
    private const string RedToBlue = "linear-gradient(90deg, rgb(255,0,0), rgb(0,0,255))";

    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

    private static byte[] Pdf(string body, PdfOptions? options = null)
        => HtmlToPdf.Convert($"<body style='margin:0'>{body}</body>", options ?? NoMargins);

    /// <summary>The RGB of a pixel, at a position given in CSS pixels (the page is rendered at 72 dpi, 1 pt per pixel).</summary>
    private static (int R, int G, int B) At(byte[] pdf, float cssX, float cssY)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int x = (int)(cssX * 0.75f), y = (int)(cssY * 0.75f);
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void Reddish((int R, int G, int B) p) => Assert.True(p.R > 200 && p.B < 60, $"Expected red, got {p}");
    private static void Bluish((int R, int G, int B) p) => Assert.True(p.B > 200 && p.R < 60, $"Expected blue, got {p}");

    [Fact]
    public void A_rotated_gradient_turns_with_its_box()
    {
        var pdf = Pdf($"<div style='width:200px;height:100px;background:{RedToBlue};transform:rotate(180deg)'></div>");
        Bluish(At(pdf, 10, 50));
        Reddish(At(pdf, 190, 50));
    }

    [Fact]
    public void A_scaled_gradient_shrinks_with_its_box()
    {
        // scale(0.5) about the centre: the box covers x 50 to 150, red at its left edge and blue at its right.
        var pdf = Pdf($"<div style='width:200px;height:100px;background:{RedToBlue};transform:scale(.5)'></div>");
        Reddish(At(pdf, 54, 50));
        Bluish(At(pdf, 146, 50));
    }

    [Fact]
    public void A_gradient_in_a_translucent_rotated_box_is_turned_once()
    {
        // The opacity makes the layer a transparency group, drawn under the rotation: its patterns are already turned.
        var pdf = Pdf($"<div style='width:200px;height:100px;background:{RedToBlue};transform:rotate(180deg);opacity:.99'></div>");
        Bluish(At(pdf, 10, 50));
        Reddish(At(pdf, 190, 50));
    }

    [Fact]
    public void A_gradient_in_nested_transforms_follows_both()
    {
        var pdf = Pdf($"<div style='width:200px;transform:rotate(90deg)'><div style='width:200px;height:200px;background:{RedToBlue};transform:rotate(90deg)'></div></div>");
        Bluish(At(pdf, 10, 100));
        Reddish(At(pdf, 190, 100));
    }

    [Fact]
    public void A_tiled_image_turns_with_its_box()
    {
        // A 20 px tile, red on its left half and blue on its right, over a box two tiles wide.
        var tile = DataUri(PixelPng(20, 20, (x, _) => x < 10 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255)));
        var pdf = Pdf($"<div style='width:40px;height:20px;background:url({tile}) 0 0/20px 20px;transform:rotate(180deg)'></div>");
        Bluish(At(pdf, 4, 10));
        Reddish(At(pdf, 16, 10));
        Bluish(At(pdf, 24, 10));
    }

    [Fact]
    public void A_tiled_gradient_turns_with_its_box()
    {
        var pdf = Pdf($"<div style='width:200px;height:100px;background:{RedToBlue} 0 0/100px 100px;transform:rotate(180deg)'></div>");
        Bluish(At(pdf, 4, 50));
        Reddish(At(pdf, 96, 50));
        Bluish(At(pdf, 104, 50));
    }

    [Fact]
    public void A_gradient_mask_in_a_rotated_box_turns_once()
    {
        var pdf = Pdf("<div style='width:200px;height:100px;background:blue;mask-image:linear-gradient(90deg, #000, transparent);transform:rotate(180deg)'></div>");
        Assert.True(At(pdf, 6, 50).R > 230, "The transparent end of the mask is on the left after the turn");
        Bluish(At(pdf, 194, 50));
    }

    [Fact]
    public void A_blurred_gradient_keeps_its_size()
    {
        // The blur renders the box at its own resolution; its gradient must be scaled to that like the box.
        var pdf = Pdf($"<div style='margin:20px;width:200px;height:100px;background:{RedToBlue};filter:blur(1px)'></div>");
        Reddish(At(pdf, 28, 70));
        Bluish(At(pdf, 212, 70));
        var middle = At(pdf, 120, 70);
        Assert.True(Math.Abs(middle.R - middle.B) < 40, $"Expected the middle of the gradient, got {middle}");
    }

    [Fact]
    public void A_gradient_on_a_scaled_page_shrinks_with_the_page()
    {
        // Scale 0.5 draws the 400 px box 200 px wide, from red at its left edge to blue at its right.
        var options = NoMargins;
        options.Scale = 0.5f;
        var pdf = Pdf($"<div style='width:400px;height:100px;background:{RedToBlue}'></div>", options);
        Reddish(At(pdf, 4, 25));
        Bluish(At(pdf, 196, 25));
    }

    [Fact]
    public void A_gradient_in_a_translucent_svg_image_is_placed_in_the_drawing()
    {
        string inner = "<svg xmlns='http://www.w3.org/2000/svg' width='200' height='100'><linearGradient id='g'><stop offset='0' stop-color='red'/>"
            + "<stop offset='1' stop-color='blue'/></linearGradient><rect width='200' height='100' fill='url(#g)'/></svg>";
        string uri = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(inner));
        var pdf = Pdf($"<svg width='200' height='100' style='display:block'><image width='200' height='100' opacity='.99' href='{uri}'/></svg>");
        Reddish(At(pdf, 4, 50));
        Bluish(At(pdf, 196, 50));
        var middle = At(pdf, 100, 50);
        Assert.True(Math.Abs(middle.R - middle.B) < 40, $"Expected the middle of the gradient, got {middle}");
    }

    [Fact]
    public void Untransformed_patterns_keep_their_bytes()
    {
        // Only patterns under a transform carry it: an untransformed tile keeps its plain translation.
        var pdf = Latin1(Pdf($"<div style='width:200px;height:100px;background:{RedToBlue} 0 0/100px 100px'></div>"));
        Assert.Matches(@"/Matrix \[1 0 0 1 0 \d+(\.\d+)?\]", pdf);
    }
}
