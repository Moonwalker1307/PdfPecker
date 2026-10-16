using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>Long-term validation: the Document Security Store and document timestamps.</summary>
public class LtvTests
{
    private const string Html = "<html><head><title>Payslip</title></head><body><h1>Lønseddel oktober</h1></body></html>";
    private const string OcspUrl = "http://ocsp.test/";
    private const string CrlUrl = "http://crl.test/root.crl";

    /// <summary>A small PKI: a root CA, and a signer it issued whose certificate points at an OCSP responder and a CRL.</summary>
    private sealed class Pki
    {
        public readonly RSA RootKey = RSA.Create(2048);
        public readonly X509Certificate2 Root;
        public readonly X509Certificate2 Signer;

        public Pki()
        {
            var rootReq = new CertificateRequest("CN=Test Root CA, O=PdfPecker Tests", RootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            rootReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootReq.PublicKey, false));
            Root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddYears(5));

            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=LTV Signer, O=PdfPecker Tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
            req.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(new[] { OcspUrl }, null));
            req.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(new[] { CrlUrl }));
            using var signer = req.Create(Root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), new byte[] { 0x01, 0x23, 0x45, 0x67 });
            Signer = X509CertificateLoader.LoadPkcs12(signer.CopyWithPrivateKey(key).Export(X509ContentType.Pfx, "pw"), "pw");
        }

        public byte[] Crl()
        {
            var builder = new CertificateRevocationListBuilder();
            return builder.Build(Root, 1, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, DateTimeOffset.UtcNow);
        }

        /// <summary>A "good" OCSP response for the signer, signed by the root CA acting as its own responder.</summary>
        public byte[] Ocsp(bool basicOnly = false)
        {
            var tbs = new AsnWriter(AsnEncodingRules.DER);
            using (tbs.PushSequence())
            {
                using (tbs.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true)))
                    tbs.WriteOctetString(SHA1.HashData(Root.PublicKey.EncodedKeyValue.RawData));
                tbs.WriteGeneralizedTime(DateTimeOffset.UtcNow, omitFractionalSeconds: true);
                using (tbs.PushSequence())
                using (tbs.PushSequence())
                {
                    using (tbs.PushSequence())
                    {
                        using (tbs.PushSequence()) { tbs.WriteObjectIdentifier("1.3.14.3.2.26"); tbs.WriteNull(); }
                        tbs.WriteOctetString(SHA1.HashData(Root.SubjectName.RawData));
                        tbs.WriteOctetString(SHA1.HashData(Root.PublicKey.EncodedKeyValue.RawData));
                        tbs.WriteInteger(Signer.SerialNumberBytes.Span);
                    }
                    tbs.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 0)); // good
                    tbs.WriteGeneralizedTime(DateTimeOffset.UtcNow, omitFractionalSeconds: true);
                }
            }
            byte[] tbsBytes = tbs.Encode();
            var basic = new AsnWriter(AsnEncodingRules.DER);
            using (basic.PushSequence())
            {
                basic.WriteEncodedValue(tbsBytes);
                using (basic.PushSequence()) { basic.WriteObjectIdentifier("1.2.840.113549.1.1.11"); basic.WriteNull(); }
                basic.WriteBitString(RootKey.SignData(tbsBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                using (basic.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                using (basic.PushSequence())
                    basic.WriteEncodedValue(Root.RawData);
            }
            byte[] basicBytes = basic.Encode();
            if (basicOnly) return basicBytes;
            var resp = new AsnWriter(AsnEncodingRules.DER);
            using (resp.PushSequence())
            {
                resp.WriteEncodedValue(new byte[] { 0x0A, 0x01, 0x00 });
                using (resp.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                using (resp.PushSequence())
                {
                    resp.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.1");
                    resp.WriteOctetString(basicBytes);
                }
            }
            return resp.Encode();
        }
    }

    private static readonly Lazy<Pki> Ca = new(() => new Pki());

    private static byte[] SignedPdf() => HtmlToPdf.Convert(Html, new PdfOptions { Signature = new PdfSignatureOptions(Ca.Value.Signer) });

    /// <summary>Answers OCSP and CRL requests the way a CA's servers would, and records them.</summary>
    private sealed class FakeCa(Pki pki, bool ocspFails = false, bool crlFails = false) : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public byte[]? OcspRequestBody;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (url == OcspUrl)
            {
                Assert.Equal("application/ocsp-request", request.Content!.Headers.ContentType!.MediaType);
                OcspRequestBody = request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
                return ocspFails ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pki.Ocsp()) };
            }
            if (url == CrlUrl)
                return crlFails ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pki.Crl()) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static PdfDict Dss(PdfFile file) => file.GetDict(file.Catalog, "DSS")!;

    private static List<byte[]> Streams(PdfFile file, PdfArray? array)
        => array == null ? new() : array.Items.Select(i => file.DecodeStream((PdfStream)file.Resolve(i)!)).ToList();

    private static string VriKey(byte[] pdf, PdfSignatureVerification v)
    {
        var hex = Encoding.Latin1.GetString(pdf, (int)v.ByteRange[1] + 1, (int)(v.ByteRange[2] - v.ByteRange[1] - 2));
        return Convert.ToHexString(SHA1.HashData(Convert.FromHexString(hex)));
    }

    [Fact]
    public void Stores_supplied_OCSP_and_CRL_and_keeps_the_signature_valid()
    {
        var signed = SignedPdf();
        var ltv = PdfSigner.AddValidationData(signed, new PdfLtvOptions
        {
            Certificates = new[] { Ca.Value.Root },
            OcspResponses = new[] { Ca.Value.Ocsp() },
            Crls = new[] { Ca.Value.Crl() },
        });
        Assert.Equal(signed, ltv.AsSpan(0, signed.Length).ToArray());

        var file = PdfFile.Open(ltv, null);
        var dss = Dss(file);
        Assert.Equal("DSS", file.GetName(dss, "Type"));
        var certs = Streams(file, file.GetArray(dss, "Certs")).Select(b => X509CertificateLoader.LoadCertificate(b).Thumbprint).ToList();
        Assert.Equal(new[] { Ca.Value.Signer.Thumbprint, Ca.Value.Root.Thumbprint }.Order(), certs.Order());
        var ocsp = Assert.Single(Streams(file, file.GetArray(dss, "OCSPs")));
        Assert.Equal(Ca.Value.Ocsp().Length, ocsp.Length);
        Assert.Equal(0x0A, new AsnReader(ocsp, AsnEncodingRules.DER).ReadSequence().PeekEncodedValue().Span[0]); // a full OCSPResponse
        var crl = Assert.Single(Streams(file, file.GetArray(dss, "CRLs")));
        Assert.Equal(Ca.Value.Root.SubjectName.RawData, new CertificateRevocationListBuilderReader(crl).Issuer);

        var v = Assert.Single(PdfSignatureVerifier.Verify(ltv, new PdfSignatureVerificationOptions { TrustedRoots = new X509Certificate2Collection(Ca.Value.Root) }));
        Assert.True(v.Valid, string.Join("\n", v.Problems));
        Assert.False(v.CoversWholeDocument);
        Assert.Equal(1, v.UpdatesAfterSigning);

        // The VRI entry for the signature points at the same streams.
        var vri = file.GetDict(dss, "VRI")!;
        var entry = (PdfDict)file.Resolve(vri[VriKey(ltv, v)])!;
        Assert.Equal(2, file.GetArray(entry, "Cert")!.Count);
        Assert.Single(file.GetArray(entry, "OCSP")!.Items);
        Assert.Single(file.GetArray(entry, "CRL")!.Items);
        Assert.NotNull(file.Resolve(entry["TU"]) as PdfString);

        // PDF 1.7 readers learn about the store from the ETSI extension.
        var esic = file.GetDict(file.GetDict(file.Catalog, "Extensions"), "ESIC")!;
        Assert.Equal(5, file.GetInt(esic, "ExtensionLevel", 0));
        Assert.True(PdfRenderer.Open(ltv).RenderPage(0, new ImageOptions { Dpi = 20 }).Width > 0);
    }

    /// <summary>Reads a CRL's issuer, enough to tell the stored CRL is the one supplied.</summary>
    private sealed class CertificateRevocationListBuilderReader
    {
        public readonly byte[] Issuer;

        public CertificateRevocationListBuilderReader(byte[] crl)
        {
            var tbs = new AsnReader(crl, AsnEncodingRules.DER).ReadSequence().ReadSequence();
            if (tbs.PeekTag().TagValue == (int)UniversalTagNumber.Integer) tbs.ReadInteger();
            tbs.ReadSequence();
            Issuer = tbs.ReadEncodedValue().ToArray();
        }
    }

    [Fact]
    public void Nothing_is_fetched_unless_asked()
    {
        var handler = new FakeCa(Ca.Value);
        var ltv = PdfSigner.AddValidationData(SignedPdf(), new PdfLtvOptions { HttpClient = new HttpClient(handler) });
        Assert.Empty(handler.Requests);
        var file = PdfFile.Open(ltv, null);
        Assert.Single(file.GetArray(Dss(file), "Certs")!.Items); // the root is neither embedded nor supplied
        Assert.Null(file.GetArray(Dss(file), "OCSPs"));
    }

    [Fact]
    public void Fetches_OCSP_from_the_certificate()
    {
        var handler = new FakeCa(Ca.Value);
        var ltv = PdfSigner.AddValidationData(SignedPdf(), new PdfLtvOptions
        {
            Certificates = new[] { Ca.Value.Root },
            FetchRevocationData = true,
            HttpClient = new HttpClient(handler),
        });
        Assert.Equal(new[] { OcspUrl }, handler.Requests);
        // The request's CertID names the signer by serial number.
        var certId = new AsnReader(handler.OcspRequestBody!, AsnEncodingRules.DER).ReadSequence().ReadSequence().ReadSequence().ReadSequence().ReadSequence();
        certId.ReadSequence();
        certId.ReadOctetString();
        certId.ReadOctetString();
        Assert.Equal(Ca.Value.Signer.SerialNumberBytes.ToArray(), certId.ReadIntegerBytes().ToArray());

        var file = PdfFile.Open(ltv, null);
        Assert.Single(file.GetArray(Dss(file), "OCSPs")!.Items);
        Assert.Null(file.GetArray(Dss(file), "CRLs"));
        Assert.True(PdfSignatureVerifier.Verify(ltv, new PdfSignatureVerificationOptions { TrustedRoots = new X509Certificate2Collection(Ca.Value.Root) })[0].Valid);
    }

    [Fact]
    public void Falls_back_to_the_CRL_and_reports_when_both_fail()
    {
        var handler = new FakeCa(Ca.Value, ocspFails: true);
        var ltv = PdfSigner.AddValidationData(SignedPdf(), new PdfLtvOptions
        {
            Certificates = new[] { Ca.Value.Root },
            FetchRevocationData = true,
            HttpClient = new HttpClient(handler),
        });
        Assert.Equal(new[] { OcspUrl, CrlUrl }, handler.Requests);
        var file = PdfFile.Open(ltv, null);
        Assert.Single(file.GetArray(Dss(file), "CRLs")!.Items);

        var ex = Assert.Throws<PdfSignatureException>(() => PdfSigner.AddValidationData(SignedPdf(), new PdfLtvOptions
        {
            Certificates = new[] { Ca.Value.Root },
            FetchRevocationData = true,
            HttpClient = new HttpClient(new FakeCa(Ca.Value, ocspFails: true, crlFails: true)),
        }));
        Assert.Contains("500", ex.Message);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public void Adds_a_document_timestamp_for_B_LTA()
    {
        using var tsa = SignatureVerificationTests.SelfSigned("Test TSA");
        var signed = SignedPdf();
        var ltv = PdfSigner.AddValidationData(signed, new PdfLtvOptions
        {
            Certificates = new[] { Ca.Value.Root },
            OcspResponses = new[] { Ca.Value.Ocsp(basicOnly: true) },
            TimestampUrl = "http://tsa.example/",
            TimestampHttpClient = new HttpClient(new SignatureTests.FakeTsa(tsa)),
        });
        var all = PdfSignatureVerifier.Verify(ltv, new PdfSignatureVerificationOptions { TrustedRoots = new X509Certificate2Collection { Ca.Value.Root, tsa } });
        Assert.Equal(2, all.Count);
        Assert.True(all[0].Valid, string.Join("\n", all[0].Problems));
        Assert.Equal(2, all[0].UpdatesAfterSigning);
        var ts = all[1];
        Assert.True(ts.IsDocumentTimestamp);
        Assert.Equal("ETSI.RFC3161", ts.SubFilter);
        Assert.Equal("Signature2", ts.FieldName);
        Assert.True(ts.TimestampValid);
        Assert.True(ts.CoversWholeDocument);
        Assert.True(ts.Valid, string.Join("\n", ts.Problems));
        Assert.Equal(tsa.Thumbprint, ts.SignerCertificate!.Thumbprint);
        Assert.Contains("/Type /DocTimeStamp", Encoding.Latin1.GetString(ltv));

        // The bare BasicOCSPResponse was stored as a full OCSPResponse.
        var file = PdfFile.Open(ltv, null);
        var ocsp = Assert.Single(Streams(file, file.GetArray(Dss(file), "OCSPs")));
        Assert.Equal(Ca.Value.Ocsp().Length, ocsp.Length);

        // A second pass adds the timestamp's own chain under its own VRI key, without duplicating what is there.
        var again = PdfSigner.AddValidationData(ltv, new PdfLtvOptions { Certificates = new[] { Ca.Value.Root } });
        var file2 = PdfFile.Open(again, null);
        var dss2 = Dss(file2);
        var certs = Streams(file2, file2.GetArray(dss2, "Certs")).Select(b => X509CertificateLoader.LoadCertificate(b).Thumbprint).ToList();
        Assert.Equal(certs.Distinct().Count(), certs.Count);
        Assert.Contains(tsa.Thumbprint, certs);
        var vri = file2.GetDict(dss2, "VRI")!;
        Assert.Equal(2, vri.Entries.Count);
        var tsHex = Encoding.Latin1.GetString(again, (int)ts.ByteRange[1] + 1, (int)(ts.ByteRange[2] - ts.ByteRange[1] - 2));
        var token = Convert.FromHexString(tsHex);
        AsnDecoder.ReadEncodedValue(token, AsnEncodingRules.BER, out _, out _, out int used);
        Assert.NotNull(vri[Convert.ToHexString(SHA1.HashData(token.AsSpan(0, used)))]);
        Assert.All(PdfSignatureVerifier.Verify(again, new PdfSignatureVerificationOptions { TrustedRoots = new X509Certificate2Collection { Ca.Value.Root, tsa } }),
            v => Assert.True(v.Valid, string.Join("\n", v.Problems)));
    }

    [Fact]
    public void Works_on_encrypted_files()
    {
        var pdf = HtmlToPdf.Convert(Html, new PdfOptions
        {
            Security = new PdfSecurity { UserPassword = "kodeord" },
            Signature = new PdfSignatureOptions(Ca.Value.Signer),
        });
        var ltv = PdfSigner.AddValidationData(pdf, new PdfLtvOptions { Certificates = new[] { Ca.Value.Root }, Crls = new[] { Ca.Value.Crl() } }, "kodeord");
        var file = PdfFile.Open(ltv, "kodeord");
        var crl = Assert.Single(Streams(file, file.GetArray(Dss(file), "CRLs")));
        Assert.Equal(Ca.Value.Crl().Length, crl.Length);
        var v = Assert.Single(PdfSignatureVerifier.Verify(ltv, new PdfSignatureVerificationOptions { TrustedRoots = new X509Certificate2Collection(Ca.Value.Root) }, "kodeord"));
        Assert.True(v.Valid, string.Join("\n", v.Problems));
    }

    [Fact]
    public void Rejects_unsigned_files_and_bad_revocation_data()
    {
        Assert.Throws<PdfSignatureException>(() => PdfSigner.AddValidationData(HtmlToPdf.Convert(Html)));
        Assert.Throws<ArgumentException>(() => PdfSigner.AddValidationData(SignedPdf(), new PdfLtvOptions { OcspResponses = new[] { new byte[] { 1, 2, 3 } } }));
        Assert.Throws<ArgumentException>(() => PdfSigner.AddValidationData(SignedPdf(), new PdfLtvOptions { Crls = new[] { new byte[] { 0x30, 0x00 } } }));
    }
}
