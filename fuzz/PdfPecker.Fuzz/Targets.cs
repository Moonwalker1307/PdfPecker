using System.Text;
using PdfPecker.Images;
using PdfPecker.Rendering.Images;

namespace PdfPecker.Fuzz;

/// <summary>One thing to fuzz: how to run an input through it, where its seeds come from and what it may throw.</summary>
internal sealed class Target
{
    public required string Name { get; init; }
    public required string Extension { get; init; }
    public required Action<byte[], CancellationToken> Run { get; init; }
    public required Func<SeedContext, IEnumerable<(string Name, byte[] Data)>> Seeds { get; init; }
    public bool Text { get; init; }
    public int MaxSize { get; init; } = 512 * 1024;
    public string[] Dictionary { get; init; } = Array.Empty<string>();
    public (string Open, string Close)[] Nesters { get; init; } = Array.Empty<(string, string)>();

    /// <summary>
    /// A per-input time limit for targets whose legitimate worst case at <see cref="MaxSize"/> is slower than the
    /// default --timeout. It only raises the limit; a longer --timeout still wins.
    /// </summary>
    public TimeSpan MinTimeout { get; init; }

    /// <summary>The time limit for one input: --timeout, or <see cref="MinTimeout"/> when that is longer.</summary>
    public TimeSpan TimeoutFor(TimeSpan requested) => requested > MinTimeout ? requested : MinTimeout;

    /// <summary>Also accept the exceptions that the library's own callers catch around this decoder (see <see cref="IsExpected"/>).</summary>
    public bool LenientDecoder { get; init; }

    public Func<Exception, bool>? ExtraExpected { get; init; }

    /// <summary>
    /// The failure modes the public API documents for bad input. Anything else escaping is a bug. The image decoders are
    /// internal and their caller, ImageLoader, also swallows I/O, argument, overflow and index errors so that one
    /// damaged picture does not fail a document; those are only counted, unless --strict is given.
    /// </summary>
    public bool IsExpected(Exception ex, bool strict)
    {
        if (ex is FormatException or InvalidDataException or PdfLimitException or PdfPasswordException or OperationCanceledException or NotSupportedException)
            return true;
        if (LenientDecoder && !strict && ex is IOException or ArgumentException or OverflowException or IndexOutOfRangeException)
            return true;
        return ExtraExpected?.Invoke(ex) ?? false;
    }
}

internal static class Targets
{
    private static byte[] Html(byte[] input, CancellationToken ct) => HtmlToPdf.Convert(Encoding.UTF8.GetString(input), new PdfOptions { MaxPages = 100 }, ct);

    private static readonly string[] HtmlWords =
    {
        "<table>", "</table>", "<tr>", "<td colspan=2>", "<td rowspan=0>", "<div>", "</div>", "<span>", "<p>", "<ul>", "<li>", "<ol>", "<form>", "<input type=radio>",
        "<select>", "<option>", "<textarea>", "<svg>", "<math>", "<img src=\"data:image/png;base64,\">", "<style>", "</style>", "<a href=\"#a\">", "<br>", "<hr>",
        "<h1>", "<pre>", "<center>", "<font>", "<details>", "<template>", "<!doctype html>", "&#0;", "&#x110000;", "&amp", "&notin;", "style=\"", "class=\"a\"", "id=\"a\"",
        "display:table-cell", "display:flex", "display:grid", "position:absolute", "float:left", "width:50%", "height:1e9px", "margin:-1e9px", "column-count:99",
        "page-break-before:always", "overflow:hidden", "transform:scale(1e9)", "content:\"x\"", "counter-increment:a 999999999", "font-size:0", "line-height:0", "<![CDATA[",
        "backdrop-filter:blur(99px)", "mask:linear-gradient(red,#0000) exclude", "-webkit-mask-image:url(#a)", "<mask id=\"a\">",
        "shape-outside:circle(50%)", "shape-outside:polygon(0 0,1e9px 0,0 100%)", "shape-outside:linear-gradient(red,#0000)", "shape-margin:1e9px",
        "shape-image-threshold:.5",
        "<style>*::first-letter{initial-letter:3 2}</style>", "<style>p::first-letter{initial-letter:1e9 raise;padding:1e9px}</style>",
    };

