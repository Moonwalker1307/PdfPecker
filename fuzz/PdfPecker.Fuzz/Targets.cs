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
        "shape-image-threshold:.5", "background:linear-gradient(in oklch longer hue,red,red,blue 1e-9%)", "background:radial-gradient(in hsl decreasing hue circle,#0000,red)",
        "display:-webkit-box", "-webkit-box-orient:vertical", "-webkit-box-flex:1e9", "-webkit-box-ordinal-group:2147483647", "-webkit-transform:rotate(9deg)",
        "background:-webkit-gradient(radial,1e9 0,1e9,0 1e9,0,from(red),color-stop(-1e9,blue),to(#0000))", "background:-webkit-linear-gradient(left top,red,blue)",
        "background:-webkit-repeating-radial-gradient(1e9px 0,1e-9px 1e9px,red,blue 1e-9px)", "-webkit-column-break-before:always",
        "<style>*::first-letter{initial-letter:3 2}</style>", "<style>p::first-letter{initial-letter:1e9 raise;padding:1e9px}</style>",
        "<img srcset=\"data:image/png;base64, 1e-30x, a 1e30x, b 1x 2w,,\">", "<img srcset=\"a 999999999w, b 1w\" sizes=\"(width>0) calc(1e9px*1e9), -1px, ((((\">",
        "<source srcset=\"a 2x\" media=\"(((\" type=\"image/png;x\">", "sizes=\"0px\"", "srcset=\"a 0.0001x, b 0x\"",
        "background-image:image-set(url(a) 1e-30x, 'b' 1e30dpi type('image/png'), linear-gradient(red,blue) 2x)",
        "list-style-image:-webkit-image-set(url(a) 0.0001dppx, url(b) 1e9dpcm)", "content:image-set(url(a) 2x type(\"((\"))",
        ":valid", ":invalid", ":open", "<input pattern=\"(a+)+\\1(((\" value=aaaa required>", "<input type=number min=1e308 step=1e-308 value=-1e308>",
        "<input type=week value=2026-W53 min=0001-W01><input type=time min=23:00 max=01:00 value=12:00:00.12>", "<input type=radio name=r required>",
        "<header data-sticky-pdf-header data-sticky-pdf-page=a>{page} of {total-pages}</header>", "<div data-sticky-pdf-first-footer data-sticky-pdf-page=\" a \">",
        "<section style=\"page:a\">", "<section style=\"page:b;break-before:page\">", "data-sticky-pdf-page=b",
        "{section-page:a}", "{section-page:%zz", "{section-page:}}", "<section id=a>",
        "content:-pdfpecker-target-pages(attr(id),count)", "content:-pdfpecker-target-pages('#a',current,upper-roman)",
        "content:-pdfpecker-target-pages(url(#a),last)", "content:counter(pages)",
        "<div data-pdf-signature=a style=\"height:1e9px\">", "<span data-pdf-signature>", "data-pdf-signature=\"a.b\"",
        "<td data-pdf-signature=\"\u0000\">", "<div data-pdf-signature=a style=\"display:inline-block;width:0;height:0\">",
        "zoom:1e9", "zoom:0", "zoom:-1", "zoom:1e-9", "zoom:50%", "<style>@scope (div) to (span) {:scope{zoom:3} p{color:red}}</style>",
        "<style>@scope (.a) to (:scope > *) {@scope (p) {:scope{display:flex}}}</style>",
        "position:running(a)", "position:running(header)", "<style>@page{@top-center{content:element(a)}@bottom-left{content:element(a,last-except)}}</style>",
        "<style>@page{@top-right{content:counter(page,upper-roman) target-counters(attr(href url),list-item,'.')}}</style>",
        "content:target-counters(attr(href url),a,'.',lower-greek)", "white-space-collapse:break-spaces", "white-space-collapse:preserve-breaks",
        "box-decoration-break:clone", "hyphens:auto;hyphenate-limit-lines:1", "hyphenate-limit-lines:2147483647", "vector-effect:non-scaling-stroke",
        "<input type=number min=5 max=1 value=3>", ":in-range", ":out-of-range", "gap:1e9px;row-rule:1px solid;column-rule:1e9px dotted",
        "<style>@page{bleed:1e9px;marks:crop cross}</style>", "<style>@page{size:1px 1px;bleed:auto;marks:crop}</style>",
        "<style>@page :nth(-2147483648n+2147483647){background:red;@top-center{content:'x'}}</style>", "<style>@page :nth(2 of a){margin:0}</style>",
        "<style>li{width:calc(sibling-index() * 1e9px);z-index:sibling-count()}</style>", "<style>*{margin-left:calc(sibling-index() * 1em)}</style>",
        "width:if(media(print): 1e9px; supports(display:grid): 1px; else: 2px)", "color:if(style(--a: 1): red; else: if(style(--b): blue))",
        "color:contrast-color(var(--bg))", "background:contrast-color(contrast-color(red))", "color:contrast-color(transparent)",
        "color:device-cmyk(0 0 0 1e30 / -1)", "background:color-mix(in srgb, device-cmyk(1, 0, 0, 0), device-cmyk(none 1 0 0))",
        "<img src=\"data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'/>\" style=\"object-view-box:inset(-1e9% 100% 1e9px 0)\">",
        "object-view-box:xywh(0 0 1e-9px 1e-9px)", "object-view-box:rect(1e9px auto 0 auto)",
        "<span style=\"box-shadow:inset 0 0 1e9px 1e9px red,inset 1px 1px blue;border-radius:50%\">x<br>y</span>",
        "text-autospace:normal", "text-autospace:ideograph-alpha ideograph-numeric replace", "<p style=\"text-autospace:auto;word-break:keep-all\">日a1本<b>b</b>語</p>",
        "<ruby>漢<rt>かん</rt>字<rp>(</rp><rt>じ</rt><rp>)</rp></ruby>", "<ruby><rt>a</rt><rt>b</rt><rb>c</rb></ruby>", "<rt>x</rt>",
        "<ruby style=\"display:block;line-height:0\"><ruby>a<rt>b</rt></ruby><rt style=\"font-size:1e9px\">c</rt></ruby>",
    };

    private static readonly (string, string)[] HtmlNesters =
    {
        ("<div>", "</div>"), ("<span>", "</span>"), ("<b>", "</b>"), ("<table><tr><td>", "</td></tr></table>"), ("<ul><li>", "</li></ul>"), ("<p>", "</p>"),
        ("<ruby>", "<rt>x</rt></ruby>"), ("<div style=\"display:flex\">", "</div>"), ("<div style=\"float:left\">", "</div>"), ("<a href=\"#\">", "</a>"), ("<svg><g>", "</g></svg>"),
        ("<div style=\"position:relative;overflow:hidden\">", "</div>"), ("<blockquote>", "</blockquote>"), ("<details>", "</details>"), ("<i>", "</i>"),
        ("<div style=\"backdrop-filter:invert(1);mask:radial-gradient(#000,#0000)\">", "</div>"), ("<picture>", "</picture>"),
        ("<div style=\"float:right;width:50%;height:9em;shape-outside:ellipse(at 0 50%);shape-margin:1em\">", "</div>"),
    };

    private static readonly string[] CssWords =
    {
        "var(--a)", "calc(", "min(", "max(", "clamp(", "!important", "@media print {", "@media (min-width: 1px) {", "@supports (display:grid) {", "@page {", "@font-face {", "@keyframes a {",
        "@import url(x);", "@container (min-width:1px) {", "@counter-style a {", "@font-palette-values --a { font-family: Fz; base-palette: 99; override-colors: 0 red, 65535 blue }", "font-palette:--a", "font-palette:dark", ":is(", ":not(", ":where(", ":has(", ":nth-child(2n+1 of p)", "::before", "::after", "::marker",
        "*", ">", "~", "+", "|", "[a=b i]", "url(data:,)", "linear-gradient(", "radial-gradient(", "conic-gradient(", "repeating-linear-gradient(", "-webkit-gradient(", "-webkit-", "rotate(", "matrix(",
        "attr(a)", "attr(data-x type(*), ", "attr(data-x px, ", "attr(class type(<length>+))", "attr(id raw-string)", "attr(title number)", "type(<color>)", "width:attr(data-x %)", "counter(a)", "counters(a, \".\")", "content:", "display:grid", "grid-template-columns: repeat(999999, 1fr)", "grid-column: 1 / span 99999", "flex: 1e9",
        "1e9px", "-1e9px", "100000%", "calc(1/0)", "calc(0/0)", "inherit", "initial", "unset", "revert", "all:unset", "--a:", "var(--a, var(--b, var(--a)))",
        "mask-image:", "-webkit-mask:", "mask-composite: exclude", "mask-mode: luminance", "mask-size: 1e9px", "url(#m)", "backdrop-filter: blur(1e9px)", "-webkit-backdrop-filter:",
        "shape-outside:", "inset(", "ellipse(", "polygon(", "path(", "margin-box", "content-box", "shape-margin: 1e9px", "shape-image-threshold: 1e9",
        "::first-letter", "initial-letter: 3", "initial-letter: 1e9 999999", "initial-letter: 2 raise",
        "rule: 1px solid red", "column-rule-color: red, repeat(auto, blue, green)", "row-rule-width: repeat(999999999, 1px)",
        "rule-inset: -1e9px / overlap-join", "column-rule-inset-cap-end: -1e9px", "row-rule-inset: 0 -1e30px", "rule-break: intersection", "rule-visibility-items: between", "rule-overlap: column-over-row",
        "zoom:", "zoom: 1e9", "zoom: 0.0001", "zoom: 1e9%", "@scope (", ") to (", ":scope", "@scope {", "position: running(a)", "content: element(a)",
        "element(a, first-except)", "@top-center {", "@bottom-right-corner {", "@page :first {", "target-counters(attr(id url), a, \".\")",
        "counter(page, lower-roman)", "white-space-collapse:", "box-decoration-break: clone", "hyphenate-limit-lines: 1",
        "bleed: 3mm", "bleed: -1e30px", "marks: crop cross", "marks: cross",
        "@page :nth(2n+1) {", "@page :nth(1 of chapter) {", ":nth(-n+3)",
        "sibling-index()", "sibling-count()", "calc(sibling-index() * 1e9px)", "if(", "media(print): ", "supports(display: grid): ", "style(--a: 1): ",
        "; else: ", "contrast-color(", "contrast-color(currentcolor)", "device-cmyk(", "device-cmyk(0 0 0 1)", "device-cmyk(0, 1, 1, 0)", "object-view-box: ", "inset(10% 1e9px)", "xywh(0 0 100% 100%)",
        "box-shadow: inset 0 0 9px red",
    };

    private static readonly (string, string)[] CssNesters =
    {
        ("{", "}"), ("(", ")"), ("[", "]"), ("calc(", ")"), ("var(--a, ", ")"), (":is(", ")"), (":not(", ")"), ("@media print {", "}"), ("@supports (a:b) {", "}"),
        ("min(", ")"), ("div ", ""), ("a > ", ""), ("b ~ ", ""), ("linear-gradient(", ")"), ("\"", "\""), ("url(", ")"),
        ("@scope (div) {", "}"), ("@scope (.a) to (p) {", "}"), ("@page {", "}"),
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
                    // The output format and its settings come from the input's length and last byte, so every encoder
                    // (PNG, JPEG, WebP and each TIFF mode and compression) sees rendered pages and runs repeat exactly.
                    int pick = d.Length == 0 ? 0 : d.Length * 31 + d[^1];
                    var options = new ImageOptions
                    {
                        Dpi = 72, MaxDegreeOfParallelism = 1, Format = (PdfImageFormat)(pick % 4),
                        Quality = 1 + (pick / 4 % 100), TiffColorMode = (TiffColorMode)(pick / 7 % 3),
                        TiffCompression = pick / 21 % 6 == 5 ? null : (TiffCompression)(pick / 21 % 6),
                    };
                    for (int i = 0; i < pages; i++)
                    {
                        renderer.GetPageSize(i);
                        renderer.RenderPageEncoded(i, options, ct);
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
                Name = "pdf-images", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                Run = (d, ct) => PdfImages.Extract(d, new PdfImagesOptions { MaxPages = 200, MaxImages = 1000 }, ct),
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
                Name = "pdf-html-structured", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                Run = (d, ct) =>
                {
                    int pages = Math.Min(PdfToImage.GetPageCount(d), 3);
                    if (pages == 0) return;
                    PdfToHtml.Convert(d, new PdfHtmlOptions
                    {
                        Pages = PdfPageRange.Parse("1-" + pages.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        // Styles too, so the fuzzer reaches the CSS written from the PDF's fills, rules and fonts.
                        Mode = PdfHtmlMode.Structured, IncludeStyles = true, MaxDegreeOfParallelism = 1,
                    }, ct);
                },
            },
            new()
            {
                Name = "pdf-markdown", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                Run = (d, ct) =>
                {
                    int pages = Math.Min(PdfToImage.GetPageCount(d), 3);
                    if (pages == 0) return;
                    PdfToMarkdown.Convert(d, new PdfMarkdownOptions
                    {
                        Pages = PdfPageRange.Parse("1-" + pages.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        IncludeImages = true, MaxDegreeOfParallelism = 1,
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
                Name = "pdf-sanitize", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                // A mutated input can turn out encrypted, which sanitizing refuses as documented.
                ExtraExpected = ex => ex is ArgumentException { ParamName: "pdf" },
                Run = (d, ct) =>
                {
                    PdfSanitizer.HasActiveContent(d);
                    PdfSanitizer.RemoveActiveContent(d, new PdfSanitizeOptions { RemoveAttachments = true }, ct);
                },
            },
            new()
            {
                Name = "pdf-redact", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                // A mutated input can turn out encrypted, which redacting refuses as documented, or have no first page for
                // the areas, which is refused as an area past the last page.
                ExtraExpected = ex => ex is ArgumentException { ParamName: "pdf" or "areas" },
                Run = (d, ct) =>
                {
                    // One band across the middle of the first page and one corner, then every "e" the text has.
                    PdfRedactor.Redact(d, new[] { new PdfRedactionArea(1, 0, 200, 2000, 300), new PdfRedactionArea(1, 0, 0, 120, 80) }, null, ct);
                    PdfRedactor.RedactText(d, "e", new PdfRedactOptions { FillColor = null }, StringComparison.Ordinal, ct);
                },
            },
            new()
            {
                Name = "pdf-pdfa", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                // A mutated input can turn out signed or encrypted, which the conversion refuses as documented.
                ExtraExpected = ex => ex is ArgumentException { ParamName: "pdf" },
                Run = (d, ct) =>
                {
                    PdfToPdfA.Convert(d, null, ct);
                    PdfToPdfA.Convert(d, new PdfAConversionOptions { Conformance = PdfConformance.PdfA3u }, ct);
                },
            },
            new()
            {
                Name = "pdf-linearize", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                // A mutated input can turn out signed or encrypted, which linearizing refuses as documented.
                ExtraExpected = ex => ex is ArgumentException { ParamName: "pdf" },
                Run = (d, ct) => PdfLinearizer.Linearize(d, ct),
            },
            new()
            {
                Name = "pdf-verify", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                Run = (d, ct) => PdfSignatureVerifier.Verify(d, new PdfSignatureVerificationOptions { TrustedRoots = SeedContext.TrustedRoots() }),
            },
            new()
            {
                Name = "pdf-properties", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                Run = (d, ct) => PdfDocumentProperties.Read(d),
            },
            new()
            {
                // PdfPecker.Testing's snapshot comparison reads any file it is given as a snapshot, without the library's
                // parser, so it gets its own target: it must take damaged files without throwing.
                Name = "pdf-snapshot", Extension = ".pdf", Seeds = pdfSeeds, MaxSize = 1 << 20,
                Run = (d, ct) =>
                {
                    var objects = PdfPecker.Testing.PdfNormalizer.Normalize(d);
                    ct.ThrowIfCancellationRequested();
                    if (PdfPecker.Testing.PdfNormalizer.FirstDifference(objects, objects) is not null)
                        throw new InvalidOperationException("A file differs from itself.");
                },
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
                Seeds = _ => new[] { "HELLO WORLD", "https://pdfpecker.net/", "0123456789012", "Blåbær 🙂\t\u0000", "(01)09506000134352(17)270131(10)AB-123" }
                    .Select((s, i) => ($"seed{i}", Encoding.UTF8.GetBytes(s))),
                Run = (d, ct) =>
                {
                    var text = Encoding.UTF8.GetString(d);
                    foreach (var level in Enum.GetValues<QrErrorCorrection>())
                        RunExpectingArgument(() => Barcode.QrCode(text, new BarcodeOptions { ErrorCorrection = level }));
                    RunExpectingArgument(() => Barcode.Code128(text));
                    RunExpectingArgument(() => Barcode.Ean13(text));
                    RunExpectingArgument(() => Barcode.DataMatrix(text));
                    RunExpectingArgument(() => Barcode.Gs1DataMatrix(text));
                    RunExpectingArgument(() => Barcode.SwissQrCode(new SwissQrBill
                    {
                        Account = "CH5800791123000889012",
                        Creditor = new SwissQrAddress { Name = text, PostalCode = "2501", Town = text },
                        Reference = text,
                        Message = text,
                    }));
                    RunExpectingArgument(() => Barcode.EpcQrCode(new EpcPayment { Name = text, Iban = "DE89370400440532013000", Text = text }));
                },
            },
            new()
            {
                // Scans and photos are untrusted. The input is a grey image: its width as a little-endian 16-bit number,
                // then one byte per pixel, row by row; mutations make module patterns that no encoder would write.
                Name = "barcode-read", Extension = ".gray", MaxSize = 256 * 1024, MinTimeout = TimeSpan.FromSeconds(10),
                Seeds = _ => BarcodeImageSeeds(),
                Run = (d, ct) =>
                {
                    if (d.Length < 3) return;
                    int width = Math.Clamp((int)BitConverter.ToUInt16(d, 0), 1, 1024), height = (d.Length - 2) / width;
                    if (height == 0) return;
                    var gray = new PdfPecker.Barcodes.Reading.GrayImage(width, height, d.AsSpan(2, width * height).ToArray());
                    Barcode.Read(gray, new BarcodeReaderOptions(), 0, 1, 1, ct);
                },
            },
            new()
            {
                // The decoded data of a code: the first byte picks a QR code version (1 to 40) or Data Matrix (0), and the
                // rest are the data codewords after error correction, whose segments the parsers read.
                Name = "barcode-codewords", Extension = ".cw", MaxSize = 4 * 1024,
                Seeds = _ => BarcodeCodewordSeeds(),
                Run = (d, ct) =>
                {
                    if (d.Length < 1) return;
                    var data = d.AsSpan(1).ToArray();
                    if (d[0] % 41 == 0) PdfPecker.Barcodes.Reading.DataMatrixDecoder.Parse(data);
                    else PdfPecker.Barcodes.Reading.QrDecoder.ParseSegments(data, d[0] % 41);
                    _ = PdfPecker.Barcodes.Reading.PaymentPayloads.Epc(Encoding.UTF8.GetString(data));
                    _ = PdfPecker.Barcodes.Reading.PaymentPayloads.Swiss(Encoding.UTF8.GetString(data));
                },
            },
            Image("png", ".png", c => c.ImageFiles(".png").Concat(c.RenderedImages().Where(i => i.Ext == ".png").Select(i => (i.Name, i.Data))), d => PdfImage.FromBytes(d)),
            Image("jpeg", ".jpg", c => c.ImageFiles(".jpg", ".jpeg").Concat(c.RenderedImages().Where(i => i.Ext == ".jpg").Select(i => (i.Name, i.Data))), d =>
            {
                PdfImage.FromBytes(d);
                JpegDecoder.Decode(d);
                ExifReader.Read(d);
                PdfImage.WithoutMetadata(d);
            }),
            Image("gif", ".gif", c => c.ImageFiles(".gif"), d => PdfImage.FromBytes(d)),
            Image("webp", ".webp", c => c.ImageFiles(".webp").Concat(c.RenderedImages().Where(i => i.Ext == ".webp").Select(i => (i.Name, i.Data))), d => PdfImage.FromBytes(d)),
            Image("bmp", ".bmp", c => c.ImageFiles(".bmp"), d => PdfImage.FromBytes(d)),
            Image("tiff", ".tif", c => c.ImageFiles(".tif", ".tiff"), d => PdfImage.FromBytes(d)),
            new()
            {
                // The public decoder: every frame of a multi-page TIFF (up to three) in each pixel format. Unlike the
                // internal decoders it may throw only what it documents.
                Name = "image-to-pixels", Extension = ".img", MaxSize = 256 * 1024,
                Seeds = c => c.ImageFiles(".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff")
                    .Concat(c.RenderedImages().Select(i => (i.Name, i.Data))),
                Run = (d, ct) =>
                {
                    int frames = Math.Min(3, ImageToPixels.GetFrameCount(d));
                    for (int frame = 0; frame < frames; frame++)
                    {
                        var format = (ImagePixelFormat)((d.Length + frame) % 3);
                        ImageToPixels.Convert(d, new ImageToPixelsOptions { Frame = frame, Format = format, MaxPixels = 20_000_000 }, ct);
                    }
                    ImageToPixels.Convert(d, new ImageToPixelsOptions { ApplyOrientation = false }, ct);
                },
            },
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
            Font("font-ttf", ".ttf", c => c.FontFiles(".ttf", ".ttc").Where(f => !f.Name.Contains("Variable") && !f.Name.Contains("RobotoFlex") && !IsColorFont(f.Name))),
            Font("font-otf", ".otf", c => c.FontFiles(".otf").Where(f => !f.Name.Contains("VarTest"))),
            Font("font-woff", ".woff", c => c.FontFiles(".woff")),
            Font("font-woff2", ".woff2", c => c.FontFiles(".woff2")),
            Font("font-variable", ".font", c => c.FontFiles(".ttf", ".otf").Where(f => f.Name.Contains("Variable") || f.Name.Contains("RobotoFlex") || f.Name.Contains("VarTest"))),
            Font("font-color", ".ttf", c => c.FontFiles(".ttf").Where(f => IsColorFont(f.Name)), ColorFontPage),
        };
        return list;
    }

    /// <summary>Codes from the library's own encoders drawn as grey images, three pixels to a module, some turned or inverted.</summary>
    private static IEnumerable<(string Name, byte[] Data)> BarcodeImageSeeds()
    {
        var bill = new SwissQrBill
        {
            Account = "CH4431999123000889012",
            Creditor = new SwissQrAddress { Name = "Example AG", PostalCode = "8001", Town = "Zurich" },
            Amount = 12.5m,
            Reference = "210000000003139471430009017",
        };
        var grids = new (string Name, bool[,] Modules)[]
        {
            ("qr", PdfPecker.Barcodes.QrEncoder.Encode("https://pdfpecker.net/", QrErrorCorrection.Medium)),
            ("qr-high", PdfPecker.Barcodes.QrEncoder.Encode("HELLO WORLD 0123456789", QrErrorCorrection.High)),
            ("qr-v7", PdfPecker.Barcodes.QrEncoder.Encode(new string('x', 130), QrErrorCorrection.Low)),
            ("swiss", PdfPecker.Barcodes.QrEncoder.Encode(bill.ToPayload(), QrErrorCorrection.Medium)),
            ("dm", PdfPecker.Barcodes.DataMatrixEncoder.Encode("Data Matrix 12345")),
            ("dm-gs1", PdfPecker.Barcodes.DataMatrixEncoder.EncodeGs1("(01)09506000134352(17)270131(10)AB-123")),
            ("code128", Row(PdfPecker.Barcodes.LinearEncoder.Code128("PdfPecker-128"))),
            ("ean13", Row(PdfPecker.Barcodes.LinearEncoder.Ean13("5901234123457"))),
        };
        foreach (var (name, modules) in grids)
        {
            yield return (name, Draw(modules, turned: false, inverted: false));
            yield return (name + "-turned", Draw(modules, turned: true, inverted: false));
        }
        yield return ("qr-inverted", Draw(grids[0].Modules, turned: false, inverted: true));

        static bool[,] Row(bool[] bars)
        {
            var grid = new bool[12, bars.Length];
            for (int y = 0; y < 12; y++)
                for (int x = 0; x < bars.Length; x++) grid[y, x] = bars[x];
            return grid;
        }

        static byte[] Draw(bool[,] modules, bool turned, bool inverted)
        {
            const int Scale = 3, Quiet = 4;
            int rows = modules.GetLength(0), cols = modules.GetLength(1);
            int w = (cols + 2 * Quiet) * Scale, h = (rows + 2 * Quiet) * Scale;
            int outW = turned ? h : w, outH = turned ? w : h;
            var data = new byte[2 + outW * outH];
            BitConverter.GetBytes((ushort)outW).CopyTo(data, 0);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int mx = x / Scale - Quiet, my = y / Scale - Quiet;
                    bool dark = mx >= 0 && my >= 0 && mx < cols && my < rows && modules[my, mx];
                    int ox = turned ? h - 1 - y : x, oy = turned ? x : y;
                    data[2 + oy * outW + ox] = (byte)(dark != inverted ? 20 : 235);
                }
            }
            return data;
        }
    }

    /// <summary>Hand-built data codewords for the segment parsers: each QR mode and Data Matrix encodation.</summary>
    private static IEnumerable<(string Name, byte[] Data)> BarcodeCodewordSeeds()
    {
        // QR: numeric "01234567", alphanumeric "AC-42", bytes "hi", ECI 26 with UTF-8 bytes, kanji, FNC1 and terminator.
        yield return ("qr-mixed", Qr(1, (4, 1), (10, 8), (10, 12), (7, 345), (4, 2), (9, 5), (11, 461), (11, 1232), (6, 36), (4, 4), (8, 2),
            (8, 'h'), (8, 'i'), (4, 7), (8, 26), (4, 4), (8, 2), (8, 0xC3), (8, 0xA6), (4, 8), (8, 1), (13, 0x0D9F), (4, 5), (4, 0)));
        yield return ("qr-structured-append", Qr(10, (4, 3), (16, 0x1234), (4, 4), (16, 3), (8, 'a'), (8, 'b'), (8, 'c'), (4, 0)));
        // Data Matrix: ASCII, digit pairs, upper shift, C40, Text, X12, EDIFACT and Base 256, then padding.
        yield return ("dm-ascii", new byte[] { 0, (byte)('A' + 1), 130 + 42, 235, 66, 129, 175 });
        yield return ("dm-c40", new byte[] { 0, 230, 91, 11, 91, 11, 254, 66 });
        yield return ("dm-text", new byte[] { 0, 239, 91, 11, 254, 66 });
        yield return ("dm-x12", new byte[] { 0, 238, 89, 191, 254, 66 });
        yield return ("dm-edifact", new byte[] { 0, 240, 16, 21, 1, 124, 0, 0 });
        yield return ("dm-base256", new byte[] { 0, 231, 44, 108, 59, 226, 126, 1, 104 });
        yield return ("dm-gs1-eci", new byte[] { 0, 232, 241, 27, 66, 232, 67 });
        yield return ("dm-macro", new byte[] { 0, 236, 66, 67 });
        yield return ("epc", Encoding.UTF8.GetBytes("\0BCD\n002\n1\nSCT\n\nExample Ltd.\nDE89370400440532013000\nEUR12.30"));

        static byte[] Qr(int version, params (int Bits, int Value)[] fields)
        {
            var bytes = new List<byte> { (byte)version };
            int acc = 0, n = 0;
            foreach (var (bits, value) in fields)
            {
                for (int i = bits - 1; i >= 0; i--)
                {
                    acc = (acc << 1) | ((value >> i) & 1);
                    if (++n == 8)
                    {
                        bytes.Add((byte)acc);
                        acc = n = 0;
                    }
                }
            }
            if (n > 0) bytes.Add((byte)(acc << (8 - n)));
            return bytes.ToArray();
        }
    }

    private static Target Image(string name, string ext, Func<SeedContext, IEnumerable<(string, byte[])>> seeds, Action<byte[]> decode)
        => new() { Name = name, Extension = ext, Seeds = seeds, LenientDecoder = true, MaxSize = 256 * 1024, Run = (d, ct) => decode(d) };

    private const string FontPage =
        "<!doctype html><body style=\"font-family:Fz;font-size:18px\"><p>Hamburgefonstiv 0123456789 fi ffl AV To Wa العربية שלום 日本語 कि 葛\U000E0100辻\U000E0101侮\uFE00</p>"
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

    // COLR colour fonts: Google's test glyphs (U+F0000 on), Noto Color Emoji, Nabla (variable) and the generated ones
    // (U+E000 on), in several text colours, so the writer keeps a font for each, and translucent, so glyphs are groups.
    private static bool IsColorFont(string name) => name.Contains("COLR") || name.Contains("Color") || name.Contains("Nabla");

    private static string ColorFontPage => ColorFontPageText.Value;

    // A class of its own, so the page is built before the target list that uses it, whatever the field order.
    private static class ColorFontPageText
    {
        public static readonly string Value =
            "<!doctype html><style>@font-palette-values --p { font-family: Fz; base-palette: 1; override-colors: 0 #0c0, 3 rgb(0 0 200 / 50%) }</style>"
            + "<body style=\"font-family:Fz;font-size:18px\"><p style=\"font-palette:dark\">ABC \uE000 \U0001F39B</p><p style=\"font-palette:--p\">ABC \uE000 \U0001F39B</p><p>ABC \uE000\uE001\uE002\uE096\uE12B \U0001F39B\U0001F39F\U0001F3AB</p>"
            + "<p style=\"color:#c00;opacity:0.6;font-variation-settings:'wght' 700,'HLGT' 8\">ABC \uE000\uE096 \U0001F39B</p><p style=\"font-size:8px\">"
            + string.Concat(Enumerable.Range(0xF0000, 0x1200).Select(char.ConvertFromUtf32)) + "</p>"
            + "<svg width=\"200\" height=\"40\"><text x=\"0\" y=\"30\" font-family=\"Fz\" fill=\"blue\">ABC \uE096 \U0001F39B</text></svg></body>";
    }

    private static Target Font(string name, string ext, Func<SeedContext, IEnumerable<(string, byte[])>> seeds, string page = FontPage) => new()
    {
        Name = name, Extension = ext, Seeds = seeds, MaxSize = 512 * 1024,
        Run = (d, ct) =>
        {
            var fonts = new FontCollection();
            fonts.Add(d, "Fz");
            // Registering is lazy about the tables; laying out and embedding text is what exercises them.
            HtmlToPdf.Convert(page, new PdfOptions { Fonts = fonts, MaxPages = 20 }, ct);
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
