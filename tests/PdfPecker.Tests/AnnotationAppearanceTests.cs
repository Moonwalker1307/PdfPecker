using static PdfPecker.Tests.FixturePdf;

namespace PdfPecker.Tests;

/// <summary>
/// Annotations without an appearance stream, which the renderer draws from their own properties as viewers do: text
/// markup, notes, shapes, lines, ink and free text. Pages are rendered at one pixel per point.
/// </summary>
public class AnnotationAppearanceTests
{
    /// <summary>A white page with the given annotations and, when given, some content under them.</summary>
    private static byte[] Page(string content, params string[] annots)
    {
        var pdf = new FixturePdf();
        var numbers = annots.Select(pdf.Add).ToList();
        return pdf.Build(200, 120, content, "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >>",
            "/Annots [" + string.Join(" ", numbers.Select(n => n + " 0 R")) + "]");
    }

    /// <summary>The darkest pixel in a column between two heights, where a thin line falls between pixel rows.</summary>
    private static (int R, int G, int B, int A) Darkest(RenderedImage img, int x, int y0, int y1)
        => Enumerable.Range(y0, y1 - y0 + 1).Select(y => At(img, x, y)).MinBy(p => p.R + p.G + p.B);

    private static string Quad(int x0, int y0, int x1, int y1) => $"[{x0} {y1} {x1} {y1} {x0} {y0} {x1} {y0}]";

    [Fact]
    public void A_highlight_multiplies_its_colour_over_the_text()
    {
        var pdf = Page("0 0 1 rg 20 20 40 40 re f",
            $"<< /Type /Annot /Subtype /Highlight /Rect [10 10 100 70] /QuadPoints {Quad(10, 10, 100, 70)} /C [1 1 0] >>");
        var img = Render(pdf);
        AssertAt(img, 80, 50, 255, 255, 0);
        // Yellow multiplied with blue is black, so the content under the highlight stays readable.
        AssertAt(img, 40, 40, 0, 0, 0);
        AssertAt(img, 150, 50, 255, 255, 255);
    }

    [Fact]
    public void Underline_strikeout_and_squiggly_draw_lines_in_the_quadrilaterals()
    {
        var img = Render(Page("",
            $"<< /Type /Annot /Subtype /Underline /Rect [10 90 190 110] /QuadPoints {Quad(10, 90, 190, 110)} /C [0 0 1] >>",
            $"<< /Type /Annot /Subtype /StrikeOut /Rect [10 50 190 70] /QuadPoints {Quad(10, 50, 190, 70)} /C [1 0 0] >>",
            $"<< /Type /Annot /Subtype /Squiggly /Rect [10 10 190 30] /QuadPoints {Quad(10, 10, 190, 30)} >>"));
        // The underline sits near the bottom of its box, the strikeout at 40% of its height.
        AssertColor(Darkest(img, 100, 88, 95), 0, 0, 255, 40);
        AssertAt(img, 100, 105, 255, 255, 255);
        AssertColor(Darkest(img, 100, 55, 61), 255, 0, 0, 80);
        // A squiggly line with no colour is black, and it zigzags around the line an underline would take.
        int dark = 0;
        for (int x = 10; x < 190; x++)
            for (int y = 9; y < 15; y++)
                if (At(img, x, y).R < 128) dark++;
        Assert.InRange(dark, 100, 900);
        AssertAt(img, 100, 25, 255, 255, 255);
    }

    [Fact]
    public void Shapes_are_stroked_and_filled_with_their_colours()
    {
        var img = Render(Page("",
            "<< /Type /Annot /Subtype /Square /Rect [10 10 90 90] /C [0 0 1] /IC [1 0 0] /BS << /W 4 >> >>",
            "<< /Type /Annot /Subtype /Circle /Rect [110 10 190 90] /IC [0 1 0] /C [] >>"));
        AssertAt(img, 50, 50, 255, 0, 0);
        AssertAt(img, 11, 50, 0, 0, 255);
        AssertAt(img, 150, 50, 0, 255, 0);
        // The circle's corners are outside it.
        AssertAt(img, 112, 12, 255, 255, 255);
    }

