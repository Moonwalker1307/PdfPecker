using System.Diagnostics;
using System.Globalization;
using Comparison;
using FluentExamples;
using PdfPecker.Fluent;

// The same invoice built with PdfPecker.Fluent and with QuestPDF, timed in one process after a long warm-up.
// Usage: dotnet run -c Release [-- iterations [warm-up seconds [output directory]]]
int iterations = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 200;
int warmupSeconds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 12;
string? output = args.Length > 2 ? args[2] : null;

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var cases = new (string Name, Func<byte[]> Fluent, Func<byte[]> Quest)[]
{
    ("invoice, 12 lines", () => Examples.Invoice(Examples.InvoiceModel.Sample(12)).GeneratePdf(),
        () => QuestPDF.Fluent.GenerateExtensions.GeneratePdf(QuestInvoice.Create(Examples.InvoiceModel.Sample(12)))),
    ("invoice, 400 lines", () => Examples.Invoice(Examples.InvoiceModel.Sample(400)).GeneratePdf(),
        () => QuestPDF.Fluent.GenerateExtensions.GeneratePdf(QuestInvoice.Create(Examples.InvoiceModel.Sample(400)))),
};

Console.WriteLine($"PdfPecker.Fluent vs QuestPDF {typeof(QuestPDF.Settings).Assembly.GetName().Version}, {iterations} iterations, .NET {Environment.Version}, {Environment.ProcessorCount} cores");

var first = Stopwatch.StartNew();
cases[0].Fluent();
double fluentFirst = first.Elapsed.TotalMilliseconds;
first.Restart();
cases[0].Quest();
double questFirst = first.Elapsed.TotalMilliseconds;
Console.WriteLine($"First document in the process: PdfPecker.Fluent {fluentFirst:F0} ms, QuestPDF {questFirst:F0} ms");

var warmup = Stopwatch.StartNew();
while (warmup.Elapsed.TotalSeconds < warmupSeconds)
    foreach (var c in cases)
    {
        c.Fluent();
        c.Quest();
    }

Console.WriteLine();
Console.WriteLine($"{"Case",-20} {"Engine",-17} {"Median",9} {"P95",9} {"Size",9} {"Alloc/op",10}");
foreach (var c in cases)
{
    int runs = c.Name.Contains("400", StringComparison.Ordinal) ? Math.Max(20, iterations / 10) : iterations;
    foreach (var (engine, run) in new[] { ("PdfPecker.Fluent", c.Fluent), ("QuestPDF", c.Quest) })
    {
        var pdf = run();
        if (output != null) File.WriteAllBytes(Path.Combine(output, $"{engine}-{c.Name.Replace(", ", "-").Replace(' ', '-')}.pdf"), pdf);
        var times = new double[runs];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < runs; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            run();
            times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
        long alloc = (GC.GetAllocatedBytesForCurrentThread() - before) / runs;
        Array.Sort(times);
        Console.WriteLine($"{c.Name,-20} {engine,-17} {times[runs / 2],7:F2}ms {times[(int)(runs * 0.95)],7:F2}ms {pdf.Length / 1024.0,7:F1}KB {alloc / 1048576.0,8:F2}MB");
    }
}

foreach (var (engine, index) in new[] { ("PdfPecker.Fluent", 0), ("QuestPDF", 1) })
{
    int total = iterations * 4;
    var sw = Stopwatch.StartNew();
    Parallel.For(0, total, _ => { if (index == 0) cases[0].Fluent(); else cases[0].Quest(); });
    Console.WriteLine($"Parallel {engine}: {total / sw.Elapsed.TotalSeconds:F0} invoices/s");
}
