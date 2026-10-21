using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace PdfPecker.Tests;

/// <summary>
/// Golden tests on PDFs that other tools wrote: Chromium, LibreOffice Writer, ReportLab and Ghostscript, plus copies that
/// qpdf rewrote with object streams and linearization or encrypted with RC4-128 and AES-256. Each page is rendered and
/// compared with a PNG, the text is extracted and compared with a text file, and merging, compacting, stamping, form
/// filling and decryption are compared object by object with PDF baselines, all under Golden/third-party/.
/// </summary>
/// <remarks>
/// The inputs in Fixtures/third-party are made by <c>tools/ThirdPartyPdfs/generate.sh</c>. A rewritten or encrypted copy
/// must give the same pages and text as the PDF it was made from, so it is compared with that PDF's baselines. Regenerate
/// the baselines with <c>PDFPECKER_UPDATE_GOLDEN=1 dotnet test --filter ThirdPartyPdfTests -f net10.0</c> and review them;
/// on a mismatch the new output is written to the test output folder (golden-actual/).
/// </remarks>
public class ThirdPartyPdfTests
{
    private const float Dpi = 36;

    private static readonly string Inputs = Path.Combine(SourceDirectory(), "Fixtures", "third-party");
    private static readonly string Root = Path.Combine(SourceDirectory(), "Golden", "third-party");

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private static bool Updating => Environment.GetEnvironmentVariable("PDFPECKER_UPDATE_GOLDEN") == "1";

    // Each input with the password that opens it and the input whose baselines it shares.
    private static readonly Dictionary<string, (string? Password, string Baseline)> Fixtures = new(StringComparer.Ordinal)
    {
        ["chromium"] = (null, "chromium"),
        ["chromium-qpdf"] = (null, "chromium"),
        ["ghostscript"] = (null, "ghostscript"),
        ["libreoffice"] = (null, "libreoffice"),
        ["libreoffice-aes256"] = ("user", "libreoffice"),
        ["libreoffice-rc4-128"] = ("user", "libreoffice"),
        ["reportlab"] = (null, "reportlab"),
    };

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var name in Fixtures.Keys.Order(StringComparer.Ordinal)) data.Add(name);
        return data;
    }

    private static byte[] Input(string name) => File.ReadAllBytes(Path.Combine(Inputs, name + ".pdf"));

    [Theory]
    [MemberData(nameof(Names))]
    public void Pages_match_the_baseline_pngs(string name)
    {
        var (password, baseline) = Fixtures[name];
        var renderer = PdfRenderer.Open(Input(name), password);
        bool owner = name == baseline;
        if (!Updating)
        {
            int expectedPages = Directory.GetFiles(Path.Combine(Root, "render"), baseline + "-*.png").Length;
            Assert.True(expectedPages == renderer.PageCount, $"{name} has {renderer.PageCount} pages, its baseline {expectedPages}");
        }
        var options = new ImageOptions { Dpi = Dpi, PngCompression = CompressionLevel.SmallestSize };
        for (int i = 0; i < renderer.PageCount; i++)
            RendererGoldenTests.AssertMatchesPng(renderer.RenderPage(i, options),
                Path.Combine(Root, "render", $"{baseline}-{i + 1}.png"), $"third-party/{name}-{i + 1}", owner);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Text_matches_the_baseline(string name)
    {
        var (password, baseline) = Fixtures[name];
        var actual = PdfText.Extract(Input(name), new PdfTextOptions { Password = password });
        var path = Path.Combine(Root, baseline + ".txt");
        if (Updating)
        {
            if (name == baseline) File.WriteAllText(path, actual);
            return;
        }
        Assert.True(File.Exists(path), $"No baseline for {name}. Create it with PDFPECKER_UPDATE_GOLDEN=1.");
        // Git may check the baseline out with CRLF line ends.
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));
    }

    public static TheoryData<string> Operations() => new()
    {
        "merge",
        "compact-chromium",
        "compact-ghostscript",
        "compact-libreoffice",
        "compact-reportlab",
        "stamp-chromium",
        "fill-libreoffice",
        "decrypt-libreoffice-aes256",
        "decrypt-libreoffice-rc4-128",
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void Operation_output_matches_the_baseline(string operation)
    {
        var expectedPath = Path.Combine(Root, operation + ".expected.pdf");
        var actual = Operate(operation);
        if (Updating)
        {
            File.WriteAllBytes(expectedPath, actual);
            return;
        }
        Assert.True(File.Exists(expectedPath), $"No baseline for {operation}. Create it with PDFPECKER_UPDATE_GOLDEN=1.");
        var difference = GoldenPdfTests.FirstDifference(GoldenPdfTests.Normalize(File.ReadAllBytes(expectedPath)), GoldenPdfTests.Normalize(actual));
        if (difference == null) return;
        var outDir = Path.Combine(AppContext.BaseDirectory, "golden-actual");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "third-party-" + operation + ".pdf");
        File.WriteAllBytes(outPath, actual);
        Assert.Fail($"third-party/{operation} differs from its baseline: {difference}\nNew output: {outPath}");
    }

    private static byte[] Operate(string operation)
    {
        if (operation.StartsWith("compact-", StringComparison.Ordinal))
            return PdfCompactor.Compact(Input(operation["compact-".Length..]));
        if (operation.StartsWith("decrypt-", StringComparison.Ordinal))
            return PdfProtector.Decrypt(Input(operation["decrypt-".Length..]), "owner");
        return operation switch
        {
            "merge" => PdfMerger.Merge(Input("chromium"), Input("ghostscript"), Input("libreoffice"), Input("reportlab")),
            "stamp-chromium" => PdfStamper.StampHtml(Input("chromium"),
                "<div style='text-align:right;font:bold 14pt sans-serif;color:#c00'>COPY</div>",
                htmlOptions: new PdfOptions { EmbedStandardFonts = true }),
            "fill-libreoffice" => PdfForm.Fill(Input("libreoffice"), new Dictionary<string, string>
            {
                ["name"] = "Jane Doe",
                ["email"] = "jane@example.com",
                ["newsletter"] = "yes",
                ["plan"] = "Premium",
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }
}
