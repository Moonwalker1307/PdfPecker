using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PdfPecker.Cli.Tests;

public sealed class PdfCommandTests : IDisposable
{
    private readonly CliRunner _cli = new();

    public void Dispose() => _cli.Dispose();

    private string ThreePages(string name = "three.pdf")
    {
        var html = _cli.Html(name + ".html", "<p>Alpha</p><div style=\"break-before:page\">Bravo</div><div style=\"break-before:page\">Charlie</div>");
        var path = _cli.Path_(name);
        _cli.Ok("convert", html, path);
        Assert.Equal(3, PdfPages.Count(File.ReadAllBytes(path)));
        return path;
    }

    [Fact]
    public void MergeWithPageSelection()
    {
        var three = ThreePages();
        var out_ = _cli.Path_("merged.pdf");
        _cli.Ok("merge", three, three + ":2", "-o", out_);
        var text = CliRunner.Text(out_);
        Assert.Equal(4, PdfPages.Count(File.ReadAllBytes(out_)));
        Assert.Contains("Bravo", text);
    }

    [Fact]
    public void MergeReadsLastInAPageSelection()
    {
        var three = ThreePages();
        var out_ = _cli.Path_("tail.pdf");
        _cli.Ok("merge", three + ":2-last", "-o", out_);
        Assert.Equal(2, PdfPages.Count(File.ReadAllBytes(out_)));
        Assert.DoesNotContain("Alpha", CliRunner.Text(out_));
    }

