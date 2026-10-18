using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfPecker.Tests;

/// <summary>
/// Cancelling stops signing and adding validation data while a timestamp, OCSP or CRL server keeps them waiting, in the
/// synchronous and the asynchronous API alike.
/// </summary>
public class SigningCancellationTests
{
    private const string Html = "<html><head><title>Cancel</title></head><body><p>Signed content.</p></body></html>";
    private const string TsaUrl = "http://tsa.example/";
    private const string OcspUrl = "http://ocsp.cancel.test/";
    private const string CrlUrl = "http://crl.cancel.test/root.crl";

    /// <summary>A root CA and a signer whose certificate names an OCSP responder and a CRL.</summary>
    private sealed class Pki
    {
        public readonly X509Certificate2 Root;
        public readonly X509Certificate2 Signer;

        public Pki()
        {
            using var rootKey = RSA.Create(2048);
            var rootReq = new CertificateRequest("CN=Cancel Root CA, O=PdfPecker Tests", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            Root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddYears(5));

            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=Cancel Signer, O=PdfPecker Tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            req.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(new[] { OcspUrl }, null));
            req.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(new[] { CrlUrl }));
            using var signer = req.Create(Root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), new byte[] { 0x07, 0x11 });
            Signer = X509CertificateLoader.LoadPkcs12(signer.CopyWithPrivateKey(key).Export(X509ContentType.Pfx, "pw"), "pw");
        }
    }

    private static readonly Lazy<Pki> Ca = new(() => new Pki());
    private static readonly Lazy<X509Certificate2> Tsa = new(() => SignatureVerificationTests.SelfSigned("Cancel TSA", timestamping: true));

    private static byte[] SignedPdf() => HtmlToPdf.Convert(Html, new PdfOptions { Signature = new PdfSignatureOptions(Ca.Value.Signer) });

    /// <summary>
    /// A server that never answers: it cancels <paramref name="cancel"/> shortly after a request arrives and then waits
    /// for the request's token, recording whether the client sent the request synchronously or asynchronously.
    /// </summary>
    private sealed class HangingServer(CancellationTokenSource cancel) : HttpMessageHandler
    {
        public readonly List<string> Calls = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Arrived("async " + request.RequestUri);
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            throw new InvalidOperationException("The request was not cancelled.");
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Arrived("sync " + request.RequestUri);
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The request was not cancelled.");
        }

        private void Arrived(string call)
        {
            lock (Calls) Calls.Add(call);
            cancel.CancelAfter(50);
        }
    }

    /// <summary>A server that sends its headers at once and then never sends the body.</summary>
    private sealed class StallingBodyServer(CancellationTokenSource cancel) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancel.CancelAfter(50);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
        }
    }

    /// <summary>A stream whose reads block until it is disposed, like a socket the server stopped writing to.</summary>
    private sealed class StallingStream : Stream
    {
        private readonly ManualResetEventSlim _disposed = new();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_disposed.Wait(TimeSpan.FromSeconds(20))) throw new InvalidOperationException("The read was not cancelled.");
            throw new ObjectDisposedException(nameof(StallingStream));
        }

        protected override void Dispose(bool disposing)
        {
            _disposed.Set();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static PdfSignatureOptions Timestamped(HttpMessageHandler tsa)
        => new(Ca.Value.Signer) { TimestampUrl = TsaUrl, TimestampHttpClient = new HttpClient(tsa) };

    private static PdfLtvOptions Fetching(HttpMessageHandler server)
        => new() { Certificates = new[] { Ca.Value.Root }, FetchRevocationData = true, HttpClient = new HttpClient(server) };

    [Fact]
    public async Task SignAsync_awaits_the_timestamp_server_and_stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var server = new HangingServer(cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfSigner.SignAsync(SignedPdf(), Timestamped(server), cancellationToken: cts.Token));
        Assert.Equal(new[] { "async " + TsaUrl }, server.Calls);
    }

    [Fact]
    public void Sign_with_a_token_stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var server = new HangingServer(cts);
        Assert.ThrowsAny<OperationCanceledException>(() => PdfSigner.Sign(SignedPdf(), Timestamped(server), null, cts.Token));
        Assert.Equal(new[] { "sync " + TsaUrl }, server.Calls);
    }

    [Fact]
    public void Sign_with_a_token_stops_a_timestamp_server_that_never_sends_the_body()
    {
        using var cts = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => PdfSigner.Sign(SignedPdf(), Timestamped(new StallingBodyServer(cts)), null, cts.Token));
    }

    [Fact]
    public async Task ConvertAsync_signs_asynchronously_with_the_callers_token()
    {
        using var cts = new CancellationTokenSource();
        var server = new HangingServer(cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtmlToPdf.ConvertAsync(Html, new PdfOptions { Signature = Timestamped(server) }, cts.Token));
        Assert.Equal(new[] { "async " + TsaUrl }, server.Calls);

        using var cts2 = new CancellationTokenSource();
        var server2 = new HangingServer(cts2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MarkdownToPdf.ConvertAsync("# Cancel", new PdfOptions { Signature = Timestamped(server2) }, cancellationToken: cts2.Token));
        Assert.Equal(new[] { "async " + TsaUrl }, server2.Calls);
    }

    [Fact]
    public void Convert_with_a_token_passes_it_to_signing()
    {
        using var cts = new CancellationTokenSource();
        var server = new HangingServer(cts);
        Assert.ThrowsAny<OperationCanceledException>(() => HtmlToPdf.Convert(Html, new PdfOptions { Signature = Timestamped(server) }, cts.Token));
        Assert.Equal(new[] { "sync " + TsaUrl }, server.Calls);
    }

    [Fact]
    public async Task Asynchronous_signing_gives_a_valid_timestamped_signature()
    {
        var options = Timestamped(new SignatureTests.FakeTsa(Tsa.Value));
        var trust = new PdfSignatureVerificationOptions { TrustedRoots = new X509Certificate2Collection { Ca.Value.Root, Tsa.Value } };

        var converted = await HtmlToPdf.ConvertAsync(Html, new PdfOptions { Signature = options });
        var v = Assert.Single(PdfSignatureVerifier.Verify(converted, trust));
        Assert.True(v.Valid, string.Join("\n", v.Problems));
        Assert.True(v.TimestampValid);

        var signedAgain = PdfSigner.Sign(converted, options, null, CancellationToken.None);
        Assert.All(PdfSignatureVerifier.Verify(signedAgain, trust), s => Assert.True(s.Valid, string.Join("\n", s.Problems)));
    }

    [Fact]
    public async Task AddValidationDataAsync_stops_while_the_OCSP_responder_hangs()
    {
        var pdf = SignedPdf();
        using var cts = new CancellationTokenSource();
        var server = new HangingServer(cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfSigner.AddValidationDataAsync(pdf, Fetching(server), cancellationToken: cts.Token));
        // Cancelling stops the call: it does not go on to the CRL as it would after a failed OCSP request.
        Assert.Equal(new[] { "async " + OcspUrl }, server.Calls);
    }

    [Fact]
    public void AddValidationData_with_a_token_stops_while_the_OCSP_responder_hangs()
    {
        var pdf = SignedPdf();
        using var cts = new CancellationTokenSource();
        var server = new HangingServer(cts);
        Assert.ThrowsAny<OperationCanceledException>(() => PdfSigner.AddValidationData(pdf, Fetching(server), null, cts.Token));
        Assert.Equal(new[] { "sync " + OcspUrl }, server.Calls);
    }

    [Fact]
    public async Task AddValidationDataAsync_stops_while_the_timestamp_server_hangs()
    {
        var pdf = SignedPdf();
        using var cts = new CancellationTokenSource();
        var server = new HangingServer(cts);
        var options = new PdfLtvOptions { Certificates = new[] { Ca.Value.Root }, TimestampUrl = TsaUrl, TimestampHttpClient = new HttpClient(server) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfSigner.AddValidationDataAsync(pdf, options, cancellationToken: cts.Token));
        Assert.Equal(new[] { "async " + TsaUrl }, server.Calls);
    }

    [Fact]
    public async Task A_cancelled_token_fetches_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var server = new HangingServer(cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfSigner.AddValidationDataAsync(SignedPdf(), Fetching(server), cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfSigner.SignAsync(SignedPdf(), Timestamped(server), cancellationToken: cts.Token));
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task AddValidationDataAsync_matches_the_synchronous_result()
    {
        var pdf = SignedPdf();
        var options = new PdfLtvOptions { Certificates = new[] { Ca.Value.Root } };
        Assert.Equal(PdfSigner.AddValidationData(pdf, options), await PdfSigner.AddValidationDataAsync(pdf, options));
        Assert.Equal(PdfSigner.AddValidationData(pdf, options), PdfSigner.AddValidationData(pdf, options, null, CancellationToken.None));

        var withTimestamp = await PdfSigner.AddValidationDataAsync(pdf, new PdfLtvOptions
        {
            Certificates = new[] { Ca.Value.Root },
            TimestampUrl = TsaUrl,
            TimestampHttpClient = new HttpClient(new SignatureTests.FakeTsa(Tsa.Value)),
        });
        var all = PdfSignatureVerifier.Verify(withTimestamp, new PdfSignatureVerificationOptions { TrustedRoots = new X509Certificate2Collection { Ca.Value.Root, Tsa.Value } });
        Assert.Equal(2, all.Count);
        Assert.True(all[1].IsDocumentTimestamp);
        Assert.All(all, s => Assert.True(s.Valid, string.Join("\n", s.Problems)));
    }
}
