using PdfPecker;

// Generates <output dir>/<profile>/<sample>.pdf for every profile and sample, plus profiles.tsv, which tells check.py
// which veraPDF flavours apply to each profile folder.

var root = FindRoot();
var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "conformance-out");
var golden = Path.Combine(root, "tests", "PdfPecker.Tests", "Golden");
var samples = Path.Combine(root, "samples", "PdfPecker.Sample", "html");
var fixtures = Path.Combine(root, "tests", "PdfPecker.Tests", "Fixtures");
var invoiceXml = File.ReadAllBytes(Path.Combine(fixtures, "einvoice", "en16931.xml"));

// Each sample is a file inside the repository, so its relative images resolve.
var inputs = new List<(string Name, string Path, bool Markdown)>
{
    ("golden-css3", Path.Combine(golden, "css3", "demo.html"), false),
    ("golden-forms", Path.Combine(golden, "forms", "demo.html"), false),
    ("golden-image-formats", Path.Combine(golden, "image-formats", "demo.html"), false),
    ("golden-paged-media", Path.Combine(golden, "paged-media", "demo.html"), false),
    ("golden-svg-effects", Path.Combine(golden, "svg-effects", "demo.html"), false),
    ("golden-transparency", Path.Combine(golden, "transparency", "demo.html"), false),
    ("golden-translucent-effects", Path.Combine(golden, "translucent-effects", "demo.html"), false),
    ("sample-invoice", Path.Combine(samples, "invoice.html"), false),
    ("sample-report", Path.Combine(samples, "report.html"), false),
    ("sample-graphics", Path.Combine(samples, "graphics.html"), false),
    ("sample-markdown", Path.Combine(samples, "guide.md"), true),
};

// Right-to-left and mixed-script text in fonts that are not the bundled ones, so subset embedding, ToUnicode maps and
// bidi reordering are all exercised. There is no CJK font in the repository, so CJK is covered only as far as the
// bundled fonts and the missing-glyph handling go.
const string Rtl = """
    <html lang="en"><head><title>Scripts</title><style>
    @font-face { font-family: 'NotoArabic'; src: url('fonts/NotoSansArabic-Subset.ttf'); }
    @font-face { font-family: 'NotoHebrew'; src: url('fonts/NotoSansHebrew-Subset.ttf'); }
    .ar { font-family: NotoArabic; direction: rtl; font-size: 18pt }
    .he { font-family: NotoHebrew; direction: rtl; font-size: 18pt }
    </style></head><body>
    <h1>Mixed scripts</h1>
    <p class="ar" lang="ar">مرحبا بالعالم 123</p>
    <p class="he" lang="he">שלום עולם 123</p>
    <p>Latin text with <span class="ar" lang="ar">عربي</span> inside, and 日本語のテキスト.</p>
    </body></html>
    """;

// Colour fonts, which are written as Type 3 fonts whose glyphs paint transparency groups: emoji with gradients, a
// variable colour font, and emoji-style sequences joined into one glyph, in translucent text. Only code points outside
// the Private Use Area, which PDF/UA-2 would want described.
const string ColorFonts = """
    <html lang="en"><head><title>Colour fonts</title><style>
    @font-face { font-family: 'Emoji'; src: url('fonts/NotoColorEmoji-Subset.ttf'); }
    @font-face { font-family: 'Nabla'; src: url('fonts/Nabla-Subset.ttf'); }
    @font-face { font-family: 'Grid'; src: url('fonts/ColorGrid.ttf'); }
    p { font-size: 32pt; margin: 0 0 8pt }
    </style></head><body>
    <h1>Colour fonts</h1>
    <p style="font-family: Emoji">&#x1F39B;&#x1F39F;&#x1F3AB;</p>
    <p style="font-family: Nabla; font-variation-settings: 'wght' 700">ABC</p>
    <p style="font-family: Grid; color: #c00; opacity: 0.6">&#x1F1E9;&#x1F1F0; &#x1F3F4;&#xE0067;&#xE0062;&#xE007F; &#x1F3FD; A</p>
    </body></html>
    """;

var profiles = new (string Name, PdfConformance Conformance, PdfAccessibility Accessibility, string[] Flavours, bool Linearize)[]
{
    ("pdfa-2b", PdfConformance.PdfA2b, PdfAccessibility.None, ["2b"], false),
    ("pdfa-2u", PdfConformance.PdfA2u, PdfAccessibility.None, ["2u"], false),
    ("pdfa-3b", PdfConformance.PdfA3b, PdfAccessibility.None, ["3b"], false),
    ("pdfa-3u", PdfConformance.PdfA3u, PdfAccessibility.None, ["3u"], false),
    ("pdfa-4", PdfConformance.PdfA4, PdfAccessibility.None, ["4"], false),
    ("pdfua-1", PdfConformance.None, PdfAccessibility.PdfUA1, ["ua1"], false),
    ("pdfua-2-pdfa-4", PdfConformance.PdfA4, PdfAccessibility.PdfUA2, ["4", "ua2"], false),
    ("pdfa-2b-pdfua-1", PdfConformance.PdfA2b, PdfAccessibility.PdfUA1, ["2b", "ua1"], false),
    ("pdfa-3b-pdfua-1", PdfConformance.PdfA3b, PdfAccessibility.PdfUA1, ["3b", "ua1"], false),
    // Tagged PDF has no veraPDF profile of its own: these files are only generated, and the PDF/UA-1 profiles above
    // cover the structure tree. The empty flavour list tells check.py to skip them.
    ("tagged", PdfConformance.None, PdfAccessibility.Tagged, [], false),
    // Linearized (Fast Web View) files are rewritten after writing, so they are checked again for each standard.
    ("pdfa-2b-linearized", PdfConformance.PdfA2b, PdfAccessibility.None, ["2b"], true),
    ("pdfua-1-linearized", PdfConformance.None, PdfAccessibility.PdfUA1, ["ua1"], true),
    ("pdfua-2-pdfa-4-linearized", PdfConformance.PdfA4, PdfAccessibility.PdfUA2, ["4", "ua2"], true),
};

