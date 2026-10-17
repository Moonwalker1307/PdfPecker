using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

// Compares PdfPecker with other .NET HTML-to-PDF converters on the same documents and machine.
// Usage: dotnet run -c Release                      run every engine, each in a fresh process, and print a Markdown table
//        dotnet run -c Release -- <engine>          run one engine in this process and print its result as JSON
//        dotnet run -c Release -- versions <other>  run PdfPecker from this build and from another build of this
//                                                   program (its PdfPecker.Comparison.dll) in turns and compare them;
//                                                   bench/compare-versions.sh builds the other version from a git ref
// Chromium-based engines use CHROMIUM_PATH (default /opt/pw-browsers/chromium); wkhtmltopdf must be on PATH.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

if (args.Length > 1 && args[0] == "versions")
{
    Versions.Compare(Path.GetFullPath(args[1]), args.Length > 2 ? int.Parse(args[2]) : 3);
    return;
}

if (args.Length > 0 && args[0] != "all")
{
    var result = await Bench.Run(args[0]);
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}

var engines = new[] { "pdfpecker", "htmlrenderer", "itext", "wkhtmltopdf", "puppeteer", "playwright", "syncfusion" };
var results = new List<Result>();
foreach (var engine in engines)
{
    Console.Error.WriteLine($"Running {engine}...");
    var result = Bench.RunInChild(typeof(Bench).Assembly.Location, engine);
    if (result == null) { Console.Error.WriteLine($"  {engine} failed"); continue; }
    results.Add(result);
}

Console.WriteLine(Bench.MachineLine());
Console.WriteLine();
Console.WriteLine("| Engine | First PDF (cold) | Invoice, 1 page | Feature tour, 5 pages | Report, 30 pages | Parallel docs/s | Peak memory |");
Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- |");
foreach (var r in results)
    Console.WriteLine($"| {r.Engine} | {Ms(r.ColdMs)} | {Ms(r.Medians[0])} | {Ms(r.Medians[1])} | {Ms(r.Medians[2])} | {r.ParallelDocsPerSecond:F0} | {r.PeakMemoryMb:F0} MB |");
Console.WriteLine();
Console.WriteLine("Pages and size per document:");
foreach (var r in results)
    Console.WriteLine($"  {r.Engine,-13} {string.Join("  ", r.Pages.Zip(r.SizesKb, (p, s) => $"{p,3} p {s,6:F0} KB"))}");

static string Ms(double ms) => ms >= 100 ? $"{ms:F0} ms" : ms >= 10 ? $"{ms:F1} ms" : $"{ms:F2} ms";

record Result(string Engine, double ColdMs, double[] Medians, int[] Pages, double[] SizesKb, double ParallelDocsPerSecond, double PeakMemoryMb);

static class Bench
{
    static readonly string HtmlDir = Path.Combine(AppContext.BaseDirectory, "html");

    public static async Task<Result> Run(string name)
    {
        // The generated 30-page report is written next to the samples so every engine can load it from a file.
        var reportPath = Path.Combine(HtmlDir, "large-report.html");
        File.WriteAllText(reportPath, Documents.LargeReport());
        var docs = new[] { "invoice.html", "report.html", "large-report.html" }.Select(f => Path.Combine(HtmlDir, f)).ToArray();

        long t0 = Stopwatch.GetTimestamp();
        await using IEngine engine = await Engines.Create(name);
        await engine.Convert(docs[0]);
        double coldMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        var outDir = Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(outDir);
        var medians = new double[docs.Length];
        var pages = new int[docs.Length];
        var sizes = new double[docs.Length];
        // Warm up on every document for 3 seconds (JIT tiering, caches, browser processes) before anything is timed.
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed.TotalSeconds < 3) foreach (var doc in docs) await engine.Convert(doc);

        for (int d = 0; d < docs.Length; d++)
        {
            // Time runs for up to 5 seconds (at least 5, at most 200).
            var pdf = await engine.Convert(docs[d]);
            var times = new List<double>();
            var budget = Stopwatch.StartNew();
            while (times.Count < 5 || (times.Count < 200 && budget.Elapsed.TotalSeconds < 5))
            {
                long s = Stopwatch.GetTimestamp();
                await engine.Convert(docs[d]);
                times.Add(Stopwatch.GetElapsedTime(s).TotalMilliseconds);
            }
            times.Sort();
            medians[d] = times[times.Count / 2];
            pages[d] = PdfPecker.PdfToImage.GetPageCount(pdf);
            sizes[d] = pdf.Length / 1024.0;
            File.WriteAllBytes(Path.Combine(outDir, $"{name}-{Path.GetFileNameWithoutExtension(docs[d])}.pdf"), pdf);
        }

