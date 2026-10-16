using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfPecker;

// A short, machine-readable run for the performance regression check in .github/workflows/perf.yml: about 20 seconds,
// three cases, JSON output. Allocation per conversion is deterministic, so the workflow compares it tightly; time is
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
        results.Add(Measure("decode-tiff", DecodeTiffFixtures(), runs: 300));

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

    static (string, double, long, double?) Measure(string name, string html, string? baseDir, int runs)
    {
        var options = new PdfOptions { BaseDirectory = baseDir };
        // Warm up so tiered JIT has produced optimized code before anything is timed.
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed.TotalSeconds < 2) HtmlToPdf.Convert(html, options);

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < runs; i++) HtmlToPdf.Convert(html, options);
        double mean = Stopwatch.GetElapsedTime(t0).TotalMilliseconds / runs;
        long alloc = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / runs;
        return (name, mean, alloc, null);
    }

    static (string, double, long, double?) Measure(string name, Action run, int runs)
    {
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed.TotalSeconds < 2) run();

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < runs; i++) run();
        double mean = Stopwatch.GetElapsedTime(t0).TotalMilliseconds / runs;
        long alloc = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / runs;
        return (name, mean, alloc, null);
    }

    // Decodes every TIFF fixture (each compression, strips and tiles, planar, 1 to 16 bits) once per run. It calls the
    // decoder directly, because a conversion would serve repeated images from its cache and mostly measure layout.
    static Action DecodeTiffFixtures()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "tiff"), "*.tif").Order().Select(File.ReadAllBytes).ToArray();
        return () =>
        {
            foreach (var file in files) PdfPecker.Images.TiffDecoder.Decode(file, PdfPecker.Rendering.Images.PdfImageDecoder.MaxPixels);
        };
    }
}