    private static readonly (string, string)[] HtmlNesters =
    {
        ("<div>", "</div>"), ("<span>", "</span>"), ("<b>", "</b>"), ("<table><tr><td>", "</td></tr></table>"), ("<ul><li>", "</li></ul>"), ("<p>", "</p>"),
        ("<div style=\"display:flex\">", "</div>"), ("<div style=\"float:left\">", "</div>"), ("<a href=\"#\">", "</a>"), ("<svg><g>", "</g></svg>"),
        ("<div style=\"position:relative;overflow:hidden\">", "</div>"), ("<blockquote>", "</blockquote>"), ("<details>", "</details>"), ("<i>", "</i>"),
        ("<div style=\"backdrop-filter:invert(1);mask:radial-gradient(#000,#0000)\">", "</div>"),
        ("<div style=\"float:right;width:50%;height:9em;shape-outside:ellipse(at 0 50%);shape-margin:1em\">", "</div>"),
    };

    private static readonly string[] CssWords =
    {
        "var(--a)", "calc(", "min(", "max(", "clamp(", "!important", "@media print {", "@media (min-width: 1px) {", "@supports (display:grid) {", "@page {", "@font-face {", "@keyframes a {",
        "@import url(x);", "@container (min-width:1px) {", "@counter-style a {", ":is(", ":not(", ":where(", ":has(", ":nth-child(2n+1 of p)", "::before", "::after", "::marker",
        "*", ">", "~", "+", "|", "[a=b i]", "url(data:,)", "linear-gradient(", "radial-gradient(", "conic-gradient(", "repeating-linear-gradient(", "rotate(", "matrix(",
        "attr(a)", "counter(a)", "counters(a, \".\")", "content:", "display:grid", "grid-template-columns: repeat(999999, 1fr)", "grid-column: 1 / span 99999", "flex: 1e9",
        "1e9px", "-1e9px", "100000%", "calc(1/0)", "calc(0/0)", "inherit", "initial", "unset", "revert", "all:unset", "--a:", "var(--a, var(--b, var(--a)))",
        "mask-image:", "-webkit-mask:", "mask-composite: exclude", "mask-mode: luminance", "mask-size: 1e9px", "url(#m)", "backdrop-filter: blur(1e9px)", "-webkit-backdrop-filter:",
        "shape-outside:", "inset(", "ellipse(", "polygon(", "path(", "margin-box", "content-box", "shape-margin: 1e9px", "shape-image-threshold: 1e9",
        "::first-letter", "initial-letter: 3", "initial-letter: 1e9 999999", "initial-letter: 2 raise",
    };

    private static readonly (string, string)[] CssNesters =
    {
        ("{", "}"), ("(", ")"), ("[", "]"), ("calc(", ")"), ("var(--a, ", ")"), (":is(", ")"), (":not(", ")"), ("@media print {", "}"), ("@supports (a:b) {", "}"),
        ("min(", ")"), ("div ", ""), ("a > ", ""), ("b ~ ", ""), ("linear-gradient(", ")"), ("\"", "\""), ("url(", ")"),
    };

    private static readonly string[] SvgWords =
    {
        "<g>", "</g>", "<defs>", "<use href=\"#a\"/>", "<symbol id=\"a\">", "<pattern id=\"a\" width=\"1\" height=\"1\">", "<mask id=\"a\">", "<clipPath id=\"a\">", "<marker id=\"a\">",
        "<filter id=\"a\">", "<feGaussianBlur stdDeviation=\"1e9\"/>", "<feTurbulence numOctaves=\"99999\"/>", "<feConvolveMatrix order=\"99999\"/>", "<feMorphology radius=\"1e9\"/>",
        "<feImage href=\"#a\"/>", "<feComposite operator=\"arithmetic\"/>", "<text>", "<tspan>", "<textPath href=\"#a\">", "<image href=\"#a\"/>", "<foreignObject>", "<switch>",
        "<linearGradient id=\"a\" href=\"#a\">", "<radialGradient id=\"a\" href=\"#a\">", "<stop offset=\"1e9\"/>", "<path d=\"M0 0 A 1e9 1e9 0 1 1 1 1\"/>", "<path d=\"M0 0 L1e9 1e9\"/>",
        "stroke-dasharray=\"0.0001\"", "stroke-width=\"1e9\"", "transform=\"scale(1e9)\"", "transform=\"matrix(0 0 0 0 0 0)\"", "viewBox=\"0 0 0 0\"", "viewBox=\"0 0 1e9 1e9\"",
        "width=\"1e9\"", "filter=\"url(#a)\"", "mask=\"url(#a)\"", "clip-path=\"url(#a)\"", "marker-start=\"url(#a)\"", "fill=\"url(#a)\"", "href=\"#a\"", "font-size=\"1e9\"", "textLength=\"1e9\"",
    };

