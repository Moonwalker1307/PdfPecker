using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfPecker;
using PdfPecker.Fonts;
using PdfPecker.Fonts.Cff;
using PdfPecker.Images;
using PdfPecker.Rendering.Fonts;

// A short, machine-readable run for the performance regression check in .github/workflows/perf.yml: about 30 seconds,
// eight cases, JSON output. Allocation per conversion is deterministic, so the workflow compares it tightly; time is
// noisy on shared runners, so it is compared with a wide margin.
// Usage: dotnet run -c Release -- --perf results.json
static class Perf
{
    public static void Run(string outputPath)
    {
        var htmlDir = Path.Combine(AppContext.BaseDirectory, "html");
        var invoice = File.ReadAllText(Path.Combine(htmlDir, "invoice.html"));
        var report = Documents.LargeReport();

        var results = new List<(string Name, double MeanMs, long AllocBytes, double? PerSecond)>();
        results.Add(Measure("invoice", invoice, htmlDir, runs: 100));
        results.Add(Measure("report-30", report, null, runs: 15));

        // The image decoders and the renderer, on the test fixtures. One run decodes every fixture once, or renders one
        // page, so the figures are per pass over the fixtures.
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var bmps = Fixtures(fixtureDir, "*.bmp");
        var webps = Fixtures(fixtureDir, "*.webp");
        var tiffs = Fixtures(fixtureDir, "*.tif");
        results.Add(Measure("decode-bmp", () => { foreach (var b in bmps) BmpDecoder.Decode(b, MaxPixels); }, runs: 20000));
        results.Add(Measure("decode-webp", () => { foreach (var w in webps) WebPDecoder.Decode(w, MaxPixels); }, runs: 1000));
        results.Add(Measure("decode-tiff", () => { foreach (var t in tiffs) TiffDecoder.Decode(t, MaxPixels); }, runs: 300));
        var pdf = File.ReadAllBytes(Path.Combine(fixtureDir, "features.pdf"));
        var imageOptions = new ImageOptions { Dpi = 96 };
        results.Add(Measure("render-png", () => PdfToImage.ConvertPage(pdf, 0, imageOptions), runs: 30));

        // The font tables: kerning pairs, ligature lookups, variation deltas (GDEF, GPOS, CFF2) and CFF parsing and
        // subsetting, on each test font. A pass loads every font afresh, so none of it is cached from the one before.
        var fonts = Fixtures(Path.Combine(fixtureDir, "fonts"), "*.ttf").Concat(Fixtures(Path.Combine(fixtureDir, "fonts"), "*.otf")).ToArray();
        results.Add(Measure("parse-font-tables", () => { foreach (var f in fonts) ParseFontTables(f); }, runs: 300));

        // Throughput with every core busy; documents per second is the figure, so higher is better.
        var options = new PdfOptions { BaseDirectory = htmlDir };
        for (int i = 0; i < 20; i++) HtmlToPdf.Convert(invoice, options);
        int total = 400;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, total, _ => HtmlToPdf.Convert(invoice, options));
        sw.Stop();
        results.Add(("throughput-invoice", sw.Elapsed.TotalMilliseconds / total, 0, total / sw.Elapsed.TotalSeconds));

        var json = new StringBuilder("{\n  \"cases\": [\n");
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var perSecond = r.PerSecond is { } p ? p.ToString("F2", CultureInfo.InvariantCulture) : "null";
            json.Append(CultureInfo.InvariantCulture,
                $"    {{ \"name\": \"{r.Name}\", \"meanMs\": {r.MeanMs:F4}, \"allocBytes\": {r.AllocBytes}, \"perSecond\": {perSecond} }}{(i + 1 < results.Count ? "," : "")}\n");
            Console.WriteLine($"{r.Name,-20} {r.MeanMs,9:F3} ms {r.AllocBytes / 1024.0,10:F0} KB {(r.PerSecond is { } q ? $"{q:F0} docs/s" : "")}");
        }
        json.Append("  ]\n}\n");
        File.WriteAllText(outputPath, json.ToString());
    }

    // The pixel limit the library uses when it loads images for a document.
    const long MaxPixels = 100_000_000;

    static byte[][] Fixtures(string dir, string pattern)
        => Directory.GetFiles(dir, pattern).Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToArray();

    static void ParseFontTables(byte[] data)
    {
        foreach (var font in FontProgram.Load(data))
        {
            var kerning = font.Kerning;
            var ligatures = font.Ligatures;
            if (kerning != null && ligatures != null) kerning.Pair(ligatures.StartsLigature(1) ? 1 : 2, 3);
            if (font.Table("CFF ") is { } cff)
            {
                var table = font.Data.AsSpan(cff.Offset, cff.Length).ToArray();
                CffFont.Parse(table);
                CffSubsetter.Subset(table, [0, 1, 2, 3, 10, 25]);
            }
            var axes = FontVariations.ReadAxes(font);
            if (axes.Length == 0 || !FontVariations.CanInstance(font)) continue;
            var values = axes.ToDictionary(a => a.Tag, a => (a.Min + a.Max) / 2);
            FontVariations.Instance(font, FontVariations.Normalize(font, axes, values), values, axes);
        }
    }

    static (string, double, long, double?) Measure(string name, string html, string? baseDir, int runs)
    {
        var options = new PdfOptions { BaseDirectory = baseDir };
        return Measure(name, () => HtmlToPdf.Convert(html, options), runs);
    }

    static (string, double, long, double?) Measure(string name, Action run, int runs)
    {
        // Warm up so tiered JIT has produced optimized code before anything is timed.
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed.TotalSeconds < 2) run();

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < runs; i++) run();
        double mean = Stopwatch.GetElapsedTime(t0).TotalMilliseconds / runs;
        long alloc = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / runs;
        return (name, mean, alloc, null);
    }
}
