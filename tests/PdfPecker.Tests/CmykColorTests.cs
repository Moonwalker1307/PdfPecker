using PdfPecker.Css;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>device-cmyk() of CSS Color 5: its sRGB stand-in, and the DeviceCMYK colours the PDF writer writes for it.</summary>
public class CmykColorTests
{
    [Theory]
    [InlineData("device-cmyk(0 0 0 1)", 0xFF000000)]
    [InlineData("device-cmyk(0 0 0 0)", 0xFFFFFFFF)]
    [InlineData("device-cmyk(1 0 0 0)", 0xFF00FFFF)]
    [InlineData("device-cmyk(0% 100% 100% 0%)", 0xFFFF0000)]
    [InlineData("device-cmyk(0, 1, 1, 0)", 0xFFFF0000)]
    [InlineData("device-cmyk(0 0 0 0.5 / 50%)", 0x80808080)]
    [InlineData("device-cmyk(none 0 0 1.5)", 0xFF000000)]
    public void Outside_a_conversion_the_colour_is_its_naive_conversion(string css, uint expected)
    {
        Assert.True(CssColor.TryParse(css, CssColor.Black, out uint color));
        Assert.Equal(expected, color);
    }

    [Theory]
    [InlineData("device-cmyk(0 0 0)")]
    [InlineData("device-cmyk(0 0 0 0 0)")]
    [InlineData("device-cmyk(0, 0, 0)")]
    [InlineData("device-cmyk(0%, 0, 0, 0)")]
    [InlineData("device-cmyk(0 0 0 10deg)")]
    [InlineData("device-cmyk(0, 0, 0, 0 / 1)")]
    public void Invalid_device_cmyk_is_rejected(string css)
        => Assert.False(CssColor.TryParse(css, CssColor.Black, out _));

    [Fact]
    public void Colours_with_the_same_conversion_get_different_stand_ins()
    {
        var colors = new CmykColors();
        uint black = colors.Register(new Cmyk(0, 0, 0, 1));
        uint rich = colors.Register(new Cmyk(0.6f, 0.4f, 0.4f, 1));
        Assert.NotEqual(black, rich);
        // Neither is plain black, which an author may also write as sRGB.
        Assert.NotEqual(0u, black);
        Assert.NotEqual(0u, rich);
        Assert.Equal(black, colors.Register(new Cmyk(0, 0, 0, 1)));
        Assert.True(colors.TryGet(0xFF000000 | rich, out var back));
        Assert.Equal(new Cmyk(0.6f, 0.4f, 0.4f, 1), back);
        Assert.False(colors.TryGet(0xFF000000, out _));
    }

    [Fact]
    public void Text_backgrounds_and_borders_are_written_as_DeviceCMYK()
    {
        var pdf = HtmlToPdf.Convert("""
            <div style="color: device-cmyk(0 0 0 1); background: device-cmyk(0.1 0.2 0.3 0);
                        border: 2px solid device-cmyk(100% 0% 0% 0%)">Ink</div>
            <p style="color: black">Plain black</p>
            """);
        var content = AllContent(pdf);
        Assert.Contains("0 0 0 1 k", content);
        Assert.Contains("0.1 0.2 0.3 0 k", content);
        Assert.Contains("1 0 0 0 K", content);
        // Plain sRGB black stays sRGB.
        Assert.Contains("0 0 0 rg", content);
    }

    [Fact]
    public void Translucent_cmyk_keeps_its_inks_and_uses_a_fill_alpha()
    {
        var content = AllContent(HtmlToPdf.Convert("<div style='background: device-cmyk(0 1 0 0 / 0.5); height: 20px'></div>"));
        Assert.Contains("0 1 0 0 k", content);
        Assert.Contains(" gs", content);
    }

    [Fact]
    public void Svg_fills_and_strokes_are_written_as_DeviceCMYK()
    {
        var content = AllContent(HtmlToPdf.Convert("""
            <svg width="40" height="40"><rect width="30" height="30" fill="device-cmyk(0 0 1 0)"
                stroke="device-cmyk(0 0.5 0 0.25)" /></svg>
            """));
        Assert.Contains("0 0 1 0 k", content);
        Assert.Contains("0 0.5 0 0.25 K", content);
    }

    [Fact]
    public void Pdf_a_keeps_cmyk_colours_as_srgb()
    {
        var content = AllContent(HtmlToPdf.Convert("<p style='color: device-cmyk(0 1 1 0)'>Red</p>",
            new PdfOptions { Conformance = PdfConformance.PdfA2b }));
        Assert.DoesNotContain(" k\n", content);
        Assert.Contains("1 0 0 rg", content);
    }

    [Fact]
    public void Documents_without_cmyk_are_unchanged()
    {
        var html = "<p style='color: #123456; background: cyan'>Text</p>";
        Assert.Equal(AllContent(HtmlToPdf.Convert(html)), AllContent(HtmlToPdf.Convert(html)));
        Assert.DoesNotContain(" k\n", AllContent(HtmlToPdf.Convert(html)));
    }

    [Fact]
    public void The_renderer_paints_the_cmyk_inks()
    {
        var pdf = HtmlToPdf.Convert("<style>@page{size:20px 20px;margin:0}</style><div style='height:20px;background:device-cmyk(0 1 1 0)'></div>");
        var pixels = PdfRenderer.Open(pdf).RenderPageExact(0, 15, 15, new ImageOptions(), CancellationToken.None).Premultiplied;
        // The renderer turns ink into a printed colour, so magenta and yellow are a strong red, not sRGB's pure red.
        // Pixels are 0xAABBGGRR.
        Assert.Equal(0xFF172EFFu, pixels[7 * 15 + 7]);
    }
}
