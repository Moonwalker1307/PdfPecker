using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

public class FormFieldTests
{
    private const string Form = """
        <!DOCTYPE html><html><body>
        <p>Name <input type="text" name="name" value="Jane Doe" title="Full name" maxlength="40"></p>
        <p>Email <input type="email" name="email" placeholder="you@example.com" required></p>
        <p>Password <input type="password" name="pw" value="secret"></p>
        <p><input type="checkbox" name="news" checked> News <input type="checkbox" name="terms" value="agree"> Terms</p>
        <p><input type="radio" name="size" value="S"> S <input type="radio" name="size" value="M" checked> M <input type="radio" name="size" value="L"> L</p>
        <p><select name="country"><option value="dk">Danmark</option><option value="se" selected>Sverige</option></select></p>
        <p><select name="colors" multiple><option selected>Red</option><option>Green</option><option selected>Blue</option></select></p>
        <p><textarea name="notes" rows="3">
        Line one
        Line two</textarea></p>
        <p><input type="text" name="locked" value="x" readonly> <input type="text" name="off" value="y" disabled> <button>Send</button></p>
        <input type="hidden" name="secret" value="z">
        </body></html>
        """;

    private static (PdfFile File, Dictionary<string, PdfDict> Fields) Read(byte[] pdf, string? password = null)
    {
        var file = PdfFile.Open(pdf, password);
        var acro = file.GetDict(file.Catalog, "AcroForm");
        var fields = new Dictionary<string, PdfDict>();
        if (acro == null) return (file, fields);
        foreach (var f in file.GetArray(acro, "Fields")!.Items)
        {
            var d = (PdfDict)file.Resolve(f)!;
            fields[((PdfString)file.Resolve(d["T"])!).Text] = d;
        }
        return (file, fields);
    }

    private static string? Text(PdfFile file, PdfDict d, string key) => (file.Resolve(d[key]) as PdfString)?.Text;

    [Fact]
    public void ControlsBecomeFieldsWithTheirValues()
    {
        var pdf = HtmlToPdf.Convert(Form);
        PdfTestHelpers.AssertStructurallyValid(pdf);
        var (file, fields) = Read(pdf);
        Assert.Equal(new[] { "name", "email", "pw", "news", "terms", "size", "country", "colors", "notes", "locked" }, fields.Keys);

        var name = fields["name"];
        Assert.Equal("Tx", file.GetName(name, "FT"));
        Assert.Equal("Jane Doe", Text(file, name, "V"));
        Assert.Equal("Full name", Text(file, name, "TU"));
        Assert.Equal(40, file.GetInt(name, "MaxLen", 0));
        Assert.StartsWith("/Helv 10 Tf", Text(file, name, "DA"));
        Assert.Equal("Widget", file.GetName(name, "Subtype"));
        Assert.NotNull(file.GetStream(file.GetDict(name, "AP"), "N"));

        Assert.Equal(2, file.GetInt(fields["email"], "Ff", 0)); // required
        Assert.Null(fields["email"]["V"]);                       // the placeholder is only drawn
        Assert.Equal(1 << 13 | 1 << 22, file.GetInt(fields["pw"], "Ff", 0));
        Assert.Null(fields["pw"]["V"]);                          // a password is not stored

        Assert.Equal("Yes", file.GetName(fields["news"], "V"));
        Assert.Equal("Yes", file.GetName(fields["news"], "AS"));
        Assert.Equal("Off", file.GetName(fields["terms"], "V"));
        Assert.NotNull(file.GetDict(file.GetDict(fields["terms"], "AP"), "N")!["agree"]);

        var size = fields["size"];
        Assert.Equal(1 << 15 | 1 << 14, file.GetInt(size, "Ff", 0));
        Assert.Equal("M", file.GetName(size, "V"));
        var kids = file.GetArray(size, "Kids")!.Items.Select(k => (PdfDict)file.Resolve(k)!).ToList();
        Assert.Equal(new[] { "Off", "M", "Off" }, kids.Select(k => file.GetName(k, "AS")));
        Assert.Equal(new[] { "S", "M", "L" }, kids.Select(k => file.GetDict(file.GetDict(k, "AP"), "N")!.Entries.Keys.First(n => n != "Off")));

        var country = fields["country"];
        Assert.Equal("Ch", file.GetName(country, "FT"));
        Assert.Equal(1 << 17, file.GetInt(country, "Ff", 0));
        Assert.Equal("se", Text(file, country, "V"));
        Assert.Equal(2, file.GetArray(country, "Opt")!.Items.Count);

        var colors = fields["colors"];
        Assert.Equal(1 << 21, file.GetInt(colors, "Ff", 0));
        Assert.Equal(new double[] { 0, 2 }, file.GetNumbers(colors, "I"));

        var notes = fields["notes"];
        Assert.Equal(1 << 12, file.GetInt(notes, "Ff", 0));
        Assert.Equal("Line one\nLine two", Text(file, notes, "V"));
        Assert.Equal(1, file.GetInt(fields["locked"], "Ff", 0));

        // Every widget is on the page, printable.
        var page = (PdfDict)file.Resolve(file.GetArray(file.GetDict(file.Catalog, "Pages"), "Kids")!.Items[0])!;
        Assert.Equal(12, file.GetArray(page, "Annots")!.Items.Count);
        Assert.Equal(4, file.GetInt(name, "F", 0));
        Assert.Null(file.GetDict(file.Catalog, "AcroForm")!["NeedAppearances"]);
    }

