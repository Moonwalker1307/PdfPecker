using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>Removing what pages draw in given areas, so that it is gone from the file and not only covered.</summary>
public class RedactionTests
{
    private const string Page = "<style>@page { size: 400pt 300pt; margin: 0 } body { margin: 0; font: 12pt Helvetica }"
        + " p { position: absolute; left: 20pt; margin: 0 }</style>";

    /// <summary>A PDF of the given objects, numbered from 1 with the catalog first, and a correct cross-reference table.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Stream(string content) => "<< /Length " + Encoding.Latin1.GetByteCount(content) + " >>\nstream\n" + content + "\nendstream";

    /// <summary>A one-page PDF (400 by 300 points) with Helvetica as /F1 and the given content.</summary>
    private static byte[] OnePage(string content, string pageExtra = "", params string[] more)
        => Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R " + pageExtra + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream(content),
        }.Concat(more).ToArray());

    private static RenderedImage Render(byte[] pdf, int page = 0) => PdfRenderer.Open(pdf).RenderPage(page, new ImageOptions { Dpi = 72 });

    /// <summary>The colour of a pixel as 0xRRGGBB, flattened on white.</summary>
    private static uint Pixel(RenderedImage image, int x, int y)
    {
        uint p = image.Premultiplied[y * image.Width + x], a = p >> 24;
        uint Channel(int shift) => ((p >> shift) & 255) + (255 - a);
        return Channel(0) << 16 | Channel(8) << 8 | Channel(16);
    }

    /// <summary>Every byte of the file, with each stream that inflates also inflated.</summary>
    private static string AllBytes(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var sb = new StringBuilder(text);
        foreach (Match m in Regex.Matches(text, "stream\r?\n"))
        {
            int start = m.Index + m.Length, end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) continue;
            try
            {
                using var z = new System.IO.Compression.ZLibStream(new MemoryStream(pdf, start, end - start), System.IO.Compression.CompressionMode.Decompress);
                using var ms = new MemoryStream();
                z.CopyTo(ms);
                sb.Append(Encoding.Latin1.GetString(ms.ToArray()));
            }
            catch (InvalidDataException)
            {
            }
        }
        return sb.ToString();
    }

    private static PdfTextWord Word(byte[] pdf, string text)
        => PdfText.ExtractPages(pdf).SelectMany(p => p.Lines).SelectMany(l => l.Words).First(w => w.Text == text);

    [Fact]
    public void Redacted_text_no_longer_extracts_and_the_rest_keeps_its_place()
    {
        var pdf = HtmlToPdf.Convert(Page + "<p style='top:40pt'>Name Ada Lovelace</p><p style='top:80pt'>Account 12345678 closed</p>");
        var closed = Word(pdf, "closed");

        var redacted = PdfRedactor.RedactText(pdf, "12345678");

        var text = PdfText.Extract(redacted);
        Assert.DoesNotContain("12345678", text);
        Assert.Contains("Ada Lovelace", text);
        Assert.Contains("Account", text);
        var after = Word(redacted, "closed");
        Assert.Equal(closed.X, after.X, 2);
        Assert.Equal(closed.Y, after.Y, 2);
        // The digits are not in the file in any form: not in a content stream, nor in an older revision.
        Assert.DoesNotContain("12345678", AllBytes(redacted));
        PdfTestHelpers.AssertStructurallyValid(redacted);
    }

    [Fact]
    public void A_black_box_covers_the_area_by_default_and_none_is_drawn_without_a_fill()
    {
        var pdf = HtmlToPdf.Convert(Page + "<p style='top:40pt'>Secret</p>");
        var word = Word(pdf, "Secret");
        var area = new[] { PdfRedactionArea.Of(1, word) };
        int x = (int)(word.X + word.Width / 2), y = (int)(word.Y + word.Height / 2);

        Assert.Equal(0x000000u, Pixel(Render(PdfRedactor.Redact(pdf, area)), x, y));
        var blank = PdfRedactor.Redact(pdf, area, new PdfRedactOptions { FillColor = null });
        Assert.Equal(0xFFFFFFu, Pixel(Render(blank), x, y));
        Assert.Equal(0xFF0000u, Pixel(Render(PdfRedactor.Redact(pdf, area, new PdfRedactOptions { FillColor = "red" })), x, y));
    }

    [Fact]
    public void Image_pixels_under_the_area_are_overwritten_and_the_rest_of_the_image_stays()
    {
        var red = PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(40, 20, 255, 0, 0));
        var pdf = HtmlToPdf.Convert(Page + $"<img src='{red}' style='position:absolute;left:100pt;top:100pt;width:200pt;height:100pt'>");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 100, 100, 100, 100) }, new PdfRedactOptions { FillColor = null });

        var image = Render(redacted);
        Assert.Equal(0xFFFFFFu, Pixel(image, 150, 150));
        Assert.Equal(0xFF0000u, Pixel(image, 250, 150));
        // The image the page draws now is a new one; the original, with its red pixels in the area, is not in the file.
        Assert.Single(PdfImages.Extract(redacted));
    }

    [Fact]
    public void An_image_wholly_inside_an_area_is_removed()
    {
        var red = PdfTestHelpers.DataUri(PdfTestHelpers.SolidPng(4, 4, 255, 0, 0));
        var pdf = HtmlToPdf.Convert(Page + $"<img src='{red}' style='position:absolute;left:100pt;top:100pt;width:50pt;height:50pt'>");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 90, 90, 80, 80) });

        Assert.Empty(PdfImages.Extract(redacted));
    }

    [Fact]
    public void Shapes_wholly_inside_an_area_go_and_shapes_crossing_it_stay()
    {
        // A small square inside the area and a long bar through it, in one path and in two.
        var pdf = OnePage("0 0 1 rg 50 150 20 20 re 150 150 200 10 re f 1 0 0 rg 50 50 20 20 re f");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 40, 120, 140, 40), new PdfRedactionArea(1, 40, 220, 40, 40) },
            new PdfRedactOptions { FillColor = null });

        var image = Render(redacted);
        Assert.Equal(0xFFFFFFu, Pixel(image, 60, 140)); // the blue square
        Assert.Equal(0x0000FFu, Pixel(image, 300, 145)); // the bar, outside the area
        Assert.Equal(0xFFFFFFu, Pixel(image, 60, 240)); // the red square, a path of its own
    }

    [Fact]
    public void Text_in_forms_is_redacted_in_a_copy_and_other_pages_drawing_the_form_keep_it()
    {
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count 2 /Kids [3 0 R 7 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /XObject << /X1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 1 0 0 1 20 200 cm /X1 Do Q"),
            "<< /Type /XObject /Subtype /Form /BBox [0 0 300 50] /Resources << /Font << /F1 6 0 R >> >> /Length 38 >>\nstream\nBT /F1 12 Tf 0 10 Td (Classified) Tj ET\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Resources << /XObject << /X1 5 0 R >> >> /Contents 4 0 R >>");

        var areas = PdfRedactor.FindText(pdf, "Classified").Where(a => a.PageNumber == 1).ToList();
        Assert.Single(areas);
        var redacted = PdfRedactor.Redact(pdf, areas);

        var pages = PdfText.ExtractPages(redacted);
        Assert.DoesNotContain("Classified", pages[0].Text);
        Assert.Contains("Classified", pages[1].Text);
    }

    [Fact]
    public void Replacement_text_of_marked_content_that_lost_glyphs_goes_too()
    {
        var pdf = OnePage("BT /F1 12 Tf 20 200 Td /Span << /ActualText (Top secret) >> BDC (XXXXXXXX) Tj EMC ET");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 10, 80, 200, 30) });

        Assert.DoesNotContain("secret", PdfText.Extract(redacted));
        Assert.DoesNotContain("secret", AllBytes(redacted));
    }

    [Fact]
    public void Invisible_and_letter_spaced_text_are_removed_and_text_after_them_keeps_its_place()
    {
        var pdf = OnePage("BT /F1 12 Tf 3 Tr 20 200 Td (hidden) Tj 0 Tr 2 Tc 0 -40 Td [(spaced) -3000 (after)] TJ ET");
        var after = Word(pdf, "after");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 10, 85, 70, 60) });

        var text = PdfText.Extract(redacted);
        Assert.DoesNotContain("hidden", text);
        Assert.DoesNotContain("spaced", text);
        Assert.Equal(after.X, Word(redacted, "after").X, 2);
    }

    [Fact]
    public void Text_on_a_rotated_page_is_found_and_removed()
    {
        var pdf = OnePage("BT /F1 12 Tf 20 200 Td (Rotated secret) Tj ET", "/Rotate 90");

        var redacted = PdfRedactor.RedactText(pdf, "secret");

        var text = PdfText.Extract(redacted);
        Assert.DoesNotContain("secret", text);
        Assert.Contains("Rotated", text);
    }

    [Fact]
    public void Links_and_form_fields_that_touch_an_area_are_removed_with_their_values()
    {
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R 7 0 R] >> >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Contents 4 0 R /Annots [5 0 R 6 0 R 7 0 R] >>",
            Stream("0 g 0 0 1 1 re f"),
            "<< /Type /Annot /Subtype /Link /Rect [20 250 120 270] /A << /S /URI /URI (https://example.com/private) >> >>",
            "<< /FT /Tx /T (ssn) /V (078-05-1120) /Type /Annot /Subtype /Widget /Rect [20 200 120 220] /P 3 0 R >>",
            "<< /FT /Tx /T (city) /V (Copenhagen) /Type /Annot /Subtype /Widget /Rect [20 100 120 120] /P 3 0 R >>");

        var redacted = PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(1, 0, 20, 200, 90) });

        var content = AllBytes(redacted);
        Assert.DoesNotContain("example.com/private", content);
        Assert.DoesNotContain("078-05-1120", content);
        var file = PdfFile.Open(redacted, null);
        var fields = file.GetArray(file.GetDict(file.Catalog, "AcroForm"), "Fields")!;
        var city = Assert.IsType<PdfDict>(file.Resolve(Assert.Single(fields.Items)));
        Assert.Equal("Copenhagen", (file.Resolve(city["V"]) as PdfString)?.Text);
    }

    [Fact]
    public void A_regular_expression_finds_what_to_redact_and_the_same_input_gives_the_same_bytes()
    {
        var pdf = HtmlToPdf.Convert(Page + "<p style='top:40pt'>Mail ada@example.com or call +45 12 34 56 78</p>",
            new PdfOptions { CreationDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        var email = new Regex(@"\S+@\S+");

        var first = PdfRedactor.RedactText(pdf, email);

        Assert.Equal(first, PdfRedactor.RedactText(pdf, email));
        var text = PdfText.Extract(first);
        Assert.DoesNotContain("example.com", text);
        Assert.Contains("+45 12 34 56 78", text);
    }

    [Fact]
    public void Areas_on_pages_past_the_last_and_encrypted_pdfs_are_refused()
    {
        var pdf = OnePage("BT /F1 12 Tf 20 200 Td (x) Tj ET");
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfRedactor.Redact(pdf, new[] { new PdfRedactionArea(2, 0, 0, 10, 10) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfRedactionArea(0, 0, 0, 10, 10));
        Assert.Throws<ArgumentException>(() => PdfRedactor.Redact(pdf, Array.Empty<PdfRedactionArea>(), new PdfRedactOptions { FillColor = "not a colour" }));

        var encrypted = PdfProtector.Encrypt(pdf, new PdfSecurity { UserPassword = "u", OwnerPassword = "o" });
        Assert.Throws<ArgumentException>(() => PdfRedactor.Redact(encrypted, new[] { new PdfRedactionArea(1, 0, 0, 10, 10) }));
        Assert.Throws<InvalidDataException>(() => PdfRedactor.Redact(Encoding.ASCII.GetBytes("not a pdf"), Array.Empty<PdfRedactionArea>()));
    }
}
