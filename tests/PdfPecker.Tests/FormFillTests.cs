using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>Reading, filling and flattening the form fields of existing PDFs.</summary>
public class FormFillTests
{
    private const string Form = """
        <!DOCTYPE html><html><body style="font:12px Helvetica">
        <p>Name <input type="text" name="name" style="width:200px" title="Full name" maxlength="40" required></p>
        <p><input type="checkbox" name="news"> News <input type="checkbox" name="terms" value="agree"> Terms</p>
        <p><input type="radio" name="size" value="S"> S <input type="radio" name="size" value="M" checked> M <input type="radio" name="size" value="L"> L</p>
        <p><select name="country"><option value="dk">Danmark</option><option value="se" selected>Sverige</option></select></p>
        <p><select name="colors" multiple size="3"><option>Red</option><option>Green</option><option>Blue</option></select></p>
        <p><textarea name="notes" rows="3" style="width:300px"></textarea></p>
        <p><input type="text" name="locked" value="fixed" readonly></p>
        <p><a href="https://example.com/">A link that stays</a></p>
        </body></html>
        """;

    private static byte[] FormPdf(PdfOptions? options = null) => HtmlToPdf.Convert(Form, options);

    private static PdfFormField Field(byte[] pdf, string name, string? password = null)
        => Assert.Single(PdfForm.GetFields(pdf, password), f => f.Name == name);

    /// <summary>The dark pixels of the first page rendered at 72 dpi, without annotations unless asked.</summary>
    private static int Ink(byte[] pdf, bool annotations = true, string? password = null)
    {
        var img = PdfRenderer.Open(pdf, password).RenderPage(0, new ImageOptions { Dpi = 72, RenderAnnotations = annotations });
        var rgba = img.Rgba;
        int dark = 0;
        for (int i = 0; i < rgba.Length; i += 4)
            if (rgba[i] + rgba[i + 1] + rgba[i + 2] < 300) dark++;
        return dark;
    }

    [Fact]
    public void Lists_the_fields_with_their_types_values_and_choices()
    {
        var fields = PdfForm.GetFields(FormPdf());
        Assert.Equal(new[] { "name", "news", "terms", "size", "country", "colors", "notes", "locked" }, fields.Select(f => f.Name));

        var name = fields[0];
        Assert.Equal(PdfFormFieldType.Text, name.Type);
        Assert.Null(name.Value);
        Assert.True(name.Required);
        Assert.Equal(40, name.MaxLength);
        Assert.Equal("Full name", name.ToolTip);
        Assert.Equal(1, name.Page);

        Assert.Equal(PdfFormFieldType.CheckBox, fields[1].Type);
        Assert.Equal("Off", fields[1].Value);
        Assert.Equal("agree", Assert.Single(fields[2].Options).Value);

        var size = fields[3];
        Assert.Equal(PdfFormFieldType.RadioButton, size.Type);
        Assert.Equal("M", size.Value);
        Assert.Equal(new[] { "S", "M", "L" }, size.Options.Select(o => o.Value));

        var country = fields[4];
        Assert.Equal(PdfFormFieldType.ComboBox, country.Type);
        Assert.Equal("se", country.Value);
        Assert.Equal(new[] { "dk", "se" }, country.Options.Select(o => o.Value));
        Assert.Equal("Danmark", country.Options[0].Text);

        Assert.Equal(PdfFormFieldType.ListBox, fields[5].Type);
        Assert.True(fields[5].MultiSelect);
        Assert.True(fields[6].Multiline);
        Assert.True(fields[7].ReadOnly);
        Assert.Equal("fixed", fields[7].Value);

        Assert.Empty(PdfForm.GetFields(HtmlToPdf.Convert("<p>No form</p>")));
    }

