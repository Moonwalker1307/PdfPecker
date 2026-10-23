using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

/// <summary>Linearized output (Fast Web View): <see cref="PdfOptions.Linearize"/> and <see cref="PdfLinearizer"/>.</summary>
public class LinearizationTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>Noise that Flate cannot shrink, so an image's size shows which section of the file holds it.</summary>
    private static byte[] Noise(int seed)
    {
        var random = new Random(seed);
        return PdfTestHelpers.PixelPng(120, 120, (_, _) => ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
    }

    private static readonly string Html = "<html lang=\"en\"><head><title>Linearized</title></head><body>"
        + "<h1>Chapter one</h1><p>First page with a <a href=\"#two\">link</a>.</p><p>Name <input type=\"text\" name=\"name\" value=\"Jane\"></p>"
        + "<h1 id=\"two\" style=\"page-break-before:always\">Chapter two</h1><img src=\"" + PdfTestHelpers.DataUri(Noise(1)) + "\">"
        + "<h1 style=\"page-break-before:always\">Chapter three</h1><img src=\"" + PdfTestHelpers.DataUri(Noise(2)) + "\">"
        + "<p style=\"opacity:.5\">Translucent text that needs a transparency group.</p></body></html>";

    private static PdfOptions Options(bool linearize) => new()
    {
        Linearize = linearize,
        CreationDate = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
    };

    private static uint[][] Render(byte[] pdf, string? password = null)
    {
        var r = PdfRenderer.Open(pdf, password);
        return Enumerable.Range(0, r.PageCount).Select(i => r.RenderPage(i, new ImageOptions { Dpi = 36 }).Premultiplied).ToArray();
    }

    private static void AssertSameRendering(byte[] expected, byte[] actual, string? password = null)
    {
        var a = Render(expected, password);
        var b = Render(actual, password);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.True(a[i].AsSpan().SequenceEqual(b[i]), $"page {i + 1} renders differently");
    }

    [Fact]
    public void Linearized_conversion_is_valid_and_renders_the_same()
    {
        var plain = HtmlToPdf.Convert(Html, Options(false));
        var linear = HtmlToPdf.Convert(Html, Options(true));
        var layout = AssertLinearized(linear, pages: 3);
        AssertSameRendering(plain, linear);
        Assert.Equal(PdfText.Extract(plain), PdfText.Extract(linear));

        // The images of pages two and three are not part of the first page section: each page has its own resources.
        Assert.True(layout.FirstPageEnd < linear.Length / 2, $"The first page section ends at {layout.FirstPageEnd} of {linear.Length} bytes.");
        // The fields, link and bookmarks are still there.
        var text = PdfTestHelpers.Latin1(linear);
        Assert.Contains("/AcroForm", text);
        Assert.Contains("/Outlines", text);
        Assert.Contains("/Subtype /Link", text);
    }

    [Fact]
    public void Output_stays_the_same_without_the_option_and_is_reproducible_with_it()
    {
        var plain = HtmlToPdf.Convert(Html, Options(false));
        Assert.DoesNotContain("/Linearized", PdfTestHelpers.Latin1(plain));
        Assert.Equal(HtmlToPdf.Convert(Html, Options(true)), HtmlToPdf.Convert(Html, Options(true)));
    }

    [Fact]
    public void Linearized_files_can_be_encrypted()
    {
        var options = Options(true);
        options.Security = new PdfSecurity { UserPassword = "open", OwnerPassword = "admin" };
        var linear = HtmlToPdf.Convert(Html, options);
        AssertLinearized(linear, pages: 3, hintStreamEncrypted: true);
        Assert.True(PdfProtector.IsEncrypted(linear));
        Assert.Throws<PdfPasswordException>(() => PdfText.Extract(linear));
        Assert.Contains("Chapter three", PdfText.Extract(linear, new PdfTextOptions { Password = "open" }));
        options.Linearize = false;
        AssertSameRendering(HtmlToPdf.Convert(Html, options), linear, "open");
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2b, PdfAccessibility.None, "%PDF-1.7")]
    [InlineData(PdfConformance.None, PdfAccessibility.PdfUA1, "%PDF-1.7")]
    [InlineData(PdfConformance.PdfA4, PdfAccessibility.PdfUA2, "%PDF-2.0")]
    public void Linearized_files_keep_pdfa_and_pdfua(PdfConformance conformance, PdfAccessibility accessibility, string header)
    {
        var options = Options(true);
        options.Conformance = conformance;
        options.Accessibility = accessibility;
        var linear = HtmlToPdf.Convert(Html, options);
        AssertLinearized(linear, pages: 3);
        var text = PdfTestHelpers.Latin1(linear);
        Assert.StartsWith(header, text);
        Assert.Contains("/Metadata", text);
        if (accessibility == PdfAccessibility.None) return;
        Assert.Contains("/StructTreeRoot", text);
        // Literal strings stay literals: veraPDF compares a hex /Alt with an annotation's /Contents by its spelling.
        Assert.Contains("/Lang (en)", text);
    }

    [Fact]
    public void Linearize_and_signature_together_are_refused()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var options = Options(true);
        options.Signature = new PdfSignatureOptions(cert);
        Assert.Contains("incremental update", Assert.Throws<ArgumentException>(() => HtmlToPdf.Convert(Html, options)).Message);
    }

    [Fact]
    public void Linearizes_merged_and_third_party_files()
    {
        var merged = PdfMerger.Merge(HtmlToPdf.Convert(Html, Options(false)), Fixture("features.pdf"), Fixture("objstm-cairo.pdf"));
        var linear = PdfLinearizer.Linearize(merged);
        AssertLinearized(linear, pages: PdfRenderer.Open(merged).PageCount);
        AssertSameRendering(merged, linear);

        foreach (var name in new[] { "third-party/chromium.pdf", "third-party/libreoffice.pdf", "third-party/reportlab.pdf", "third-party/ghostscript.pdf" })
        {
            var source = Fixture(name);
            var result = PdfLinearizer.Linearize(source);
            AssertLinearized(result, pages: PdfRenderer.Open(source).PageCount);
            AssertSameRendering(source, result);
        }

        // The stream overload gives the same bytes, and the result is a function of the input.
        using var output = new MemoryStream();
        PdfLinearizer.Linearize(new MemoryStream(merged), output);
        Assert.Equal(linear, output.ToArray());
        Assert.Equal(linear, PdfLinearizer.Linearize(merged));
    }

    [Fact]
    public void Refuses_signed_encrypted_and_unreadable_files()
    {
        var source = HtmlToPdf.Convert(Html, Options(false));
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        Assert.Contains("signed", Assert.Throws<ArgumentException>(() => PdfLinearizer.Linearize(PdfSigner.Sign(source, new PdfSignatureOptions(cert)))).Message);
        var encrypted = PdfProtector.Encrypt(source, new PdfSecurity { UserPassword = "open", OwnerPassword = "admin" });
        Assert.Contains("encrypted", Assert.Throws<ArgumentException>(() => PdfLinearizer.Linearize(encrypted)).Message);
        Assert.Throws<InvalidDataException>(() => PdfLinearizer.Linearize(Encoding.ASCII.GetBytes("not a pdf at all")));
        Assert.Throws<ArgumentNullException>(() => PdfLinearizer.Linearize((byte[])null!));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => PdfLinearizer.Linearize(source, cancelled.Token));
    }

    // ---- what a linearized file must look like (PDF 1.7 annex F) ----

    internal sealed record Layout(long FirstPageEnd);

    /// <summary>
    /// Checks the structure qpdf's <c>--check-linearization</c> checks: the linearization dictionary first, its /L, /O,
    /// /E, /N, /T and /H values, both cross-reference tables and the page offsets and lengths of the hint stream.
    /// </summary>
    internal static Layout AssertLinearized(byte[] pdf, int pages, bool hintStreamEncrypted = false)
    {
        var text = PdfTestHelpers.Latin1(pdf);
        var lin = Regex.Match(text, @"^%PDF-\d\.\d\n%[^\n]*\n(\d+) 0 obj\n<< /Linearized 1 /L (\d+) /H \[(\d+) (\d+)\] /O (\d+) /E (\d+) /N (\d+) /T (\d+) *>>\nendobj\n");
        Assert.True(lin.Success, "No linearization dictionary at the start of the file.");
        long L = long.Parse(lin.Groups[2].Value), hOffset = long.Parse(lin.Groups[3].Value), hLength = long.Parse(lin.Groups[4].Value);
        int firstPage = int.Parse(lin.Groups[5].Value), n = int.Parse(lin.Groups[7].Value);
        long E = long.Parse(lin.Groups[6].Value), T = long.Parse(lin.Groups[8].Value);
        Assert.Equal(pdf.Length, L);
        Assert.Equal(pages, n);

        // The first table, right after the dictionary, lists the first half; its trailer leads to the main table.
        int firstXref = lin.Length + lin.Index;
        var head = Regex.Match(text.Substring(firstXref), @"^xref\n(\d+) (\d+)\n");
        Assert.True(head.Success, "No cross-reference table after the linearization dictionary.");
        int firstNum = int.Parse(head.Groups[1].Value), firstCount = int.Parse(head.Groups[2].Value);
        var offsets = new Dictionary<int, long>();
        for (int i = 0; i < firstCount; i++) offsets[firstNum + i] = long.Parse(text.Substring(firstXref + head.Length + 20 * i, 10));
        var trailer = Regex.Match(text.Substring(firstXref + head.Length + 20 * firstCount), @"^trailer\n<< /Size (\d+) .*?/Prev (\d+) *>>\nstartxref\n0\n%%EOF\n");
        Assert.True(trailer.Success, "The first trailer has no /Prev.");
        Assert.Equal(firstNum + firstCount, int.Parse(trailer.Groups[1].Value));
        int mainXref = int.Parse(trailer.Groups[2].Value);
        var mainHead = Regex.Match(text.Substring(mainXref), @"^xref\n0 (\d+)\n");
        Assert.True(mainHead.Success, "/Prev does not lead to the main table.");
        Assert.Equal(firstNum, int.Parse(mainHead.Groups[1].Value));
        Assert.Equal(mainXref + mainHead.Length - 1, T);
        Assert.Equal("0000000000 65535 f ", text.Substring((int)T + 1, 19));
        for (int i = 1; i < firstNum; i++) offsets[i] = long.Parse(text.Substring(mainXref + mainHead.Length + 20 * i, 10));
        Assert.EndsWith("startxref\n" + firstXref + "\n%%EOF\n", text);
        foreach (var (num, offset) in offsets) Assert.StartsWith(num + " 0 obj\n", text.Substring((int)offset, 20));

        // Objects lie one after another, so each one's length is the distance to the next.
        var starts = offsets.Values.Append(firstXref).Append(mainXref).OrderBy(o => o).ToList();
        long Next(long offset) => starts[starts.IndexOf(offset) + 1];
        long Length(int num) => Next(offsets[num]) - offsets[num];

        Assert.Contains(hOffset, offsets.Values);
        Assert.Equal(hLength, Next(hOffset) - hOffset);
        Assert.True(offsets[firstPage] > hOffset && offsets[firstPage] < E, "The first page object is not in the first page section.");
        Assert.Contains("/Type /Page", text.Substring((int)offsets[firstPage], (int)Length(firstPage)));
        Assert.Equal(firstNum > 1 ? offsets[1] : mainXref, E);
        if (hintStreamEncrypted) return new Layout(E);

        // The page offset hint table: each page's objects start at its page object and fill the space up to the next page.
        var hint = text.Substring((int)hOffset, (int)hLength);
        var data = pdf.AsSpan((int)hOffset + hint.IndexOf("stream\n", StringComparison.Ordinal) + 7);
        using var z = new ZLibStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
        var decoded = new MemoryStream();
        z.CopyTo(decoded);
        var bits = new BitReader(decoded.ToArray());
        long minObjects = bits.Read(32), firstPageOffset = bits.Read(32);
        int nbObjects = (int)bits.Read(16);
        long minLength = bits.Read(32);
        int nbLength = (int)bits.Read(16);
        bits.Read(32 + 16 + 32 + 16);
        int nbShared = (int)bits.Read(16), nbIdentifier = (int)bits.Read(16);
        bits.Read(32);
        Assert.Equal(offsets[firstPage], firstPageOffset + hLength); // hint offsets leave the hint stream out
        var counts = Enumerable.Range(0, n).Select(_ => minObjects + bits.Read(nbObjects)).ToList();
        bits.Align();
        var lengths = Enumerable.Range(0, n).Select(_ => minLength + bits.Read(nbLength)).ToList();
        bits.Align();
        var shared = Enumerable.Range(0, n).Select(_ => bits.Read(nbShared)).ToList();
        Assert.Equal(0, shared[0]);
        // The first page's objects are numbered from its page object, the later pages' from 1.
        int pageNum = firstPage;
        for (int p = 0; p < n; p++)
        {
            long sum = 0;
            for (int k = 0; k < counts[p]; k++) sum += Length(pageNum + k);
            Assert.Equal(lengths[p], sum);
            if (p == 0) Assert.Equal(E, offsets[firstPage] + sum);
            pageNum = p == 0 ? 1 : pageNum + (int)counts[p];
        }
        Assert.True(nbIdentifier > 0 || n == 1, "Later pages name no shared objects.");
        return new Layout(E);
    }

    private sealed class BitReader(byte[] data)
    {
        private int _bit;

        public long Read(int count)
        {
            long v = 0;
            for (int i = 0; i < count; i++, _bit++) v = (v << 1) | (uint)((data[_bit >> 3] >> (7 - (_bit & 7))) & 1);
            return v;
        }

        public void Align() => _bit = (_bit + 7) & ~7;
    }
}

