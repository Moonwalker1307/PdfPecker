using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfPecker;
using PdfPecker.Images;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Parsing;

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

        // The renderer's PDF functions and colour spaces, which shadings, tint transforms and images call per sample.
        var functions = new FunctionWorkload();
        results.Add(Measure("eval-functions", functions.Run, runs: 100));

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

/// <summary>
/// One run evaluates a sampled, an exponential, a stitching and two PostScript functions over a few thousand inputs,
/// converts as many colours through CMYK, Lab, CalRGB, Separation and DeviceN, and parses an Indexed space and a
/// PostScript function, whose palette and program are built when they are parsed. The functions come from a small PDF
/// built here, because the fixtures are made by the library and hold only exponential and stitching functions.
/// </summary>
sealed class FunctionWorkload
{
    const int Steps = 4096, GridSide = 64;

    readonly PdfFile _file;
    readonly PdfFunction[] _oneInput, _twoInputs;
    readonly ColorSpace[] _spaces;
    readonly PdfObject _indexed, _program;
    double _checksum;

    public FunctionWorkload()
    {
        var objects = new List<(string Dict, byte[]? Stream)>();
        int Add(string dict, byte[]? stream = null)
        {
            objects.Add((dict, stream));
            return objects.Count + 1;
        }
        var random = new Random(1);
        byte[] Bytes(int count)
        {
            var bytes = new byte[count];
            random.NextBytes(bytes);
            return bytes;
        }

        int sampled = Add("/FunctionType 0 /Domain [0 1] /Range [0 1 0 1 0 1] /Size [256] /BitsPerSample 8", Bytes(768));
        int grid = Add("/FunctionType 0 /Domain [0 1 0 1] /Range [0 1 0 1 0 1] /Size [16 16] /BitsPerSample 16", Bytes(1536));
        int gamma = Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 2.2 >>");
        int linear = Add("<< /FunctionType 2 /Domain [0 1] /C0 [0 1 0] /C1 [1 1 0] /N 1 >>");
        int stitching = Add($"<< /FunctionType 3 /Domain [0 1] /Functions [{gamma} 0 R {linear} 0 R {gamma} 0 R] /Bounds [0.3 0.6] /Encode [0 1 1 0 0 1] >>");
        int ifElse = Add("/FunctionType 4 /Domain [0 1] /Range [0 1 0 1 0 1]",
            Encoding.ASCII.GetBytes("{ dup 0.5 gt { pop 1 0 0 } { 2 mul 0 0 } ifelse }"));
        int stackOps = Add("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]",
            Encoding.ASCII.GetBytes("{ 2 copy mul 3 1 roll 2 copy add 2 div 4 1 roll sub abs 90 mul sin dup 0.5 lt { 1 exch sub } if }"));
        int indexed = Add($"[/Indexed /DeviceRGB 255 <{Convert.ToHexString(Bytes(768))}>]");
        int[] spaces =
        {
            Add("/DeviceCMYK"),
            Add("[/Lab << /WhitePoint [0.9505 1 1.089] >>]"),
            Add("[/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [2.2 2.2 2.2] /Matrix [0.4124 0.2126 0.0193 0.3576 0.7152 0.1192 0.1805 0.0722 0.9505] >>]"),
            Add($"[/Separation /Spot /DeviceRGB {ifElse} 0 R]"),
            Add($"[/DeviceN [/A /B] /DeviceRGB {grid} 0 R]"),
        };

        _file = PdfFile.Open(BuildPdf(objects), null);
        _oneInput = new[] { sampled, gamma, stitching, ifElse }.Select(Function).ToArray();
        _twoInputs = new[] { grid, stackOps }.Select(Function).ToArray();
        _spaces = spaces.Select(number => ColorSpace.Parse(_file, _file.GetObject(number), null)!).ToArray();
        _indexed = _file.GetObject(indexed)!;
        _program = _file.GetObject(stackOps)!;
    }

    PdfFunction Function(int number) => PdfFunction.Create(_file, _file.GetObject(number))!;

    public void Run()
    {
        Span<double> input = stackalloc double[2], output = stackalloc double[4];
        double sum = 0;
        foreach (var function in _oneInput)
        {
            for (int i = 0; i < Steps; i++)
            {
                input[0] = i / (double)(Steps - 1);
                function.Eval(input, output);
                sum += output[0];
            }
        }
        foreach (var function in _twoInputs)
        {
            for (int i = 0; i < GridSide * GridSide; i++)
            {
                input[0] = i % GridSide / (double)(GridSide - 1);
                input[1] = i / GridSide / (double)(GridSide - 1);
                function.Eval(input, output);
                sum += output[0];
            }
        }
        Span<float> color = stackalloc float[4];
        foreach (var space in _spaces)
        {
            for (int i = 0; i < Steps; i++)
            {
                float t = i / (float)(Steps - 1);
                color[0] = t;
                color[1] = 1 - t;
                color[2] = t * t;
                color[3] = 0.5f * t;
                sum += space.ToRgb(color[..space.Components]);
            }
        }
        sum += ColorSpace.Parse(_file, _indexed, null)!.ToRgb(stackalloc float[] { 7 });
        sum += PdfFunction.Create(_file, _program)!.Outputs;
        _checksum += sum;
    }

    static byte[] BuildPdf(List<(string Dict, byte[]? Stream)> objects)
    {
        var pdf = new MemoryStream();
        void Write(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
        var offsets = new List<long>();
        Write("%PDF-1.7\n");
        offsets.Add(pdf.Position);
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        for (int i = 0; i < objects.Count; i++)
        {
            var (dict, stream) = objects[i];
            offsets.Add(pdf.Position);
            if (stream == null)
            {
                Write($"{i + 2} 0 obj\n{dict}\nendobj\n");
                continue;
            }
            Write($"{i + 2} 0 obj\n<< {dict} /Length {stream.Length} >>\nstream\n");
            pdf.Write(stream);
            Write("\nendstream\nendobj\n");
        }
        long xref = pdf.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }
}
