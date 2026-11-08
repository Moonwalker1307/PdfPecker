using System.IO.Compression;
using System.Text;
using PdfPecker.Rendering;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;
using static PdfPecker.Tests.FixturePdf;

namespace PdfPecker.Tests;

/// <summary>
/// The renderer's per-document cache: colour spaces and functions parsed once, and pages whose output does not depend
/// on which pages were rendered before them, although each page has a decode budget of its own.
/// </summary>
public class RenderCacheTests
{
    private static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>
    /// Two spot colours over CMYK whose tint transforms are sampled functions of 65,536 entries (262 KB decoded each),
    /// used in turn by <paramref name="fills"/> small fills, each setting its colour space first as layout programs
    /// write them (or, without <paramref name="setEveryTime"/>, setting the first once); then a green image and text whose
    /// font maps its codes to other letters with a compressed ToUnicode map.
    /// </summary>
    private static byte[] SpotColourPage(int fills, int spaces = 2, bool setEveryTime = true)
    {
        var pdf = new FixturePdf();
        int Function(int ink)
        {
            var samples = new byte[65536 * 4];
            for (int i = ink; i < samples.Length; i += 4) samples[i] = 255;
            return pdf.AddStream("/FunctionType 0 /Domain [0 1] /Range [0 1 0 1 0 1 0 1] /Size [65536] /BitsPerSample 8 /Filter /FlateDecode", Deflate(samples));
        }
        int cyan = Function(0), magenta = Function(1);
        int image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode",
            Deflate(new byte[] { 0, 200, 0, 0, 200, 0, 0, 200, 0, 0, 200, 0 }));
        const string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /X def\n" +
                            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
                            "5 beginbfchar <48> <0058> <65> <0059> <6C> <005A> <6F> <0057> <20> <0020> endbfchar\n" +
                            "endcmap CMapName currentdict /CMap defineresource pop end end";
        int toUnicode = pdf.AddStream("/Filter /FlateDecode", Deflate(Encoding.Latin1.GetBytes(cmap)));
        int font = pdf.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode {toUnicode} 0 R >>");
        var content = new StringBuilder();
        if (!setEveryTime) content.Append("/CS0 cs ");
        for (int i = 0; i < fills; i++)
        {
            if (setEveryTime) content.Append($"/CS{i % spaces} cs ");
            content.Append($"1 scn {i * 4 % 200} {100 + i * 4 / 200 % 100} 4 4 re f\n");
        }
        content.Append("q 100 0 0 50 0 0 cm /Im0 Do Q BT /F1 12 Tf 110 20 Td (Hello) Tj ET");
        return pdf.Build(200, 200, content.ToString(),
            $"/ColorSpace << /CS0 [/Separation /SpotA /DeviceCMYK {cyan} 0 R] /CS1 [/Separation /SpotB /DeviceCMYK {magenta} 0 R] >> " +
            $"/XObject << /Im0 {image} 0 R >> /Font << /F1 {font} 0 R >>");
    }

    [Fact]
    public void Setting_a_spot_colour_before_every_fill_keeps_the_page_decode_budget()
    {
        // Each cs decoded the tint transform again: 3,000 of them used up the page's 512 MB decode budget, after which
        // the image was left out without an error and the text lost its ToUnicode map.
        var pdf = SpotColourPage(3000);
        var img = Render(pdf);
        AssertAt(img, 50, 25, 0, 200, 0);
        Assert.Equal("XYZZW", PdfText.Extract(pdf).Trim());
    }

    [Fact]
    public void A_colour_space_set_before_every_fill_paints_as_when_set_once()
    {
        var again = SpotColourPage(50, spaces: 1);
        var once = SpotColourPage(50, spaces: 1, setEveryTime: false);
        Assert.Equal(Render(once).Rgba, Render(again).Rgba);
        var img = Render(again);
        AssertAt(img, 2, 102, 0, 174, 239, tolerance: 40); // cyan ink
    }

    /// <summary>A page that draws two different images of the same size, one above the other.</summary>
    private static byte[] TwoImagePage()
    {
        var pdf = new FixturePdf();
        int Image(byte r, byte g, byte b)
        {
            var pixels = new byte[64 * 64 * 3];
            for (int i = 0; i < pixels.Length; i += 3) (pixels[i], pixels[i + 1], pixels[i + 2]) = (r, g, b);
            return pdf.AddStream("/Type /XObject /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", Deflate(pixels));
        }
        int red = Image(255, 0, 0), blue = Image(0, 0, 255);
        return pdf.Build(20, 40, "q 20 0 0 20 0 20 cm /A Do Q q 20 0 0 20 0 0 cm /B Do Q", $"/XObject << /A {red} 0 R /B {blue} 0 R >>");
    }

    private const long AmpleBudget = 1L << 40;

    /// <summary>Renders the page with the given decode budget and returns the canvas and what decoding used of it.</summary>
    private static (Canvas Canvas, long Used) RenderWithBudget(RenderDocument doc, RenderCache cache, long budget)
    {
        var page = doc.Pages[0];
        var canvas = new Canvas(20, 40, 0);
        using (PdfFile.BeginRender(CancellationToken.None, budget))
        {
            new PageRenderer(doc.File, cache, canvas, CancellationToken.None).RenderPage(page.Dict, page.Resources, new Matrix(1, 0, 0, -1, 0, 40), annotations: false);
            return (canvas, budget - PdfFile.DecodeLimit);
        }
    }

    // The top image (red) and the bottom one (blue), or 0 where nothing was drawn.
    private static uint Top(Canvas c) => c.Pixels[10 * c.Width + 10];
    private static uint Bottom(Canvas c) => c.Pixels[30 * c.Width + 10];

    [Fact]
    public void An_image_a_page_had_no_budget_left_for_still_draws_on_the_next_page()
    {
        // The first render runs out of budget before the images; the document-wide cache kept them as missing, so every
        // later page that drew them showed nothing, depending on which pages had been rendered first.
        var doc = new RenderDocument(TwoImagePage(), null);
        var cache = new RenderCache();
        var starved = RenderWithBudget(doc, cache, 0).Canvas;
        Assert.Equal(0u, Top(starved));
        var next = RenderWithBudget(doc, cache, AmpleBudget).Canvas;
        Assert.Equal(0xFF0000FFu, Top(next));
        Assert.Equal(0xFFFF0000u, Bottom(next));
    }

    [Fact]
    public void A_page_pays_for_cached_images_as_if_it_had_decoded_them()
    {
        // With a budget for one of its two images the page draws the first and not the second, also when another page
        // already decoded both: it is charged for what it finds in the cache, once, as for what it decodes itself.
        long both = RenderWithBudget(new RenderDocument(TwoImagePage(), null), new RenderCache(), AmpleBudget).Used;
        long one = both * 3 / 4;
        var alone = RenderWithBudget(new RenderDocument(TwoImagePage(), null), new RenderCache(), one);
        Assert.Equal(0xFF0000FFu, Top(alone.Canvas));
        Assert.Equal(0u, Bottom(alone.Canvas));

        var doc = new RenderDocument(TwoImagePage(), null);
        var cache = new RenderCache();
        RenderWithBudget(doc, cache, AmpleBudget);
        var after = RenderWithBudget(doc, cache, one);
        Assert.Equal(alone.Canvas.Pixels, after.Canvas.Pixels);
        Assert.Equal(alone.Used, after.Used);
    }
}
