using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

/// <summary>
/// The cancellation token and Stream overloads: an already-cancelled token stops the work before any output is written,
/// and a Stream overload gives the same bytes as the byte[] overload it mirrors.
/// </summary>
public class OverloadTests
{
    private const string Html = "<h1>Overloads</h1><p>one</p><p style='page-break-before:always'>two</p><p style='page-break-before:always'>three</p>";
    private const string Markdown = "# Overloads\n\nSome *Markdown* text.\n";

    // A fixed creation date, so two conversions of the same input give the same bytes even when the clock ticks between them.
    private static PdfOptions Options() => new() { CreationDate = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) };

    private static readonly Lazy<byte[]> ThreePages = new(() => HtmlToPdf.Convert(Html, Options()));
    private static readonly Lazy<X509Certificate2> Cert = new(() => SignatureVerificationTests.SelfSigned("Overload Signer"));

    private static CancellationToken Cancelled() => new(canceled: true);

    private static MemoryStream In(byte[] data) => new(data, writable: false);

    /// <summary>
    /// The PDF as text with the file identifiers and dates zeroed: rewritten files get a random second identifier and
    /// the time of the rewrite as their modification date.
    /// </summary>
    private static string WithoutId(byte[] pdf)
        => Regex.Replace(PdfTestHelpers.WithoutDates(pdf), @"/ID ?\[<([0-9A-Fa-f]*)> ?<([0-9A-Fa-f]*)>\]", m => new string('0', m.Length));

    private static void AssertSamePdf(byte[] expected, byte[] actual) => Assert.Equal(WithoutId(expected), WithoutId(actual));

    private static void AssertSamePdfs(byte[][] expected, byte[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) AssertSamePdf(expected[i], actual[i]);
    }

    private static void WithFolder(Action<string> test)
    {
        var dir = Directory.CreateTempSubdirectory("pdfpecker-overloads-");
        try
        {
            test(dir.FullName);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static void AssertCancelledWithoutOutput(Action<Stream> write)
    {
        var output = new MemoryStream();
        Assert.ThrowsAny<OperationCanceledException>(() => write(output));
        Assert.Equal(0, output.Length);
    }

    private static async Task AssertCancelledWithoutOutputAsync(Func<Stream, Task> write)
    {
        var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write(output));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task HtmlToPdf_overloads_honour_the_token_and_write_streams()
    {
        AssertCancelledWithoutOutput(o => HtmlToPdf.Convert(Html, o, Options(), Cancelled()));
        await AssertCancelledWithoutOutputAsync(o => HtmlToPdf.ConvertAsync(Html, o, Options(), Cancelled()));
        WithFolder(dir =>
        {
            var html = Path.Combine(dir, "in.html");
            File.WriteAllText(html, Html);
            var pdf = Path.Combine(dir, "out.pdf");
            Assert.ThrowsAny<OperationCanceledException>(() => HtmlToPdf.ConvertFile(html, pdf, Options(), Cancelled()));
            Assert.False(File.Exists(pdf));
            HtmlToPdf.ConvertFile(html, pdf, Options(), CancellationToken.None);
            Assert.Equal(ThreePages.Value, File.ReadAllBytes(pdf));
        });

        var sync = new MemoryStream();
        HtmlToPdf.Convert(Html, sync, Options(), CancellationToken.None);
        Assert.Equal(ThreePages.Value, sync.ToArray());
        var viaAsync = new MemoryStream();
        await HtmlToPdf.ConvertAsync(Html, viaAsync, Options());
        Assert.Equal(ThreePages.Value, viaAsync.ToArray());
    }

    [Fact]
    public async Task MarkdownToPdf_overloads_honour_the_token_and_write_streams()
    {
        var expected = MarkdownToPdf.Convert(Markdown, Options());
        Assert.ThrowsAny<OperationCanceledException>(() => MarkdownToPdf.Convert(Markdown, Options(), null, Cancelled()));
        AssertCancelledWithoutOutput(o => MarkdownToPdf.Convert(Markdown, o, Options(), null, Cancelled()));
        await AssertCancelledWithoutOutputAsync(o => MarkdownToPdf.ConvertAsync(Markdown, o, Options(), null, Cancelled()));
        WithFolder(dir =>
        {
            var md = Path.Combine(dir, "in.md");
            File.WriteAllText(md, Markdown);
            var pdf = Path.Combine(dir, "out.pdf");
            Assert.ThrowsAny<OperationCanceledException>(() => MarkdownToPdf.ConvertFile(md, pdf, Options(), null, Cancelled()));
            Assert.False(File.Exists(pdf));
            MarkdownToPdf.ConvertFile(md, pdf, Options(), null, CancellationToken.None);
            Assert.Equal(expected, File.ReadAllBytes(pdf));
        });

        Assert.Equal(expected, MarkdownToPdf.Convert(Markdown, Options(), null, CancellationToken.None));
        var sync = new MemoryStream();
        MarkdownToPdf.Convert(Markdown, sync, Options(), null, CancellationToken.None);
        Assert.Equal(expected, sync.ToArray());
        var viaAsync = new MemoryStream();
        await MarkdownToPdf.ConvertAsync(Markdown, viaAsync, Options());
        Assert.Equal(expected, viaAsync.ToArray());
    }

    [Fact]
    public void PdfToImage_overloads_honour_the_token_and_read_streams()
    {
        var pdf = ThreePages.Value;
        var options = new ImageOptions { Dpi = 20 };
        Assert.ThrowsAny<OperationCanceledException>(() => PdfToImage.ConvertPage(pdf, 0, options, Cancelled()));
        Assert.ThrowsAny<OperationCanceledException>(() => PdfToImage.Convert(pdf, options, Cancelled()));
        Assert.ThrowsAny<OperationCanceledException>(() => PdfToImage.ConvertToTiff(pdf, options, Cancelled()));
        AssertCancelledWithoutOutput(o => PdfToImage.ConvertToTiff(In(pdf), o, options, Cancelled()));
        WithFolder(dir =>
        {
            var input = Path.Combine(dir, "doc.pdf");
            File.WriteAllBytes(input, pdf);
            var images = Path.Combine(dir, "images");
            Assert.ThrowsAny<OperationCanceledException>(() => PdfToImage.ConvertFile(input, images, options, Cancelled()));
            Assert.False(Directory.Exists(images));
            var tiff = Path.Combine(dir, "doc.tif");
            Assert.ThrowsAny<OperationCanceledException>(() => PdfToImage.ConvertFileToTiff(input, tiff, options, Cancelled()));
            Assert.False(File.Exists(tiff));
            Assert.Equal(3, PdfToImage.ConvertFile(input, images, options, CancellationToken.None).Count);
        });

        Assert.Equal(3, PdfToImage.GetPageCount(In(pdf)));
        Assert.Equal(PdfToImage.ConvertPage(pdf, 2, options), PdfToImage.ConvertPage(In(pdf), 2, options));
        Assert.Equal(PdfToImage.Convert(pdf, options), PdfToImage.Convert(In(pdf), options));
        var tiffStream = new MemoryStream();
        PdfToImage.ConvertToTiff(In(pdf), tiffStream, options);
        Assert.Equal(PdfToImage.ConvertToTiff(pdf, options), tiffStream.ToArray());
    }

    [Fact]
    public void PdfPages_stream_overloads_match_the_byte_array_ones()
    {
        var pdf = ThreePages.Value;
        Assert.Equal(3, PdfPages.Count(In(pdf)));

        var removed = new MemoryStream();
        PdfPages.Remove(In(pdf), removed, PdfPageRange.Parse("2"));
        AssertSamePdf(PdfPages.Remove(pdf, "2"), removed.ToArray());

        var rotated = new MemoryStream();
        PdfPages.Rotate(In(pdf), rotated, 90, "1,3");
        AssertSamePdf(PdfPages.Rotate(pdf, 90, "1,3"), rotated.ToArray());

        AssertSamePdfs(PdfPages.Split(pdf, 2), PdfPages.Split(In(pdf), 2));
        var ranges = new[] { PdfPageRange.Parse("3"), PdfPageRange.Parse("1-2") };
        AssertSamePdfs(PdfPages.Split(pdf, ranges), PdfPages.Split(In(pdf), ranges));

        AssertCancelledWithoutOutput(o => PdfPages.Remove(In(pdf), o, PdfPageRange.Parse("2"), null, Cancelled()));
        AssertCancelledWithoutOutput(o => PdfPages.Rotate(In(pdf), o, 90, null, null, Cancelled()));
        Assert.ThrowsAny<OperationCanceledException>(() => PdfPages.Split(In(pdf), 1, null, Cancelled()));
    }

    [Fact]
    public async Task PdfSigner_overloads_honour_the_token_and_use_streams()
    {
        var pdf = ThreePages.Value;
        PdfSignatureOptions Options() => new(Cert.Value) { SigningTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) };
        var signed = PdfSigner.Sign(pdf, Options());

        AssertCancelledWithoutOutput(o => PdfSigner.Sign(In(pdf), o, Options(), null, Cancelled()));
        await AssertCancelledWithoutOutputAsync(o => PdfSigner.SignAsync(In(pdf), o, Options(), null, Cancelled()));
        AssertCancelledWithoutOutput(o => PdfSigner.AddValidationData(In(signed), o, null, null, Cancelled()));
        await AssertCancelledWithoutOutputAsync(o => PdfSigner.AddValidationDataAsync(In(signed), o, null, null, Cancelled()));
        WithFolder(dir =>
        {
            var input = Path.Combine(dir, "in.pdf");
            File.WriteAllBytes(input, pdf);
            var output = Path.Combine(dir, "signed.pdf");
            Assert.ThrowsAny<OperationCanceledException>(() => PdfSigner.SignFile(input, output, Options(), null, Cancelled()));
            Assert.False(File.Exists(output));
            PdfSigner.SignFile(input, output, Options(), null, CancellationToken.None);
            Assert.Equal(signed, File.ReadAllBytes(output));
        });

        var sync = new MemoryStream();
        PdfSigner.Sign(In(pdf), sync, Options());
        Assert.Equal(signed, sync.ToArray());
        var viaAsync = new MemoryStream();
        await PdfSigner.SignAsync(In(pdf), viaAsync, Options());
        Assert.Equal(signed, viaAsync.ToArray());

        var ltv = new MemoryStream();
        PdfSigner.AddValidationData(In(signed), ltv);
        // Validation data records the time it was added, so separate runs are compared without their dates.
        Assert.Equal(PdfTestHelpers.WithoutDates(PdfSigner.AddValidationData(signed)), PdfTestHelpers.WithoutDates(ltv.ToArray()));
        var ltvAsync = new MemoryStream();
        await PdfSigner.AddValidationDataAsync(In(signed), ltvAsync);
        Assert.Equal(PdfTestHelpers.WithoutDates(ltv.ToArray()), PdfTestHelpers.WithoutDates(ltvAsync.ToArray()));
    }

    [Fact]
    public void PdfSignatureVerifier_overloads_honour_the_token_and_read_streams()
    {
        var signed = PdfSigner.Sign(ThreePages.Value, new PdfSignatureOptions(Cert.Value));
        Assert.ThrowsAny<OperationCanceledException>(() => PdfSignatureVerifier.Verify(signed, null, null, Cancelled()));
        Assert.ThrowsAny<OperationCanceledException>(() => PdfSignatureVerifier.Verify(In(signed), null, null, Cancelled()));

        var fromBytes = Assert.Single(PdfSignatureVerifier.Verify(signed));
        var fromStream = Assert.Single(PdfSignatureVerifier.Verify(In(signed)));
        Assert.True(fromStream.IsIntact);
        Assert.Equal(fromBytes.ByteRange, fromStream.ByteRange);
        Assert.Equal(fromBytes.SignerName, fromStream.SignerName);
        WithFolder(dir =>
        {
            var path = Path.Combine(dir, "signed.pdf");
            File.WriteAllBytes(path, signed);
            Assert.ThrowsAny<OperationCanceledException>(() => PdfSignatureVerifier.VerifyFile(path, null, null, Cancelled()));
            Assert.True(Assert.Single(PdfSignatureVerifier.VerifyFile(path, null, null, CancellationToken.None)).IsIntact);
        });
    }

    [Fact]
    public void PdfProtector_overloads_honour_the_token()
    {
        var pdf = ThreePages.Value;
        var security = new PdfSecurity { UserPassword = "open" };
        Assert.ThrowsAny<OperationCanceledException>(() => PdfProtector.Encrypt(pdf, security, null, Cancelled()));
        AssertCancelledWithoutOutput(o => PdfProtector.Encrypt(In(pdf), o, security, null, Cancelled()));

        var encrypted = PdfProtector.Encrypt(pdf, security, null, CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(() => PdfProtector.Decrypt(encrypted, "open", Cancelled()));
        AssertCancelledWithoutOutput(o => PdfProtector.Decrypt(In(encrypted), o, "open", Cancelled()));
        var decrypted = new MemoryStream();
        PdfProtector.Decrypt(In(encrypted), decrypted, "open", CancellationToken.None);
        AssertSamePdf(PdfProtector.Decrypt(encrypted, "open"), decrypted.ToArray());
    }

    [Fact]
    public void PdfAttachments_overloads_honour_the_token_and_use_streams()
    {
        var pdf = ThreePages.Value;
        var data = "a,b\n1,2\n"u8.ToArray();
        PdfAttachment Attachment() => new("data.csv", data) { CreationDate = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), ModDate = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) };
        var attached = PdfAttachments.Add(pdf, Attachment());

        Assert.ThrowsAny<OperationCanceledException>(() => PdfAttachments.Add(pdf, new[] { Attachment() }, null, Cancelled()));
        AssertCancelledWithoutOutput(o => PdfAttachments.Add(In(pdf), o, new[] { Attachment() }, null, Cancelled()));
        Assert.ThrowsAny<OperationCanceledException>(() => PdfAttachments.List(attached, null, Cancelled()));
        Assert.ThrowsAny<OperationCanceledException>(() => PdfAttachments.Extract(attached, "data.csv", null, Cancelled()));

        var added = new MemoryStream();
        PdfAttachments.Add(In(pdf), added, new[] { Attachment() });
        Assert.Equal(attached, added.ToArray());
        Assert.Equal("data.csv", Assert.Single(PdfAttachments.List(In(attached))).FileName);
        Assert.Equal(data, PdfAttachments.Extract(In(attached), "data.csv"));
        Assert.Null(PdfAttachments.Extract(In(attached), "missing.csv"));
    }

    [Fact]
    public void PdfEInvoice_reads_from_a_stream()
    {
        var xml = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "einvoice", "en16931.xml"));
        var pdf = HtmlToPdf.Convert("<h1>Invoice</h1>", new PdfOptions { EInvoice = new PdfEInvoice(xml) });
        var invoice = PdfEInvoice.Read(In(pdf));
        Assert.NotNull(invoice);
        Assert.Equal(xml, invoice.Xml);
        Assert.Null(PdfEInvoice.Read(In(ThreePages.Value)));
    }

    [Fact]
    public void PdfStamper_StampFile_honours_the_token()
    {
        WithFolder(dir =>
        {
            var input = Path.Combine(dir, "in.pdf");
            File.WriteAllBytes(input, ThreePages.Value);
            var stamp = Path.Combine(dir, "stamp.pdf");
            File.WriteAllBytes(stamp, HtmlToPdf.Convert("<p>PAID</p>", Options()));
            var output = Path.Combine(dir, "out.pdf");
            Assert.ThrowsAny<OperationCanceledException>(() => PdfStamper.StampFile(input, stamp, output, null, Cancelled()));
            Assert.False(File.Exists(output));
            PdfStamper.StampFile(input, stamp, output, null, CancellationToken.None);
            AssertSamePdf(PdfStamper.Stamp(ThreePages.Value, File.ReadAllBytes(stamp)), File.ReadAllBytes(output));
        });
    }

    [Fact]
    public void PdfOptions_Clone_copies_every_setting_and_leaves_the_original_alone()
    {
        var original = new PdfOptions { Title = "Original", PageSize = PageSize.Letter, MaxPages = 7, Linearize = true };
        var copy = original.Clone();
        foreach (var property in typeof(PdfOptions).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            Assert.Equal(property.GetValue(original), property.GetValue(copy));

        copy.Title = "Copy";
        copy.MaxPages = 9;
        Assert.Equal("Original", original.Title);
        Assert.Equal(7, original.MaxPages);
        // The copy is shallow, as its documentation says: the font collection is the same object.
        Assert.Same(original.Fonts, copy.Fonts);
    }
}
