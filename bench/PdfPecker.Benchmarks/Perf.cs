using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfPecker;
using PdfPecker.Fonts;
using PdfPecker.Fonts.Cff;
using PdfPecker.Images;
using PdfPecker.Markdown;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Parsing;
using PdfPecker.Text;

// A short, machine-readable run for the performance regression check in .github/workflows/perf.yml: about 30 seconds,
// sixteen cases, JSON output. Allocation per conversion is deterministic, so the workflow compares it tightly; time is
// noisy on shared runners, so it is compared with a wide margin and only against runs on the same processor model.
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

        // Lossless WebP on its own, as the fixtures of decode-webp are small and mostly lossy: the lossless fixtures and
        // the first page of features.pdf at 96 dpi, written by the library's own encoder. One run decodes each once.
        var losslessWebps = Fixtures(fixtureDir, "lossless*.webp")
            .Append(PdfToImage.ConvertPage(pdf, 0, new ImageOptions { Dpi = 96, Format = PdfImageFormat.WebP })).ToArray();
        results.Add(Measure("decode-webp-lossless", () => { foreach (var w in losslessWebps) WebPDecoder.Decode(w, MaxPixels); }, runs: 50));

        // The JPEG and CCITT fax decoders, which photographs in pages and scanned PDFs go through: the JPEG fixtures, the
        // first page of features.pdf at 96 dpi written by the library's own JPEG encoder (4:2:0), and the same page at
        // 150 dpi in black and white as Group 4 fax data. One run decodes each once.
        var jpegs = Fixtures(fixtureDir, "*.jpg").Append(PdfToImage.ConvertPage(pdf, 0, new ImageOptions { Dpi = 96, Format = PdfImageFormat.Jpeg })).ToArray();
        var (fax, faxWidth, faxHeight) = Group4Page(pdf);
        results.Add(Measure("decode-jpeg-ccitt", () =>
        {
            foreach (var j in jpegs) PdfPecker.Rendering.Images.JpegDecoder.Decode(j);
            PdfPecker.Rendering.Images.CcittFaxDecoder.Decode(fax, -1, faxWidth, faxHeight, byteAlign: false, blackIs1: false);
        }, runs: 30));

        // The renderer's PDF functions and colour spaces, which shadings, tint transforms and images call per sample.
        var functions = new FunctionWorkload();
        results.Add(Measure("eval-functions", functions.Run, runs: 100));

        // The font tables: kerning pairs, ligature lookups, variation deltas (GDEF, GPOS, CFF2) and CFF parsing and
        // subsetting, on each test font. A pass loads every font afresh, so none of it is cached from the one before.
        var fonts = Fixtures(Path.Combine(fixtureDir, "fonts"), "*.ttf").Concat(Fixtures(Path.Combine(fixtureDir, "fonts"), "*.otf")).ToArray();
        results.Add(Measure("parse-font-tables", () => { foreach (var f in fonts) ParseFontTables(f); }, runs: 300));

        // The renderer's font programs: TrueType outlines and cmaps, CFF charstrings, a Type 1 program and an embedded
        // CMap. A pass parses every program afresh and traces every glyph, so no outline is cached from the one before.
        var rendererFonts = new RendererFontWorkload(fonts);
        results.Add(Measure("parse-render-fonts", rendererFonts.Run, runs: 50));

        // Font programs: every test font, WOFF, WOFF2 and collection files among them, is parsed afresh and the subset a
        // document would embed is built for a mixed text, which covers the sfnt reader, the character map, the WOFF
        // decoders and the sfnt builder. One run subsets every font once.
        var fontFiles = new[] { "*.ttf", "*.otf", "*.ttc", "*.woff", "*.woff2" }.SelectMany(p => Fixtures(Path.Combine(fixtureDir, "fonts"), p)).ToArray();
        results.Add(Measure("subset-fonts", () => { foreach (var f in fontFiles) SubsetFont(f); }, runs: 200));

        // The bidi algorithm: the levels, the L1 reset and the visual order of generated paragraphs that mix Arabic,
        // Hebrew and Latin letters, numbers, brackets, explicit embeddings and isolates. One run resolves them all once.
        var paragraphs = BidiParagraphs();
        results.Add(Measure("bidi-reorder", () => { foreach (var p in paragraphs) Reorder(p); }, runs: 300));

        // Opening PDFs and decoding every stream of them: the lexer, the filters and the decryptor, on the test fixtures
        // (RC4, AES-128 and an unencrypted file with an object stream) and on a generated 30-page report, plain and with
        // AES-256. One run opens and decodes each file once.
        var parseInputs = ParseInputs(fixtureDir, report);
        results.Add(Measure("parse-pdf", () => { foreach (var (data, password) in parseInputs) OpenAndDecodeAll(data, password); }, runs: 60));

        // The Markdown parser: block structure and inlines of a large document, with every kind of block (nested lists,
        // task items, quotes, fences, indented code, tables, HTML blocks, reference definitions) and the sample guide.
        var markdown = MarkdownDocument(Path.Combine(htmlDir, "guide.md"));
        results.Add(Measure("parse-markdown", () => new BlockParser(gfm: true).Parse(markdown), runs: 100));

        // Throughput with every core busy; documents per second is the figure, so higher is better.
        var options = new PdfOptions { BaseDirectory = htmlDir };
        for (int i = 0; i < 20; i++) HtmlToPdf.Convert(invoice, options);
        int total = 400;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, total, _ => HtmlToPdf.Convert(invoice, options));
        sw.Stop();
        results.Add(("throughput-invoice", sw.Elapsed.TotalMilliseconds / total, 0, total / sw.Elapsed.TotalSeconds));

        var cpu = CpuModel().Replace("\\", "", StringComparison.Ordinal).Replace("\"", "", StringComparison.Ordinal);
        Console.WriteLine($"{cpu}, {Environment.ProcessorCount} cores");
        var json = new StringBuilder($"{{\n  \"cpu\": \"{cpu}\",\n  \"cores\": {Environment.ProcessorCount},\n  \"cases\": [\n");
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

    /// <summary>The first page of a PDF at 150 dpi, thresholded to black and white and coded as Group 4 fax data.</summary>
    static (byte[] Data, int Width, int Height) Group4Page(byte[] pdf)
    {
        var page = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 150 });
        var rgba = page.Rgba;
        var black = new byte[page.Width * page.Height];
        for (int i = 0; i < black.Length; i++) black[i] = rgba[i * 4] + rgba[i * 4 + 1] + rgba[i * 4 + 2] < 3 * 128 ? (byte)1 : (byte)0;
        return (PdfPecker.Rendering.CcittFaxEncoder.EncodeGroup4(black, page.Width, page.Height), page.Width, page.Height);
    }

    // About 200 KB of Markdown, the same on every run: sections that cycle through the block kinds, then the sample guide.
    static string MarkdownDocument(string guidePath)
    {
        var sb = new StringBuilder();
        sb.Append("[docs]: https://example.com/docs \"Documentation\"\n[api]: <https://example.com/api> 'API'\n\n");
        for (int s = 1; s <= 120; s++)
        {
            sb.Append("# Section ").Append(s).Append("\n\n");
            sb.Append("Lorem ipsum dolor sit amet, *consectetur* adipiscing elit, **sed do eiusmod** tempor incididunt ut labore, see [the docs] and [the api][api] or `code`.\n");
            sb.Append("A second line of the same paragraph with a [link](https://example.com/").Append(s).Append(") and an ![image](a.png).\n\n");
            sb.Append("Setext heading ").Append(s).Append("\n---\n\n");
            sb.Append("- first item\n- [ ] open task\n- [x] done task\n  1. nested one\n  2. nested two\n     - deeper\n       > quoted in a list\n- last item\n\n");
            sb.Append("> A block quote that spans\nlazy continuation\n>\n> > and nests\n\n");
            sb.Append("```csharp\nvar x = ").Append(s).Append(";\nConsole.WriteLine(x);\n```\n\n");
            sb.Append("    indented code\n\n    more code\n\n");
            sb.Append("| Name | Qty | Price |\n|:-----|:---:|------:|\n| Apple | ").Append(s).Append(" | 1.20 |\n| Pear | 2 |\n| Plum | 3 | 0.80 | extra |\n\n");
            sb.Append("<div class=\"note\">\nraw *html*\n</div>\n\n<!-- a comment\nover lines -->\n\n***\n\n");
        }
        sb.Append(File.ReadAllText(guidePath));
        return sb.ToString();
    }

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

    // The text of a document with Latin, accented, Arabic, Hebrew and CJK characters, numbers and punctuation.
    const string SubsetText = "Hamburgefonstiv fi ffl AV To Wa 0123456789 .,;:!?()[]{} ÆØÅ éèêëàâäîïôöûü ñç ß €£¥ العربية שלום 日本語 कि";

    static void SubsetFont(byte[] data)
    {
        foreach (var font in FontProgram.Load(data))
        {
            var subset = new GlyphSubset(font);
            foreach (char c in SubsetText) subset.Cid(c);
            FontSubsetter.Build(subset);
        }
    }

    // Paragraphs of 1 to 60 characters drawn from runs of each script and class, the same ones on every run, with a
    // paragraph direction of its own, left to right or right to left; a few nest embeddings past the depth limit.
    static (BidiClass[] Types, int[] CodePoints, int Level)[] BidiParagraphs()
    {
        string[] pools =
        {
            "abcdefgXYZ", "\u0627\u0628\u062A\u0644\u0645\u0646\u0648\u064A", "\u05D0\u05D1\u05D2\u05D3\u05E9\u05DC\u05DD", "0123456789",
            "\u0660\u0661\u0662\u0663", " ", ".,:/", "+-", "%$#\u20AC", "()[]{}\u00AB\u00BB", "\u202A\u202B\u202C\u202D\u202E", "\u2066\u2067\u2068\u2069",
            "\t", "\u200B\u00AD", "\u0300\u064E\u05B0", "!?\"'*",
        };
        var rng = new Random(7);
        var paragraphs = new (BidiClass[], int[], int)[600];
        for (int i = 0; i < paragraphs.Length; i++)
        {
            var text = new StringBuilder();
            if (i % 50 == 0)
            {
                for (int d = 0; d < 130; d++) text.Append(d % 2 == 0 ? '\u2067' : '\u202B');
                text.Append("ab\u05D0\u05D1(1)");
            }
            else
                for (int k = 0, length = 1 + rng.Next(60); k < length; k++)
                {
                    var pool = pools[rng.Next(pools.Length)];
                    text.Append(pool[rng.Next(pool.Length)]);
                }
            var codePoints = text.ToString().Select(c => (int)c).ToArray();
            paragraphs[i] = (codePoints.Select(UnicodeTables.Bidi).ToArray(), codePoints, rng.Next(3) - 1);
        }
        return paragraphs;
    }

    static void Reorder((BidiClass[] Types, int[] CodePoints, int Level) paragraph)
    {
        int level = paragraph.Level < 0 ? Bidi.FirstStrongLevel(paragraph.Types) : paragraph.Level;
        var levels = Bidi.ResolveLevels(paragraph.Types, paragraph.CodePoints, paragraph.Level);
        Bidi.ResetWhitespace(paragraph.Types, levels, level);
        Bidi.VisualOrder(levels);
    }

    static (byte[] Data, string? Password)[] ParseInputs(string fixtureDir, string report)
    {
        var generated = HtmlToPdf.Convert(report);
        var security = new PdfSecurity { UserPassword = "user", OwnerPassword = "owner" };
        return
        [
            (File.ReadAllBytes(Path.Combine(fixtureDir, "features.pdf")), null),
            (File.ReadAllBytes(Path.Combine(fixtureDir, "objstm-cairo.pdf")), null),
            (File.ReadAllBytes(Path.Combine(fixtureDir, "encrypted-rc4-40.pdf")), "user"),
            (File.ReadAllBytes(Path.Combine(fixtureDir, "encrypted-aes128.pdf")), "user"),
            (File.ReadAllBytes(Path.Combine(fixtureDir, "password.pdf")), "secret"),
            (generated, null),
            (PdfProtector.Encrypt(generated, security), "user"),
        ];
    }

    // Image streams are left encoded, as the renderer does until it needs their pixels.
    static void OpenAndDecodeAll(byte[] data, string? password)
    {
        var file = PdfFile.Open(data, password);
        int last = file.MaxObjectNumber;
        for (int number = 1; number <= last; number++)
        {
            if (file.GetObject(number) is PdfStream stream) file.DecodeStream(stream, stopAtImageFilter: true, out _, out _);
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

        // The runs are timed in rounds and the median round is reported, so a burst from a noisy neighbour on a shared
        // runner moves one round instead of the whole figure. Each round starts from a collected heap, so a case does not
        // pay for garbage the one before it left behind.
        int perRound = Math.Max(1, runs / Rounds);
        var rounds = new double[Rounds];
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int r = 0; r < Rounds; r++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < perRound; i++) run();
            rounds[r] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds / perRound;
        }
        long alloc = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / (perRound * Rounds);
        Array.Sort(rounds);
        return (name, rounds[Rounds / 2], alloc, null);
    }

    const int Rounds = 5;

    // The processor model, so the comparison can tell a slower runner from slower code. GitHub's hosted runners are
    // drawn from several processor generations, which differ by a third or more in speed.
    static string CpuModel()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.Ordinal)) return line[(line.IndexOf(':') + 1)..].Trim();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    }
}

