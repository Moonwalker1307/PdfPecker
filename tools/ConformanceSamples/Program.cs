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

var profiles = new (string Name, PdfConformance Conformance, PdfAccessibility Accessibility, string[] Flavours)[]
{
    ("pdfa-2b", PdfConformance.PdfA2b, PdfAccessibility.None, ["2b"]),
    ("pdfa-2u", PdfConformance.PdfA2u, PdfAccessibility.None, ["2u"]),
    ("pdfa-3b", PdfConformance.PdfA3b, PdfAccessibility.None, ["3b"]),
    ("pdfa-3u", PdfConformance.PdfA3u, PdfAccessibility.None, ["3u"]),
    ("pdfa-4", PdfConformance.PdfA4, PdfAccessibility.None, ["4"]),
    ("pdfua-1", PdfConformance.None, PdfAccessibility.PdfUA1, ["ua1"]),
    ("pdfua-2-pdfa-4", PdfConformance.PdfA4, PdfAccessibility.PdfUA2, ["4", "ua2"]),
    ("pdfa-2b-pdfua-1", PdfConformance.PdfA2b, PdfAccessibility.PdfUA1, ["2b", "ua1"]),
    ("pdfa-3b-pdfua-1", PdfConformance.PdfA3b, PdfAccessibility.PdfUA1, ["3b", "ua1"]),
    // Tagged PDF has no veraPDF profile of its own: these files are only generated, and the PDF/UA-1 profiles above
    // cover the structure tree. The empty flavour list tells check.py to skip them.
    ("tagged", PdfConformance.None, PdfAccessibility.Tagged, []),
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
    };

    foreach (var input in inputs)
    {
        var options = Options(Path.GetDirectoryName(input.Path)!);
        var text = File.ReadAllText(input.Path);
        var pdf = input.Markdown ? MarkdownToPdf.Convert(text, options) : HtmlToPdf.Convert(text, options);
        File.WriteAllBytes(Path.Combine(dir, input.Name + ".pdf"), pdf);
    }

    File.WriteAllBytes(Path.Combine(dir, "rtl-scripts.pdf"), HtmlToPdf.Convert(Rtl, Options(fixtures)));
}

// The attachment levels: the golden e-invoice page with its XML attached. PDF/A-3 and PDF/A-4f may carry attachments.
foreach (var (name, conformance, accessibility, flavours) in new (string, PdfConformance, PdfAccessibility, string[])[]
{
    ("pdfa-3b-einvoice", PdfConformance.PdfA3b, PdfAccessibility.None, ["3b"]),
    ("pdfa-4f-einvoice", PdfConformance.PdfA4f, PdfAccessibility.None, ["4f"]),
    ("pdfa-3b-pdfua-1-einvoice", PdfConformance.PdfA3b, PdfAccessibility.PdfUA1, ["3b", "ua1"]),
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
