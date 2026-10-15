using System.Diagnostics;
using PdfPecker;

// Memory and CPU needed per conversion, for the system requirements in the docs (pdfpecker.net/docs).
// Every measurement runs in a fresh child process, so its peak memory belongs to that one document alone:
//   - time and CPU of the first conversion (JIT included) and of later ones once warmed up,
//   - peak resident memory of the process and peak managed heap during a conversion,
//   - the smallest GC heap limit (DOTNET_GCHeapHardLimit) under which the conversion still succeeds.
// Usage: dotnet run -c Release -- --requirements [extra.html ...]
static class Requirements
{
    static readonly string[] Variants = { "default", "embed-fonts", "pdfa" };

    public static void Run(string[] args)
    {
        if (args[0] == "--requirements-child") { Child(args[1], args[2], args.Length > 3 ? args[3] : null, fitOnly: false); return; }
        if (args[0] == "--requirements-fit") { Child(args[1], args[2], args.Length > 3 ? args[3] : null, fitOnly: true); return; }

        var cases = new List<(string Name, string? File)>
        {
            ("invoice", null), ("feature-tour", null), ("report-30", null), ("report-1000", null),
        };
        foreach (var file in args.Skip(1)) cases.Add((Path.GetFileNameWithoutExtension(file), Path.GetFullPath(file)));

        Console.WriteLine($"PdfPecker requirements, .NET {Environment.Version}, {Environment.ProcessorCount} cores, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine();
        Console.WriteLine($"{"Case",-22} {"Variant",-12} {"Pages",6} {"First",9} {"Later",9} {"CPU/op",9} {"Alloc/op",10} {"Peak heap",10} {"Peak RSS",10} {"Idle RSS",9} {"Min heap",9}");
        foreach (var (name, file) in cases)
            foreach (var variant in Variants)
            {
                var line = Spawn(new[] { "--requirements-child", name, variant, file }, heapLimit: null, out _);
                long minHeap = MinHeap(name, variant, file);
                // Below 16 MB the runtime does not hold to the limit, so smaller results are not meaningful.
                Console.WriteLine($"{line} {(minHeap <= 16L << 20 ? "<=16MB" : $"{minHeap / 1048576.0:F0}MB"),9}");
            }
    }

    // Binary search for the smallest GC heap hard limit that still converts the document, to 2 MB.
    static long MinHeap(string name, string variant, string? file)
    {
        long lo = 0, hi = 32L << 20;
        while (!Fits(hi))
        {
            lo = hi;
            hi *= 2;
            if (hi > 8L << 30) return -1;
        }
        while (hi - lo > 2L << 20)
        {
            long mid = (lo + hi) / 2;
            if (Fits(mid)) hi = mid; else lo = mid;
        }
        return hi;

        bool Fits(long limit)
        {
            Spawn(new[] { "--requirements-fit", name, variant, file }, limit, out int code);
            return code == 0;
        }
    }

    static string Spawn(string?[] args, long? heapLimit, out int exitCode)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
        // Started as "dotnet PdfPecker.Benchmarks.dll" rather than through the apphost: pass the dll on.
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") psi.ArgumentList.Add(typeof(Requirements).Assembly.Location);
        foreach (var a in args) if (a != null) psi.ArgumentList.Add(a);
        if (heapLimit is long limit) psi.Environment["DOTNET_GCHeapHardLimit"] = limit.ToString("X");
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        exitCode = p.ExitCode;
        return output.Trim();
    }

    static (string Html, PdfOptions Options) Load(string name, string variant, string? file)
    {
        var htmlDir = Path.Combine(AppContext.BaseDirectory, "html");
        var (html, baseDir) = name switch
        {
            "invoice" when file == null => (File.ReadAllText(Path.Combine(htmlDir, "invoice.html")), htmlDir),
            "feature-tour" when file == null => (File.ReadAllText(Path.Combine(htmlDir, "report.html")), htmlDir),
            "report-30" when file == null => (Documents.LargeReport(), null),
            "report-1000" when file == null => (Documents.LargeReport(670), null),
            _ => (File.ReadAllText(file!), Path.GetDirectoryName(file)),
        };
        var options = new PdfOptions { BaseDirectory = baseDir };
        if (variant == "embed-fonts") options.EmbedStandardFonts = true;
        if (variant == "pdfa") options.Conformance = PdfConformance.PdfA2b;
        return (html, options);
    }

    static void Child(string name, string variant, string? file, bool fitOnly)
    {
        var (html, options) = Load(name, variant, file);
        if (fitOnly)
        {
            try { HtmlToPdf.Convert(html, options); }
            catch (OutOfMemoryException) { Environment.Exit(1); }
            return;
        }

        var self = Process.GetCurrentProcess();
        GC.Collect();
        self.Refresh();
        long idleRss = self.WorkingSet64;

        // Sample the managed heap during the first conversion; the GC frees between samples, so this is a lower bound
        // on the true peak, but sampling every millisecond catches it closely.
        long peakHeap = 0;
        bool done = false;
        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                long h = GC.GetTotalMemory(false);
                if (h > peakHeap) peakHeap = h;
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        sampler.Start();

        long t0 = Stopwatch.GetTimestamp();
        var pdf = HtmlToPdf.Convert(html, options);
        double first = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        // Peaks are taken after this one conversion: the warm-up below lets garbage pile up to the GC's budget, which
        // shows how lazy the GC is on an idle machine rather than what a conversion needs.
        Volatile.Write(ref done, true);
        sampler.Join();
        self.Refresh();
        long peakRss = self.PeakWorkingSet64;

        // Warm up so tiered JIT has produced optimized code, as in a long-running service.
        var warm = Stopwatch.StartNew();
        while (warm.Elapsed.TotalSeconds < 2) HtmlToPdf.Convert(html, options);

        int runs = name == "report-1000" ? 3 : 20;
        var times = new double[runs];
        var cpuBefore = self.TotalProcessorTime;
        long allocBefore = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < runs; i++)
        {
            t0 = Stopwatch.GetTimestamp();
            HtmlToPdf.Convert(html, options);
            times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
        self.Refresh();
        double cpu = (self.TotalProcessorTime - cpuBefore).TotalMilliseconds / runs;
        long alloc = (GC.GetTotalAllocatedBytes(true) - allocBefore) / runs;
        Array.Sort(times);

        int pages = CountPages(pdf);
        Console.Write($"{name,-22} {variant,-12} {pages,6} {Ms(first),9} {Ms(times[runs / 2]),9} {Ms(cpu),9} {Mb(alloc),10} {Mb(peakHeap),10} {Mb(peakRss),10} {Mb(idleRss),9}");
    }

    static string Ms(double ms) => ms >= 100 ? $"{ms:F0}ms" : $"{ms:F1}ms";
    static string Mb(long bytes) => $"{bytes / 1048576.0:F0}MB";

    static int CountPages(byte[] pdf)
    {
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var marker = "/Type /Pages /Count ";
        int i = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        int j = i;
        while (char.IsDigit(text[j])) j++;
        return int.Parse(text.AsSpan(i, j - i));
    }
}
