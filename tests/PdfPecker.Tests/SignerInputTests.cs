using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PdfPecker.Tests;

/// <summary>
/// Unreadable PDFs given to the signer must fail with a documented exception (<see cref="InvalidDataException"/> when
/// the file can't be parsed, as for every other API that reads a PDF, or a <see cref="PdfSignatureException"/> when it
/// parses but can't be signed) instead of leaking the parser's internal exception type.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class SignerInputTests
{
    private static readonly Lazy<X509Certificate2> Cert = new(() =>
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Signer input tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "pw"), "pw");
    });

    public static TheoryData<string> Inputs() => new() { "garbage", "header-only", "truncated" };

    private static byte[] Input(string kind)
    {
        var valid = HtmlToPdf.Convert("<p>Signed</p>");
        return kind switch
        {
            "garbage" => Encoding.ASCII.GetBytes("this is not a PDF at all"),
            "header-only" => Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF\n"),
            _ => valid[..(valid.Length / 3)],
        };
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task Unreadable_pdfs_fail_with_a_documented_exception(string kind)
    {
        var options = new PdfSignatureOptions(Cert.Value);
        AssertDocumented(Assert.ThrowsAny<Exception>(() => PdfSigner.Sign(Input(kind), options)));
        AssertDocumented(await Assert.ThrowsAnyAsync<Exception>(() => PdfSigner.SignAsync(Input(kind), options)));
    }

    private static void AssertDocumented(Exception ex) =>
        Assert.True(ex is InvalidDataException or PdfSignatureException, ex.GetType().FullName + ": " + ex.Message);
}