    [Fact]
    public void ValuesAreDrawnInTheAppearance()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed(Form);
        var text = PdfTestHelpers.Latin1(pdf);
        var content = PdfTestHelpers.AllContent(pdf); // the pages only
        Assert.Contains("/Tx BMC", text);
        Assert.Contains("(Jane Doe)", text);
        Assert.DoesNotContain("(Jane Doe)", content);
        // The disabled field and the button are part of the page.
        Assert.Contains("(y)", content);
        Assert.Contains("(Send)", content);
    }

    [Fact]
    public void DuplicateNamesAreMadeUnique()
    {
        var pdf = HtmlToPdf.Convert("""
            <input name="a"><input name="a"><input name="b.c"><input><input>
            <input type="checkbox" name="tag" value="x"><input type="checkbox" name="tag" value="y">
            <input type="radio" name="r"><input type="radio" name="r"><input type="radio">
            """);
        var (_, fields) = Read(pdf);
        Assert.Equal(new[] { "a", "a_2", "b_c", "text1", "text2", "tag", "tag_2", "r", "radio1" }, fields.Keys);
    }

    [Fact]
    public void FormFieldsOffDrawsTheControls()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed(Form, new PdfOptions { FormFields = false });
        Assert.DoesNotContain("/AcroForm", PdfTestHelpers.Latin1(pdf));
        Assert.DoesNotContain("/Widget", PdfTestHelpers.Latin1(pdf));
        Assert.Contains("(Jane Doe)", PdfTestHelpers.AllContent(pdf));
    }

    [Fact]
    public void DocumentsWithoutControlsHaveNoForm()
    {
        var pdf = HtmlToPdf.Convert("<p>No form here</p>");
        Assert.DoesNotContain("/AcroForm", PdfTestHelpers.Latin1(pdf));
    }

    [Fact]
    public void CheckedBoxIsDrawnBlue()
    {
        var html = "<!DOCTYPE html><body style='margin:0'><input type=checkbox checked style='width:40px;height:40px;margin:0'>";
        foreach (bool fields in new[] { true, false })
        {
            var pdf = HtmlToPdf.Convert(html, new PdfOptions { FormFields = fields, Margins = new PageMargins(0) });
            var (r, g, b) = Pixel(pdf, 15, 8);
            Assert.True(b > 200 && r < 60, $"fields={fields}: expected blue, got {r},{g},{b}");
        }
    }

    [Fact]
    public void EncryptedFormKeepsItsValues()
    {
        var pdf = HtmlToPdf.Convert(Form, new PdfOptions { Security = new PdfSecurity { UserPassword = "open", OwnerPassword = "owner" } });
        var (file, fields) = Read(pdf, "open");
        Assert.Equal("Jane Doe", Text(file, fields["name"], "V"));
        Assert.Equal("se", Text(file, fields["country"], "V"));
        Assert.Equal(1, PdfToImage.GetPageCount(pdf, "open"));
    }

    [Fact]
    public void PdfAFormHasAppearancesAndNoNeedAppearances()
    {
        var pdf = HtmlToPdf.Convert(Form, new PdfOptions { Conformance = PdfConformance.PdfA2b });
        var (file, fields) = Read(pdf);
        Assert.Equal(10, fields.Count);
        foreach (var f in fields.Values)
            foreach (var widget in file.GetArray(f, "Kids")?.Items.Select(k => (PdfDict)file.Resolve(k)!) ?? new[] { f })
            {
                Assert.Equal(4, file.GetInt(widget, "F", 0));
                Assert.NotNull(file.GetDict(widget, "AP"));
            }
    }

    [Fact]
    public void SignedFormKeepsItsFields()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Form Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var signer = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "pw"), "pw");
        var pdf = HtmlToPdf.Convert(Form, new PdfOptions { Signature = new PdfSignatureOptions(signer) });
        var (file, fields) = Read(pdf);
        Assert.Equal(11, fields.Count);
        Assert.Equal("Sig", file.GetName(fields["Signature1"], "FT"));
        Assert.Equal("Jane Doe", Text(file, fields["name"], "V"));
    }

    [Fact]
    public void MergedFormsKeepBothSetsOfFields()
    {
        var a = HtmlToPdf.Convert(Form);
        var merged = PdfMerger.Merge(a, a);
        var (_, fields) = Read(merged);
        Assert.Contains("name", fields.Keys);
        Assert.Contains("name_2", fields.Keys);
        Assert.Contains("size_2", fields.Keys);
        Assert.Equal(20, fields.Count);
    }

    [Fact]
    public void ControlsAlignWithTheTextBesideThem()
    {
        // The value's baseline is the line's baseline: the label and the value share a baseline.
        var pdf = PdfTestHelpers.ConvertUncompressed("<!DOCTYPE html><p style='font:16px Arial'>Label <input value='Value' style='height:40px'></p>",
            new PdfOptions { FormFields = false });
        var content = PdfTestHelpers.AllContent(pdf);
        var ys = System.Text.RegularExpressions.Regex.Matches(content, @"1 0 0 1 [\d.]+ ([\d.]+) Tm").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Single(ys);
    }

    [Fact]
    public void LabelsDescribeFields()
    {
        var pdf = HtmlToPdf.Convert("""
            <label>First name <input name="first"></label>
            <label for="last">Last name</label> <input id="last" name="last">
            <input name="plain">
            """);
        var (file, fields) = Read(pdf);
        Assert.Equal("First name", Text(file, fields["first"], "TU"));
        Assert.Equal("Last name", Text(file, fields["last"], "TU"));
        Assert.Null(fields["plain"]["TU"]);
    }

    [Fact]
    public void TaggedFormPutsWidgetsInFormElements()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed("<html lang='en'><head><title>Form</title></head><body>" + Form.Split("<body>")[1],
            new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 });
        var (file, fields) = Read(pdf);
        var widgets = fields.Values.SelectMany(f => file.GetArray(f, "Kids")?.Items.Select(k => (PdfDict)file.Resolve(k)!) ?? new[] { f }).ToList();
        Assert.Equal(12, widgets.Count);
        Assert.All(widgets, w => Assert.True(file.GetInt(w, "StructParent", -1) >= 0));
        var text = PdfTestHelpers.Latin1(pdf);
        Assert.Equal(12, System.Text.RegularExpressions.Regex.Matches(text, "/S /Form ").Count);
        Assert.Contains("/Type /OBJR", text);
    }

    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }
}