Directory.CreateDirectory(outDir);
var manifest = new List<string>();
foreach (var profile in profiles)
{
    var dir = Path.Combine(outDir, profile.Name);
    Directory.CreateDirectory(dir);
    manifest.Add(profile.Name + "\t" + string.Join(',', profile.Flavours));

    PdfOptions Options(string baseDirectory) => new()
    {
        Conformance = profile.Conformance,
        Accessibility = profile.Accessibility,
        EmbedStandardFonts = true,
        AllowFileAccess = true,
        BaseDirectory = baseDirectory,
        Title = "Conformance sample",
        Language = "en",
        Linearize = profile.Linearize,
    };

    foreach (var input in inputs)
    {
        var options = Options(Path.GetDirectoryName(input.Path)!);
        var text = File.ReadAllText(input.Path);
        var pdf = input.Markdown ? MarkdownToPdf.Convert(text, options) : HtmlToPdf.Convert(text, options);
        File.WriteAllBytes(Path.Combine(dir, input.Name + ".pdf"), pdf);
    }

    File.WriteAllBytes(Path.Combine(dir, "rtl-scripts.pdf"), HtmlToPdf.Convert(Rtl, Options(fixtures)));
    File.WriteAllBytes(Path.Combine(dir, "colour-fonts.pdf"), HtmlToPdf.Convert(ColorFonts, Options(fixtures)));

    // Every viewer setting and page labels with a prefix, on the report sample, which has several pages and bookmarks.
    var report = Path.Combine(samples, "report.html");
    var viewer = Options(Path.GetDirectoryName(report)!);
    viewer.Viewer = new PdfViewerSettings
    {
        PageLayout = PdfPageLayout.TwoPageRight,
        PageMode = PdfPageMode.Bookmarks,
        StartPage = 2,
        Zoom = PdfZoom.FitWidth,
        DisplayDocTitle = true,
        FitWindow = true,
        CenterWindow = true,
        HideToolbar = true,
        HideMenuBar = true,
        HideWindowControls = true,
        DisablePrintScaling = true,
        Duplex = PdfDuplex.FlipLongEdge,
    };
    viewer.PageLabels.Add(new PdfPageLabel(1, PdfPageNumberStyle.LowerRoman));
    viewer.PageLabels.Add(new PdfPageLabel(2, PdfPageNumberStyle.Decimal, "Side "));
    File.WriteAllBytes(Path.Combine(dir, "viewer-settings.pdf"), HtmlToPdf.Convert(File.ReadAllText(report), viewer));
}

// The attachment levels: the golden e-invoice page with its XML attached. PDF/A-3 and PDF/A-4f may carry attachments.
foreach (var (name, conformance, accessibility, flavours, linearize) in new (string, PdfConformance, PdfAccessibility, string[], bool)[]
{
    ("pdfa-3b-einvoice", PdfConformance.PdfA3b, PdfAccessibility.None, ["3b"], false),
    ("pdfa-4f-einvoice", PdfConformance.PdfA4f, PdfAccessibility.None, ["4f"], false),
    ("pdfa-3b-pdfua-1-einvoice", PdfConformance.PdfA3b, PdfAccessibility.PdfUA1, ["3b", "ua1"], false),
    ("pdfa-3b-einvoice-linearized", PdfConformance.PdfA3b, PdfAccessibility.None, ["3b"], true),
})
{
    var dir = Path.Combine(outDir, name);
    Directory.CreateDirectory(dir);
    manifest.Add(name + "\t" + string.Join(',', flavours));
    var htmlPath = Path.Combine(golden, "einvoice", "demo.html");
    var pdf = HtmlToPdf.Convert(File.ReadAllText(htmlPath), new PdfOptions
    {
        Conformance = conformance,
        Accessibility = accessibility,
        EmbedStandardFonts = true,
        Title = "Invoice INV-2026-0042",
        Language = "en",
        EInvoice = new PdfEInvoice(invoiceXml),
        Linearize = linearize,
    });
    File.WriteAllBytes(Path.Combine(dir, "einvoice.pdf"), pdf);
}

File.WriteAllLines(Path.Combine(outDir, "profiles.tsv"), manifest);
Console.WriteLine($"Wrote {manifest.Count} profile folders to {outDir}");

static string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "PdfPecker.sln"))) return dir.FullName;
    throw new InvalidOperationException("PdfPecker.sln not found above " + AppContext.BaseDirectory);
}