    [Fact]
    public void Lines_get_their_endings_and_ink_its_strokes()
    {
        var img = Render(Page("",
            "<< /Type /Annot /Subtype /Line /Rect [0 80 200 120] /L [20 100 180 100] /C [1 0 0] /BS << /W 2 >> /LE [/Square /ClosedArrow] /IC [0 0 1] >>",
            "<< /Type /Annot /Subtype /Ink /Rect [0 0 200 60] /InkList [[20 30 180 30] [100 10 100 50]] /C [0 0.5 0] /BS << /W 4 >> >>"));
        AssertAt(img, 100, 100, 255, 0, 0, 30);
        // The ends are filled with the interior colour.
        AssertAt(img, 20, 100, 0, 0, 255, 30);
        AssertAt(img, 173, 100, 0, 0, 255, 30);
        AssertAt(img, 60, 30, 0, 128, 0, 10);
        AssertAt(img, 100, 45, 0, 128, 0, 10);
    }

    [Fact]
    public void A_note_draws_its_icon_in_the_corner()
    {
        var img = Render(Page("", "<< /Type /Annot /Subtype /Text /Rect [20 80 40 100] /Contents (Read me) /C [1 0 0] >>"));
        AssertAt(img, 25, 85, 255, 0, 0);
        AssertAt(img, 60, 85, 255, 255, 255);
    }

    [Fact]
    public void Free_text_is_wrapped_in_its_box_and_extracted()
    {
        var pdf = Page("",
            "<< /Type /Annot /Subtype /FreeText /Rect [10 10 110 110] /Contents (Typed text that does not fit one line) /DA (/Helv 12 Tf 0 0 1 rg) /IC [1 1 0.8] /BS << /W 1 >> >>");
        var img = Render(pdf);
        // A yellow background, a border in the text colour and blue text.
        AssertAt(img, 100, 20, 255, 255, 204);
        AssertAt(img, 10.5, 60, 0, 0, 255, 60);
        Assert.Contains(Enumerable.Range(12, 90).SelectMany(x => Enumerable.Range(80, 28).Select(y => At(img, x, y))), p => p.B > 200 && p.R < 60);
        var text = PdfText.Extract(pdf);
        Assert.Contains("Typed text that", text);
        Assert.True(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length >= 2, text);
        Assert.DoesNotContain("Typed text", PdfText.Extract(pdf, new PdfTextOptions { IncludeAnnotations = false }));
    }

    [Fact]
    public void Opacity_hidden_flags_and_the_option_are_respected()
    {
        var pdf = Page("",
            "<< /Type /Annot /Subtype /Square /Rect [10 10 90 90] /IC [0 0 0] /CA 0.5 >>",
            "<< /Type /Annot /Subtype /Square /Rect [110 10 190 90] /IC [0 0 0] /F 2 >>");
        var img = Render(pdf);
        AssertAt(img, 50, 50, 128, 128, 128, 8);
        AssertAt(img, 150, 50, 255, 255, 255);
        var plain = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72, RenderAnnotations = false });
        AssertAt(plain, 50, 50, 255, 255, 255);
    }

    [Fact]
    public void An_appearance_with_no_state_for_the_current_one_draws_nothing()
    {
        // Appearance states without the one the annotation is in: viewers show nothing, so nothing is drawn for it.
        var pdf = new FixturePdf();
        int off = pdf.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 80 80]", "0 g 0 0 80 80 re f");
        int annot = pdf.Add($"<< /Type /Annot /Subtype /Square /Rect [10 10 90 90] /IC [1 0 0] /AP << /N << /Off {off} 0 R >> >> /AS /On >>");
        var img = Render(pdf.Build(200, 120, "", "", $"/Annots [{annot} 0 R]"));
        AssertAt(img, 50, 50, 255, 255, 255);
    }

    [Fact]
    public void Huge_markup_is_drawn_within_bounded_time()
    {
        // 200,000 squiggly quadrilaterals would make a drawing of hundreds of megabytes; it stops growing at a megabyte.
        var quads = string.Join(" ", Enumerable.Repeat("0 120 200 120 0 0 200 0", 200_000));
        var pdf = Page("", $"<< /Type /Annot /Subtype /Squiggly /Rect [0 0 200 120] /QuadPoints [{quads}] >>");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Render(pdf);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }
}
