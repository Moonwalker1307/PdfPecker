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
}
