using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

/// <summary>
/// Renders every sample document under Golden/ and compares the PDF with its saved baseline (*.expected.pdf next to the
/// HTML), so any change to existing output shows up as a failing test.
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
        foreach (var html in Directory.GetFiles(Root, "*.html", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(Root, html).Replace('\\', '/'));
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
        var outPath = Path.Combine(outDir, sample.Replace('/', '_').Replace(".html", ".pdf", StringComparison.Ordinal));
        File.WriteAllBytes(outPath, actual);
        Assert.Fail($"{sample} differs from its baseline: {difference}\nNew output: {outPath}");
    }

    private static byte[] Render(string htmlPath)
    {
        var options = new PdfOptions
        {
            EmbedStandardFonts = true, // bundled fonts, so the output does not depend on the machine
            AllowFileAccess = true,
            BaseDirectory = Path.GetDirectoryName(htmlPath),
        };
        return HtmlToPdf.Convert(File.ReadAllText(htmlPath), options);
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

    private sealed record PdfObject(int Number, string Dictionary, string Stream);

    private static readonly Regex ObjectPattern = new(@"(\d+) 0 obj\n", RegexOptions.Compiled);
    private static readonly Regex Dates = new(@"\(D:[^)]*\)|<xmp:(?:Create|Modify|Metadata)Date>[^<]*", RegexOptions.Compiled);

    /// <summary>The file's objects in order, streams inflated, with dates blanked. The trailer (random /ID) is left out.</summary>
    private static List<PdfObject> Normalize(byte[] pdf)
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
                stream = Dates.Replace(Encoding.Latin1.GetString(data), "");
                end = text.IndexOf("endobj", dataAt + len, StringComparison.Ordinal);
                objects.Add(new PdfObject(int.Parse(m.Groups[1].Value), Dates.Replace(dict, ""), stream));
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
        catch (InvalidDataException)
        {
            return data;
        }
    }

    /// <summary>A description of the first difference, or null when the files match.</summary>
    private static string? FirstDifference(List<PdfObject> expected, List<PdfObject> actual)
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
