using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>Signing into the empty signature fields a template already has.</summary>
public class SignatureFieldTests
{
    private static readonly Lazy<X509Certificate2> Cert = new(() =>
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Field Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "pw"), "pw");
    });

    /// <summary>
    /// A one-page form with an empty signature field "Approver" (field and widget in one), a text field "Name", and a
    /// signature field "Parties.Buyer" whose widget is a separate kid.
    /// </summary>
    internal static byte[] Template()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [4 0 R 5 0 R 8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Approver) /Rect [100 100 300 160] /P 3 0 R /F 4 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [100 200 300 220] /P 3 0 R >>",
            "<< /T (Parties) /Kids [7 0 R] >>",
            "<< /T (Buyer) /FT /Sig /Parent 6 0 R /Kids [8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 7 0 R /Rect [320 100 520 150] /P 3 0 R >>",
        };
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

    private static void AssertSignatureValid(byte[] pdf)
    {
        var m = Regex.Matches(Encoding.Latin1.GetString(pdf), @"/ByteRange \[\s*(\d+) (\d+) (\d+) (\d+)\s*\] /Contents <([0-9A-F]+)>").Single();
        var r = Enumerable.Range(1, 4).Select(i => int.Parse(m.Groups[i].Value)).ToArray();
        var signed = pdf.AsSpan(0, r[1]).ToArray().Concat(pdf.AsSpan(r[2], r[3]).ToArray()).ToArray();
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        cms.Decode(Convert.FromHexString(m.Groups[5].Value));
        cms.CheckSignature(verifySignatureOnly: true);
    }

    private static PdfArray Fields(PdfFile file) => file.GetArray(file.GetDict(file.Catalog, "AcroForm"), "Fields")!;

    [Fact]
    public void Signs_into_an_existing_empty_field()
    {
        var signed = PdfSigner.Sign(Template(), new PdfSignatureOptions(Cert.Value) { FieldName = "Approver" });
        AssertSignatureValid(signed);
        var file = PdfFile.Open(signed, null);
        Assert.Equal(3, Fields(file).Count); // no second "Approver"
        Assert.Equal(3, file.GetArray((PdfDict)file.GetObject(3)!, "Annots")!.Count);
        var field = (PdfDict)file.GetObject(4)!;
        Assert.IsType<PdfDict>(file.Resolve(field["V"]));
        Assert.NotNull(file.GetDict(field, "AP"));
        Assert.Equal(new double[] { 100, 100, 300, 160 }, file.GetNumbers(field, "Rect"));
    }

    [Fact]
    public void Signs_a_nested_field_with_a_separate_widget()
    {
        var signed = PdfSigner.Sign(Template(), new PdfSignatureOptions(Cert.Value) { FieldName = "Parties.Buyer" });
        AssertSignatureValid(signed);
        var file = PdfFile.Open(signed, null);
        Assert.IsType<PdfDict>(file.Resolve(((PdfDict)file.GetObject(7)!)["V"]));
        Assert.NotNull(file.GetDict((PdfDict)file.GetObject(8)!, "AP"));
        Assert.Equal(3, Fields(file).Count);
    }

    [Fact]
    public void Refuses_a_field_that_is_not_a_signature_field()
    {
        var ex = Assert.Throws<PdfSignatureException>(() => PdfSigner.Sign(Template(), new PdfSignatureOptions(Cert.Value) { FieldName = "Name" }));
        Assert.Contains("not a signature field", ex.Message);
    }

    [Fact]
    public void Refuses_a_field_that_is_already_signed()
    {
        var once = PdfSigner.Sign(Template(), new PdfSignatureOptions(Cert.Value) { FieldName = "Approver" });
        var ex = Assert.Throws<PdfSignatureException>(() => PdfSigner.Sign(once, new PdfSignatureOptions(Cert.Value) { FieldName = "Approver" }));
        Assert.Contains("already signed", ex.Message);
    }

    [Fact]
    public void A_new_name_still_adds_a_field()
    {
        var signed = PdfSigner.Sign(Template(), new PdfSignatureOptions(Cert.Value) { FieldName = "Witness" });
        AssertSignatureValid(signed);
        var file = PdfFile.Open(signed, null);
        Assert.Equal(4, Fields(file).Count);
        Assert.Equal(4, file.GetArray((PdfDict)file.GetObject(3)!, "Annots")!.Count);
    }

    private const string Agreement = """
        <!DOCTYPE html><html lang="en"><head><title>Agreement</title><style>
          @page { size: 595pt 842pt; margin: 0 }
          body { margin: 0 }
          .sign { position: absolute; top: 100pt; width: 200pt; height: 60pt; border-bottom: 1px solid #666 }
        </style></head><body>
        <p>Signed by both sides.</p>
        <div class="sign" style="left: 50pt" data-pdf-signature="Seller" title="Seller's signature">Sign here</div>
        <div class="sign" style="left: 300pt" data-pdf-signature="Buyer"></div>
        </body></html>
        """;

    [Fact]
    public void Html_makes_empty_signature_fields_over_the_boxes()
    {
        var pdf = HtmlToPdf.Convert(Agreement);
        var fields = PdfForm.GetFields(pdf);
        Assert.Equal(new[] { "Seller", "Buyer" }, fields.Select(f => f.Name));
        Assert.All(fields, f => Assert.Equal(PdfFormFieldType.Signature, f.Type));
        var file = PdfFile.Open(pdf, null);
        Assert.Equal(1, file.GetInt(file.GetDict(file.Catalog, "AcroForm")!, "SigFlags", 0));
        var seller = (PdfDict)file.Resolve(Fields(file).Items[0])!;
        Assert.Null(file.Resolve(seller["V"]));
        Assert.Equal("Seller's signature", ((PdfString)file.Resolve(seller["TU"])!).Text);
        Assert.NotNull(file.Resolve(file.GetDict(seller, "AP")!["N"]));
        // The widget covers the border box, 200 by 60 points, 100 points from the top of the page.
        var rect = file.GetNumbers(seller, "Rect")!;
        Assert.Equal(50, rect[0], 0.5);
        Assert.Equal(842 - 160.75, rect[1], 0.5); // and its 1px bottom border
        Assert.Equal(250, rect[2], 0.5);
        Assert.Equal(842 - 100, rect[3], 0.5);
        // The box's own content stays on the page.
        Assert.Contains("Sign here", PdfText.Extract(pdf));
        Assert.Empty(PdfSignatureVerifier.Verify(pdf));
    }

    [Fact]
    public void Signs_an_empty_field_made_from_html()
    {
        var pdf = HtmlToPdf.Convert(Agreement);
        var once = PdfSigner.Sign(pdf, new PdfSignatureOptions(Cert.Value) { FieldName = "Buyer" });
        var twice = PdfSigner.Sign(once, new PdfSignatureOptions(Cert.Value) { FieldName = "Seller" });
        var results = PdfSignatureVerifier.Verify(twice);
        Assert.Equal(new[] { "Buyer", "Seller" }, results.Select(r => r.FieldName));
        Assert.All(results, r => Assert.True(r.DigestMatches && r.SignatureValid));
        var file = PdfFile.Open(twice, null);
        Assert.Equal(2, Fields(file).Count); // signed in place, no new fields
        var buyer = (PdfDict)file.Resolve(Fields(file).Items[1])!;
        Assert.Equal(300, file.GetNumbers(buyer, "Rect")![0], 0.5);
    }

    [Fact]
    public void Unnamed_fields_get_numbered_names()
    {
        var pdf = HtmlToPdf.Convert("<div data-pdf-signature style='height: 40px'></div><div data-pdf-signature style='height: 40px'></div>");
        Assert.Equal(new[] { "Signature1", "Signature2" }, PdfForm.GetFields(pdf).Select(f => f.Name));
    }

    [Fact]
    public void Without_form_fields_the_box_is_only_drawn()
    {
        var pdf = HtmlToPdf.Convert(Agreement, new PdfOptions { FormFields = false });
        Assert.Empty(PdfForm.GetFields(pdf));
        Assert.Contains("Sign here", PdfText.Extract(pdf));
    }

    [Fact]
    public void A_tagged_field_is_a_form_element_with_the_box_content()
    {
        var pdf = PdfTestHelpers.ConvertUncompressed(Agreement, new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 });
        var file = PdfFile.Open(pdf, null);
        foreach (var f in Fields(file).Items)
            Assert.True(file.GetInt((PdfDict)file.Resolve(f)!, "StructParent", -1) >= 0);
        Assert.Equal(2, Regex.Matches(PdfTestHelpers.Latin1(pdf), "/S /Form ").Count);
        var signed = PdfSigner.Sign(pdf, new PdfSignatureOptions(Cert.Value) { FieldName = "Seller" });
        Assert.True(PdfSignatureVerifier.Verify(signed).Single().SignatureValid);
    }

    [Fact]
    public void Odd_places_make_no_field_and_do_not_fail()
    {
        // An inline box, a hidden one, one in a transformed box and an input with the attribute: none is a signature
        // field. A box taller than a page still is one, on the page where it starts.
        var pdf = HtmlToPdf.Convert("""
            <p>Inline <span data-pdf-signature="Inline">here</span></p><div data-pdf-signature="Hidden" style="display: none"></div>
            <div style="transform: rotate(5deg)"><div data-pdf-signature="Turned" style="height: 30px"></div></div>
            <input data-pdf-signature="Input" name="text"><div data-pdf-signature="Tall" style="height: 2000px; border: 1px solid"></div>
            """);
        var fields = PdfForm.GetFields(pdf);
        Assert.Equal(new[] { "text", "Tall" }, fields.Select(f => f.Name));
        Assert.Equal(PdfFormFieldType.Text, fields[0].Type);
        Assert.Equal(PdfFormFieldType.Signature, fields[1].Type);
    }
}
