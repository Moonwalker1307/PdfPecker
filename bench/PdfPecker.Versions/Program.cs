using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

// Compares two versions of PdfPecker on this machine; bench/compare-versions.sh builds the other version from a git ref
// and runs it. The comparison with other converters is in the PdfPecker.Benchmarks repository.
// Usage: dotnet run -c Release -- versions <other> [rounds]  run PdfPecker from this build and from another build of
//                                                            this program (its PdfPecker.Versions.dll) in turns
//        dotnet run -c Release -- pdfpecker                  run once in this process and print the result as JSON
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

if (args.Length > 1 && args[0] == "versions")
{
    Versions.Compare(Path.GetFullPath(args[1]), args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 3);
    return;
}

Console.WriteLine(JsonSerializer.Serialize(Bench.Run()));

record Result(string Engine, double ColdMs, double[] Medians, int[] Pages, double[] SizesKb, double ParallelDocsPerSecond, double PeakMemoryMb);

static class Bench
{
    static readonly string HtmlDir = Path.Combine(AppContext.BaseDirectory, "html");

    public static Result Run()
    {
        // The generated 30-page report is written next to the samples and loaded from a file like them.
        var reportPath = Path.Combine(HtmlDir, "large-report.html");
        File.WriteAllText(reportPath, Documents.LargeReport());
        var docs = new[] { "invoice.html", "report.html", "large-report.html" }.Select(f => Path.Combine(HtmlDir, f)).ToArray();

        long t0 = Stopwatch.GetTimestamp();
        Convert(docs[0]);
        double coldMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        var outDir = Path.Combine(AppContext.BaseDirectory, "out");
        Directory.CreateDirectory(outDir);
        var medians = new double[docs.Length];
        var pages = new int[docs.Length];
        var sizes = new double[docs.Length];
        // Warm up on every document (JIT tiering, caches) before anything is timed: 3 seconds, or the number of seconds
        // in PDFPECKER_BENCH_WARMUP, since full JIT tiering can take longer than 3 seconds.
        double warmSeconds = double.TryParse(Environment.GetEnvironmentVariable("PDFPECKER_BENCH_WARMUP"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var w) ? w : 3;
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed.TotalSeconds < warmSeconds) foreach (var doc in docs) Convert(doc);

        for (int d = 0; d < docs.Length; d++)
        {
            // Time runs for up to 5 seconds (at least 5, at most 200).
            var pdf = Convert(docs[d]);
            var times = new List<double>();
            var budget = Stopwatch.StartNew();
            while (times.Count < 5 || (times.Count < 200 && budget.Elapsed.TotalSeconds < 5))
            {
                long s = Stopwatch.GetTimestamp();
                Convert(docs[d]);
                times.Add(Stopwatch.GetElapsedTime(s).TotalMilliseconds);
            }
            times.Sort();
            medians[d] = times[times.Count / 2];
            pages[d] = PdfPecker.PdfToImage.GetPageCount(pdf);
            sizes[d] = pdf.Length / 1024.0;
            File.WriteAllBytes(Path.Combine(outDir, $"pdfpecker-{Path.GetFileNameWithoutExtension(docs[d])}.pdf"), pdf);
        }

        // Throughput with one conversion per core running at once, on the 5-page feature tour.
        int workers = Environment.ProcessorCount, done = 0;
        var sw = Stopwatch.StartNew();
        Task.WaitAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            while (sw.Elapsed.TotalSeconds < 5) { Convert(docs[1]); Interlocked.Increment(ref done); }
        })));
        double perSecond = done / sw.Elapsed.TotalSeconds;

        // Peak memory, measured separately because sampling /proc slows the timed runs down: each document again,
        // then one conversion per core at once for two seconds.
        double peakMb;
        using (var memory = new MemorySampler())
        {
            foreach (var doc in docs) Convert(doc);
            sw.Restart();
            Task.WaitAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
            {
                foreach (var doc in docs) Convert(doc);
                while (sw.Elapsed.TotalSeconds < 2) Convert(docs[1]);
            })));
            peakMb = memory.PeakMb;
        }

        return new Result("pdfpecker", coldMs, medians, pages, sizes, perSecond, peakMb);
    }

    static byte[] Convert(string htmlPath) => PdfPecker.HtmlToPdf.Convert(File.ReadAllText(htmlPath),
        new PdfPecker.PdfOptions { BaseDirectory = Path.GetDirectoryName(htmlPath) });

    /// <summary>
    /// Runs PdfPecker in a fresh process of the given build of this program. .NET sizes the garbage collector's
    /// allocation budget from the CPU's L3 cache, so the same build peaks at 90 MB on one cloud VM and 190 MB on
    /// another; a fixed 16 MB budget keeps peak memory comparable between machines (unless DOTNET_GCgen0size is
    /// already set). Returns null when the run fails.
    /// </summary>
    public static Result? RunInChild(string assembly)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true };
        psi.ArgumentList.Add(assembly);
        psi.ArgumentList.Add("pdfpecker");
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
                runs[label].Add(Bench.RunInChild(assembly) ?? throw new InvalidOperationException($"The {label} build failed."));
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
/// Samples the proportional set size (PSS) of this process and every process it started. PSS splits shared pages
/// between the processes that map them, so they are not counted several times over. Linux only.
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