    [Fact]
    public void Fills_every_kind_of_field_and_reads_the_values_back()
    {
        var pdf = FormPdf();
        int before = Ink(pdf);
        var filled = PdfForm.Fill(pdf, new Dictionary<string, string>
        {
            ["name"] = "Jane Q. Public",
            ["news"] = "true",
            ["terms"] = "agree",
            ["size"] = "L",
            ["country"] = "Danmark", // a choice's text selects it too
            ["colors"] = "Red\nBlue",
            ["notes"] = "First line of the notes, long enough that it has to wrap onto another line in the box.\nSecond paragraph.",
        });
        PdfTestHelpers.AssertStructurallyValid(filled);

        Assert.Equal("Jane Q. Public", Field(filled, "name").Value);
        Assert.Equal("Yes", Field(filled, "news").Value);
        Assert.Equal("agree", Field(filled, "terms").Value);
        Assert.Equal("L", Field(filled, "size").Value);
        Assert.Equal("dk", Field(filled, "country").Value);
        Assert.Equal(new[] { "Red", "Blue" }, Field(filled, "colors").Values);
        Assert.StartsWith("First line", Field(filled, "notes").Value);

        // The values are drawn: much more ink than the empty form, and viewers are not asked to redraw.
        Assert.True(Ink(filled) > before + 400, $"{Ink(filled)} vs {before}");
        var text = Encoding.Latin1.GetString(filled);
        Assert.DoesNotContain("/NeedAppearances true", text);
        // The radio buttons switch over: only the chosen widget is on.
        var file = PdfFile.Open(filled, null);
        var states = AllWidgets(file).Where(w => file.GetDict(w, "Parent") is { } p && (file.Resolve(p["T"]) as PdfString)?.Text == "size")
            .Select(w => file.GetName(w, "AS")).ToList();
        Assert.Equal(new[] { "Off", "Off", "L" }, states);

        // Clearing works too.
        var cleared = PdfForm.Fill(filled, new Dictionary<string, string> { ["news"] = "Off", ["size"] = "Off", ["name"] = "" });
        Assert.Equal("Off", Field(cleared, "news").Value);
        Assert.Equal("Off", Field(cleared, "size").Value);
        Assert.Equal("", Field(cleared, "name").Value);
    }

    private static List<PdfDict> AllWidgets(PdfFile file)
    {
        var result = new List<PdfDict>();
        var renderer = new Rendering.RenderDocument(file.Data, null);
        foreach (var page in renderer.Pages)
            if (file.GetArray(page.Dict, "Annots") is { } annots)
                foreach (var a in annots.Items)
                    if (file.Resolve(a) is PdfDict d && file.GetName(d, "Subtype") == "Widget") result.Add(d);
        return result;
    }

    [Fact]
    public void Flattening_draws_the_fields_into_the_page_and_removes_them()
    {
        var filled = PdfForm.Fill(FormPdf(), new Dictionary<string, string> { ["name"] = "Jane Q. Public", ["news"] = "Yes" },
            new PdfFormFillOptions { Flatten = true });
        Assert.Empty(PdfForm.GetFields(filled));
        var text = Encoding.Latin1.GetString(filled);
        Assert.DoesNotContain("/AcroForm", text);
        Assert.DoesNotContain("/Widget", text);
        Assert.Contains("/Link", text); // other annotations stay
        // The page alone, without annotations, now shows what the fields showed.
        var withFields = PdfForm.Fill(FormPdf(), new Dictionary<string, string> { ["name"] = "Jane Q. Public", ["news"] = "Yes" });
        Assert.InRange(Ink(filled, annotations: false), Ink(withFields) - 20, Ink(withFields) + 20);
        Assert.True(Ink(filled, annotations: false) > Ink(withFields, annotations: false) + 200);

        // Flatten alone keeps the values a form already has.
        var flat = PdfForm.Flatten(withFields);
        Assert.Empty(PdfForm.GetFields(flat));
        Assert.InRange(Ink(flat, annotations: false), Ink(withFields) - 20, Ink(withFields) + 20);
    }

    [Fact]
    public void Values_beyond_the_fields_font_embed_a_fallback_font()
    {
        var pdf = FormPdf();
        const string value = "Привет, Ελλάδα — Łódź";
        var filled = PdfForm.Fill(pdf, new Dictionary<string, string> { ["name"] = value });
        Assert.Equal(value, Field(filled, "name").Value);
        var text = Encoding.Latin1.GetString(filled);
        Assert.Contains("LiberationSans", text);
        Assert.Contains("/PdfPeckerFill", text);
        Assert.True(Ink(filled) > Ink(pdf) + 100);
        // Latin text that the field's WinAnsi Helvetica has does not embed anything.
        var latin = Encoding.Latin1.GetString(PdfForm.Fill(pdf, new Dictionary<string, string> { ["name"] = "Søren Æbelø" }));
        Assert.DoesNotContain("/PdfPeckerFill", latin);
    }

