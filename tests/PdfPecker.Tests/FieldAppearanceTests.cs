using static PdfPecker.Tests.FixturePdf;

namespace PdfPecker.Tests;

/// <summary>
/// Form fields whose appearance the viewer is asked to draw (/NeedAppearances) or that have none, as tools that fill in
/// only the values leave them: the renderer draws them as viewers do. Pages are rendered at one pixel per point.
/// </summary>
public class FieldAppearanceTests
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>A page with the given widgets, which are also the form's fields.</summary>
    private static byte[] Form(bool needAppearances, params string[] widgets)
    {
        var pdf = new FixturePdf();
        var numbers = widgets.Select(pdf.Add).ToList();
        string refs = string.Join(" ", numbers.Select(n => n + " 0 R"));
        string need = needAppearances ? "/NeedAppearances true " : "";
        return pdf.Build(200, 120, "", "", "/Annots [" + refs + "]",
            $"/AcroForm << /Fields [{refs}] {need}/DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv {Helvetica} >> >> >> ");
    }

    /// <summary>How many pixels in the rectangle (PDF space, y up) are darker than mid grey.</summary>
    private static int Dark(RenderedImage img, int x0, int y0, int x1, int y1)
    {
        int dark = 0;
        for (int x = x0; x < x1; x++)
            for (int y = y0; y < y1; y++)
                if (At(img, x, y).R < 128) dark++;
        return dark;
    }

    [Fact]
    public void Text_fields_the_viewer_is_asked_to_draw_show_their_values()
    {
        var pdf = Form(true,
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Jane Example) /Rect [10 80 190 100] /DA (/Helv 12 Tf 0 g) /F 4 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (city) /V (Copenhagen) /Rect [10 40 190 60] /DA (/Helv 12 Tf 0 0 1 rg) /F 4 >>");
        var img = Render(pdf);
        Assert.InRange(Dark(img, 10, 80, 120, 100), 40, 2000);
        // The second value is drawn in its own colour.
        Assert.Contains(Enumerable.Range(10, 100).SelectMany(x => Enumerable.Range(40, 20).Select(y => At(img, x, y))),
            p => p.B > 200 && p.R < 60 && p.G < 60);
        Assert.Equal(0, Dark(img, 10, 0, 190, 30));

        string text = PdfText.Extract(pdf);
        Assert.Contains("Jane Example", text, StringComparison.Ordinal);
        Assert.Contains("Copenhagen", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_without_an_appearance_is_drawn_even_when_the_viewer_is_not_asked_to()
    {
        var img = Render(Form(false,
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Jane Example) /Rect [10 80 190 100] /DA (/Helv 12 Tf 0 g) /F 4 >>"));
        Assert.InRange(Dark(img, 10, 80, 120, 100), 40, 2000);
    }

    [Fact]
    public void A_stored_appearance_stays_unless_the_viewer_is_asked_to_draw_the_field()
    {
        var pdf = new FixturePdf();
        // The file's own appearance: a stale value at the right of a pale yellow box.
        int ap = pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 180 20] /Resources << /Font << /Helv {Helvetica} >> >>",
            "1 1 0.8 rg 0 0 180 20 re f BT 0 g /Helv 12 Tf 120 5 Td (Stale) Tj ET");
        int w = pdf.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Jane) /Rect [10 80 190 100] /DA (/Helv 12 Tf 0 g) /F 4 /AP << /N {ap} 0 R >> >>");
        byte[] Build(string need) => pdf.Build(200, 120, "", "", $"/Annots [{w} 0 R]",
            $"/AcroForm << /Fields [{w} 0 R] {need}/DR << /Font << /Helv {Helvetica} >> >> >> ");

        var stored = Render(Build(""));
        Assert.InRange(Dark(stored, 125, 80, 190, 100), 20, 2000);
        Assert.Equal(0, Dark(stored, 10, 80, 60, 100));
        // Asked to draw the field, the renderer shows the value on the stored background instead, as viewers do.
        var redrawn = Render(Build("/NeedAppearances true "));
        Assert.Equal(0, Dark(redrawn, 125, 80, 190, 100));
        Assert.InRange(Dark(redrawn, 10, 80, 60, 100), 20, 2000);
        AssertAt(redrawn, 100, 82, 255, 255, 204);
    }

    [Fact]
    public void A_check_box_that_is_on_without_an_appearance_shows_a_check_mark()
    {
        var img = Render(Form(false,
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (on) /V /Yes /AS /Yes /Rect [20 40 60 80] /F 4 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (off) /V /Off /AS /Off /Rect [120 40 160 80] /F 4 >>"));
        Assert.InRange(Dark(img, 20, 40, 60, 80), 50, 1600);
        Assert.Equal(0, Dark(img, 120, 40, 160, 80));
    }

    [Fact]
    public void Hidden_fields_stay_hidden()
    {
        var img = Render(Form(true,
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Jane Example) /Rect [10 80 190 100] /DA (/Helv 12 Tf 0 g) /F 2 >>"));
        Assert.Equal(0, Dark(img, 0, 0, 200, 120));
    }
}
