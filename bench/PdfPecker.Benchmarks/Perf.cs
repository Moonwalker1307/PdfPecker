using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfPecker;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Fonts.Cff;
using PdfPecker.Images;
using PdfPecker.Markdown;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Parsing;
using PdfPecker.Text;

// A short, machine-readable run for the performance regression check in .github/workflows/perf.yml: about 30 seconds,
// eighteen cases, JSON output. Allocation per conversion is deterministic, so the workflow compares it tightly; time is
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

        // The image encoders on their own, which render-png only times together with rendering: the first page of
        // features.pdf at 96 dpi as lossless WebP, JPEG (4:2:0) and RGB PNG at the default settings, and the same page
        // with a transparent background as RGBA PNG and WebP with alpha. One run encodes each once.
        var page = PdfRenderer.Open(pdf).RenderPage(0, imageOptions);
        var transparentPage = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 96, Transparent = true });
        results.Add(Measure("encode-webp-jpeg-png", () =>
        {
            page.ToWebP();
            page.ToJpeg();
            page.ToPng();
            transparentPage.ToWebP();
            transparentPage.ToPng();
        }, runs: 10));

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

        // Box painting: a generated page of boxes with every border style, rounded corners, outer and inset shadows with
        // blur and spread, gradient and image backgrounds with each size and repeat, border images, outlines, text
        // decorations and a few transformed, translucent and clipped boxes, laid out and written as a PDF.
        results.Add(Measure("paint-boxes", DrawingPage(), htmlDir, runs: 30));

        // Inline layout: a text-heavy page of justified and hyphenated paragraphs with soft hyphens, dashes and long
        // words, every white-space mode with tabs, letter and word spacing, small caps and text transforms, inline
        // boxes and images, sub- and superscripts, decorations and shadows, right-to-left and East Asian text, a line
        // cut with an ellipsis, a clamped paragraph, leaders and text beside floats, laid out and written as a PDF.
        results.Add(Measure("inline-text", TextPage(), htmlDir, runs: 30));

        // The CSS parser: a stylesheet of some three thousand rules with every shorthand, nested rules, conditional
        // blocks, layers, named pages with margin boxes, font faces, registered properties and counter styles, parsed
        // into a fresh stylesheet.
        var css = LargeStylesheet();
        results.Add(Measure("parse-css", () => CssParser.ParseStylesheet(css, new Stylesheet()), runs: 50));

        // Variable fonts: static instances of the test fonts with gvar and with CFF2 outlines at eight points from the
        // minimum to the maximum of every axis, through fvar, avar, gvar with its inferred deltas, HVAR, GDEF and GPOS
        // and the CFF2 charstrings. The fonts are loaded and the points normalized once, so a run makes the instances.
        var variableFonts = VariableFontInstances(Path.Combine(fixtureDir, "fonts"));
        results.Add(Measure("instance-fonts", () => { foreach (var v in variableFonts) FontVariations.Instance(v.Font, v.Coords, v.Values, v.Axes); }, runs: 100));

        // Complex text shaping: Arabic paragraphs with vowel marks and Hebrew ones with points, shaped word by word and
        // whole in both directions through the GSUB (joining forms, required and standard ligatures) and GPOS (kerning,
        // cursive attachment, mark-to-base, mark-to-ligature and mark-to-mark) lookups of the Noto test fonts.
        var shapingRuns = ShapingRuns(Path.Combine(fixtureDir, "fonts"));
        var shaped = new List<ShapedGlyph>();
        results.Add(Measure("shape-complex", () => { foreach (var (font, text, rtl) in shapingRuns) ComplexShaper.Shape(font, text, 0, text.Length, rtl, shaped); }, runs: 500));

        // Gradients: some four hundred CSS gradient values of every kind (linear with angles and corners, radial with
        // each size keyword and position, conic with start angles), plain and repeating, with translucent and
        // positioned stops, parsed, resolved for two boxes and written as shading patterns (tiled for the repeating
        // ones) and conic calculator functions.
        var gradients = GradientValues();
        results.Add(Measure("resolve-gradients", () => { foreach (var g in gradients) WriteGradient(g); }, runs: 200));

        // SVG filters: a page of filtered shapes whose chains run every primitive (blurs, offsets, drop shadows, floods,
        // merges, each composite operator and blend mode, colour matrices, transfer functions, morphology, turbulence,
        // convolution with each edge mode, displacement, tiling and both lightings with each light), plus paragraphs of
        // blurred text-shadows in one and in several colours, laid out and written as a PDF.
        results.Add(Measure("svg-filters", FilterPage(), null, runs: 20));

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

    // A page of about 180 small boxes, the same on every run, that together use every path of the box painter: each
    // border style at several widths, with and without rounded corners, uniform and differing sides, double and 3D
    // styles; outer and inset box shadows, sharp and blurred, with spread; colour, gradient and image backgrounds with
    // each background-size and background-repeat; border images from a gradient and from the logo; outlines; and text
    // decorations, transforms, opacity, overflow clips and a clip-path on some boxes.
    // A text-heavy page, the same on every run, that takes every path of inline layout: paragraphs that are justified
    // and hyphenated, with soft hyphens, zero-width spaces, dashes and words too long for the line; each white-space
    // mode with tabs and preserved newlines; letter and word spacing, small caps, text transforms and a ::first-line
    // style; inline blocks, images, sub- and superscripts, padded inline boxes, links and footnote calls; decorations in
    // each style and sharp and blurred text shadows; Arabic, Hebrew and East Asian text; a nowrap line cut with an
    // ellipsis, a clamped paragraph, leaders, text beside floats and two columns.
    // A stylesheet of about three thousand rules, the same on every run: selectors of each kind, every shorthand with
    // !important, var() and calc() values, nested rules with & and nested @media, @media, @supports, @container and
    // @layer blocks, named pages with margin boxes and a footnote area, a font face, a registered property, a counter
    // style, an import (skipped, as the bench sets no loader), comments and escapes.
    static List<string> GradientValues()
    {
        string[] heads =
        {
            "", "45deg, ", "to right, ", "to bottom left, ", "0.3turn, ", "-1.2rad, ", "100grad, ",
            "circle, ", "ellipse closest-side at 30% 70%, ", "circle farthest-corner at left top, ", "40px 20px at center, ", "closest-corner at 10px bottom, ",
            "from 30deg, ", "from 0.25turn at 25% 75%, ", "at right 20px, ", "from -90deg at center in oklch, ",
        };
        string[] stops =
        {
            "red, blue", "red 10%, #0f08 30%, blue 60%, green", "rgba(0,0,0,0) 0, black 20px, 40px, white 80%",
            "#123 0 10%, #456 10% 20%, #789 20% 30%", "transparent, red 25%, transparent 50%, blue 75%, transparent",
            "hsl(120deg 50% 50% / 50%) 5%, yellow 2em, orange 15%",
        };
        string[] kinds = { "linear-gradient", "radial-gradient", "conic-gradient" };
        var list = new List<string>();
        for (int k = 0; k < kinds.Length; k++)
            foreach (var repeating in new[] { "", "repeating-" })
                foreach (var head in heads.Where((_, i) => i == 0 || (k == 0 ? i < 7 : k == 1 ? i is >= 7 and < 12 : i >= 12)))
                    foreach (var stop in stops)
                        foreach (var size in new[] { "", " " })
                        {
                            // Conic stops are angles or percentages, not lengths.
                            var stopList = k == 2 ? stop.Replace("20px", "72deg").Replace("40px", "0.2turn").Replace("2em", "1rad") : stop;
                            list.Add(repeating + kinds[k] + "(" + head + stopList + ")" + size);
                        }
        return list;
    }

    // One gradient as a painted background: parsed, resolved for two boxes and written as a pattern over a page-sized
    // area, so that the repeating ones are tiled, with the calculator function of a conic one.
    static void WriteGradient(string value)
    {
        var css = CssGradient.Parse(value, 0xFF000000, 16, 16, 8, 6)!;
        var b = new PdfPecker.Pdf.ByteBuffer(1024);
        foreach (var (w, h) in new[] { (200f, 120f), (37f, 300f) })
        {
            var g = css.Resolve(w, h, 0xFFFFFFFF);
            g.WritePattern(b, PdfPecker.Svg.Matrix.Identity, -w, -h, 3 * w, 3 * h);
            if (g.Conic) g.ConicFunction(gray: false);
        }
    }

    static string LargeStylesheet(int sections = 300)
    {
        var sb = new StringBuilder();
        sb.Append("@charset \"utf-8\";\n@import url(theme.css) layer(theme) supports(display: grid) print;\n@layer reset, base, components, utilities;\n");
        sb.Append("@font-face { font-family: Brand; src: url('brand.woff2') format('woff2'), url(brand.ttf); font-weight: 400 700; font-display: swap; unicode-range: U+0000-00FF; }\n");
        sb.Append("@property --accent { syntax: '<color>'; inherits: true; initial-value: #0a7; }\n");
        sb.Append("@counter-style thumbs { system: cyclic; symbols: '+' '-'; suffix: ' '; }\n");
        sb.Append("@page { size: A4; margin: 2cm 1.5cm; @top-center { content: 'Report'; font: italic 9pt/1.2 Georgia, serif; } @bottom-right { content: counter(page) ' / ' counter(pages); } @footnote { border-top: 1pt solid #999; padding-top: 4pt; } }\n");
        sb.Append("@page wide:first { margin-top: 4cm; } @page :left { margin-right: 3cm; } @page :blank { @top-center { content: none } }\n");
        for (int i = 0; i < sections; i++)
        {
            sb.Append("/* section ").Append(i).Append(" */\n");
            sb.Append($".s{i} {{ margin: {i % 7}px {i % 5}px; padding: {i % 3}em 1em 0; border: 1px solid #{i % 10}{i % 10}{i % 10}; border-radius: 4px / 2px 6px; background: url(\"img/{i}.png\") no-repeat center / cover, linear-gradient(to right, #fff, var(--accent)); color: rgb({i % 256}, 0, 0) !important; }}\n");
            sb.Append($".s{i} h2, .s{i} h3 {{ font: bold {10 + i % 6}pt/1.4 Brand, 'Helvetica Neue', sans-serif; text-decoration: underline wavy #333 1px; letter-spacing: calc(0.1em - {i % 3}px); }}\n");
            sb.Append($".s{i} > p:first-child::first-letter, .s{i} li:nth-child(2n+1) {{ font-variant: small-caps tabular-nums; list-style: square inside; }}\n");
            sb.Append($"#id{i}[data-kind=\"{i}\"]:not(.hidden) {{ display: grid; grid-template: \"a b\" 1fr \"c d\" / 1fr 2fr; gap: {i % 4}px 8px; place-items: center start; grid-area: hd / 1 / 2 / 3; }}\n");
            sb.Append($".f{i} {{ display: flex; flex: 1 1 {i % 10}%; flex-flow: row wrap; inset: 0 auto auto 0; outline: 2px dashed currentColor; columns: 2 12em; column-rule: thin dotted; container: card{i} / inline-size; }}\n");
            sb.Append($".n{i} {{ color: #333; &:hover {{ color: var(--accent, #0a7); }} & .child {{ margin-inline: 1em; padding-block: {i % 5}px; }} @media print {{ display: none; }} > span {{ text-decoration: none; }} }}\n");
            sb.Append($"@media screen and (min-width: {300 + i}px) {{ .s{i} {{ width: {50 + i % 50}%; }} @supports (display: grid) {{ .s{i} .grid {{ display: grid; }} }} }}\n");
            sb.Append($"@container card{i} (min-width: 20em) {{ .s{i} .title {{ font-size: 1.{i % 10}em; }} }}\n");
            sb.Append($"@layer components.cards {{ .card{i} {{ box-shadow: 0 {i % 4}px {i % 9}px rgba(0, 0, 0, .{i % 10}); page-break-inside: avoid; word-wrap: break-word; }} }}\n");
            sb.Append($"@page section{i} {{ margin: {i % 3}cm; @top-left {{ content: \"Section {i}\"; }} }}\n");
            sb.Append($".u{i}::before {{ content: \"\\201C\" attr(data-q) \"\\201D\"; quotes: '\"' '\"'; }} .u{i}\\:x {{ --gap-{i}: {i}px; gap: var(--gap-{i}); }}\n");
        }
        return sb.ToString();
    }

    static string TextPage(int sections = 10)
    {
        const string Lorem = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur.";
        const string English = "The quick brown fox jumps over the lazy dog while extraordinarily knowledgeable internationalization specialists contemplate antidisestablishmentarianism and photosynthesis in well-known state-of-the-art laboratories; the characteristically uncharacteristic documentation nevertheless remains incomprehensible.";
        const string Shy = "Super­cali­fragi­listic­expi­ali­docious words with soft­hyphens, zero​width​spaces, en–dashes, em—dashes and twenty-one hyphen-ated com-pounds break where they may — or not.";
        const string Arabic = "بِسْمِ اللَّهِ الرَّحْمَٰنِ الرَّحِيمِ مرحبا بالعالم ١٢٣ كتاب مدرسة";
        const string Hebrew = "שָׁלוֹם עוֹלָם עברית ספר בית תל־אביב ירושלים 123";
        const string Cjk = "日本語のテキストはスペースなしで改行されます。これはペンです。東京都千代田区、中文排版测试，标点符号！한국어 텍스트도 있습니다。シャンプーとトーキョー「引用」（括弧）";

        var sb = new StringBuilder();
        sb.Append("<html lang='en'><head><style>");
        sb.Append("body{font:9pt Arial;margin:12px} p{margin:0 0 4px} .j{text-align:justify;hyphens:auto} .c{columns:2;column-gap:10px} ");
        sb.Append(".sc{font-variant:small-caps} .up{text-transform:uppercase} .cap{text-transform:capitalize} .ls{letter-spacing:1px;word-spacing:3px} ");
        sb.Append(".nw{white-space:nowrap;overflow:hidden;text-overflow:ellipsis;width:200px} .clamp{-webkit-line-clamp:2;overflow:hidden;display:-webkit-box;-webkit-box-orient:vertical} ");
        sb.Append("pre{font:8pt 'Courier New';tab-size:4;margin:2px 0} .pw{white-space:pre-wrap;width:220px} .pl{white-space:pre-line} .bs{white-space:break-spaces;width:150px} ");
        sb.Append(".ib{display:inline-block;width:60px;border:1px solid #888;padding:2px;vertical-align:middle} .fl{float:left;width:70px;height:40px;background:#ddd;margin:2px 6px 2px 0} .fr{float:right;width:50px;height:60px;background:#eee;margin:2px 0 2px 6px} ");
        sb.Append(".rtl{direction:rtl;text-align:right} .ba{word-break:break-all;width:120px} .kа{word-break:keep-all} .aw{overflow-wrap:anywhere;width:100px} .st{line-break:strict;width:140px} .lo{line-break:loose;width:140px} ");
        sb.Append(".toc span:first-child::after{content:leader(dotted)} .toc2 span:first-child::after{content:leader('_ ')} .fn{float:footnote;font-size:7pt} @page{@footnote{border-top:1pt solid black;padding-top:2pt}} ");
        sb.Append(".dec{text-decoration:underline} .dw{text-decoration:underline wavy red} .dd{text-decoration:line-through double} .ts{text-shadow:1px 1px 2px rgba(0,0,0,0.5)} .tb{text-shadow:0 0 4px blue} ");
        sb.Append(".bg{background:#ffd;padding:1px 3px;border:1px solid #cc9;border-radius:3px} .rel{position:relative;top:-2px} .ti{text-indent:2em} .tal{text-align:justify;text-align-last:center} .fl1::first-line{font-weight:bold;color:#246} .fl1::first-letter{font-size:200%;float:left;line-height:0.8} ");
        sb.Append("</style></head><body>");

        for (int i = 0; i < sections; i++)
        {
            sb.Append("<p class='j ti'>").Append(Lorem).Append(' ').Append(English).Append(" <b>").Append(Shy).Append("</b> <i>").Append(Lorem.Substring(0, 120)).Append("</i></p>");
            sb.Append("<p class='fl1 tal'>").Append(English).Append(' ').Append(Lorem).Append("</p>");
            sb.Append("<p class='ls'>").Append(Lorem.Substring(0, 200)).Append(" <span class='sc'>").Append(English.Substring(0, 100)).Append("</span> <span class='up'>").Append(Shy.Substring(0, 80)).Append("</span> <span class='cap'>").Append(Lorem.Substring(0, 90)).Append("</span></p>");
            sb.Append("<p>").Append(Lorem.Substring(0, 80)).Append(" <span class='ib'>inline block ").Append(i).Append("</span> <img src='logo.png' style='width:24px;vertical-align:middle'> <sup>").Append(i).Append("</sup> text<sub>sub</sub> <span class='bg'>boxed inline</span> <span class='rel'>shifted</span> <a href='#p").Append(i).Append("'>a link that wraps across the line end</a> <span class='dec'>underlined words</span> <span class='dw'>wavy</span> <span class='dd'>struck</span> <span class='ts'>shadowed text</span> <span class='tb'>blurred shadow</span> <span class='fn'>A footnote about item ").Append(i).Append(".</span> ").Append(English.Substring(0, 120)).Append("</p>");
            sb.Append("<p class='rtl'>").Append(Arabic).Append(" PDF 2024 ").Append(Hebrew).Append(" <b>").Append(Arabic).Append("</b></p>");
            sb.Append("<p>").Append(Cjk).Append(' ').Append(English.Substring(0, 60)).Append(' ').Append(Cjk).Append("</p>");
            sb.Append("<div class='st'>").Append(Cjk).Append("</div><div class='lo'>").Append(Cjk).Append("</div>");
            sb.Append("<p class='nw'>").Append(English).Append("</p><p class='clamp'>").Append(Lorem).Append("</p>");
            sb.Append("<pre>\tfunction\tname(a, b) {\n\t\treturn a + b;\t// tabs\n\t}\n").Append(English.Substring(0, 70)).Append("</pre>");
            sb.Append("<div class='pw'>").Append(Shy).Append("\n   leading spaces   kept\n").Append(English.Substring(0, 90)).Append("</div>");
            sb.Append("<div class='pl'>").Append(Lorem.Substring(0, 60)).Append("\n\nnew   lines\n").Append(English.Substring(0, 40)).Append("</div>");
            sb.Append("<div class='bs'>").Append(Lorem.Substring(0, 100)).Append("     ").Append(English.Substring(0, 50)).Append("</div>");
            sb.Append("<div class='ba'>").Append(Lorem.Substring(0, 150)).Append(" longwordwithoutanybreakopportunityatall</div><div class='aw'>Pneumonoultramicroscopicsilicovolcanoconiosis ").Append(English.Substring(0, 50)).Append("</div>");
            sb.Append("<div><div class='fl'></div><div class='fr'></div>").Append(Lorem).Append(' ').Append(English).Append("</div>");
            sb.Append("<p class='toc'><span>Chapter ").Append(i + 1).Append(" Introduction to typesetting</span><span>").Append(i * 7 + 3).Append("</span></p>");
            sb.Append("<p class='toc2'><span>Section ").Append(i + 1).Append(".1 Line breaking</span><span>").Append(i * 7 + 5).Append("</span></p>");
            sb.Append("<div class='c'><p class='j'>").Append(English).Append(' ').Append(Lorem).Append("</p><p>").Append(Shy).Append(' ').Append(English).Append("</p></div>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    // Twelve filtered drawings of 160 × 100 px, each with a chain of primitives, and text-shadow paragraphs.
    static string FilterPage()
    {
        string[] composites = { "over", "in", "out", "atop", "xor", "lighter", "arithmetic" };
        string[] blends = { "normal", "multiply", "screen", "darken", "lighten", "overlay", "hard-light", "color-dodge", "color-burn", "soft-light", "difference", "exclusion" };
        string[] edges = { "duplicate", "wrap", "none" };
        string[] lights =
        {
            "<feDistantLight azimuth='45' elevation='40'/>", "<fePointLight x='60' y='30' z='40'/>",
            "<feSpotLight x='20' y='10' z='60' pointsAtX='80' pointsAtY='60' specularExponent='4' limitingConeAngle='35'/>",
        };
        string[] channels = { "R", "G", "B", "A" };
        var sb = new StringBuilder("<html><head><style>body{margin:8px;font:10pt Arial} svg{display:inline-block;margin:2px} ");
        sb.Append(".s1{text-shadow:1px 1px 3px rgba(0,0,0,0.6)} .s2{text-shadow:2px 2px 2px red, -1px -1px 4px blue, 0 0 6px green}</style></head><body>");
        for (int i = 0; i < 12; i++)
        {
            string composite = composites[i % composites.Length], blend = blends[i % blends.Length], edge = edges[i % edges.Length], light = lights[i % lights.Length];
            sb.Append("<svg width='160' height='100'><defs><filter id='f").Append(i).Append("' x='0' y='0' width='160' height='100' filterUnits='userSpaceOnUse'>");
            sb.Append("<feTurbulence type='").Append(i % 2 == 0 ? "fractalNoise" : "turbulence").Append("' baseFrequency='0.04 0.07' numOctaves='3' seed='").Append(i).Append("' result='noise'/>");
            sb.Append("<feGaussianBlur in='SourceGraphic' stdDeviation='").Append(1 + i % 3).Append("' result='blur'/>");
            sb.Append("<feOffset in='blur' dx='3' dy='2' result='moved'/>");
            sb.Append("<feComposite in='noise' in2='moved' operator='").Append(composite).Append("' k1='0.3' k2='0.6' k3='0.5' k4='0.05' result='mix'/>");
            sb.Append("<feBlend in='mix' in2='SourceGraphic' mode='").Append(blend).Append("' result='blend'/>");
            sb.Append("<feColorMatrix in='blend' type='").Append(i % 3 == 0 ? "saturate' values='0.4" : i % 3 == 1 ? "hueRotate' values='60" : "matrix' values='0.5 0.3 0.2 0 0 0.2 0.6 0.2 0 0 0.1 0.2 0.7 0 0 0 0 0 1 0").Append("' result='cm'/>");
            sb.Append("<feComponentTransfer in='cm' result='ct'><feFuncR type='table' tableValues='0 0.5 1'/><feFuncG type='gamma' amplitude='1.2' exponent='0.8'/><feFuncB type='discrete' tableValues='0 0.3 0.6 1'/><feFuncA type='linear' slope='0.9'/></feComponentTransfer>");
            sb.Append("<feMorphology in='ct' operator='").Append(i % 2 == 0 ? "dilate" : "erode").Append("' radius='2' result='morph'/>");
            sb.Append("<feConvolveMatrix in='morph' order='3' kernelMatrix='0 -1 0 -1 5 -1 0 -1 0' edgeMode='").Append(edge).Append("' result='sharp'/>");
            sb.Append("<feDisplacementMap in='sharp' in2='noise' scale='12' xChannelSelector='").Append(channels[i % 4]).Append("' yChannelSelector='").Append(channels[(i + 1) % 4]).Append("' result='disp'/>");
            sb.Append(i % 2 == 0
                ? "<feDiffuseLighting in='blur' surfaceScale='4' diffuseConstant='1.1' lighting-color='#ffe' result='light'>"
                : "<feSpecularLighting in='blur' surfaceScale='4' specularConstant='1' specularExponent='12' lighting-color='#fff' result='light'>");
            sb.Append(light).Append(i % 2 == 0 ? "</feDiffuseLighting>" : "</feSpecularLighting>");
            sb.Append("<feComposite in='light' in2='disp' operator='arithmetic' k1='0.6' k2='0.2' k3='0.6' result='lit'/>");
            sb.Append("<feFlood flood-color='#36c' flood-opacity='0.3' x='0' y='0' width='20' height='20' result='cell'/><feTile in='cell' result='tiles'/>");
            sb.Append("<feDropShadow in='lit' dx='2' dy='2' stdDeviation='2' flood-color='black' flood-opacity='0.5' result='shadow'/>");
            sb.Append("<feMerge><feMergeNode in='tiles'/><feMergeNode in='shadow'/></feMerge></filter></defs>");
            sb.Append("<g filter='url(#f").Append(i).Append(")'><rect x='20' y='15' width='120' height='70' rx='10' fill='#c63'/><circle cx='80' cy='50' r='28' fill='#3a8'/>");
            sb.Append("<text x='30' y='60' font-size='22' fill='white'>Filter ").Append(i).Append("</text></g></svg>");
        }
        for (int i = 0; i < 6; i++)
            sb.Append("<p class='s").Append(1 + i % 2).Append("'>Shadowed text, a whole line of it, blurred once per shadow and drawn under the glyphs.</p>");
        return sb.Append("</body></html>").ToString();
    }

    static string DrawingPage()
    {
        string[] styles = { "solid", "dashed", "dotted", "double", "groove", "ridge", "inset", "outset" };
        string[] widths = { "1px", "2px", "3px", "6px" };
        string[] colors = { "#c33", "#2a7", "#36c", "rgba(0,0,0,0.5)", "#363636", "gold" };
        string[] radii = { "0", "6px", "50%", "12px 4px", "10px / 20px" };
        string[] shadows =
        {
            "2px 2px 4px rgba(0,0,0,0.5)", "0 0 10px red", "5px 5px 0 3px blue", "inset 0 0 8px black", "inset 2px 2px 5px rgba(0,0,0,0.6), 1px 1px 3px rgba(0,0,0,0.3)",
            "0 0 0 3px green", "-4px -4px 12px 2px rgba(0,128,0,0.7)", "inset -3px -3px 6px 2px rgba(255,0,0,0.5)",
        };
        string[] backgrounds =
        {
            "#eef", "linear-gradient(red, blue)", "linear-gradient(45deg, #000 25%, transparent 25%) 0 0 / 8px 8px", "radial-gradient(circle at 30% 30%, white, black)",
            "conic-gradient(from 90deg, red, blue, red)", "repeating-linear-gradient(90deg, #ccc 0 5px, #fff 5px 10px)", "url(logo.png) center / contain no-repeat",
            "url(logo.png) 0 0 / 20px round", "url(gradient.jpg) 0 0 / 30px space", "url(logo.png) 0 0 / cover", "url(logo.png) repeat-x", "#ddd url(logo.png) 50% 50% / 15px repeat",
        };
        string[] borderImages = { "linear-gradient(red, blue) 30% / 6px / 2px round", "url(logo.png) 10 fill / 8px / 1px repeat", "url(logo.png) 25% / 1 / 0 stretch" };
        string[] outlines = { "1px solid red", "2px dashed blue", "3px dotted green", "4px double black", "3px groove gray" };
        string[] extras = { "", "transform:rotate(3deg);", "opacity:0.6;", "overflow:hidden; border-radius:8px;", "clip-path:circle(45%);", "text-decoration:underline wavy red;", "text-decoration:line-through;", "text-shadow:1px 1px 2px black;" };

        var sb = new StringBuilder();
        sb.Append("<html><head><style>body{margin:8px;font:7pt Arial} .b{display:inline-block;width:34px;height:22px;margin:1px;padding:2px;vertical-align:top}</style></head><body>");
        for (int i = 0; i < 180; i++)
        {
            var style = new StringBuilder();
            string color = colors[i % colors.Length];
            switch (i % 5)
            {
                case 0: style.Append("border:").Append(widths[i % widths.Length]).Append(' ').Append(styles[i % styles.Length]).Append(' ').Append(color).Append(';'); break;
                case 1: style.Append("border:").Append(widths[(i / 5) % widths.Length]).Append(' ').Append(styles[(i / 5) % styles.Length]).Append(' ').Append(color)
                    .Append("; border-top:").Append(widths[(i / 7) % widths.Length]).Append(' ').Append(styles[(i / 3) % styles.Length]).Append(" #000;"); break;
                case 2: style.Append("border:").Append(widths[i % widths.Length]).Append(' ').Append(styles[(i / 2) % styles.Length]).Append(' ').Append(color)
                    .Append("; border-radius:").Append(radii[(i / 5) % radii.Length]).Append(';'); break;
                case 3: style.Append("border:2px solid ").Append(color).Append("; border-image:").Append(borderImages[(i / 5) % borderImages.Length]).Append(';'); break;
                default: style.Append("border-width:1px 3px 2px 4px; border-style:").Append(styles[i % styles.Length]).Append(' ').Append(styles[(i + 3) % styles.Length])
                    .Append("; border-color:").Append(color).Append(" #000; border-radius:").Append(radii[(i / 3) % radii.Length]).Append(';'); break;
            }
            if (i % 3 != 2) style.Append("box-shadow:").Append(shadows[(i / 3) % shadows.Length]).Append(';');
            style.Append("background:").Append(backgrounds[(i / 2) % backgrounds.Length]).Append(';');
            if (i % 4 == 1) style.Append("outline:").Append(outlines[(i / 4) % outlines.Length]).Append("; outline-offset:").Append(i % 3 - 1).Append("px;");
            style.Append(extras[(i / 9) % extras.Length]);
            sb.Append("<div class=b style='").Append(style).Append("'>box ").Append(i).Append("</div>");
        }
        sb.Append("<table style='border-collapse:collapse;margin-top:6px'>");
        for (int r = 0; r < 6; r++)
        {
            sb.Append("<tr>");
            for (int c = 0; c < 8; c++) sb.Append("<td style='border:").Append(widths[(r + c) % widths.Length]).Append(' ').Append(styles[(r * 8 + c) % styles.Length]).Append(' ').Append(colors[c % colors.Length]).Append("'>").Append(r).Append(',').Append(c).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table></body></html>");
        return sb.ToString();
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

    static List<(FontProgram Font, float[] Coords, Dictionary<uint, float> Values, VariationAxis[] Axes)> VariableFontInstances(string dir)
    {
        var instances = new List<(FontProgram, float[], Dictionary<uint, float>, VariationAxis[])>();
        foreach (var name in new[] { "Inter-Variable-Subset.ttf", "RobotoFlex-Subset.ttf", "VarTestCFF-Subset.otf" })
        {
            var font = FontProgram.Load(File.ReadAllBytes(Path.Combine(dir, name)))[0];
            var axes = FontVariations.ReadAxes(font);
            for (int i = 0; i < 8; i++)
            {
                var values = axes.ToDictionary(a => a.Tag, a => a.Min + (a.Max - a.Min) * i / 7);
                instances.Add((font, FontVariations.Normalize(font, axes, values), values, axes));
            }
        }
        return instances;
    }

    static List<(FontProgram Font, string Text, bool Rtl)> ShapingRuns(string dir)
    {
        const string arabic = "بِسْمِ اللَّهِ الرَّحْمَنِ الرَّحِيمِ. كِتَابٌ جَمِيلٌ وَقَلَمٌ عَلَى الطَّاوِلَةِ، لا إله إلا الله. "
            + "مرحبا بالعالم في كل مكان، العربية لغة جميلة تكتب من اليمين إلى اليسار وتتصل حروفها ببعضها في الكلمة الواحدة.";
        const string hebrew = "בְּרֵאשִׁית בָּרָא אֱלֹהִים אֵת הַשָּׁמַיִם וְאֵת הָאָרֶץ. שָׁלוֹם עֲלֵיכֶם, יִשְׂרָאֵל. "
            + "עברית נכתבת מימין לשמאל, והאותיות שלה עומדות כל אחת לבדה עם ניקוד מתחת ומעל לאותיות.";
        var runs = new List<(FontProgram, string, bool)>();
        foreach (var (name, text) in new[] { ("NotoSansArabic-Subset.ttf", arabic), ("NotoSansHebrew-Subset.ttf", hebrew) })
        {
            var font = FontProgram.Load(File.ReadAllBytes(Path.Combine(dir, name)))[0];
            foreach (bool rtl in new[] { true, false })
            {
                runs.Add((font, text, rtl));
                foreach (var word in text.Split(' ')) runs.Add((font, word, rtl));
            }
        }
        return runs;
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