    [Fact]
    public void A_font_of_ones_own_for_the_values()
    {
        var font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "WorkSans-Regular.ttf"));
        var filled = PdfForm.Fill(FormPdf(), new Dictionary<string, string> { ["name"] = "Ŵork Šans ŧext" }, new PdfFormFillOptions { Font = font });
        Assert.Contains("WorkSans", Encoding.Latin1.GetString(filled));
        Assert.Throws<ArgumentException>(() => PdfForm.Fill(FormPdf(), new Dictionary<string, string> { ["name"] = "Ŵ" }, new PdfFormFillOptions { Font = new byte[] { 1, 2, 3 } }));
    }

    [Fact]
    public void Encrypted_pdfs_stay_encrypted()
    {
        var pdf = FormPdf(new PdfOptions { Security = new PdfSecurity { UserPassword = "open", OwnerPassword = "admin" } });
        Assert.Throws<PdfPasswordException>(() => PdfForm.GetFields(pdf));
        Assert.Throws<PdfPasswordException>(() => PdfForm.Fill(pdf, new Dictionary<string, string> { ["name"] = "x" }));
        var filled = PdfForm.Fill(pdf, new Dictionary<string, string> { ["name"] = "Secret Agent" }, new PdfFormFillOptions { Password = "open" });
        Assert.True(PdfProtector.IsEncrypted(filled));
        Assert.Throws<PdfPasswordException>(() => PdfForm.GetFields(filled));
        Assert.Equal("Secret Agent", Field(filled, "name", "admin").Value);
        // An incremental update: the original bytes come first, the values are not readable in the file.
        Assert.True(filled.AsSpan(0, pdf.Length).SequenceEqual(pdf));
        Assert.DoesNotContain("Secret Agent", Encoding.Latin1.GetString(filled));
        Assert.True(Ink(filled, password: "open") > Ink(pdf, password: "open"));

        var flat = PdfForm.Flatten(filled, "open");
        Assert.True(PdfProtector.IsEncrypted(flat));
        Assert.Empty(PdfForm.GetFields(flat, "open"));
    }

    [Fact]
    public void Signed_pdfs_are_filled_with_an_incremental_update()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Form Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var signed = PdfSigner.Sign(FormPdf(), new PdfSignatureOptions(cert));
        var filled = PdfForm.Fill(signed, new Dictionary<string, string> { ["name"] = "After signing" });
        Assert.True(filled.AsSpan(0, signed.Length).SequenceEqual(signed));
        Assert.Equal("After signing", Field(filled, "name").Value);
        Assert.Contains(PdfForm.GetFields(filled), f => f.Type == PdfFormFieldType.Signature);
        Assert.Throws<InvalidOperationException>(() => PdfForm.Flatten(signed));
    }

    private static byte[] BuildPdf(params string[] objects)
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
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A form as other tools write it: hierarchical names (address.city, address.zip), a comb field, a centred field with
    /// automatic size, a check box without appearances, and NeedAppearances set; no field has an appearance.
    /// </summary>
    private static byte[] HandWritten() => BuildPdf(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 8 0 R 9 0 R] /NeedAppearances true /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Annots [6 0 R 7 0 R 8 0 R 9 0 R] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /T (address) /FT /Tx /Kids [6 0 R 7 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /T (city) /Parent 5 0 R /Rect [20 240 220 270] /Q 1 /MK << /BC [0 0 1] /BG [0.9 0.9 1] >> /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /T (zip) /Parent 5 0 R /Rect [20 190 120 220] /Ff 16777216 /MaxLen 5 /DA (/Helv 12 Tf 1 0 0 rg) /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /T (ok) /FT /Btn /Rect [20 140 40 160] /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /T (note) /FT /Tx /V (Existing value) /Rect [20 80 380 120] /P 3 0 R >>");

    [Fact]
    public void Hierarchical_names_comb_fields_and_forms_without_appearances()
    {
        var pdf = HandWritten();
        var fields = PdfForm.GetFields(pdf);
        Assert.Equal(new[] { "address.city", "address.zip", "ok", "note" }, fields.Select(f => f.Name));
        Assert.Equal(5, fields[1].MaxLength);
        Assert.Empty(fields[2].Options); // no appearance states yet

        var filled = PdfForm.Fill(pdf, new Dictionary<string, string> { ["address.city"] = "Aarhus", ["address.zip"] = "8000", ["ok"] = "yes" });
        Assert.Equal("Aarhus", Field(filled, "address.city").Value);
        Assert.Equal("8000", Field(filled, "address.zip").Value);
        Assert.Equal("Yes", Field(filled, "ok").Value);
        Assert.Equal("Existing value", Field(filled, "note").Value);

        var file = PdfFile.Open(filled, null);
        Assert.Null(file.GetDict(file.Catalog, "AcroForm")!["NeedAppearances"]);
        string Appearance(string name)
        {
            var widget = AllWidgets(file).Single(w => (file.Resolve(w["T"]) as PdfString)?.Text == name);
            return Encoding.Latin1.GetString(file.DecodeStream(file.GetStream(file.GetDict(widget, "AP"), "N")!));
        }
        // The comb field draws each digit in its own cell, in the field's colour and size.
        var zip = Appearance("zip");
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(zip, " Tj").Count);
        Assert.Contains("/Helv 12 Tf 1 0 0 rg", zip);
        // The automatic size fills the box's height, the field's colours are drawn, and the text is centred.
        var city = Appearance("city");
        Assert.Matches(@"/Helv (1[5-9]|2\d)(\.\d+)? Tf", city);
        Assert.Contains("0.9 0.9 1 rg", city);
        Assert.Contains("0 0 1 RG", city);
        // A field that was not filled still gets the appearance NeedAppearances asked for.
        Assert.Contains("Tj", Appearance("note"));
        Assert.True(Ink(filled) > 300);

        var flat = PdfForm.Flatten(filled);
        Assert.Empty(PdfForm.GetFields(flat));
        Assert.InRange(Ink(flat, annotations: false), Ink(filled) - 20, Ink(filled) + 20);

        // Filling again keeps the box and background of the old appearance and replaces only its text.
        var again = PdfForm.Fill(filled, new Dictionary<string, string> { ["address.city"] = "Odense" });
        var file2 = PdfFile.Open(again, null);
        var widget2 = AllWidgets(file2).Single(w => (file2.Resolve(w["T"]) as PdfString)?.Text == "city");
        var city2 = Encoding.Latin1.GetString(file2.DecodeStream(file2.GetStream(file2.GetDict(widget2, "AP"), "N")!));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(city2, "0.9 0.9 1 rg"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(city2, " Tj"));
    }

    [Fact]
    public void Bad_values_are_refused()
    {
        var pdf = FormPdf();
        Assert.Throws<ArgumentException>(() => PdfForm.Fill(pdf, new Dictionary<string, string> { ["nope"] = "x" }));
        Assert.Throws<ArgumentException>(() => PdfForm.Fill(pdf, new Dictionary<string, string> { ["size"] = "XL" }));
        Assert.Throws<ArgumentException>(() => PdfForm.Fill(pdf, new Dictionary<string, string> { ["country"] = "no" }));
        Assert.Throws<ArgumentException>(() => PdfForm.Fill(pdf, new Dictionary<string, string> { ["name"] = new string('x', 41) }));
        Assert.Throws<ArgumentException>(() => PdfForm.Fill(pdf, new Dictionary<string, string> { ["news"] = "maybe" }));
        var ignored = PdfForm.Fill(pdf, new Dictionary<string, string> { ["nope"] = "x", ["name"] = "Ok" }, new PdfFormFillOptions { IgnoreUnknownFields = true });
        Assert.Equal("Ok", Field(ignored, "name").Value);
        Assert.Throws<InvalidDataException>(() => PdfForm.GetFields(Encoding.ASCII.GetBytes("not a pdf")));
    }

    [Fact]
    public void Stream_and_file_overloads()
    {
        var pdf = FormPdf();
        var output = new MemoryStream();
        PdfForm.Fill(new MemoryStream(pdf), output, new Dictionary<string, string> { ["name"] = "Streamed" });
        Assert.Equal("Streamed", Field(output.ToArray(), "name").Value);
        Assert.Equal(8, PdfForm.GetFields(new MemoryStream(pdf)).Count);

        var path = Path.Combine(Path.GetTempPath(), "pdfpecker-form-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            PdfForm.FillFile(path, path, new Dictionary<string, string> { ["name"] = "In place" });
            Assert.Equal("In place", Field(File.ReadAllBytes(path), "name").Value);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