    private static readonly (string, string)[] SvgNesters =
    {
        ("<g>", "</g>"), ("<g transform=\"scale(2)\">", "</g>"), ("<g opacity=\".5\">", "</g>"), ("<g filter=\"url(#a)\">", "</g>"), ("<g mask=\"url(#a)\">", "</g>"), ("<g clip-path=\"url(#a)\">", "</g>"),
        ("<svg>", "</svg>"), ("<symbol id=\"a\">", "</symbol>"), ("<pattern id=\"a\" width=\"5\" height=\"5\" patternUnits=\"userSpaceOnUse\">", "</pattern>"), ("<defs>", "</defs>"),
        ("<a>", "</a>"), ("<text>", "</text>"), ("<tspan>", "</tspan>"), ("<marker id=\"a\">", "</marker>"),
    };

    private static readonly string[] MarkdownWords =
    {
        "# ", "## ", "> ", "- ", "* ", "1. ", "```", "~~~", "    ", "\t", "| a | b |", "|---|---|", "[a](b)", "![a](b)", "[a][b]", "[b]: c", "[^1]", "[^1]: x", "[^a]: ", "\n    ", ": ", "\n: x\n", "---\ntitle: x\n---\n", "<div>", "</div>", "<!--",
        "**", "__", "*", "_", "`", "``", "~~", "<http://a.b>", "&amp;", "&#0;", "- [ ] ", "---", "***", "===", "\\", "  \n", "<script>", "www.a.b", "<a href=\"", "<br>",
    };

    private static readonly (string, string)[] MarkdownNesters =
    {
        ("> ", ""), ("- ", ""), ("  - ", ""), ("1. ", ""), ("*", "*"), ("**", "**"), ("[", "](x)"), ("[", "]"), ("(", ")"), ("`", "`"), ("<div>", "</div>"), ("![", "](x)"), ("~~", "~~"), ("| ", " |"), ("[^a]: ", ""), (": ", ""),
    };

    public static IReadOnlyList<Target> All { get; } = Build();