    /// <summary>The number of images in a TIFF file: the length of its chain of image file directories.</summary>
    private static int TiffPages(byte[] tiff)
    {
        bool little = tiff[0] == (byte)'I';
        uint U32(long at) => little ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan((int)at))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan((int)at));
        int U16(long at) => little ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan((int)at))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan((int)at));
        int count = 0;
        for (long ifd = U32(4); ifd != 0 && count < 1000; count++) ifd = U32(ifd + 2 + 12L * U16(ifd));
        return count;
    }

    [Fact]
    public void MergeRefusesToOverwriteAnInputAndNeedsAnOutput()
    {
        var three = ThreePages();
        Assert.Equal(2, _cli.Run("merge", three, three, "-o", three).Exit);
        Assert.Equal(2, _cli.Run("merge", three, three).Exit);
    }

    [Theory]
    [InlineData("C:\\docs\\a.pdf", "C:\\docs\\a.pdf", null)]
    [InlineData("a.pdf:1-3,5", "a.pdf", "1-3,5")]
    [InlineData("a.pdf:2-last", "a.pdf", "2-last")]
    [InlineData("a.pdf:last-1", "a.pdf", "last-1")]
    [InlineData("a.pdf:1, 3", "a.pdf", "1, 3")]
    [InlineData("notes:draft.pdf", "notes:draft.pdf", null)]
    [InlineData("a.pdf", "a.pdf", null)]
    public void PageSelectionSyntax(string arg, string path, string? pages)
    {
        var (p, r) = PageCommands.SplitPages(arg);
        Assert.Equal(path, p);
        Assert.Equal(pages, r);
    }

    [Fact]
    public void SplitIntoFilesOfOnePageOrOfRanges()
    {
        var three = ThreePages();
        _cli.Ok("split", three, "--out-dir", _cli.Path_("parts"));
        Assert.Equal(3, Directory.GetFiles(_cli.Path_("parts"), "three-*.pdf").Length);

        _cli.Ok("split", three, "--range", "1-2", "--range", "3", "--out-dir", _cli.Path_("ranges"), "--prefix", "r");
        Assert.Equal(2, PdfPages.Count(File.ReadAllBytes(_cli.Path_("ranges/r-001.pdf"))));
        Assert.Equal(1, PdfPages.Count(File.ReadAllBytes(_cli.Path_("ranges/r-002.pdf"))));
        Assert.Equal(2, _cli.Run("split", three, "--every", "0").Exit);
    }

    [Fact]
    public void ExtractRemoveAndRotate()
    {
        var three = ThreePages();
        _cli.Ok("extract", three, _cli.Path_("e.pdf"), "--pages", "2-3");
        Assert.Equal(2, PdfPages.Count(File.ReadAllBytes(_cli.Path_("e.pdf"))));
        Assert.DoesNotContain("Alpha", CliRunner.Text(_cli.Path_("e.pdf")));

        _cli.Ok("remove", three, _cli.Path_("r.pdf"), "--pages", "2");
        Assert.Equal(2, PdfPages.Count(File.ReadAllBytes(_cli.Path_("r.pdf"))));
        Assert.DoesNotContain("Bravo", CliRunner.Text(_cli.Path_("r.pdf")));

        _cli.Ok("rotate", three, _cli.Path_("rot.pdf"), "--degrees", "90", "--pages", "1");
        Assert.Contains("/Rotate 90", Encoding.Latin1.GetString(File.ReadAllBytes(_cli.Path_("rot.pdf"))));

        Assert.Equal(2, _cli.Run("extract", three, _cli.Path_("x.pdf")).Exit);
        Assert.Equal(2, _cli.Run("extract", three, three, "--pages", "1").Exit);
    }

    [Fact]
    public void EncryptThenDecrypt()
    {
        var three = ThreePages();
        _cli.Ok("encrypt", three, _cli.Path_("enc.pdf"), "--user-password", "secret", "--allow", "print");
        Assert.True(PdfProtector.IsEncrypted(File.ReadAllBytes(_cli.Path_("enc.pdf"))));

        Assert.Equal(1, _cli.Run("decrypt", _cli.Path_("enc.pdf"), _cli.Path_("bad.pdf"), "--password", "wrong").Exit);
        _cli.Ok("decrypt", _cli.Path_("enc.pdf"), _cli.Path_("dec.pdf"), "--password", "secret");
        Assert.False(PdfProtector.IsEncrypted(File.ReadAllBytes(_cli.Path_("dec.pdf"))));
        Assert.Contains("Alpha", CliRunner.Text(_cli.Path_("dec.pdf")));

        Environment.SetEnvironmentVariable("PDFPECKER_TEST_PW", "secret");
        try
        {
            _cli.Ok("text", _cli.Path_("enc.pdf"), _cli.Path_("t.txt"), "--password-env", "PDFPECKER_TEST_PW");
            Assert.Contains("Alpha", File.ReadAllText(_cli.Path_("t.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDFPECKER_TEST_PW", null);
        }

        Assert.Equal(2, _cli.Run("encrypt", three, _cli.Path_("n.pdf")).Exit);
    }

    [Fact]
    public void TextGoesToStandardOutputByDefault()
    {
        var r = _cli.Ok("text", ThreePages(), "--pages", "2");
        Assert.Contains("Bravo", r.OutText);
        Assert.DoesNotContain("Alpha", r.OutText);
        Assert.EndsWith("\n", r.OutText);
    }

    [Fact]
    public void InfoCompactLinearizeAndSanitize()
    {
        var three = ThreePages();
        var info = _cli.Ok("info", three).OutText;
        Assert.Contains("pages:     3", info);
        Assert.Contains("encrypted: no", info);

        _cli.Ok("compact", three, _cli.Path_("c.pdf"), "--remove-metadata");
        _cli.Ok("linearize", three, _cli.Path_("l.pdf"));
        _cli.Ok("sanitize", three, _cli.Path_("s.pdf"));
        Assert.Equal(3, PdfPages.Count(File.ReadAllBytes(_cli.Path_("c.pdf"))));
        Assert.Equal(3, PdfPages.Count(File.ReadAllBytes(_cli.Path_("l.pdf"))));
        Assert.Equal(0, _cli.Run("sanitize", three, "--check").Exit);
    }

    [Fact]
    public void ToImageWritesOneFilePerPage()
    {
        var three = ThreePages();
        _cli.Ok("to-image", three, _cli.Path_("img"), "--dpi", "40", "--pages", "1,3");
        Assert.True(File.Exists(_cli.Path_("img/three.pdf-001.png")) || Directory.GetFiles(_cli.Path_("img"), "*-001.png").Length == 1);
        Assert.Equal(2, Directory.GetFiles(_cli.Path_("img"), "*.png").Length);
        var png = File.ReadAllBytes(Directory.GetFiles(_cli.Path_("img"), "*.png")[0]);
        Assert.Equal(0x89, png[0]);

        _cli.Ok("to-image", three, _cli.Path_("tiff"), "--format", "tiff", "--dpi", "40");
        Assert.Single(Directory.GetFiles(_cli.Path_("tiff")));
        Assert.Equal(3, TiffPages(File.ReadAllBytes(Directory.GetFiles(_cli.Path_("tiff"))[0])));
        _cli.Ok("to-image", three, _cli.Path_("tiff2"), "--format", "tiff", "--dpi", "40", "--pages", "2-last");
        Assert.Equal(2, TiffPages(File.ReadAllBytes(Directory.GetFiles(_cli.Path_("tiff2"))[0])));
        Assert.Equal(2, _cli.Run("to-image", three, "--format", "bmp").Exit);
    }

    [Fact]
    public void ToHtmlInBothModes()
    {
        var three = ThreePages();
        _cli.Ok("to-html", three, _cli.Path_("v.html"));
        _cli.Ok("to-html", three, _cli.Path_("s.html"), "--mode", "structured");
        Assert.Contains("Bravo", File.ReadAllText(_cli.Path_("s.html")));
        Assert.Contains("<html", File.ReadAllText(_cli.Path_("v.html")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StampAPdfAndAnHtmlPage()
    {
        var three = ThreePages();
        var mark = _cli.Html("mark.html", "<p style=\"font-size:40px\">DRAFT</p>");
        _cli.Ok("stamp", three, mark, _cli.Path_("stamped.pdf"), "--pages", "1", "--opacity", "0.5", "--position", "top-right");
        // Only the first page is stamped: its text gains the stamp's letters and the others are as they were.
        var before = PdfText.ExtractPages(File.ReadAllBytes(three));
        var after = PdfText.ExtractPages(File.ReadAllBytes(_cli.Path_("stamped.pdf")));
        Assert.NotEqual(before[0].Text, after[0].Text);
        Assert.Contains("DRA", after[0].Text);
        Assert.Equal(before[1].Text, after[1].Text);

        var stampPdf = _cli.Path_("stamp.pdf");
        _cli.Ok("convert", mark, stampPdf);
        _cli.Ok("stamp", three, stampPdf, _cli.Path_("s2.pdf"), "--underlay");
        Assert.Equal(3, PdfPages.Count(File.ReadAllBytes(_cli.Path_("s2.pdf"))));
        Assert.Equal(2, _cli.Run("stamp", three, stampPdf, _cli.Path_("s3.pdf"), "--position", "middle").Exit);

        // An encrypted stamp opens with a password from the environment.
        _cli.Ok("convert", mark, _cli.Path_("locked.pdf"), "--user-password", "stamp-pw");
        Environment.SetEnvironmentVariable("PDFPECKER_TEST_STAMP_PW", "stamp-pw");
        try
        {
            _cli.Ok("stamp", three, _cli.Path_("locked.pdf"), _cli.Path_("s4.pdf"), "--stamp-password-env", "PDFPECKER_TEST_STAMP_PW");
            Assert.Contains("DRA", PdfText.ExtractPages(File.ReadAllBytes(_cli.Path_("s4.pdf")))[0].Text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDFPECKER_TEST_STAMP_PW", null);
        }
    }

    [Fact]
    public void SignThenVerify()
    {
        var three = ThreePages();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test Signer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllBytes(_cli.Path_("me.pfx"), cert.Export(X509ContentType.Pfx, "pw"));
        File.WriteAllBytes(_cli.Path_("me.cer"), cert.Export(X509ContentType.Cert));

        var signed = _cli.Path_("signed.pdf");
        Environment.SetEnvironmentVariable("PDFPECKER_CERT_PASSWORD", "pw");
        try
        {
            _cli.Ok("sign", three, signed, "--cert", _cli.Path_("me.pfx"), "--reason", "Approved", "--page", "1", "--rect", "36,36,200,60");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDFPECKER_CERT_PASSWORD", null);
        }

        // Without the certificate as a trusted root the chain is not trusted, so the verdict is negative but the report works.
        var untrusted = _cli.Run("verify", signed);
        Assert.Equal(1, untrusted.Exit);
        Assert.Contains("Test Signer", untrusted.OutText);

        var trusted = _cli.Run("verify", signed, "--trusted-root", _cli.Path_("me.cer"));
        Assert.Equal(0, trusted.Exit);
        Assert.Contains("VALID", trusted.OutText);

        Assert.Equal(1, _cli.Run("verify", three).Exit);
        Assert.Equal(2, _cli.Run("sign", three, _cli.Path_("x.pdf")).Exit);
        Assert.Equal(1, _cli.Run("sign", three, _cli.Path_("x.pdf"), "--cert", _cli.Path_("me.pfx"), "--cert-password", "wrong").Exit);
    }

    [Fact]
    public void OutputThatIsTheInputIsRefused()
    {
        var three = ThreePages();
        Assert.Equal(2, _cli.Run("rotate", three, three, "--degrees", "90").Exit);
    }
}
