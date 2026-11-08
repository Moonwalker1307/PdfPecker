using System.Text;

namespace PdfPecker.Cli.Tests;

public sealed class ConvertTests : IDisposable
{
    private readonly CliRunner _cli = new();

    public void Dispose() => _cli.Dispose();

    [Fact]
    public void ConvertWritesAPdfNextToTheInput()
    {
        var html = _cli.Html("in.html", "<h1>Hello</h1>");
        var r = _cli.Ok("convert", html);
        Assert.Contains("Wrote", r.Err);
        Assert.Contains("Hello", CliRunner.Text(_cli.Path_("in.pdf")));
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(File.ReadAllBytes(_cli.Path_("in.pdf")), 0, 5));
    }

    [Fact]
    public void MarkdownIsRecognisedByExtension()
    {
        var md = _cli.Write("notes.md", "# Heading\n\nSome *text*.\n");
        _cli.Ok("convert", md, _cli.Path_("out.pdf"));
        var text = CliRunner.Text(_cli.Path_("out.pdf"));
        Assert.Contains("Heading", text);
        Assert.Contains("Some text.", text);
    }

    [Fact]
    public void StandardInputToStandardOutput()
    {
        var r = _cli.RunWithInput(Encoding.UTF8.GetBytes("<p>From stdin</p>"), CancellationToken.None, "convert", "-", "-");
        Assert.Equal(0, r.Exit);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(r.Out, 0, 5));
        Assert.Contains("From stdin", PdfText.Extract(r.Out));
    }

    [Fact]
    public void StandardInputReadsLocalFilesOnlyFromTheBaseDirectory()
    {
        // The current folder is wherever the tool runs, so piped HTML may not read a file there; --base-dir lets it.
        var name = "stdin-" + Guid.NewGuid().ToString("N") + ".css";
        var here = Path.Combine(Directory.GetCurrentDirectory(), name);
        File.WriteAllText(here, "p::after { content: 'from the current folder' }");
        _cli.Write(name, "p::after { content: 'from the base folder' }");
        try
        {
            var html = Encoding.UTF8.GetBytes($"<link rel=\"stylesheet\" href=\"{name}\"><p>Piped</p>");
            var plain = _cli.RunWithInput(html, CancellationToken.None, "convert", "-", "-");
            Assert.Equal(0, plain.Exit);
            Assert.DoesNotContain("folder", PdfText.Extract(plain.Out));

            var based = _cli.RunWithInput(html, CancellationToken.None, "convert", "-", "-", "--base-dir", _cli.Dir);
            Assert.Contains("from the base folder", PdfText.Extract(based.Out));
        }
        finally
        {
            File.Delete(here);
        }
    }

    [Fact]
    public void PasswordsCanComeFromTheEnvironment()
    {
        var html = _cli.Html("in.html", "<p>secret page</p>");
        Environment.SetEnvironmentVariable("PDFPECKER_TEST_CONVERT_PW", "pw");
        try
        {
            _cli.Ok("convert", html, _cli.Path_("e.pdf"), "--user-password-env", "PDFPECKER_TEST_CONVERT_PW");
            var pdf = File.ReadAllBytes(_cli.Path_("e.pdf"));
            Assert.True(PdfProtector.IsEncrypted(pdf));
            Assert.Contains("secret page", PdfText.Extract(PdfProtector.Decrypt(pdf, "pw")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDFPECKER_TEST_CONVERT_PW", null);
        }
        Assert.Equal(2, _cli.Run("convert", html, _cli.Path_("f.pdf"), "--owner-password-env", "PDFPECKER_TEST_UNSET_VARIABLE").Exit);
    }

    [Fact]
    public void ReadingStandardInputNeedsAnOutput()
    {
        var r = _cli.Run("convert", "-");
        Assert.Equal(2, r.Exit);
    }

    [Fact]
    public void SeveralInputsAreJoined()
    {
        var a = _cli.Html("a.html", "<p>First file</p>");
        var b = _cli.Html("b.html", "<p>Second file</p>");
        _cli.Ok("convert", a, b, _cli.Path_("both.pdf"));
        var pdf = File.ReadAllBytes(_cli.Path_("both.pdf"));
        Assert.Equal(2, PdfPages.Count(pdf));
        var text = PdfText.Extract(pdf);
        Assert.Contains("First file", text);
        Assert.Contains("Second file", text);
    }

    [Fact]
    public void SeveralInputsGetTheWholeFileOptionsAfterTheyAreJoined()
    {
        var a = _cli.Html("a.html", "<p>First file</p>");
        var b = _cli.Html("b.html", "<p>Second file</p>");

        _cli.Ok(a, b, _cli.Path_("owner.pdf"), "--owner-password", "own");
        Assert.True(PdfProtector.IsEncrypted(File.ReadAllBytes(_cli.Path_("owner.pdf"))));

        _cli.Ok(a, b, _cli.Path_("user.pdf"), "--user-password", "usr");
        var user = File.ReadAllBytes(_cli.Path_("user.pdf"));
        Assert.True(PdfProtector.IsEncrypted(user));
        Assert.Throws<PdfPasswordException>(() => PdfPages.Count(user));
        Assert.Equal(2, PdfPages.Count(user, "usr"));

        _cli.Ok(a, b, _cli.Path_("pdfa.pdf"), "--pdfa", "3b");
        var pdfa = Encoding.Latin1.GetString(File.ReadAllBytes(_cli.Path_("pdfa.pdf")));
        Assert.Contains("/OutputIntents", pdfa);
        Assert.Contains("pdfaid", pdfa);

        _cli.Ok(a, b, _cli.Path_("fast.pdf"), "--linearize", "--user-password", "usr");
        var fast = File.ReadAllBytes(_cli.Path_("fast.pdf"));
        Assert.Contains("/Linearized", Encoding.Latin1.GetString(fast, 0, 1024));
        Assert.True(PdfProtector.IsEncrypted(fast));

        // A fixed date makes the joined file reproducible, with that date and a file ID that is not random.
        _cli.Ok(a, b, _cli.Path_("d1.pdf"), "--creation-date", "2026-01-01T00:00:00Z");
        _cli.Ok(a, b, _cli.Path_("d2.pdf"), "--creation-date", "2026-01-01T00:00:00Z");
        var d1 = File.ReadAllBytes(_cli.Path_("d1.pdf"));
        Assert.Equal(d1, File.ReadAllBytes(_cli.Path_("d2.pdf")));
        Assert.Contains("D:20260101000000", Encoding.Latin1.GetString(d1));
    }

    [Fact]
    public void SeveralInputsRefuseWhatTheJoinedFileCannotHave()
    {
        var a = _cli.Html("a.html", "<p>First file</p>");
        var b = _cli.Html("b.html", "<p>Second file</p>");
        var pdfa4 = _cli.Run(a, b, _cli.Path_("x.pdf"), "--pdfa", "4");
        Assert.Equal(2, pdfa4.Exit);
        Assert.Contains("single input", pdfa4.Err);
        Assert.Equal(2, _cli.Run(a, b, _cli.Path_("x.pdf"), "--accessibility", "ua2").Exit);
        Assert.False(File.Exists(_cli.Path_("x.pdf")));
        // A single input still has every level.
        _cli.Ok(a, _cli.Path_("one.pdf"), "--pdfa", "4");
    }

    [Fact]
    public void PageTokensCountTheWholeOutputWithSeveralInputs()
    {
        var a = _cli.Html("a.html", "<p>One</p><div style=\"break-before:page\">Two</div>", "Q&amp;A &lt;draft&gt;");
        var b = _cli.Html("b.html", "<p>One</p><div style=\"break-before:page\">Two</div><div style=\"break-before:page\">Three</div>", "Second");
        _cli.Ok(a, b, _cli.Path_("all.pdf"), "--footer-center", "[page]/[topage] ([sitepage] of [sitepages])", "--header-left", "[title] in [doctitle]");
        var text = CliRunner.Text(_cli.Path_("all.pdf"));
        Assert.Contains("1/5 (1 of 2)", text);
        Assert.Contains("2/5 (2 of 2)", text);
        Assert.Contains("3/5 (1 of 3)", text);
        Assert.Contains("5/5 (3 of 3)", text);
        Assert.Contains("Q&A <draft> in Q&A <draft>", text);
        Assert.Contains("Second in Q&A <draft>", text);

        // Only [page] needs no total: the first input keeps its own conversion, the second counts on from it.
        _cli.Ok(a, b, _cli.Path_("pages.pdf"), "--footer-right", "p. [page]");
        var pages = CliRunner.Text(_cli.Path_("pages.pdf"));
        Assert.Contains("p. 2", pages);
        Assert.Contains("p. 5", pages);
    }

    [Fact]
    public void AnOutputThatIsNotAPdfNameIsRefusedSoASourceIsNotOverwritten()
    {
        var a = _cli.Html("a.html", "<p>A</p>");
        var b = _cli.Html("b.html", "<p>B</p>");
        var r = _cli.Run("convert", a, b);
        Assert.Equal(2, r.Exit);
        Assert.Contains("<p>B</p>", File.ReadAllText(b));
    }

    [Fact]
    public void WebAddressesAreRefused()
    {
        var r = _cli.Run("convert", "https://example.com/page.html", _cli.Path_("x.pdf"));
        Assert.Equal(2, r.Exit);
        Assert.Contains("never fetches", r.Err);
        Assert.False(File.Exists(_cli.Path_("x.pdf")));
    }

    [Fact]
    public void ARelativeImageNextToTheInputIsFound()
    {
        // A 1x1 PNG.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        File.WriteAllBytes(_cli.Path_("dot.png"), png);
        var html = _cli.Html("img.html", "<img src=\"dot.png\" width=\"50\" height=\"50\">");
        _cli.Ok("convert", html);
        Assert.True(PdfImages.Extract(File.ReadAllBytes(_cli.Path_("img.pdf"))).Count >= 1);
    }

    [Fact]
    public void MissingInputFailsWithExitCodeOne()
    {
        var r = _cli.Run("convert", _cli.Path_("nope.html"));
        Assert.Equal(1, r.Exit);
        Assert.StartsWith("error:", r.Err);
    }

    [Fact]
    public void UnknownOptionsAndCommandsAreUsageErrors()
    {
        Assert.Equal(2, _cli.Run("convert", "--frobnicate", "x.html").Exit);
        Assert.Equal(2, _cli.Run("frobnicate").Exit);
        Assert.Equal(2, _cli.Run().Exit);
    }

    [Fact]
    public void HelpAndVersionPrintToStandardOutput()
    {
        var help = _cli.Ok("help");
        Assert.Contains("wkhtmltopdf", help.OutText);
        Assert.Contains("--footer-left/-center/-right", _cli.Ok("help", "convert").OutText);
        Assert.Contains("--every", _cli.Ok("split", "--help").OutText);
        Assert.StartsWith("pdfpecker ", _cli.Ok("--version").OutText);
    }

    [Theory]
    [InlineData("A4", 595.28, 841.89)]
    [InlineData("Letter", 612, 792)]
    [InlineData("B5", 498.9, 708.7)]
    [InlineData("100x150mm", 283.46, 425.2)]
    [InlineData("4inx6in", 288, 432)]
    [InlineData("800pxx600px", 600, 450)]
    [InlineData("800x600px", 600, 450)]
    public void PageSizes(string name, double width, double height)
    {
        var size = Units.ParsePageSize(name);
        Assert.Equal(width, size.Width, 1);
        Assert.Equal(height, size.Height, 1);
    }

    [Theory]
    [InlineData("10mm", 28.35)]
    [InlineData("10", 28.35)]
    [InlineData("1in", 72)]
    [InlineData("2.54cm", 72)]
    [InlineData("36pt", 36)]
    [InlineData("96px", 72)]
    public void Lengths(string text, double points) => Assert.Equal(points, Units.ParseLength(text, "margin"), 0.1);

    [Fact]
    public void NonsenseLengthsAndSizesAreUsageErrors()
    {
        Assert.Throws<UsageException>(() => Units.ParseLength("wide", "margin"));
        Assert.Throws<UsageException>(() => Units.ParsePageSize("A99"));
    }

    private (double W, double H) MediaBox(string pdfPath)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(pdfPath));
        var m = System.Text.RegularExpressions.Regex.Match(text, @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]");
        Assert.True(m.Success, "no MediaBox");
        return (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void WkhtmltopdfStyleCallSetsSizeOrientationAndIgnoresBrowserOptions()
    {
        var html = _cli.Html("in.html", "<p>x</p>");
        var r = _cli.Ok("-s", "Letter", "-O", "Landscape", "-T", "15mm", "--javascript-delay", "100", "--cookie", "a", "b", "--grayscale", html, _cli.Path_("wk.pdf"));
        Assert.Contains("--javascript-delay is not supported", r.Err);
        Assert.Contains("--cookie is not supported", r.Err);
        var (w, h) = MediaBox(_cli.Path_("wk.pdf"));
        Assert.Equal(792, w, 0);
        Assert.Equal(612, h, 0);
    }

    [Fact]
    public void QuietHidesWarningsAndProgress()
    {
        var html = _cli.Html("in.html", "<p>x</p>");
        var r = _cli.Ok("-q", "--dpi", "300", html, _cli.Path_("q.pdf"));
        Assert.Equal("", r.Err);
    }

    [Fact]
    public void PageSizeFromWidthAndHeight()
    {
        var html = _cli.Html("in.html", "<p>x</p>");
        _cli.Ok("convert", "--page-width", "100mm", "--page-height", "150mm", html);
        var (w, h) = MediaBox(_cli.Path_("in.pdf"));
        Assert.Equal(283.46, w, 0.1);
        Assert.Equal(425.2, h, 0.1);
        Assert.Equal(2, _cli.Run("convert", "--page-width", "100mm", html).Exit);
    }

    [Fact]
    public void HeadersAndFootersUseWkhtmltopdfTokens()
    {
        var html = _cli.Html("two.html", "<p>One</p><div style=\"break-before:page\">Two</div>", "My Report");
        _cli.Ok(html, _cli.Path_("two.pdf"), "--footer-center", "Page [page] of [topage]", "--header-left", "[title]", "--header-right", "Say \"hi\"");
        var text = CliRunner.Text(_cli.Path_("two.pdf"));
        Assert.Contains("Page 1 of 2", text);
        Assert.Contains("Page 2 of 2", text);
        Assert.Contains("My Report", text);
        Assert.Contains("Say \"hi\"", text);
    }

    [Fact]
    public void UserStyleSheetAndMetadata()
    {
        var html = _cli.Html("in.html", "<p id=\"p\">styled</p>");
        var css = _cli.Write("my.css", "#p { color: red; }");
        _cli.Ok("convert", html, "--user-style-sheet", css, "--title", "T1", "--author", "Me");
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(_cli.Path_("in.pdf")));
        Assert.Contains("T1", raw);
        Assert.Contains("Me", raw);
    }

    [Fact]
    public void PdfAAndPasswordsReachTheLibrary()
    {
        var html = _cli.Html("in.html", "<p>archive</p>");
        _cli.Ok("convert", html, _cli.Path_("a.pdf"), "--pdfa", "2b");
        Assert.Contains("pdfaid", Encoding.Latin1.GetString(File.ReadAllBytes(_cli.Path_("a.pdf"))));
        _cli.Ok("convert", html, _cli.Path_("s.pdf"), "--user-password", "pw");
        Assert.True(PdfProtector.IsEncrypted(File.ReadAllBytes(_cli.Path_("s.pdf"))));
        Assert.Equal(2, _cli.Run("convert", html, "--pdfa", "9z").Exit);
    }

    [Fact]
    public async Task WatchRendersAgainWhenTheFileChanges()
    {
        var html = _cli.Html("live.html", "<p>First version</p>");
        var pdf = _cli.Path_("live.pdf");
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => _cli.RunWithInput(null, cts.Token, "watch", html, pdf));

        await WaitFor(() => File.Exists(pdf) && CliRunner.Text(pdf).Contains("First version"));

        // A broken change must not stop the watch.
        File.WriteAllText(html, "<p>Second version</p>");
        await WaitFor(() => CliRunner.Text(pdf).Contains("Second version"));

        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, result.Exit);
        Assert.Contains("Watching", result.Err);
    }

    [Fact]
    public async Task WatchRendersAgainWhenAnInputStartsWithTheOutputName()
    {
        // Changes to any path that started with the output's were ignored, so with -o report the input report.html
        // was rendered once and never again.
        var html = _cli.Html("report.html", "<p>First version</p>");
        var pdf = _cli.Path_("report");
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => _cli.RunWithInput(null, cts.Token, "watch", html, "-o", pdf));

        await WaitFor(() => File.Exists(pdf) && CliRunner.Text(pdf).Contains("First version"));
        File.WriteAllText(html, "<p>Second version</p>");
        await WaitFor(() => CliRunner.Text(pdf).Contains("Second version"));

        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, result.Exit);
    }

    [Fact]
    public void WatchNeedsFilesAndAnOutputFile()
    {
        Assert.Equal(2, _cli.Run("watch", "-").Exit);
        var html = _cli.Html("w.html", "<p>x</p>");
        Assert.Equal(2, _cli.Run("watch", html, "-").Exit);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition()) return;
            }
            catch (IOException)
            {
            }
            catch (PdfPeckerException)
            {
            }

            await Task.Delay(100);
        }

        Assert.Fail("The condition was not met within 30 seconds.");
    }
}