    private static List<Target> Build()
    {
        var pdfSeeds = (SeedContext c) => c.Pdfs();
        var list = new List<Target>
        {
            new()
            {
                Name = "pdf-render", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                Run = (d, ct) =>
                {
                    var renderer = PdfRenderer.Open(d);
                    int pages = Math.Min(renderer.PageCount, 3);
                    var options = new ImageOptions { Dpi = 72, MaxDegreeOfParallelism = 1 };
                    for (int i = 0; i < pages; i++)
                    {
                        renderer.GetPageSize(i);
                        renderer.RenderPageToPng(i, options, ct);
                    }
                },
            },
            new()
            {
                Name = "pdf-text", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                Run = (d, ct) => PdfText.Extract(d, new PdfTextOptions { MaxPages = 200 }, ct),
            },
            new()
            {
                Name = "pdf-html", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                Run = (d, ct) =>
                {
                    int pages = Math.Min(PdfToImage.GetPageCount(d), 3);
                    if (pages == 0) return;
                    PdfToHtml.Convert(d, new PdfHtmlOptions
                    {
                        Pages = PdfPageRange.Parse("1-" + pages.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Dpi = 36, ImageFormat = PdfImageFormat.Png, MaxDegreeOfParallelism = 1,
                    }, ct);
                },
            },
            new()
            {
                Name = "pdf-merge", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                Run = (d, ct) => PdfMerger.Merge(new[] { d, SeedContext.Small.Value }, new PdfMergeOptions { MaxPages = 500 }, ct),
            },
            new()
            {
                Name = "pdf-form", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                // Flattening a signed PDF is a documented failure that a mutated input can legitimately trigger.
                ExtraExpected = ex => ex is InvalidOperationException,
                Run = (d, ct) =>
                {
                    var fields = PdfForm.GetFields(d);
                    var values = new Dictionary<string, string>();
                    foreach (var f in fields.Take(8)) values[f.Name] = "Fuzz";
                    if (values.Count > 0)
                    {
                        // The driver sets every field to the same text, which a checkbox or a choice field rightly refuses
                        // with the documented ArgumentException; the fill code before that check is what is under test.
                        try { PdfForm.Fill(d, values, new PdfFormFillOptions { IgnoreUnknownFields = true }, ct); }
                        catch (ArgumentException) { }
                    }
                    PdfForm.Flatten(d, null, ct);
                },
            },
            new()
            {
                Name = "pdf-compact", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                // A mutated input can turn out signed or encrypted, which compacting refuses as documented.
                ExtraExpected = ex => ex is ArgumentException { ParamName: "pdf" },
                Run = (d, ct) =>
                {
                    PdfCompactor.Compact(d, null, ct);
                    PdfCompactor.Compact(d, new PdfCompactOptions { ImageQuality = 50, MaxImageDpi = 72, RemoveMetadata = true }, ct);
                },
            },
            new()
            {
                Name = "pdf-verify", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                Run = (d, ct) => PdfSignatureVerifier.Verify(d, new PdfSignatureVerificationOptions { TrustedRoots = SeedContext.TrustedRoots() }),
            },
            new()
            {
                Name = "html", Extension = ".html", Text = true, MaxSize = 128 * 1024, Seeds = c => c.HtmlFiles(),
                Dictionary = HtmlWords, Nesters = HtmlNesters, Run = (d, ct) => Html(d, ct),
            },
            new()
            {
                Name = "css", Extension = ".css", Text = true, MaxSize = 128 * 1024, Seeds = c => c.CssFiles(),
                Dictionary = CssWords, Nesters = CssNesters,
                Run = (d, ct) => Html(Encoding.UTF8.GetBytes(
                    "<!doctype html><html><head><style>" + Encoding.UTF8.GetString(d) + "</style></head><body><div class=\"a b\" id=\"c\" data-x=\"1\"><h1>Heading</h1>"
                    + "<p title=\"x\">Text <span>span</span> <b>bold</b> <i>it</i> <a href=\"#c\">link</a></p><ul><li>One<li>Two<ol><li>Deep</ol></ul>"
                    + "<table><thead><tr><th>A<th>B<tbody><tr><td>cell<td>cell</table><img alt=x><input type=text value=v></div><p>After</p></body></html>"), ct),
            },
            new()
            {
                Name = "svg", Extension = ".svg", Text = true, MaxSize = 128 * 1024, Seeds = c => c.SvgSnippets(),
                Dictionary = SvgWords, Nesters = SvgNesters,
                // Every filtered shape gets its own raster pass, so 128 KB of small blurred shapes legitimately takes
                // about 10 s on a laptop, run twice below, and longer on a CI runner. The cost is linear and the filter
                // code honours cancellation, so only a limit well above that separates real hangs from big inputs.
                MinTimeout = TimeSpan.FromSeconds(30),
                Run = (d, ct) =>
                {
                    var svg = Encoding.UTF8.GetString(d);
                    // Once inline in the page and once as an image file, which are separate paths into the SVG code.
                    Html(Encoding.UTF8.GetBytes("<!doctype html><body><p>before</p>" + svg + "<p>after</p></body>"), ct);
                    Html(Encoding.UTF8.GetBytes("<!doctype html><body><img style=\"width:100px\" src=\"data:image/svg+xml;base64," + Convert.ToBase64String(d) + "\"></body>"), ct);
                },
            },
            new()
            {
                Name = "markdown", Extension = ".md", Text = true, MaxSize = 128 * 1024, Seeds = c => c.MarkdownFiles(),
                Dictionary = MarkdownWords, Nesters = MarkdownNesters,
                Run = (d, ct) =>
                {
                    // The HTML lists footnotes at the end, as GitHub does; the PDF floats them to the bottom of their pages.
                    var markdown = Encoding.UTF8.GetString(d);
                    MarkdownToHtml.ConvertDocument(markdown);
                    MarkdownToPdf.Convert(markdown, new PdfOptions { MaxPages = 100 }, null, ct);
                },
            },
            new()
            {
                // Barcode text often comes from data the caller does not control, such as an invoice reference.
                Name = "barcode", Extension = ".txt", Text = true, MaxSize = 8 * 1024,
                Seeds = _ => new[] { "HELLO WORLD", "https://pdfpecker.net/", "0123456789012", "Blåbær 🙂\t\u0000" }
                    .Select((s, i) => ($"seed{i}", Encoding.UTF8.GetBytes(s))),
                Run = (d, ct) =>
                {
                    var text = Encoding.UTF8.GetString(d);
                    foreach (var level in Enum.GetValues<QrErrorCorrection>())
                        RunExpectingArgument(() => Barcode.QrCode(text, new BarcodeOptions { ErrorCorrection = level }));
                    RunExpectingArgument(() => Barcode.Code128(text));
                    RunExpectingArgument(() => Barcode.Ean13(text));
                    RunExpectingArgument(() => Barcode.EpcQrCode(new EpcPayment { Name = text, Iban = "DE89370400440532013000", Text = text }));
                },
            },
            Image("png", ".png", c => c.ImageFiles(".png").Concat(c.RenderedImages().Where(i => i.Ext == ".png").Select(i => (i.Name, i.Data))), d => PdfImage.FromBytes(d)),
            Image("jpeg", ".jpg", c => c.ImageFiles(".jpg", ".jpeg").Concat(c.RenderedImages().Where(i => i.Ext == ".jpg").Select(i => (i.Name, i.Data))), d =>
            {
                PdfImage.FromBytes(d);
                JpegDecoder.Decode(d);
            }),
            Image("gif", ".gif", c => c.ImageFiles(".gif"), d => PdfImage.FromBytes(d)),
            Image("webp", ".webp", c => c.ImageFiles(".webp").Concat(c.RenderedImages().Where(i => i.Ext == ".webp").Select(i => (i.Name, i.Data))), d => PdfImage.FromBytes(d)),
            Image("bmp", ".bmp", c => c.ImageFiles(".bmp"), d => PdfImage.FromBytes(d)),
            Image("tiff", ".tif", c => c.ImageFiles(".tif", ".tiff"), d => PdfImage.FromBytes(d)),
            Image("jpx", ".jp2", c => c.Files("tests/PdfPecker.Tests/Fixtures/codecs", 2_000_000, ".jp2", ".j2k"), d => JpxDecoder.Decode(d)),
            new()
            {
                // The input is: width, height and globals length as little-endian ints, the globals, then the stream.
                Name = "jbig2", Extension = ".jb2bin", LenientDecoder = true, MaxSize = 256 * 1024,
                Seeds = c => c.Files("tests/PdfPecker.Tests/Fixtures/codecs", 1_000_000, ".jb2").Select(f =>
                {
                    var (w, h) = SeedContext.PbmSize(File.ReadAllBytes(Path.ChangeExtension(Path.Combine(c.Root, f.Name), ".pbm")));
                    var globalsPath = Path.ChangeExtension(Path.Combine(c.Root, f.Name), ".glob");
                    var globals = File.Exists(globalsPath) ? File.ReadAllBytes(globalsPath) : Array.Empty<byte>();
                    var ms = new MemoryStream();
                    foreach (var v in new[] { w, h, globals.Length }) ms.Write(BitConverter.GetBytes(v));
                    ms.Write(globals);
                    ms.Write(f.Data);
                    return (f.Name, ms.ToArray());
                }),
                Run = (d, ct) =>
                {
                    if (d.Length < 12) return;
                    int w = BitConverter.ToInt32(d, 0), h = BitConverter.ToInt32(d, 4), g = BitConverter.ToInt32(d, 8);
                    // The caller of the decoder has validated the page size against the image dictionary; mirror the cap.
                    if (w <= 0 || h <= 0 || g < 0 || g > d.Length - 12 || (long)w * h > PdfImageDecoder.MaxPixels) return;
                    Jbig2Decoder.Decode(d.AsSpan(12 + g).ToArray(), g == 0 ? null : d.AsSpan(12, g).ToArray(), w, h);
                },
            },
            Font("font-ttf", ".ttf", c => c.FontFiles(".ttf", ".ttc").Where(f => !f.Name.Contains("Variable") && !f.Name.Contains("RobotoFlex"))),
            Font("font-otf", ".otf", c => c.FontFiles(".otf").Where(f => !f.Name.Contains("VarTest"))),
            Font("font-woff", ".woff", c => c.FontFiles(".woff")),
            Font("font-woff2", ".woff2", c => c.FontFiles(".woff2")),
            Font("font-variable", ".font", c => c.FontFiles(".ttf", ".otf").Where(f => f.Name.Contains("Variable") || f.Name.Contains("RobotoFlex") || f.Name.Contains("VarTest"))),
        };
        return list;
    }

    private static Target Image(string name, string ext, Func<SeedContext, IEnumerable<(string, byte[])>> seeds, Action<byte[]> decode)
        => new() { Name = name, Extension = ext, Seeds = seeds, LenientDecoder = true, MaxSize = 256 * 1024, Run = (d, ct) => decode(d) };

    private const string FontPage =
        "<!doctype html><body style=\"font-family:Fz;font-size:18px\"><p>Hamburgefonstiv 0123456789 fi ffl AV To Wa العربية שלום 日本語 कि</p>"
        + "<p style=\"font-weight:700;font-style:italic;font-variation-settings:'wght' 700,'wdth' 75,'opsz' 14,'slnt' -10;font-feature-settings:'smcp','liga','onum','ss01','kern';font-size:30px\">Hamburgefonstiv fi ffl AV</p>"
        + "<p style=\"font-variant:super full-width jis04;font-kerning:none;font-synthesis:weight style;font-size-adjust:ex-height 0.5;font-stretch:75%\">Hamburgefonstiv 0123456789 AV To</p>"
        + "<p style=\"font-variant:small-caps;letter-spacing:2px;text-transform:uppercase;text-decoration:underline\">Ligatures and kerning: office waffle</p></body>";

    /// <summary>Runs one barcode encoder; ArgumentException is its documented answer to text it cannot encode.</summary>
    private static void RunExpectingArgument(Func<string> encode)
    {
        try
        {
            encode();
        }
        catch (ArgumentException)
        {
        }
    }

    private static Target Font(string name, string ext, Func<SeedContext, IEnumerable<(string, byte[])>> seeds) => new()
    {
        Name = name, Extension = ext, Seeds = seeds, MaxSize = 512 * 1024,
        Run = (d, ct) =>
        {
            var fonts = new FontCollection();
            fonts.Add(d, "Fz");
            // Registering is lazy about the tables; laying out and embedding text is what exercises them.
            HtmlToPdf.Convert(FontPage, new PdfOptions { Fonts = fonts, MaxPages = 20 }, ct);
        },
    };

    // Targets that fail on purpose, to check that the supervisor copes with a fatal crash and with a thread that cannot be
    // cancelled. They are not listed and not part of "all".
    private static readonly Target[] SelfTests =
    {
        new()
        {
            Name = "selftest-crash", Extension = ".bin", Text = true, MaxSize = 4096, Dictionary = new[] { "crash" },
            Seeds = _ => new[] { ("seed", Encoding.UTF8.GetBytes("some text with a trigger word")) },
            Run = (d, ct) => { if (Encoding.UTF8.GetString(d).Contains("crash")) Environment.FailFast("selftest crash"); },
        },
        new()
        {
            Name = "selftest-hang", Extension = ".bin", Text = true, MaxSize = 4096, Dictionary = new[] { "hang" },
            Seeds = _ => new[] { ("seed", Encoding.UTF8.GetBytes("some text with a trigger word")) },
            Run = (d, ct) => { if (Encoding.UTF8.GetString(d).Contains("hang")) while (true) Thread.SpinWait(1000); },
        },
    };

    public static Target? Find(string name)
        => All.Concat(SelfTests).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
