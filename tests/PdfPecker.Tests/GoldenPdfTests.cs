using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

/// <summary>
/// Renders every sample document under Golden/ (HTML, and Markdown in *.md files) and compares the PDF with its saved
/// baseline (*.expected.pdf next to the sample), so any change to existing output shows up as a failing test. A few
/// operations on finished PDFs (merging, stamping and encrypting) have baselines of their own under Golden/operations/.
/// </summary>
/// <remarks>
/// The comparison is object by object, with compressed streams inflated, so it does not depend on the zlib build, and
/// with the creation date and the random file ID ignored. When a change in output is intended, regenerate the baselines
/// with <c>PDFPECKER_UPDATE_GOLDEN=1 dotnet test --filter GoldenPdfTests -f net10.0</c> and review the new PDFs.
/// On a mismatch the new output is written to the test output folder (golden-actual/) for inspection.
/// </remarks>
public class GoldenPdfTests
{
    private static readonly string Root = Path.Combine(SourceDirectory(), "Golden");

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        // HTML that PdfToHtmlGoldenTests expects (*.expected.html) is a baseline, not a sample.
        var files = Directory.GetFiles(Root, "*.html", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".expected.html", StringComparison.Ordinal))
            .Concat(Directory.GetFiles(Root, "*.md", SearchOption.AllDirectories));
        foreach (var file in files.OrderBy(p => p, StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(Root, file).Replace('\\', '/'));
        return data;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Output_matches_the_baseline(string sample)
    {
        var htmlPath = Path.Combine(Root, sample);
        var expectedPath = Path.ChangeExtension(htmlPath, ".expected.pdf");
        var actual = Render(htmlPath);

        if (Environment.GetEnvironmentVariable("PDFPECKER_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllBytes(expectedPath, actual);
            return;
        }
        Assert.True(File.Exists(expectedPath), $"No baseline for {sample}. Create it with PDFPECKER_UPDATE_GOLDEN=1.");

        var difference = FirstDifference(Normalize(File.ReadAllBytes(expectedPath)), Normalize(actual));
        if (difference == null) return;
        var outDir = Path.Combine(AppContext.BaseDirectory, "golden-actual");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, Path.ChangeExtension(sample.Replace('/', '_'), ".pdf"));
        File.WriteAllBytes(outPath, actual);
        Assert.Fail($"{sample} differs from its baseline: {difference}\nNew output: {outPath}");
    }

    // In these cultures the minus and plus signs carry a direction mark, so parsing "-1" with the current culture fails
    // and selectors, grid lines, order and list numbers come out differently.
    public static TheoryData<string, string> SamplesInOtherCultures()
    {
        var data = new TheoryData<string, string>();
        foreach (var sample in Samples())
            foreach (var culture in new[] { "ar-SA", "fa-IR" })
                data.Add(sample, culture);
        return data;
    }

    [Theory]
    [MemberData(nameof(SamplesInOtherCultures))]
    public void Output_does_not_depend_on_the_current_culture(string sample, string culture)
    {
        var htmlPath = Path.Combine(Root, sample);
        var expectedPath = Path.ChangeExtension(htmlPath, ".expected.pdf");
        if (!File.Exists(expectedPath)) return; // Output_matches_the_baseline reports a missing baseline
        var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        byte[] actual;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            actual = Render(htmlPath);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = saved;
        }
        var difference = FirstDifference(Normalize(File.ReadAllBytes(expectedPath)), Normalize(actual));
        Assert.True(difference == null, $"{sample} under {culture} differs from its baseline: {difference}");
    }

    // Samples that need options of their own, by path under Golden/.
    private static readonly Dictionary<string, Action<PdfOptions>> SampleOptions = new(StringComparer.Ordinal)
    {
        ["tagged/demo.html"] = o => o.Accessibility = PdfAccessibility.PdfUA1,
        ["complex/pdfa.html"] = o => o.Conformance = PdfConformance.PdfA2u,
    };

    private static byte[] Render(string path)
    {
        var options = new PdfOptions
        {
            EmbedStandardFonts = true, // bundled fonts, so the output does not depend on the machine
            AllowFileAccess = true,
            BaseDirectory = Path.GetDirectoryName(path),
        };
        if (SampleOptions.TryGetValue(Path.GetRelativePath(Root, path).Replace('\\', '/'), out var configure)) configure(options);
        var text = File.ReadAllText(path);
        return path.EndsWith(".md", StringComparison.Ordinal) ? MarkdownToPdf.Convert(text, options) : HtmlToPdf.Convert(text, options);
    }

    // ---- operations on finished PDFs ----

    public static TheoryData<string> Operations() => new() { "merge", "stamp", "encrypt", "protect" };

    // Encrypted output differs on every run (random keys and IVs), so it is compared twice: decrypted, which covers the
    // content, and with strings and stream data left out, which covers the encryption dictionary and the object layout.
    private static readonly Dictionary<string, string> EncryptedOperations = new(StringComparer.Ordinal)
    {
        ["encrypt"] = "owner",
        ["protect"] = "owner",
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void Operation_output_matches_the_baseline(string operation)
    {
        var expectedPath = Path.Combine(Root, "operations", operation + ".expected.pdf");
        var actual = Operate(operation);
        if (Environment.GetEnvironmentVariable("PDFPECKER_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllBytes(expectedPath, actual);
            return;
        }
        Assert.True(File.Exists(expectedPath), $"No baseline for {operation}. Create it with PDFPECKER_UPDATE_GOLDEN=1.");
        var expected = File.ReadAllBytes(expectedPath);
        var difference = EncryptedOperations.TryGetValue(operation, out var password)
            ? FirstDifference(Normalize(PdfProtector.Decrypt(expected, password)), Normalize(PdfProtector.Decrypt(actual, password)))
                ?? FirstDifference(Skeleton(expected), Skeleton(actual))
            : FirstDifference(Normalize(expected), Normalize(actual));
        Assert.True(difference == null, $"operations/{operation} differs from its baseline: {difference}");
    }

    /// <summary>Runs an operation on samples rendered fresh, so its baseline follows only that operation's output.</summary>
    private static byte[] Operate(string operation)
    {
        var tagged = Render(Path.Combine(Root, "tagged", "demo.html"));
        var table = Render(Path.Combine(Root, "large-table", "demo.html"));
        return operation switch
        {
            "merge" => PdfMerger.Merge(table, tagged),
            "stamp" => PdfStamper.StampHtml(table,
                "<div style='text-align:right;font:bold 14pt sans-serif;color:#c00'>COPY</div>",
                htmlOptions: new PdfOptions { EmbedStandardFonts = true }),
            "encrypt" => HtmlToPdf.Convert(File.ReadAllText(Path.Combine(Root, "large-table", "demo.html")), new PdfOptions
            {
                EmbedStandardFonts = true,
                Security = new PdfSecurity { UserPassword = "user", OwnerPassword = "owner", Permissions = PdfPermissions.Print },
            }),
            // A PDF from another tool, with forms and tags, so the protector's rewrite of objects it did not write is covered.
            "protect" => PdfProtector.Encrypt(File.ReadAllBytes(Path.Combine(SourceDirectory(), "Fixtures", "third-party", "libreoffice.pdf")),
                new PdfSecurity { UserPassword = "user", OwnerPassword = "owner", Permissions = PdfPermissions.Print | PdfPermissions.FillForms | PdfPermissions.ExtractForAccessibility }),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    [Fact]
    public void Comparison_ignores_dates_and_ids_but_not_content()
    {
        var html = Path.Combine(Root, "einvoice", "demo.html");
        var a = Normalize(Render(html));
        var b = Normalize(Render(html));
        Assert.Null(FirstDifference(a, b));
        // Another zlib build compresses differently; only the inflated content counts.
        var smallest = new PdfOptions { EmbedStandardFonts = true, AllowFileAccess = true, CompressionLevel = System.IO.Compression.CompressionLevel.SmallestSize };
        Assert.Null(FirstDifference(a, Normalize(HtmlToPdf.Convert(File.ReadAllText(html), smallest))));
        var options = new PdfOptions { Compress = false };
        var c = Normalize(HtmlToPdf.Convert("<p>one</p>", options));
        var d = Normalize(HtmlToPdf.Convert("<p>two</p>", options));
        Assert.Contains("(one)", FirstDifference(c, d), StringComparison.Ordinal);
    }

    // ---- comparison ----

    internal sealed record PdfObject(int Number, string Dictionary, string Stream);

    private static readonly Regex ObjectPattern = new(@"(\d+) 0 obj\n", RegexOptions.Compiled);
    // A cross-reference stream carries the trailer, and with it the file ID, which the compactor derives from a hash of
    // the compressed output and so from the zlib build. Its data, the byte offsets, depends on that build too, and is
    // left out like a classic cross-reference table.
    private static readonly Regex FileIds = new(@"/ID\s*\[\s*<[0-9A-Fa-f]*>\s*<[0-9A-Fa-f]*>\s*\]", RegexOptions.Compiled);
    private static readonly Regex XRefType = new(@"/Type\s*/XRef\b", RegexOptions.Compiled);
    // Dates as literal strings, as hex strings (which the stamper writes) and in XMP metadata.
    private static readonly Regex Dates = new(@"\(D:[^)]*\)|(?<=/(?:CreationDate|ModDate) )<[0-9A-Fa-f]*>|<xmp:(?:Create|Modify|Metadata)Date>[^<]*", RegexOptions.Compiled);

    /// <summary>The file's objects in order, streams inflated, with dates blanked. The trailer (random /ID) is left out.</summary>
    internal static List<PdfObject> Normalize(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var objects = new List<PdfObject>();
        foreach (Match m in ObjectPattern.Matches(text))
        {
            int start = m.Index + m.Length;
            int streamAt = text.IndexOf("stream\n", start, StringComparison.Ordinal);
            int end = text.IndexOf("endobj", start, StringComparison.Ordinal);
            if (end < 0) break;
            string stream = "";
            if (streamAt >= 0 && streamAt < end)
            {
                var dict = text.Substring(start, streamAt - start);
                var length = Regex.Match(dict, @"/Length (\d+)");
                int len = length.Success ? int.Parse(length.Groups[1].Value) : 0;
                int dataAt = streamAt + "stream\n".Length;
                var data = pdf.AsSpan(dataAt, Math.Min(len, pdf.Length - dataAt)).ToArray();
                if (dict.Contains("/FlateDecode", StringComparison.Ordinal))
                {
                    data = Inflate(data);
                    // The compressed length depends on the zlib build; the inflated data is compared instead.
                    dict = dict.Remove(length.Index, length.Length);
                }
                stream = XRefType.IsMatch(dict) ? "" : Dates.Replace(Encoding.Latin1.GetString(data), "");
                end = text.IndexOf("endobj", dataAt + len, StringComparison.Ordinal);
                objects.Add(new PdfObject(int.Parse(m.Groups[1].Value), FileIds.Replace(Dates.Replace(dict, ""), ""), stream));
            }
            else objects.Add(new PdfObject(int.Parse(m.Groups[1].Value), Dates.Replace(text.Substring(start, end - start), ""), ""));
        }
        return objects;
    }

    private static byte[] Inflate(byte[] data)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }
        // Encrypted stream data is random, and zlib reports some random headers as an IOException rather than as
        // invalid data; either way the bytes are compared as they are.
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return data;
        }
    }

    private static readonly Regex Strings = new(@"\((?:\\.|[^\\()])*\)|(?<!<)<(?!<)[0-9A-Fa-f\s]*>", RegexOptions.Compiled);
    private static readonly Regex Lengths = new(@"/Length \d+", RegexOptions.Compiled);

    /// <summary>The objects of an encrypted file without the parts that change with the random key: strings, stream data and lengths.</summary>
    private static List<PdfObject> Skeleton(byte[] pdf) =>
        Normalize(pdf).Select(o => new PdfObject(o.Number, Lengths.Replace(Strings.Replace(o.Dictionary, "()"), ""), "")).ToList();

    /// <summary>A description of the first difference, or null when the files match.</summary>
    internal static string? FirstDifference(List<PdfObject> expected, List<PdfObject> actual)
    {
        for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            var (e, a) = (expected[i], actual[i]);
            if (e.Dictionary != a.Dictionary)
                return $"object {e.Number}: expected {Excerpt(e.Dictionary, a.Dictionary)}";
            if (e.Stream != a.Stream)
                return $"object {e.Number} stream ({e.Stream.Length} vs {a.Stream.Length} bytes): expected {Excerpt(e.Stream, a.Stream)}";
        }
        return expected.Count == actual.Count ? null : $"expected {expected.Count} objects, got {actual.Count}";
    }

    /// <summary>The first differing line of each side.</summary>
    private static string Excerpt(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        int line = 0;
        while (line < e.Length && line < a.Length && e[line] == a[line]) line++;
        static string Clip(string[] lines, int i) => i < lines.Length ? lines[i].Length > 200 ? lines[i][..200] + "…" : lines[i] : "(end)";
        return $"line {line + 1} \"{Clip(e, line)}\" but got \"{Clip(a, line)}\"";
    }
}