/// <summary>
/// One run parses each test font with the renderer's TrueType reader, traces every glyph and looks the printable ASCII
/// characters up in its Unicode cmap; parses the CFF table of each OpenType font and traces its glyphs; parses a Type 1
/// program and traces its glyphs; and parses an embedded CMap and maps a run of two-byte codes through it. The Type 1
/// program and the CMap are built here, because no fixture holds one: the program has hints, flex, subroutines and
/// accented composites, and the CMap has cidrange, cidchar, bfchar and bfrange sections of the sizes real fonts write.
/// </summary>
sealed class RendererFontWorkload
{
    const int CMapCodes = 0x4000;

    readonly byte[][] _sfnts, _cffs;
    readonly byte[] _type1, _cmap;
    readonly int _type1Length1;
    long _checksum;

    public RendererFontWorkload(byte[][] fonts)
    {
        _sfnts = fonts;
        _cffs = fonts.Select(TrueTypeFont.Parse).Select(f => f?.CffTable).OfType<byte[]>().ToArray();
        (_type1, _type1Length1) = Type1Program();
        _cmap = Encoding.ASCII.GetBytes(CMapText());
    }

    public void Run()
    {
        long sum = 0;
        foreach (var data in _sfnts)
        {
            var font = TrueTypeFont.Parse(data)!;
            sum += Trace(font);
            for (int c = 0x20; c < 0x7F; c++) sum += font.Lookup(3, 1, c);
        }
        foreach (var data in _cffs) sum += Trace(CffFont.Parse(data)!);
        sum += Trace(Type1Font.Parse(_type1, _type1Length1)!);

        var cmap = CMap.Parse(_cmap)!;
        Span<byte> bytes = stackalloc byte[2];
        for (int code = 0; code < CMapCodes; code++)
        {
            bytes[0] = (byte)(code >> 8);
            bytes[1] = (byte)code;
            int pos = 0;
            sum += cmap.ToCid(cmap.NextCode(bytes, ref pos, out _)) + (cmap.ToUnicode((uint)code)?.Length ?? 0);
        }
        _checksum += sum;
    }

