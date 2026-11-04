using System.Text;
using static PdfPecker.Tests.PdfToPdfATests;

namespace PdfPecker.Tests;

/// <summary>
/// Inputs that could make the PDF/A conversion run without end or fail with an undocumented exception: content that
/// draws itself, attachments nested in attachments, and dictionaries with sizes far beyond what they hold. Each must end
/// quickly with a result that reports what it could not fix.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class PdfToPdfAHostileInputTests
{
    private static PdfAConversionResult Bounded(byte[] pdf, PdfConformance conformance = PdfConformance.PdfA2b)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var task = Task.Run(() => PdfToPdfA.Convert(pdf, new PdfAConversionOptions { Conformance = conformance }, cts.Token));
        Assert.True(task.Wait(TimeSpan.FromSeconds(30)), "Did not finish in time");
        return task.Result;
    }

    private static string Page(string resources, int contents)
        => $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources {resources} /Contents {contents} 0 R >>";

    [Fact]
    public void A_form_that_draws_itself_and_a_long_chain_of_forms_end()
    {
        // Object 5 draws itself; objects 6 on draw the next one, 3,000 deep, and each also draws the page's own font.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            Page("<< /XObject << /Self 5 0 R /Chain 6 0 R >> /Font << /F 4 0 R >> >>", 7),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject << /Self 5 0 R >> >> /Length 8 >>\nstream\n/Self Do\nendstream",
        };
        const int chain = 3000;
        int first = 8;
        objects.Add("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject << /N " + first + " 0 R >> >> /Length 5 >>\nstream\n/N Do\nendstream");
        objects.Add(Stream("/Self Do /Chain Do BT /F 9 Tf (x) Tj ET"));
        for (int i = 0; i < chain; i++)
        {
            int next = first + i + 1;
            objects.Add(i < chain - 1
                ? "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject << /N " + next + " 0 R >> /Font << /F 4 0 R >> >> /Length 26 >>\nstream\n/N Do BT /F 9 Tf (y) Tj ET\nendstream"
                : "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 9 >>\nstream\n0 0 1 1 re\nendstream");
        }
        var result = Bounded(BuildPdf(objects.ToArray()));
        Assert.NotEmpty(result.Pdf);
    }

    [Fact]
    public void A_type3_font_that_shows_its_own_text_ends()
    {
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            Page("<< /Font << /T3 4 0 R >> >>", 6),
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 5 0 R >> /Encoding << /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [500] /Resources << /Font << /T3 4 0 R >> >> >>",
            Stream("500 0 d0 BT /T3 1 Tf (aaaa) Tj ET"),
            Stream("BT /T3 12 Tf 10 10 Td (a) Tj ET"));
        Assert.NotEmpty(Bounded(pdf, PdfConformance.PdfA2u).Pdf);
    }

    [Fact]
    public void Widths_and_codes_far_beyond_the_font_are_bounded()
    {
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            Page("<< /Font << /F 4 0 R /C 5 0 R >> >>", 7),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar -2147483648 /LastChar 2147483647 /Widths [600] >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /W [0 [1 2 3] 4 2147483647 500 0 65535 1] /DW 1000 >>",
            Stream("BT /F 12 Tf (abc) Tj /C 12 Tf <0000FFFF7FFF> Tj ET"));
        var result = Bounded(pdf);
        Assert.NotEmpty(result.Pdf);
    }

    [Fact]
    public void A_devicen_space_with_too_many_colorants_is_reported_and_not_sampled()
    {
        var names = string.Join(" ", Enumerable.Range(0, 2000).Select(i => "/Spot" + i));
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            Page("<< /ColorSpace << /CS 4 0 R >> >>", 5),
            $"[/DeviceN [{names}] /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 0 0] /N 1 >>]",
            Stream("/CS cs 0.5 sc 0 0 10 10 re f"));
        var result = Bounded(pdf);
        Assert.Contains(result.Problems, p => p.Contains("32 colorants", StringComparison.Ordinal));
    }

    [Fact]
    public void Attachments_nested_in_attachments_stop_at_a_depth()
    {
        // Each level attaches the PDF before it; the conversion converts a few levels and removes what is deeper.
        var pdf = HtmlToPdf.Convert("<p>0</p>");
        for (int level = 1; level <= 8; level++)
        {
            var options = new PdfOptions();
            options.Attachments.Add(new PdfAttachment("level" + (level - 1) + ".pdf", pdf));
            pdf = HtmlToPdf.Convert("<p>" + level + "</p>", options);
        }
        var result = Bounded(pdf);
        Assert.True(result.IsConformant, string.Join("\n", result.Problems));
        int depth = 0;
        var current = result.Pdf;
        while (PdfAttachments.List(current) is { Count: 1 } list)
        {
            current = list[0].Data;
            depth++;
        }
        Assert.Equal(2, depth);
    }

    [Fact]
    public void Damaged_streams_and_objects_are_reported_not_thrown()
    {
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a) << /Type /Filespec /F (a) /EF << /F 6 0 R >> >>] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            Page("<< /Font << /F 4 0 R >> /XObject << /I 5 0 R >> >>", 7),
            "<< /Type /Font /Subtype /TrueType /BaseFont /Arial /FontDescriptor << /Type /FontDescriptor /FontName /Arial /Flags 32 /FontFile2 6 0 R >> >>",
            "<< /Type /XObject /Subtype /Image /Width 100000 /Height 100000 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length 4 >>\nstream\nxxxx\nendstream",
            "<< /Filter /FlateDecode /Length 10 >>\nstream\nnot zlib!!\nendstream",
            Stream("BT /F 12 Tf (abc) Tj ET /I Do"));
        foreach (var conformance in new[] { PdfConformance.PdfA2b, PdfConformance.PdfA3u })
            Assert.NotEmpty(Bounded(pdf, conformance).Pdf);
    }

    [Fact]
    public void An_information_entry_with_an_empty_name_is_kept_out_of_the_xmp()
    {
        // Found by the pdf-pdfa fuzz target (exception-aee97909): a custom property named by the empty name "/" made the
        // XMP writer read the first character of an empty string.
        var result = Bounded(Encoding.Latin1.GetBytes("\n1 0 obj<<4 0 obj<<//trailer<</Root 1 0 R/Info 4 0 R"));
        Assert.NotEmpty(result.Pdf);
    }

    [Fact]
    public void A_font_table_placed_past_the_end_of_the_program_is_cut_off()
    {
        // Found by the pdf-pdfa fuzz target (exception-46bbf5a8): rewriting an embedded TrueType font copies its other
        // tables, and one whose directory gives it a length of almost 4 GB, negative as a 32-bit integer, was sliced
        // out of range.
        var font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "Inter-Features.ttf"));
        int tables = font[4] << 8 | font[5];
        for (int i = 0; i < tables; i++)
        {
            int record = 12 + 16 * i;
            if (Encoding.ASCII.GetString(font, record, 4) is not ("name" or "post")) continue;
            font[record + 12] = 0xF0;
        }
        var pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            Page("<< /Font << /F 4 0 R >> >>", 7),
            // Widths that disagree with the program, so the conversion rewrites it.
            "<< /Type /Font /Subtype /TrueType /BaseFont /Inter /FirstChar 65 /LastChar 67 /Widths [100 100 100] /FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /Inter /Flags 32 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 6 0 R >>",
            $"<< /Length {font.Length} >>\nstream\n{Encoding.Latin1.GetString(font)}\nendstream",
            Stream("BT /F 12 Tf 10 10 Td (ABC) Tj ET"));
        Assert.NotEmpty(Bounded(pdf).Pdf);
    }

    [Fact]
    public void A_catalog_lost_when_the_cross_reference_table_is_rebuilt_is_invalid_data()
    {
        // Found by the pdf-pdfa fuzz target (exception-8c168e97), as it found it: the cross-reference table is damaged,
        // so reading every object rebuilds it, and the rebuilt table has no catalog where the first one had one.
        const string pdf = "JVBERi0xLjcaMSAwIG9iago8PGAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1R5cGUgL1BhZ2VzIC9L"
            + "aWRzIFszIDAgUl0gL0NvdW50IDEgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL1BhZ2UgL1BhcmVsdCAyIDAgUiAvTWVkaWFCb3ggWzAgMCAx"
            + "MDAAMTAwXSAvQ29udGVudHMgNCAwIFIgL1Jlc291cmNlcyA8PCAvWE9iamVjdCA8PCAvSWwgNSAwIFIgPj4gPj4gPj4KZW5kb2JqCjQgMCBvQmoK"
            + "PDwgL0xlbmd0aCAyOSA+PgpzdHJlYW0KcSAxMDAgMCAwIDEwMCAwIDAgY20gL0ltIERvIFEKZW5kc3RyZWFtCmVuZG9iago1IDAgb2JqCjw8IC9U"
            + "eXBlIC9YT2JqZWN0IC9TdWJ0eXBlIC9JbWFnZSAvV2lkdGggMTYwIC9IZWlnaHQgMTAwIC9Db2xvclNwYWNlIC9EZXZpY2VHcmF5IC9CaXRzUGVy"
            + "Q29tcG9uZW50IDEgL0ZpbHRlciAvSkJJRzJEZWNvZGUgIC9MZW5ndGggMTE2ID4+CnN0cmVhbQoAAAACMAABAAAAEwAAAKAAAABkAAAAAAAAAAAA"
            + "AAAAAEADBiABAQAAAD8AAACgAAAAZAAAAAAAAAAAAAAS/////wAAgAmeWTf6gK5a7NilJkNoFKPWPG3GyWWRzG8iCA21GASm0qMf/6wAAAAEMQAB"
            + "AAAAAAplbmRzdHJlYW0KZW5kb2JqCnhyZWYKMCA2CjAwMDAgMzUgZiAKOSAwMCBuIAowIDAwIG4hCjAwMTUgMDAgbiAKNCAwNCBuIAowIDAwCnRy"
            + "YWlsZXIKPDwgNiAvUm9vdCAxIDAgUiA+PgpzdGFydHhyZWYKNjEw";
        try
        {
            Assert.NotEmpty(Bounded(Convert.FromBase64String(pdf)).Pdf);
        }
        catch (AggregateException ex) when (ex.InnerException is InvalidDataException)
        {
        }
    }

    [Fact]
    public void Truncated_pdfs_fail_with_documented_exceptions()
    {
        var whole = Encoding.Latin1.GetString(PdfToPdfATests_Plain());
        for (int length = 0; length < whole.Length; length += 37)
        {
            var part = Encoding.Latin1.GetBytes(whole[..length]);
            try
            {
                Bounded(part);
            }
            catch (AggregateException ex) when (ex.InnerException is InvalidDataException or ArgumentException { ParamName: "pdf" })
            {
            }
        }
    }

    private static byte[] PdfToPdfATests_Plain() => BuildPdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        Page("<< /Font << /F1 4 0 R >> >>", 5),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        Stream("BT /F1 14 Tf 20 60 Td (Hello) Tj ET"));
}
