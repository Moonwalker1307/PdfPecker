using System.Diagnostics;
using System.Text;
using PdfPecker;

// A dependency-free benchmark harness: warm up, then time many conversions and report percentiles.
// Usage: dotnet run -c Release [-- iterations]
int iterations = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 200;
var htmlDir = Path.Combine(AppContext.BaseDirectory, "html");

var cases = new List<(string Name, string Html, string? BaseDir)>
{
    ("invoice (1 page)", File.ReadAllText(Path.Combine(htmlDir, "invoice.html")), htmlDir),
    ("feature tour (5 pages)", File.ReadAllText(Path.Combine(htmlDir, "report.html")), htmlDir),
    ("large report (30 pages)", LargeReport(), null),
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

static int CountPages(byte[] pdf)
{
    var text = Encoding.Latin1.GetString(pdf);
    var marker = "/Type /Pages /Count ";
    int i = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
    int j = i;
    while (char.IsDigit(text[j])) j++;
    return int.Parse(text.AsSpan(i, j - i));
}

static string LargeReport()
{
    var sb = new StringBuilder();
    sb.Append("<html><head><style>body{font-family:Arial;font-size:10pt} h2{color:#2c5282} table{border-collapse:collapse;width:100%} td,th{border:1px solid #ccc;padding:3px 5px} th{background:#eee} .r{text-align:right} p{text-align:justify}</style></head><body>");
    for (int s = 1; s <= 20; s++)
    {
        sb.Append("<h2>Section ").Append(s).Append("</h2>");
        for (int p = 0; p < 3; p++)
            sb.Append("<p>Lorem ipsum dolor sit amet, <b>consectetur</b> adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis <i>nostrud exercitation</i> ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur.</p>");
        sb.Append("<table><thead><tr><th>#</th><th>Product</th><th>Category</th><th class=r>Qty</th><th class=r>Price</th></tr></thead><tbody>");
        for (int r = 1; r <= 50; r++)
            sb.Append("<tr><td>").Append(r).Append("</td><td>Product ").Append(s * 100 + r).Append("</td><td>Category ").Append(r % 7)
              .Append("</td><td class=r>").Append(r * 3 % 17).Append("</td><td class=r>").Append((r * 13.37).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append("</td></tr>");
        sb.Append("</tbody></table>");
        sb.Append("<ul><li>First point</li><li>Second point with <a href='https://example.com'>a link</a></li><li>Third point</li></ul>");
    }
    sb.Append("</body></html>");
    return sb.ToString();
}