    static long Trace(IGlyphSource font)
    {
        long sum = 0;
        for (int gid = 0; gid < font.GlyphCount; gid++)
        {
            if (font.GetGlyph(gid) is { } path) sum += path.Points.Count;
            sum += (long)font.Advance(gid);
        }
        return sum;
    }

    // Sixty glyphs of lines and curves, each with stem hints and a flex, a subroutine for hint replacement, and ten
    // accented composites (seac), eexec-encrypted as a PDF embeds it. The cleartext length is what FontFile's Length1 says.
    static (byte[] Program, int Length1) Type1Program()
    {
        const string clear = "%!PS-AdobeFont-1.0: PerfType1 1.0\n12 dict begin\n/FontName /PerfType1 def\n/PaintType 0 def\n/FontType 1 def\n" +
            "/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n/Encoding StandardEncoding def\n/FontBBox {0 0 1000 1000} readonly def\n" +
            "currentdict end\ncurrentfile eexec\n";
        var random = new Random(1);
        var glyphs = new List<(string Name, string Program)> { (".notdef", "0 500 hsbw endchar") };
        for (int i = 0; i < 60; i++)
        {
            var program = new StringBuilder($"0 {500 + random.Next(300)} hsbw 0 {random.Next(50)} hstem {random.Next(50, 100)} 40 vstem 0 callsubr ");
            program.Append($"{random.Next(50, 100)} {random.Next(50, 100)} rmoveto ");
            for (int segment = random.Next(3, 12); segment > 0; segment--)
            {
                program.Append(random.Next(3) == 0
                    ? $"{random.Next(-200, 200)} {random.Next(-200, 200)} rlineto "
                    : $"{random.Next(-150, 150)} {random.Next(-150, 150)} {random.Next(-150, 150)} {random.Next(-150, 150)} {random.Next(-150, 150)} {random.Next(-150, 150)} rrcurveto ");
            }
            program.Append("1 callsubr ");
            for (int point = 0; point < 7; point++) program.Append($"{random.Next(-40, 40)} {random.Next(-40, 40)} rmoveto 2 callsubr ");
            program.Append($"50 {random.Next(500)} {random.Next(500)} 0 callothersubr pop pop setcurrentpoint closepath endchar");
            glyphs.Add(($"g{i}", program.ToString()));
        }
        for (int i = 0; i < 10; i++) glyphs.Add(($"c{i}", $"0 600 hsbw 0 {random.Next(100, 300)} {random.Next(100, 300)} {65 + i} {65 + 32 + i} seac"));
        // The standard Subrs 0 to 3 drive flex and hint replacement; Subr 4 is a hint replacement body.
        string[] subrs =
        {
            "3 0 callothersubr pop pop setcurrentpoint return", "0 1 callothersubr return", "0 2 callothersubr return", "return",
            "0 50 hstem 100 40 vstem return",
        };

        var encrypted = new List<byte>();
        void Text(string s) => encrypted.AddRange(Encoding.Latin1.GetBytes(s));
        void Binary(string intro, string program, string outro)
        {
            var bytes = Encrypt(Charstring(program), 4330);
            Text($"{intro}{bytes.Length} RD ");
            encrypted.AddRange(bytes);
            Text($" {outro}\n");
        }
        Text("dup /Private 8 dict dup begin\n/RD {string currentfile exch readstring pop} executeonly def\n/ND {noaccess def} executeonly def\n" +
            "/NP {noaccess put} executeonly def\n/BlueValues [] def\n/password 5839 def\n");
        Text($"/Subrs {subrs.Length} array\n");
        for (int i = 0; i < subrs.Length; i++) Binary($"dup {i} ", subrs[i], "NP");
        Text("ND\n");
        Text($"2 index /CharStrings {glyphs.Count} dict dup begin\n");
        foreach (var (name, program) in glyphs) Binary($"/{name} ", program, "ND");
        Text("end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");
        var cleartext = Encoding.Latin1.GetBytes(clear);
        return (cleartext.Concat(Encrypt(encrypted.ToArray(), 55665)).ToArray(), cleartext.Length);
    }

