using System.Diagnostics;
using System.Globalization;
using FluentExamples;
using PdfPecker;
using PdfPecker.Fluent;

// A dependency-free benchmark harness. For each example it times three things: composing the document and writing its
// HTML, the whole GeneratePdf call, and HtmlToPdf on the same HTML, so the difference between the last two is what the
// fluent layer costs on top of PdfPecker itself.
// Usage: dotnet run -c Release [-- iterations [warm-up seconds]]
int iterations = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 200;
int warmupSeconds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 12;

var cases = new List<(string Name, Func<IDocument> Create)>
{
    ("invoice (1 page)", () => Examples.Invoice(Examples.InvoiceModel.Sample(12))),
    ("catalogue (landscape)", Examples.Catalogue),
    ("report (30 chapters)", () => Examples.Report(30)),
};

Console.WriteLine($"PdfPecker.Fluent benchmark, {iterations} iterations per case, .NET {Environment.Version}, {Environment.ProcessorCount} cores");
Console.WriteLine();

// Warm-up: long enough for tiered JIT to finish, which takes longer than a few seconds for the PDF path.
var warmup = Stopwatch.StartNew();
while (warmup.Elapsed.TotalSeconds < warmupSeconds)
    foreach (var (_, create) in cases) create().GeneratePdf();

Console.WriteLine($"{"Case",-24} {"Pages",6} {"Compose+HTML",13} {"GeneratePdf",12} {"HtmlToPdf",10} {"Overhead",9} {"Alloc HTML",11} {"Alloc PDF",10}");
foreach (var (name, create) in cases)
{
    var document = create();
    var html = DocumentGenerator.Html(document, false, out var images);
    var options = new PdfOptions { ResourceResolver = images.Resolve };
    int pages = PdfToImage.GetPageCount(document.GeneratePdf());

    int runs = name.StartsWith("report", StringComparison.Ordinal) ? Math.Max(20, iterations / 5) : iterations;
    var (htmlTime, htmlAlloc) = Measure(runs, () => DocumentGenerator.Html(create(), false, out _));
    var (pdfTime, pdfAlloc) = Measure(runs, () => create().GeneratePdf());
    var (plainTime, _) = Measure(runs, () => HtmlToPdf.Convert(html, options));
    Console.WriteLine($"{name,-24} {pages,6} {htmlTime,11:F3}ms {pdfTime,10:F2}ms {plainTime,8:F2}ms {(pdfTime - plainTime) / plainTime,8:P1} {htmlAlloc / 1024.0,9:F0}KB {pdfAlloc / 1048576.0,8:F2}MB");
}

// Throughput with every core busy.
{
    int total = iterations * 4;
    var sw = Stopwatch.StartNew();
    Parallel.For(0, total, _ => cases[0].Create().GeneratePdf());
    Console.WriteLine();
    Console.WriteLine($"Parallel: {total} x '{cases[0].Name}' in {sw.Elapsed.TotalMilliseconds:F0} ms = {total / sw.Elapsed.TotalSeconds:F0} documents/s");
}

static (double Median, long Alloc) Measure(int runs, Action action)
{
    var times = new double[runs];
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < runs; i++)
    {
        long t0 = Stopwatch.GetTimestamp();
        action();
        times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }
    long alloc = (GC.GetAllocatedBytesForCurrentThread() - before) / runs;
    Array.Sort(times);
    return (times[runs / 2], alloc);
}
