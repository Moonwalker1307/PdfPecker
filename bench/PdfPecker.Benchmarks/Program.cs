using System.Diagnostics;
using System.Text;
using PdfPecker;

// A dependency-free benchmark harness: warm up, then time many conversions and report percentiles.
// Usage: dotnet run -c Release [-- iterations]
//        dotnet run -c Release -- --requirements [extra.html ...]   (memory and CPU per conversion, see Requirements.cs)
if (args.Length > 0 && args[0].StartsWith("--requirements"))
{
    Requirements.Run(args);
    return;
}

int iterations = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 200;
var htmlDir = Path.Combine(AppContext.BaseDirectory, "html");

var cases = new List<(string Name, string Html, string? BaseDir)>
{
    ("invoice (1 page)", File.ReadAllText(Path.Combine(htmlDir, "invoice.html")), htmlDir),
    ("feature tour (5 pages)", File.ReadAllText(Path.Combine(htmlDir, "report.html")), htmlDir),
    ("large report (30 pages)", Documents.LargeReport(), null),
};

Console.WriteLine($"PdfPecker benchmark, {iterations} iterations per case, .NET {Environment.Version}, {Environment.ProcessorCount} cores");
Console.WriteLine();
Console.WriteLine($"{"Case",-26} {"Pages",6} {"Size",9} {"Mean",9} {"Median",9} {"P95",9} {"Pages/s",9} {"Alloc/op",10}");

// Warm-up: run every case for a while so tiered JIT has produced optimized code before anything is timed.
var warmup = Stopwatch.StartNew();
while (warmup.Elapsed.TotalSeconds < 3)
    foreach (var (_, html, baseDir) in cases) HtmlToPdf.Convert(html, new PdfOptions { BaseDirectory = baseDir });

foreach (var (name, html, baseDir) in cases)
{
    var options = new PdfOptions { BaseDirectory = baseDir };
    var pdf = HtmlToPdf.Convert(html, options);

    int runs = name.StartsWith("large") ? Math.Max(20, iterations / 5) : iterations;
    var times = new double[runs];
    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < runs; i++)
    {
        long t0 = Stopwatch.GetTimestamp();
        HtmlToPdf.Convert(html, options);
        times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }
    long alloc = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / runs;
    Array.Sort(times);
    int pages = CountPages(pdf);
    double mean = times.Average();
    Console.WriteLine($"{name,-26} {pages,6} {pdf.Length / 1024.0,7:F1}KB {mean,7:F2}ms {times[runs / 2],7:F2}ms {times[(int)(runs * 0.95)],7:F2}ms {pages / (mean / 1000),9:F0} {alloc / 1024.0 / 1024.0,8:F2}MB");
}

// Throughput with all cores busy.
{
    var html = cases[1].Html;
    var options = new PdfOptions { BaseDirectory = cases[1].BaseDir };
    int total = iterations * 4;
    var sw = Stopwatch.StartNew();
    Parallel.For(0, total, _ => HtmlToPdf.Convert(html, options));
    sw.Stop();
    Console.WriteLine();
    Console.WriteLine($"Parallel: {total} x '{cases[1].Name}' in {sw.Elapsed.TotalMilliseconds:F0} ms = {total / sw.Elapsed.TotalSeconds:F0} documents/s");
}

// Markdown: parsing alone (Markdown to HTML) and the full Markdown to PDF path.
{
    var guide = File.ReadAllText(Path.Combine(htmlDir, "guide.md"));
    var big = string.Concat(Enumerable.Repeat(guide, 50));
    Console.WriteLine();
    Console.WriteLine($"{"Markdown case",-34} {"Input",9} {"Mean",9} {"Median",9} {"MB/s",8}");
    foreach (var (name, md, toPdf) in new[]
    {
        ("guide.md to HTML", guide, false),
        ("guide.md x50 to HTML", big, false),
        ("guide.md to PDF (2 pages)", guide, true),
    })
    {
        var pdfOptions = new PdfOptions { BaseDirectory = htmlDir };
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed.TotalSeconds < 1) Run();
        int runs = iterations;
        var times = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            Run();
            times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
        Array.Sort(times);
        double mean = times.Average();
        Console.WriteLine($"{name,-34} {md.Length / 1024.0,7:F1}KB {mean,7:F3}ms {times[runs / 2],7:F3}ms {md.Length / 1024.0 / 1024.0 / (mean / 1000),8:F1}");

        void Run()
        {
            if (toPdf) MarkdownToPdf.Convert(md, pdfOptions);
            else MarkdownToHtml.Convert(md);
        }
    }
}

// PDF to PNG: render the generated PDFs back to images.
{
    Console.WriteLine();
    Console.WriteLine($"{"Render (PDF to PNG)",-26} {"DPI",6} {"Render",12} {"+PNG",12} {"Pages/s",9}");
    foreach (var (name, html, baseDir) in cases)
    {
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { BaseDirectory = baseDir });
        foreach (var dpi in new[] { 96f, 150f })
        {
            var options = new ImageOptions { Dpi = dpi };
            var renderer = PdfRenderer.Open(pdf);
            for (int i = 0; i < renderer.PageCount; i++) renderer.RenderPage(i, options).ToPng(); // warm up
            int rounds = Math.Max(3, iterations / (renderer.PageCount * 10));
            double render = 0, png = 0;
            for (int k = 0; k < rounds; k++)
            {
                renderer = PdfRenderer.Open(pdf); // cold document each round: fonts and images are decoded again
                for (int i = 0; i < renderer.PageCount; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    var img = renderer.RenderPage(i, options);
                    render += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    t0 = Stopwatch.GetTimestamp();
                    img.ToPng();
                    png += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                }
            }
            int pages = rounds * renderer.PageCount;
            Console.WriteLine($"{name,-26} {dpi,6:F0} {render / pages,8:F2}ms/p {png / pages,8:F2}ms/p {pages / ((render + png) / 1000),9:F0}");
        }
    }
}

static int CountPages(byte[] pdf)
{
    var text = Encoding.Latin1.GetString(pdf);
    var marker = "/Type /Pages /Count ";
    int i = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
    int j = i;
    while (char.IsDigit(text[j])) j++;
    return int.Parse(text.AsSpan(i, j - i));
}