    // The Type 1 cipher with four leading bytes, for eexec and for charstrings alike.
    static byte[] Encrypt(byte[] plain, int key)
    {
        var output = new byte[plain.Length + 4];
        int r = key;
        for (int i = 0; i < output.Length; i++)
        {
            int c = (i < 4 ? 0 : plain[i - 4]) ^ (r >> 8);
            r = ((c + r) * 52845 + 22719) & 0xFFFF;
            output[i] = (byte)c;
        }
        return output;
    }

    static byte[] Charstring(string program)
    {
        var bytes = new List<byte>();
        foreach (var token in program.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out int n))
            {
                if (n is >= -107 and <= 107) bytes.Add((byte)(n + 139));
                else if (n is >= 108 and <= 1131) bytes.AddRange(new[] { (byte)(247 + (n - 108 >> 8)), (byte)(n - 108) });
                else if (n is >= -1131 and <= -108) bytes.AddRange(new[] { (byte)(251 + (-n - 108 >> 8)), (byte)(-n - 108) });
                else bytes.AddRange(new[] { (byte)255, (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n });
                continue;
            }
            bytes.AddRange(token switch
            {
                "hstem" => new byte[] { 1 }, "vstem" => new byte[] { 3 }, "vmoveto" => new byte[] { 4 }, "rlineto" => new byte[] { 5 },
                "hlineto" => new byte[] { 6 }, "vlineto" => new byte[] { 7 }, "rrcurveto" => new byte[] { 8 }, "closepath" => new byte[] { 9 },
                "callsubr" => new byte[] { 10 }, "return" => new byte[] { 11 }, "hsbw" => new byte[] { 13 }, "endchar" => new byte[] { 14 },
                "rmoveto" => new byte[] { 21 }, "hmoveto" => new byte[] { 22 }, "seac" => new byte[] { 12, 6 }, "callothersubr" => new byte[] { 12, 16 },
                "pop" => new byte[] { 12, 17 }, "setcurrentpoint" => new byte[] { 12, 33 },
                _ => throw new ArgumentException(token),
            });
        }
        return bytes.ToArray();
    }

    // An embedded CMap of the shape subsetters write: a two-byte codespace, CID ranges and single CIDs, and a ToUnicode
    // half with single characters, a ligature string and ranges, including one with a destination array.
    static string CMapText()
    {
        var text = new StringBuilder("%!PS-Adobe-3.0 Resource-CMap\n/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> def\n/CMapName /Perf-H def\n/CMapType 1 def\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        text.Append("100 begincidrange\n");
        for (int i = 0; i < 100; i++) text.Append($"<{i * 64:X4}> <{i * 64 + 47:X4}> {i * 48 + 1}\n");
        text.Append("endcidrange\n100 begincidchar\n");
        for (int i = 0; i < 100; i++) text.Append($"<{0x2000 + i * 3:X4}> {7000 + i}\n");
        text.Append("endcidchar\n100 beginbfchar\n");
        for (int i = 0; i < 100; i++) text.Append($"<{i * 64:X4}> <{0x20 + i:X4}>\n");
        text.Append("<3000> <00660066006C>\nendbfchar\n100 beginbfrange\n");
        for (int i = 0; i < 99; i++) text.Append($"<{i * 64 + 1:X4}> <{i * 64 + 47:X4}> <{0x100 + i * 47:X4}>\n");
        text.Append("<3100> <3102> [<0041> <0042> <0043>]\nendbfrange\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return text.ToString();
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