        // Throughput with one conversion per core running at once, on the 5-page feature tour.
        int workers = Environment.ProcessorCount, done = 0;
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            while (sw.Elapsed.TotalSeconds < 5) { await engine.Convert(docs[1]); Interlocked.Increment(ref done); }
        })));
        double perSecond = done / sw.Elapsed.TotalSeconds;

        // Peak memory, measured separately because sampling /proc slows the timed runs down: each document again,
        // then one conversion per core at once for two seconds.
        double peakMb;
        using (var memory = new MemorySampler())
        {
            foreach (var doc in docs) await engine.Convert(doc);
            sw.Restart();
            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
            {
                foreach (var doc in docs) await engine.Convert(doc);
                while (sw.Elapsed.TotalSeconds < 2) await engine.Convert(docs[1]);
            })));
            peakMb = memory.PeakMb;
        }

        return new Result(name, coldMs, medians, pages, sizes, perSecond, peakMb);
    }

    /// <summary>
    /// Runs one engine in a fresh process of the given build of this program. .NET sizes the garbage collector's
    /// allocation budget from the CPU's L3 cache, so the same build peaks at 90 MB on one cloud VM and 190 MB on
    /// another; a fixed 16 MB budget keeps peak memory comparable between machines (unless DOTNET_GCgen0size is
    /// already set). Returns null when the run fails.
    /// </summary>
    public static Result? RunInChild(string assembly, string engine)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true };
        psi.ArgumentList.Add(assembly);
        psi.ArgumentList.Add(engine);
        if (Environment.GetEnvironmentVariable("DOTNET_GCgen0size") == null) psi.Environment["DOTNET_GCgen0size"] = GcBudget;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.StartsWith('{'));
        return p.ExitCode == 0 && line != null ? JsonSerializer.Deserialize<Result>(line) : null;
    }

    const string GcBudget = "0x1000000";

    /// <summary>Describes the machine, which the timings depend on, for the line above every table.</summary>
    public static string MachineLine() =>
        $"{Environment.ProcessorCount} cores, {CpuName()}, {L3Cache()} L3, .NET {Environment.Version}, " +
        $"GC gen0 budget {Environment.GetEnvironmentVariable("DOTNET_GCgen0size") ?? GcBudget}";

    public static string CpuName()
    {
        try { return File.ReadLines("/proc/cpuinfo").First(l => l.StartsWith("model name")).Split(':')[1].Trim(); }
        catch { return "unknown CPU"; }
    }

    static string L3Cache()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/sys/devices/system/cpu/cpu0/cache", "index*"))
                if (File.ReadAllText(Path.Combine(dir, "level")).Trim() == "3") return File.ReadAllText(Path.Combine(dir, "size")).Trim();
        }
        catch { }
        return "unknown";
    }
}

/// <summary>
/// Compares two versions of PdfPecker on the same machine. Cloud VMs differ so much in speed and cache size that a
/// number from one machine says little about another, so a change is judged only against its base version, run in
/// turns in the same session so that a slow patch on the VM hits both.
/// </summary>
static class Versions
{
    public static void Compare(string otherAssembly, int rounds)
    {
        var builds = new[] { ("other", otherAssembly), ("this", typeof(Bench).Assembly.Location) };
        var runs = builds.ToDictionary(b => b.Item1, _ => new List<Result>());
        for (int round = 1; round <= rounds; round++)
            foreach (var (label, assembly) in builds)
            {
                Console.Error.WriteLine($"Round {round}/{rounds}: {label} build...");
                runs[label].Add(Bench.RunInChild(assembly, "pdfpecker") ?? throw new InvalidOperationException($"The {label} build failed."));
            }

        Console.WriteLine(Bench.MachineLine());
        Console.WriteLine($"Range over {rounds} rounds; other = {otherAssembly}");
        Console.WriteLine();
        Console.WriteLine("| | Other build | This build |");
        Console.WriteLine("| --- | --- | --- |");
        Row("First PDF", r => r.ColdMs, Ms);
        Row("Invoice, 1 page", r => r.Medians[0], Ms);
        Row("Feature tour, 5 pages", r => r.Medians[1], Ms);
        Row("Report, 30 pages", r => r.Medians[2], Ms);
        Row("Parallel docs/s", r => r.ParallelDocsPerSecond, v => $"{v:F0}");
        Row("Peak memory", r => r.PeakMemoryMb, v => $"{v:F0} MB");
        Row("Output size, 30 pages", r => r.SizesKb[2], v => $"{v:F1} KB");

        void Row(string name, Func<Result, double> value, Func<double, string> format) =>
            Console.WriteLine($"| {name} | {Range(runs["other"], value, format)} | {Range(runs["this"], value, format)} |");
    }

    static string Range(List<Result> results, Func<Result, double> value, Func<double, string> format)
    {
        double min = results.Min(value), max = results.Max(value);
        return format(min) == format(max) ? format(min) : $"{format(min)} – {format(max)}";
    }

    static string Ms(double ms) => ms >= 100 ? $"{ms:F0} ms" : ms >= 10 ? $"{ms:F1} ms" : $"{ms:F2} ms";
}

/// <summary>
/// Samples the proportional set size (PSS) of this process and every process it started (browser, renderer and GPU
/// processes, wkhtmltopdf). PSS splits shared pages between the processes that map them, so a browser's processes
/// are not counted several times over. Linux only.
/// </summary>
sealed class MemorySampler : IDisposable
{
    readonly Thread _thread;
    volatile bool _stop;
    long _peakKb;

    public MemorySampler()
    {
        _thread = new Thread(() => { while (!_stop) { _peakKb = Math.Max(_peakKb, TreePssKb()); Thread.Sleep(10); } }) { IsBackground = true };
        _thread.Start();
    }

    public double PeakMb => Math.Max(_peakKb, TreePssKb()) / 1024.0;

    public void Dispose() { _stop = true; _thread.Join(); }

    static long TreePssKb()
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                int ppid = int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1]);
                if (!children.TryGetValue(ppid, out var list)) children[ppid] = list = new();
                list.Add(pid);
            }
            catch { }
        }
        long total = 0;
        var stack = new Stack<int>([Environment.ProcessId]);
        while (stack.Count > 0)
        {
            int pid = stack.Pop();
            try
            {
                foreach (var line in File.ReadLines($"/proc/{pid}/smaps_rollup"))
                    if (line.StartsWith("Pss:")) { total += long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]); break; }
            }
            catch { }
            if (children.TryGetValue(pid, out var kids)) foreach (var k in kids) stack.Push(k);
        }
        return total;
    }
}