/// <summary>Damaged files given to <see cref="PdfLinearizer"/>.</summary>
[Collection(HostileInputCollection.Name)]
public class LinearizerHostileInputTests
{
    [Fact]
    public void A_damaged_page_tree_fails_with_a_documented_exception()
    {
        // The catalog and the document information listed as pages, a page twice and a page tree that loops: the result is
        // either a valid linearized file or InvalidDataException, never a broken file or an undocumented exception.
        const string pdf = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R /OpenAction [3 0 R /Fit] >>\nendobj\n"
            + "2 0 obj\n<< /Type /Pages /Kids [1 0 R 3 0 R 3 0 R 4 0 R 2 0 R] /Count 4 >>\nendobj\n"
            + "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>\nendobj\n"
            + "4 0 obj\n<< /Type /Page /Title (Info) /MediaBox [0 0 50 50] >>\nendobj\n"
            + "trailer\n<< /Root 1 0 R /Info 4 0 R >>\n%%EOF\n";
        var result = PdfLinearizer.Linearize(Encoding.Latin1.GetBytes(pdf));
        LinearizationTests.AssertLinearized(result, pages: 2);
        Assert.Throws<InvalidDataException>(() => PdfLinearizer.Linearize(Encoding.Latin1.GetBytes(pdf.Replace("[1 0 R 3 0 R 3 0 R 4 0 R 2 0 R]", "[1 0 R]"))));
    }

    [Fact]
    public void Document_information_that_is_the_page_tree_is_left_out()
    {
        // A fuzzer finding: /Info named the page tree node, which the linearized file replaces with a node of its own.
        const string pdf = "1 0 obj<<///Pages 2 0 R 2 0 obj<<///Kids[3 0 R\n3 0<<(trailer<</Root 1 0 R/Info 2 0 R";
        try
        {
            LinearizationTests.AssertLinearized(PdfLinearizer.Linearize(Encoding.Latin1.GetBytes(pdf)), pages: 1);
        }
        catch (InvalidDataException)
        {
        }
    }
}
