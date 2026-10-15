![PdfPecker](https://raw.githubusercontent.com/Moonwalker1307/PdfPecker/main/assets/logo.png)

# PdfPecker

[![NuGet](https://img.shields.io/nuget/v/PdfPecker.svg)](https://www.nuget.org/packages/PdfPecker)

Fast HTML and Markdown to PDF conversion, and PDF to PNG rendering, for .NET, with no dependencies.

PdfPecker has its own HTML parser, Markdown parser, CSS engine, layout engine and PDF writer, and its own PDF renderer.
It does not start a browser, ship native binaries or pull in third-party packages; the NuGet package depends only on
the .NET base class library.

```csharp
using PdfPecker;

byte[] pdf = HtmlToPdf.Convert("<h1>Hello</h1><p>World</p>");
File.WriteAllBytes("hello.pdf", pdf);
```

## Install

```
dotnet add package PdfPecker
```

Until the package is published to nuget.org, build it locally and add the folder as a package source:

```
dotnet pack src/PdfPecker -c Release -o ./nupkg
dotnet nuget add source "$(pwd)/nupkg" --name pdfpecker-local
dotnet add <your project> package PdfPecker
```

The package targets `net8.0`, `net9.0` and `net10.0`, so it works on .NET 8 and later.

## How it compares

The same three documents converted to A4 PDF by PdfPecker and six .NET alternatives, on one 4-core cloud VM
(Intel Xeon @ 2.8 GHz, 16 GB, Linux, .NET 10.0.12). Times are medians after a 3-second warm-up; "first PDF" includes
starting the browser where there is one. Parallel runs one conversion per core at once on the 5-page document. Peak
memory is the proportional set size (PSS) of the whole process tree, so a browser's helper processes are included
once and shared pages are not double-counted.

| Converter | Engine | To deploy | First PDF | Invoice, 1 page | Feature tour, 5 pages | Report, 30 pages | Parallel | Peak memory |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **PdfPecker** | own, managed | 3.1 MB DLL | 0.27 s | **0.86 ms** | **2.7 ms** | **39 ms** | **962 docs/s** | **83 MB** |
| HtmlRenderer.PdfSharp 1.6.1 | own, managed (CSS 2) | 2.4 MB of DLLs | 0.47 s | 5.0 ms | 16 ms | 473 ms | 88 docs/s | 255 MB |
| iText pdfHTML 6.3.4 \* | own, managed | 11 MB of DLLs | 1.68 s | 41 ms | 242 ms | 4.2 s | 8 docs/s | 614 MB |
| wkhtmltopdf 0.12.6 | Qt WebKit, native | about 90 MB with Qt | 0.31 s | 289 ms | 333 ms | 693 ms | 12 docs/s | 164 MB |
| PuppeteerSharp 25.13 | Chromium 141 | about 600 MB browser | 0.91 s | 128 ms | 166 ms | 539 ms | 12 docs/s | 849 MB |
| Microsoft.Playwright 1.63 | Chromium 141 | browser + 135 MB driver | 1.08 s | 142 ms | 191 ms | 522 ms | 11 docs/s | 986 MB |
| Syncfusion HTML to PDF 35.1.40 \* | Chrome (Blink), bundled | about 545 MB with Chrome | 2.52 s | 263 ms | 314 ms | 1.7 s | 1 doc/s | 2,195 MB |

\* Measured later (October 2026) on the same VM type with the same program. In that run the other converters were up to
70% slower than in the table above and PdfPecker slightly faster, so these two rows are approximate and, if anything,
a little slower than a like-for-like run would show.

How to read it:

- PdfPecker is 60 to 150 times faster than the Chromium-based converters on the 1 and 5-page documents and about
  14 times faster on the 30-page report, and uses a tenth of their memory. Per-document overhead (a browser tab, a process
  start) dominates for the others on small documents.
- Output is not identical. Chromium is the reference for modern CSS and runs JavaScript, which PdfPecker does not.
  HtmlRenderer supports only HTML 4 and CSS 2 (it drops the invoice's table header colours), and wkhtmltopdf uses
  a 2016 WebKit, shrinks content to fit by default (24 pages for the report instead of 29) and was archived in 2023.
- iText pdfHTML lays out the documents itself, like PdfPecker, but its time grows faster than the page count: 242 ms for
  5 pages and 4.2 s for the 30-page report, which is one long table.
- Syncfusion ran as a trial without a licence key, which adds a line of text to each page. Its `AdditionalDelay`
  (2 seconds of waiting for scripts by default) was set to 0, as the other Chromium converters don't wait either. With
  its default settings it scales content down, so the report took 13 pages instead of 29.
- PDF sizes for the 1 / 5 / 30-page documents: PdfPecker 12 / 26 / 126 KB, Chromium 72 / 139 / 171 KB,
  wkhtmltopdf 30 / 59 / 191 KB, HtmlRenderer 35 / 65 / 273 KB, iText 26 / 45 / 104 KB, Syncfusion 42 / 89 / 132 KB.
- Each converter was used the way its documentation suggests for many documents: PdfPecker and HtmlRenderer in
  process, wkhtmltopdf as one process per document, and PuppeteerSharp and Playwright with one shared browser and a
  new tab per document (Playwright in one shared browser context, tagged PDF output off for both). iText reuses one
  font provider per thread, as its knowledge base advises. Syncfusion keeps one converter per concurrent conversion with
  `ReuseBrowserProcess` on, as its support forum advises, so the parallel run starts four browsers.

IronPDF was not measured here. One user compared PdfPecker with **IronPDF 2026.7.2** (Chrome-based)
on the 26 templates of their production app, on a 22-core development machine after warm-up: IronPDF took 13.6 s
for all 26 documents one at a time and PdfPecker 17 ms (about 800 times faster), and with 22 in parallel IronPDF made
9.2 documents/s and PdfPecker 1,143 (about 125 times). 24 of the 26 documents came out identical; the other two differ
by less than 0.25 mm.

To reproduce the table (needs wkhtmltopdf on `PATH` and a Chromium at `CHROMIUM_PATH`):

```
dotnet run --project bench/PdfPecker.Comparison -c Release
```

The comparison project is not part of `PdfPecker.sln`, so its third-party packages never reach the library or its
tests. Your numbers will differ with hardware and documents.

## System requirements

**Required:** the .NET 8, .NET 9 or .NET 10 runtime (or later). Nothing else: the library is pure managed code with no
native binaries, browser or third-party packages, so it runs wherever .NET runs (Windows, Linux, macOS; x64 and ARM64).
The rendered pages are the same on every version; only the compressed bytes can differ, because each .NET version ships
its own zlib build.

**Recommended:**

- Register the fonts your templates use with `options.Fonts` (see [Fonts](#fonts)); other fonts fall back to Helvetica,
  Times or Courier metrics.
- Memory and CPU are small. Measured on a 4-core 2.1 GHz Xeon cloud VM (Linux, .NET 10, Release), each document in a
  fresh process:

  | Document | Pages | Time, warm | Time, first in process | Peak process memory | Smallest heap that works |
  | --- | --- | --- | --- | --- | --- |
  | Payslip, invoice, voucher | 1 to 3 | 1 to 3 ms | 0.2 s | 50 MB | under 16 MB |
  | Same with PDF/A or embedded fonts | 1 to 3 | 3 to 5 ms | 0.2 s | 56 MB | under 16 MB |
  | Report with tables | 30 | 30 to 40 ms | 0.3 s | 75 MB | under 16 MB |
  | Report with tables | 1,000 | 1.7 s | 3 s | 480 MB | 245 MB |

  The process itself starts at about 32 MB; a conversion uses about one core, so throughput grows with cores. PDF/A and
  font embedding add a few milliseconds and about 5 MB for the embedded font data. Memory grows roughly with page
  count, so plan about 0.5 MB per page for very long documents.

`dotnet run --project bench/PdfPecker.Benchmarks -c Release -- --requirements [your.html ...]` measures this on your
own machine and documents.

## Usage

```csharp
// String to bytes
byte[] pdf = HtmlToPdf.Convert(html);

// String to a stream
using (var file = File.Create("out.pdf")) HtmlToPdf.Convert(html, file);

// ASP.NET Core
app.MapGet("/invoice", () => Results.File(HtmlToPdf.Convert(html), "application/pdf", "invoice.pdf"));

// File to file; relative <img> and <link> paths resolve against the HTML file's folder
HtmlToPdf.ConvertFile("invoice.html", "invoice.pdf");

// Off the calling thread
byte[] pdf = await HtmlToPdf.ConvertAsync(html);
```

All methods are static and thread-safe, so you can convert many documents in parallel.

### Markdown

```csharp
// Markdown to PDF, styled like a GitHub README
byte[] pdf = MarkdownToPdf.Convert(File.ReadAllText("README.md"));

// File to file; relative image paths resolve against the Markdown file's folder
MarkdownToPdf.ConvertFile("README.md", "README.pdf", new PdfOptions { FooterText = "Page {page} of {pages}" });

// Markdown to HTML: a body fragment, or a complete page with the stylesheet
string fragment = MarkdownToHtml.Convert("# Hello\n\n*World*");     // <h1 id="hello">Hello</h1>\n<p><em>World</em></p>
string page = MarkdownToHtml.ConvertDocument(markdown);
MarkdownToHtml.ConvertFile("README.md", "README.html");
```

`MarkdownOptions` switches the GitHub extensions off for strict CommonMark (`GitHubExtensions = false`), drops heading
ids (`HeadingIds = false`), shows raw HTML as text (`AllowRawHtml = false`), replaces the stylesheet (`StyleSheet`,
see `MarkdownToHtml.DefaultStyleSheet`) or sets the title (default: the first heading). `MarkdownToPdf` takes
`PdfOptions` and `MarkdownOptions` side by side.

### Options

```csharp
var options = new PdfOptions
{
    PageSize = PageSize.Letter.Landscape,            // A3, A4 (default), A5, Letter, Legal or new PageSize(w, h) in points
    Margins = PageMargins.FromMillimeters(20, 15, 20, 15),
    Scale = 1,                                        // like Chrome's print scale (see its docs to match IronPDF's output)
    TruncateBandsToWholePoints = false,               // round header/footer height attributes down to whole points, as Chrome does
    BaseDirectory = "/var/app/templates",             // where relative image/stylesheet paths are resolved
    ResourceResolver = url => myCache.Get(url),       // serve http(s) or embedded resources yourself; null = default handling
    UserStyleSheet = "body { font-size: 10pt }",      // extra CSS applied after the document's own
    FooterText = "Page {page} of {pages}",            // also HeaderText
    Title = "Invoice 42", Author = "Accounts",        // document properties (Title defaults to <title>)
    Keywords = "invoice", Creator = "MyApp",          // also Subject, and Producer (default "PdfPecker")
    CustomProperties = { ["RunId"] = "42" },          // your own properties, in the PDF's document information
    Bookmarks = true,                                 // PDF bookmarks from h1-h6 (default)
    FormFields = true,                                // inputs, selects and textareas become fillable fields (default)
    Compress = true,                                  // Flate-compress page content (default)
    AllowFileAccess = false,                          // true: also read files outside BaseDirectory (trusted HTML only)
    MaxPages = 1000,                                  // refuse longer documents with PdfLimitException (default 100,000)
};
byte[] pdf = HtmlToPdf.Convert(html, options);
```

Local images and stylesheets are read only from inside `BaseDirectory` (the input file's folder for `ConvertFile`):
absolute paths, `file:` URLs, `..` and symbolic links that lead outside it are ignored unless `AllowFileAccess = true`,
and converting a string without a `BaseDirectory` reads no files at all. For HTML from untrusted users, keep it that
way, set a low `MaxPages`, and pass a cancellation token with a timeout (`HtmlToPdf.Convert(html, options, token)`):
cancelling stops the conversion where it is.

A CSS `@page { size: A4 landscape; margin: 15mm }` rule overrides the page size and margins unless
`IgnoreCssPageRules` is set. PdfPecker never makes network requests on its own: `http(s)` images and stylesheets are only
loaded through `ResourceResolver`.

### Fonts

Register TrueType or OpenType fonts and use them by name in CSS. Only the glyphs a document uses are embedded, and
text can use any Unicode character the fonts have (Greek, Cyrillic, Central European, CJK, ...):

```csharp
var options = new PdfOptions();
options.Fonts.AddFile("fonts/OpenSans-Regular.ttf")
             .AddFile("fonts/OpenSans-Bold.ttf");             // family, weight and style are read from the files
options.Fonts.Add(bytes, familyName: "sans-serif");           // or override a generic family
options.Fonts.AddDirectory(@"C:\Windows\Fonts");               // read once, loaded only when used
byte[] pdf = HtmlToPdf.Convert("<p style=\"font-family: 'Open Sans'\">Łódź, Ελλάδα, Москва</p>", options);
```

Documents can bring their own fonts with `@font-face` (files, `data:` URIs or `ResourceResolver` URLs; `local()`
refers to registered fonts):

```css
@font-face { font-family: Brand; src: url(fonts/brand.woff2) format("woff2"); font-weight: 400; }
@font-face { font-family: Brand; src: url(fonts/brand-bold.ttf); font-weight: 700; unicode-range: U+0000-024F; }
```

A `FontCollection` can be shared between options and parallel conversions. Formats: `.ttf`, `.otf`, `.ttc`
collections, WOFF and WOFF2 (including WOFF2 collections; decoded with .NET's built-in Brotli). Fonts are matched like browsers do: by family, then style, then the nearest
weight; a missing bold or italic face is synthesized. A character that the chosen font lacks is drawn with the next
font in the `font-family` list that has it, then the standard font, then any other registered font.

Text without a registered font uses the standard PDF fonts (Helvetica, Times, Courier), which viewers supply
themselves, so files stay small but are limited to Western European characters. Set `EmbedStandardFonts = true` to
embed the bundled Liberation fonts in their place instead: they have the same widths, so the layout does not change,
the text looks the same in every viewer, and any character they cover (Latin, Greek, Cyrillic, ...) works. PDF/A
always does this. It adds a subset of each font used to the file (typically 10-25 KB per font).

A family that no font provides falls back silently, like in a browser. To notice, set `MissingFont`: it is called
once per conversion for each family that had to be substituted, and throwing from it fails the conversion. To use the
fonts installed on the machine for families you have not registered, set `UseSystemFonts = true` (off by default, so
the output does not depend on the machine):

```csharp
options.MissingFont = family => logger.LogWarning("Font {Family} is not available", family);
options.UseSystemFonts = true;   // looks in C:\Windows\Fonts, /Library/Fonts, /usr/share/fonts, ...
```

Likewise, a character that no font has is drawn as an empty box (blank space in PDF/A). `MissingCharacter` is called
once for each such character before anything is written, so a test can make sure a template's fonts cover its data:

```csharp
options.MissingCharacter = ch => throw new InvalidOperationException($"No font has U+{char.ConvertToUtf32(ch, 0):X4}");
```

### Right-to-left text: Arabic and Hebrew

Arabic and Hebrew are laid out right to left and shaped like browsers do, with PdfPecker's own implementation of the
Unicode Bidirectional Algorithm (UAX #9, passing all of Unicode's conformance tests) and of OpenType shaping (GSUB and
GPOS, following HarfBuzz). Arabic letters join, lam-alef and other ligatures form, and harakat and Hebrew points are
placed on their letters by the font's mark positioning. Mixed text (an English product name or a number inside an
Arabic sentence) is put in the right order, and brackets are mirrored. Register a font that has the script, then set
the direction with the `dir` attribute or CSS `direction`:

```csharp
var options = new PdfOptions();
options.Fonts.AddFile("fonts/NotoNaskhArabic-Regular.ttf");
byte[] pdf = HtmlToPdf.Convert("""
    <html dir="rtl" lang="ar"><body style="font-family: 'Noto Naskh Arabic'">
      <h1>فاتورة رقم ٢٠٢٦/١٥</h1>
      <p>المبلغ الإجمالي: 1,250.00 DKK</p>
    </body></html>
    """, options);
```

In a right-to-left block, text aligns right (`text-align: start`), and table columns, flex rows, grid columns and list
markers run from the right. `dir="auto"`, `<bdi>` and `<bdo>` work as in browsers, and so do `unicode-bidi`,
`text-align: start`/`end` and the logical margin and padding properties (`margin-inline-start`, `padding-inline-end`,
...). Copied or extracted text gives back the original characters in logical order. A font without OpenType Arabic
features is shaped with the Unicode presentation forms instead.

### Watermarks, passwords and PDF/A

```csharp
var options = new PdfOptions
{
    // A diagonal stamp on every page. Also: Image (PNG, JPEG, GIF or WebP bytes), FontSize, LetterSpacing, Angle, Opacity, Bold, Behind.
    Watermark = new PdfWatermark { Text = "WATERMARK", Color = "#c00000", Opacity = 0.25f },

    // AES-256 encryption (PDF 2.0 / Acrobat X and later). Without an OwnerPassword a random one is used,
    // so the permissions cannot be lifted.
    Security = new PdfSecurity
    {
        UserPassword = "1234",                                   // needed to open the file; null = opens freely
        OwnerPassword = "owner-secret",                          // full access
        Permissions = PdfPermissions.Print | PdfPermissions.Copy,
    },
};
```

For archiving, set `Conformance = PdfConformance.PdfA2b` (or `PdfA3b`): the PDF gets XMP metadata, an sRGB output intent
and a file ID, and CMYK JPEGs are converted to RGB. All fonts are embedded: text without a registered font uses the
bundled Liberation fonts (see [Fonts](#fonts)). PDF/A does not allow encryption, so combining `Conformance` with
`Security` throws an `ArgumentException`. AES-256 keys come from a password hash that is slow on purpose, which adds
about 3 ms to a conversion.

### Accessibility: tagged PDF and PDF/UA

```csharp
var options = new PdfOptions
{
    Accessibility = PdfAccessibility.PdfUA1,   // or Tagged: the same structure without the PDF/UA claim
    Language = "da",                           // default: <html lang="...">; for PDF/UA without either, "en"
    Conformance = PdfConformance.PdfA2b,       // optional: PDF/UA combines with PDF/A
};
```

`Tagged` gives the PDF a structure tree built from the HTML, so screen readers, reflow and text extraction follow the
document rather than the page layout: headings (`H1` to `H6`), paragraphs, lists with their labels, tables with header
scopes and spans, figures with their alt text, links (tied to their clickable areas), block quotes, code and sections,
all in document order, with `lang` attributes as the language of the element. Text outside a paragraph (straight in a
`div`, say) gets one. Backgrounds, borders, running headers and footers, `HeaderText`/`FooterText`, watermarks, repeated
table headers, fixed boxes and decorative images are artifacts that assistive technology skips.

`PdfUA1` adds what ISO 14289-1 asks beyond the tags: every font embedded (the bundled Liberation fonts stand in for the
standard ones), the title shown in the viewer's title bar and the PDF/UA identification in the XMP metadata. The output
passes veraPDF's PDF/UA-1 checks, on its own and together with PDF/A-2b or PDF/A-3b (e-invoices included). It needs a
title: `Title`, the `<title>` element or else the first heading; without one, conversion throws `ArgumentException`, as
it does when `Security` leaves out `PdfPermissions.ExtractForAccessibility`.

What the tags can't fix is up to the HTML: give images an `alt` (an image without `alt`, `aria-label` or `title`, or an
`<svg>` without a `<title>`, counts as decorative), use real headings, lists and `th` cells, and mark purely visual
content `aria-hidden="true"`. Heading levels that skip one (`h1` then `h3`) are tagged one level below the previous
heading, as PDF/UA does not allow gaps.

### E-invoices (Factur-X / ZUGFeRD)

Pass the invoice's CII XML (from your invoicing system) and PdfPecker attaches it to the PDF, making a Factur-X /
ZUGFeRD e-invoice: the pages are the human-readable invoice and the XML is what accounting software reads.

```csharp
var options = new PdfOptions
{
    EInvoice = new PdfEInvoice(File.ReadAllBytes("invoice.xml")),
};
File.WriteAllBytes("invoice.pdf", HtmlToPdf.Convert(invoiceHtml, options));
```

The file is PDF/A-3b, so `Conformance` is raised to `PdfA3b` when it is left at `None` (`PdfA2b` or `Security` throw).
The XML is embedded unchanged as `factur-x.xml` (`xrechnung.xml` for XRechnung), listed as an associated file, and the
XMP metadata gets the Factur-X extension schema with the document type, version and profile. The profile (MINIMUM,
BASIC WL, BASIC, EN 16931, EXTENDED or XRECHNUNG) is read from the XML's guideline ID; pass it as the second constructor
argument to set it yourself. The relationship to the pages defaults to `Data` for MINIMUM and BASIC WL and `Alternative`
otherwise; set `Relationship` to change it. PdfPecker checks that the XML is a well-formed Cross Industry Invoice (UBL is
not allowed in Factur-X) but does not validate its content or build it from invoice data: make sure the visible pages
show the same invoice. The output passes the Mustang validator (PDF/A-3b via veraPDF, and the XML's schematron).

### PDF attachments in UBL e-invoices (Peppol, OIOUBL)

Peppol BIS Billing 3.0, OIOUBL, EHF, XRechnung UBL and other UBL e-invoices are XML only: the XML is the invoice, and
the PDF travels inside it as an attachment. `UblInvoice.AttachPdf` adds one to the invoice or credit note your
invoicing system made:

```csharp
byte[] pdf = HtmlToPdf.Convert(invoiceHtml);
byte[] xml = UblInvoice.AttachPdf(File.ReadAllBytes("invoice.xml"), pdf, "INV-1001.pdf");
```

The PDF goes in as an `AdditionalDocumentReference` with `EmbeddedDocumentBinaryObject` (`mimeCode="application/pdf"`
and the file name), after any attachments already there and where the UBL schema expects it. The rest of the XML,
including its indentation, encoding and declaration, is kept as it was. The reference ID defaults to the file name; pass
`id` and `description` to set them. For OIOUBL 2.1 (UBL 2.0) the description is written as the `DocumentType` OIOUBL
requires, `PDF` by default. The output passes the UBL schemas, the Peppol BIS 3.0 and EN 16931 schematrons, and the
OIOUBL 2.1 schematron. PdfPecker does not build or validate the rest of the invoice.

### Digital signatures

Sign the PDF with a certificate (a `.pfx` file or one from the certificate store). Adobe Reader then shows who signed
it and whether the file was changed afterwards.

```csharp
var cert = X509CertificateLoader.LoadPkcs12FromFile("signer.pfx", "pfx-password");
var options = new PdfOptions
{
    Signature = new PdfSignatureOptions(cert)
    {
        Reason = "Approved",
        Location = "Copenhagen",
        // Optional: a visible box (points from the top-left corner; Page = -1 is the last page).
        Appearance = new PdfSignatureAppearance { Page = -1, X = 340, Y = 720, Width = 220, Height = 70 },
        // Optional: a trusted RFC 3161 timestamp (PAdES B-T). The only option that uses the network.
        TimestampUrl = "http://timestamp.digicert.com",
    },
};

// Or sign any existing PDF; earlier signatures stay valid.
byte[] signed = PdfSigner.Sign(File.ReadAllBytes("in.pdf"), new PdfSignatureOptions(cert));

// A contract template with empty signature fields: sign into one by name, at its place on the page.
byte[] countersigned = PdfSigner.Sign(contract, new PdfSignatureOptions(cert) { FieldName = "Buyer" });
```

When `FieldName` names an empty signature field the PDF already has (`"Parties.Buyer"` for a nested one), the signature
goes into it and the box fills the field's rectangle; signing a field of another type or one that is already signed
throws `PdfSignatureException`. Any other name adds a new field.

Signatures are PAdES baseline (`ETSI.CAdES.detached`) by default, or classic `adbe.pkcs7.detached` with
`Format = PdfSignatureFormat.Pkcs7Detached`; RSA and ECDSA keys with SHA-256/384/512. The signature is appended as an
incremental update, and it works together with `Security` (encrypted files) and `Conformance` (PDF/A: the visible box is
drawn as outlines, so it needs no unembedded font). The CMS structure is built with .NET's own `System.Formats.Asn1` and
`System.Security.Cryptography`; no extra package is needed. Signing adds a few milliseconds (the RSA or ECDSA operation
and re-reading the file); conversions without a signature are unaffected.

Custom properties are written to the document information dictionary, where Acrobat shows them under Document
Properties > Custom and `pdfinfo -custom` lists them. For PDF/A they are also written to the XMP metadata (Acrobat's
`pdfx` namespace, with the extension schema PDF/A requires), alongside Keywords, Creator and Producer.

### Fillable forms

Form controls become fillable PDF form fields (AcroForm), drawn as Chrome prints them and filled with their current
values. Adobe Reader, Preview, Chrome, Firefox and other viewers let the reader fill them in and save the result.

```csharp
var html = """
    <p>Name <input name="name" value="Jane Doe" required></p>
    <p><label><input type="checkbox" name="newsletter" checked> Send me the newsletter</label></p>
    <p><input type="radio" name="size" value="S"> S <input type="radio" name="size" value="M" checked> M</p>
    <p><select name="country"><option value="dk">Denmark</option><option value="se">Sweden</option></select></p>
    <p><textarea name="comments" rows="4" cols="40"></textarea></p>
    """;
byte[] pdf = HtmlToPdf.Convert(html);

// Or print the controls as they are, without form fields:
byte[] flat = HtmlToPdf.Convert(html, new PdfOptions { FormFields = false });
```

| HTML | PDF field |
|------|-----------|
| `<input>` of type `text`, `email`, `tel`, `url`, `search`, `number`, `date` and the like | Text field (`maxlength` sets the maximum length) |
| `<input type="password">` | Password text field; the value is drawn as dots and not stored |
| `<textarea>` | Multi-line text field |
| `<input type="checkbox">` | Check box; its `value` is the export value (default `Yes`) |
| `<input type="radio">` | Radio buttons: those with the same `name` are one field, and each `value` is a choice |
| `<select>` | Drop-down list (combo box); with `multiple` or `size` > 1 a list box, multi-select with `multiple` |
| `<button>`, `<input type="submit">`, `reset`, `button` | Drawn only: a PDF has nothing to submit |

Fields are named after the `name` attribute (a period becomes `_`, since PDF uses it to nest fields; a repeated name
gets `_2`, `_3`, ...; a control without a name gets `text1`, `checkbox1`, ...). `readonly` and `required` set the
field's flags, `title`, `aria-label` or its `<label>` becomes its tooltip, and a `placeholder` is drawn in grey while the field is
empty. Disabled controls (or controls in a disabled `<fieldset>`) are drawn but not fillable, and hidden inputs are
left out. Each field has its own appearance, so the PDF looks right before anyone touches it and in viewers that never
redraw fields; while a field is edited, the viewer draws its text in Helvetica, Courier or Times at the control's size
and colour. Forms work with `Security` (the values are encrypted with the rest), `Conformance` (veraPDF passes the
fields as PDF/A-2b and 3b), `Accessibility` (each widget sits in a `Form` element described by its label, title
or name, and veraPDF passes PDF/UA-1), `Signature` (the signature field joins the form) and `PdfMerger` (repeated names are
renamed).

## Merging PDFs

`PdfMerger` combines PDFs into one, any PDFs, not only those PdfPecker made:

```csharp
byte[] merged = PdfMerger.Merge(invoicePdf, termsPdf, attachmentPdf);

// With passwords for encrypted inputs (by position), and properties for the result.
byte[] merged2 = PdfMerger.Merge(new[] { a, b }, new PdfMergeOptions
{
    Passwords = new string?[] { null, "secret" },
    Title = "Payslips October",
    CustomProperties = { ["RunId"] = "42" },
});

PdfMerger.MergeFiles("all.pdf", new[] { "a.pdf", "b.pdf" });

// Streams in and out: inputs are read one at a time and the result is written as it is made.
PdfMerger.Merge(new[] { invoiceStream, termsStream }, outputStream);
await PdfMerger.MergeAsync(uploads.Select(f => f.OpenReadStream()), response.Body);
```

Tagged inputs stay tagged: their structure trees are joined under one root, so the merged file is still accessible,
and when every input is PDF/UA-1 the result says so too (veraPDF passes it). An input in another language than the
first keeps its language on its own part of the tree. Untagged inputs merge as before.

With streams (and `MergeFiles`), only one input is held in memory at a time and the merged document goes to the output
as each input is done, so memory follows the largest input rather than the whole batch: merging 100 files of 32 MB in
total peaks at 14 MB of managed memory, against 105 MB with byte arrays. `MergeAsync` reads and writes asynchronously,
as ASP.NET Core requires for request and response bodies. Input streams are read from their current position and are
not disposed; the sequence is enumerated lazily, so an iterator can open each one only when its turn comes. `MergeFiles`
writes to a temporary file and moves it into place, so the output can be one of the inputs and a failed merge leaves
no partial file behind.

### Merging PDFs as they arrive

For PDFs that arrive one at a time from elsewhere, such as downloads read a few at a time, use `StreamingPdfMerger`. It
writes each PDF's pages when it is added and then lets the PDF go, so memory stays at about one input, and between
inputs you decide what comes next: skip a missing document, or check that anything was added before answering. The
output can be an HTTP response body or a file; it does not need to be seekable.

```csharp
var merger = new StreamingPdfMerger(response.Body, new PdfMergeOptions { Title = "Payslips October" });
foreach (var path in paths)
{
    var pdf = await storage.DownloadAsync(path, ct);
    if (pdf.Length == 0) continue;          // skip a missing document
    await merger.AddAsync(pdf, ct);
}
if (merger.InputCount == 0) return Results.NotFound();
await merger.CompleteAsync(ct);             // writes the page tree, bookmarks, form and cross-reference table

// Or, from an IAsyncEnumerable<byte[]>:
int pages = await PdfMerger.MergeAsync(DownloadInOrder(paths), response.Body, cancellationToken: ct);
```

The result is the same as `PdfMerger.Merge`'s, but the output is only a valid PDF once `Complete` has run. If adding a
PDF throws (a damaged or encrypted input, `MaxPages` reached), what was written cannot be used and the merger refuses
further calls. `Passwords` are matched to inputs in the order they are added.

The pages keep their content, fonts, images, links, annotations, layers and fillable form fields, and each input's
bookmarks follow each other. A form field named like one in an earlier input is renamed `name_2`, `name_3` and so on, so
the copies keep their own values. Document properties come from the first input unless `PdfMergeOptions` sets them. The
result is never encrypted and has no signatures (merging would break them); PDF/A conformance, attachments, page labels
and tags are not carried over. Images, fonts and other resources that are byte-for-byte the same in several inputs, such
as a logo on every payslip of a run, are written once.

Inputs are handled as untrusted: damaged files are repaired where possible, decoding is limited per input, and
`MaxPages` (default 100,000) caps the result. Inputs without any pages throw `InvalidDataException`. Merging was tested
on PDFs from Chrome (as IronPDF makes them), Ghostscript, LibreOffice, MuPDF, qpdf, ReportLab, pypdf incremental
updates, scanners (JPEG, CCITT G4, CMYK, OCR text layers) and damaged files (wrong or missing cross-reference tables,
truncated files, junk before the header); every merged page rendered pixel-identical to its original in Poppler.
Merging four small documents takes about 1 ms, and 1,000 one-page Chrome payslips about 1 s.

## PDF to image

`PdfToImage` and `PdfRenderer` render PDF pages to PNG (or raw RGBA pixels). This works for any PDF, not only
PdfPecker's own output, and also uses no third-party packages: the PDF parser, font engines, JPEG and fax decoders,
rasterizer and PNG encoder are all part of the library.

```csharp
// Every page as PNG bytes (rendered in parallel), or one page (0-based index)
byte[][] pages = PdfToImage.Convert(pdfBytes, new ImageOptions { Dpi = 150 });
byte[] first = PdfToImage.ConvertPage(pdfBytes, 0);

// report.pdf -> out/report-1.png, out/report-2.png, ...
PdfToImage.ConvertFile("report.pdf", "out");

// Open once, render many pages; RenderedImage also exposes the RGBA pixels
var renderer = PdfRenderer.Open(pdfBytes, password: null);
RenderedImage image = renderer.RenderPage(2, new ImageOptions { Width = 800 });
image.SavePng("page3.png");
```

`ImageOptions` sets `Dpi` (default 150) or a target `Width`/`Height` in pixels, `BackgroundColor` or `Transparent`,
`RenderAnnotations`, `Password`, `MaxDegreeOfParallelism` and `PngCompression`. A `PdfRenderer` is thread-safe and
shares decoded fonts and images between pages. Encrypted files open with the user or owner password
(`PdfPasswordException` otherwise); unreadable files throw `InvalidDataException`, as does a page that needs more than
20 million drawing operations (nested forms can multiply a few bytes into billions). Pass a cancellation token to
bound the time a page may take.

What the renderer supports:

- **Files**: cross-reference tables and streams, object streams, incremental updates, damaged files (rebuilt by
  scanning), RC4 and AES-128/256 encryption, all standard filters (Flate, LZW, ASCII85, ASCIIHex, RunLength, predictors).
- **Graphics**: paths, fills (non-zero and even-odd), strokes with joins, caps and dashes, clipping, anti-aliasing,
  all blend modes, constant alpha, soft masks, transparency groups, axial, radial, function and mesh shadings
  (types 1 to 7), tiling patterns, form XObjects, optional content (layers) and annotation appearances.
- **Text**: embedded TrueType, OpenType, CFF and Type 1 fonts, Type 3 fonts, CID fonts with CMaps, vertical text,
  all text rendering modes. The 14 standard fonts and non-embedded fonts are drawn with built-in Liberation and
  DejaVu-derived faces stretched to the document's widths.
- **Images**: every bit depth and colour space (Gray, RGB, CMYK, Lab, ICC-based, Indexed, Separation, DeviceN),
  baseline and progressive JPEG, JPEG 2000 (`JPXDecode`, including embedded alpha), JBIG2 (generic, text, symbol,
  refinement and halftone regions, with `JBIG2Globals`), CCITT fax, stencil masks, colour-key masks and soft masks.

For what the renderer does not support yet, see [Limitations](#limitations).

## What is supported

**HTML**: the common document elements: headings, paragraphs, `div`/`section`-style blocks, `span`, `a` (external links
and `#id` links inside the document), `b`/`strong`, `i`/`em`, `u`, `s`, `sub`, `sup`, `small`, `big`, `mark`, `code`,
`pre`, `blockquote`, `hr`, `br`, `ul`/`ol`/`li` (nested, with `start` and `value`), `dl`, `table` (with `thead`,
`tbody`, `tfoot`, `caption`, `colspan`, `rowspan`, `col width`), `img`, inline `<svg>`, and legacy attributes such as `align`,
`valign`, `bgcolor`, `width`, `height`, `border`, `cellpadding`, `cellspacing` and `<font>`. The `dir` attribute
(`ltr`, `rtl` and `auto`), `<bdi>` and `<bdo>` set the text direction. The parser is forgiving
about unclosed and misnested tags, the way browsers are.

**Markdown**: [CommonMark 0.31](https://spec.commonmark.org/0.31.2/) (651 of the 652 spec examples pass; the one
miss is an exotic HTML5 entity name) plus the GitHub Flavored Markdown extensions: tables with column alignment,
`~~strikethrough~~`, task list items (`- [x]`) and bare `www.`/`https://`/e-mail autolinks. Headings get GitHub-style
ids, so `[see below](#heading-text)` links work in the PDF too. The parser runs in linear time on hostile input
(deep nesting, unclosed brackets, long delimiter runs). See [Limitations](#limitations) for what is not supported.

**CSS**: `<style>` blocks, `<link rel="stylesheet">`, inline `style` attributes, `@media` (media queries level 4:
`print`, `width`/`height` and `orientation` of the page area, ranges such as `(400px <= width <= 800px)`, `and`, `or`,
`not`), `@supports` (property tests, `selector()`, `and`/`or`/`not`), `@layer` cascade layers, CSS nesting (`&`),
`@property` (initial values and inheritance of registered custom properties) and `@page` (see
[Paged media](#paged-media)). Selectors: type, `.class`, `#id`, `*`, attribute selectors (`[a]`, `=`, `~=`, `|=`,
`^=`, `$=`, `*=`, with `i`/`s` flags), descendant, `>`, `+`, `~`, `:first-child`, `:last-child`, `:only-child`,
`:nth-child()` and `:nth-last-child()` (incl. `of S`), `:first-of-type`, `:last-of-type`, `:only-of-type`,
`:nth-of-type()`, `:nth-last-of-type()`, `:not()`, `:is()`, `:where()` and `:has()` (all with selector lists; `:is()`
and `:where()` skip entries they do not understand), `:root`, `:link`, `:empty`, `:lang()`, form states (`:checked`,
`:disabled`, `:enabled`, `:required`, `:optional`, `:read-only`, `:placeholder-shown` and the like), `::before`,
`::after` and `::marker`, with specificity and `!important` handled as in browsers. Properties:

| Area | Properties |
| --- | --- |
| Box model | `display` (block, inline, inline-block, flex, inline-flex, grid, inline-grid, list-item, table parts, none), `margin`, `padding` (also the logical `margin-inline`/`-block` and `padding-inline`/`-block` properties and their `-start`/`-end` longhands), `border` (solid, dashed, dotted, double), `border-radius` (incl. elliptical `/` radii and percentages; rounds backgrounds, borders, shadows and images), `box-shadow` (outer shadows; a blur is drawn as fading bands), `overflow` (`hidden`, `clip`, `auto` and `scroll` clip to the padding box, following its rounded corners, also in table cells; `overflow-x`/`overflow-y`), `outline` and `outline-offset` (all border styles; follows rounded corners), `width`, `height`, `min-`/`max-width`, `min-height`, `box-sizing`, `aspect-ratio` (incl. `auto <ratio>`), `object-fit` and `object-position` on images |
| Text | `color`, `font`, `font-family`, `font-size`, `font-weight`, `font-style`, `line-height`, `text-align` (incl. justify, `start` and `end`), `direction`, `unicode-bidi`, `text-decoration`, `text-transform`, `text-indent`, `letter-spacing`, `word-spacing`, `white-space`, `overflow-wrap`/`word-wrap`, `word-break` (`break-all`), `vertical-align`, `text-shadow` (several shadows; a blur is drawn as faint copies of the text), `text-overflow: ellipsis` (on a box that clips its overflow), `text-decoration-style` (`solid`, `double`, `dotted`, `dashed`, `wavy`), `text-decoration-color`, `text-decoration-thickness`, `text-underline-offset`, `text-align-last`, `tab-size`, `font-variant: small-caps` (synthesized from capitals when the font has none), `line-clamp` and `-webkit-line-clamp` (with an ellipsis), `hyphens: manual | none`. Lines break as in Chrome: at spaces, after hyphens and dashes, at `&shy;` (showing a hyphen) and `&#8203;`; a word too wide for its line overflows unless `overflow-wrap` or `word-break` allows breaking it. `line-height: normal` comes from the font's own metrics, rounded to whole pixels as Chrome does, and border widths snap to whole pixels. Text is kerned as in Chrome: from the font's GPOS or `kern` table, and for the standard fonts from the Liberation fonts browsers use in their place. Embedded fonts get their standard ligatures (GSUB `liga`/`clig`, such as fi and ffi) as in Chrome, except in letter-spaced text; `font-variant-ligatures: none` or `font-feature-settings: "liga" 0` turns them off |
| Backgrounds | `background-color`, `background-image` and `background` with `url()` images (PNG, JPEG, GIF, WebP, and SVG files drawn as vectors), `linear-gradient()`, `radial-gradient()`, `conic-gradient()` (pie charts and colour wheels, drawn as smooth PDF shadings) and their `repeating-` forms (several layers allowed); `background-repeat` (incl. `space`, `round`), `background-position` (incl. edge offsets like `right 10px bottom 5px`), `background-size` (`cover`, `contain`, lengths), `background-origin`, `background-clip`. Repeated images are written once as PDF tiling patterns. `url()` in a linked stylesheet is relative to the stylesheet and follows the same file access rules as `<img>`. As in Chrome's print output, the background of `<html>`, or else of `<body>`, fills every page area (images and gradients too, positioned against the root box and continuing from page to page); `@page { background: <colour> }` also colours the page margins. `opacity` on SVG |
| Flexbox | `flex-direction`, `flex-wrap`, `flex-flow`, `flex`, `flex-grow`/`-shrink`/`-basis`, `order`, `justify-content`, `align-items`, `align-self`, `align-content`, `gap`, `margin: auto` |
| Grid | `grid-auto-flow: dense` packing, `grid-template-columns`/`-rows` (lengths, `%`, `fr`, `auto`, `min-content`, `max-content`, `minmax()`, `fit-content()`, `repeat()` incl. `auto-fill`/`auto-fit`), `grid-auto-rows`/`-columns`, `grid-auto-flow`, `grid-template-areas`, `grid-template`, named lines (`[name]`), `grid-column`/`-row` (line numbers, names and `span`), `grid-area` (incl. area names), `gap`, `justify-items`/`-self`, `align-items`/`-self`, `justify-content`, `align-content`, `place-*` |
| Columns | `column-count`, `column-width`, `columns`, `column-gap`, `column-rule` (all border styles), `column-span: all` |
| Variables | custom properties (`--name: value`) and `var(--name, fallback)`, anywhere including shorthands |
| Math functions | `calc()`, `min()`, `max()`, `clamp()`, `round()`, `mod()`, `rem()`, `abs()`, `sign()`, `sin()`, `cos()`, `tan()`, `asin()`, `acos()`, `atan()`, `atan2()`, `pow()`, `sqrt()`, `hypot()`, `log()` and `exp()` for lengths, numbers and angles, nested in any combination and mixing units (`calc(100% - 2em)`, `min(100%, 600px)`, `clamp(1rem, 2.5vw, 2rem)`), with `var()` inside them. Percentages resolve against the property's own reference (the containing block's width for `width` and margins, the parent's font size for `font-size`), so they work in widths, heights, margins, padding, flex bases, grid tracks, gaps, positions, translations, font sizes and borders |
| Positioning | `float` and `clear`; `position` (`relative`, `absolute`, `fixed`; `sticky` is drawn as `relative`) with `top`, `right`, `bottom` and `left`; `z-index` |
| Transparency | `opacity` (a block is painted as one PDF transparency group; an inline `<span>` fades its text and background), translucent colours everywhere (`rgba()`, `hsla()`, `#rrggbbaa`, `transparent`), gradients that fade to `transparent` (as a soft mask). PDF/A-2 and 3 allow all of this |
| Transforms | `transform` (`translate()`, `translateX()`/`Y()`, `scale()`, `scaleX()`/`Y()`, `rotate()`, `skew()`, `skewX()`/`Y()`, `matrix()`), `transform-origin`, and the individual `translate`, `rotate` and `scale` properties |
| Generated content | `::before`, `::after` and `::marker` with `content` (strings, `attr()`, `counter()`, `counters()`, `open-quote`/`close-quote`, and for paged media `target-counter()`, `target-text()`, `leader()`, `string()` and `content()`), `counter-reset`, `counter-increment`, `counter-set`, `string-set` |
| SVG | `fill`, `stroke`, `stroke-width`, `fill-opacity`, `stroke-opacity`, `fill-rule`, `stroke-linecap`, `stroke-linejoin`, `stroke-miterlimit`, `clip-path`, `clip-rule`, `mask`, `mask-type`, `filter`, `marker`, `marker-start`/`-mid`/`-end`, `flood-color`, `flood-opacity`, `color-interpolation-filters`, `text-anchor`, `dominant-baseline` (as CSS or presentation attributes) |
| Lists | `list-style-type` (disc, circle, square, decimal, decimal-leading-zero, alpha, roman, none, or a string such as `'— '`), `list-style`, `::marker` (content, colour, font) |
| Tables | `border-collapse` (each shared border drawn once, the wider one winning), `border-spacing`, cell `vertical-align`, automatic column widths, `table-layout: fixed`, a table `height` shared out over the rows |
| Paging | `page-break-before`/`after`/`inside`, `break-before`/`after`/`inside` (`always`, `page`, `avoid`), `orphans`, `widows`, `@page` size, margins and background, `@page :first`/`:left`/`:right`, the sixteen page-margin boxes (`@top-center` and the like), named pages (`page`), `string-set` (see [Paged media](#paged-media)) |

Units: `px`, `pt`, `em`, `rem`, `%`, `mm`, `cm`, `in`, `pc`, `ex`, `ch`, `vw`, `vh`, `vmin`, `vmax` (the viewport is
the page's content area, minus any running header and footer, in whole pixels as in Chrome). Colours: names, `#rgb`, `#rrggbb`,
`#rrggbbaa`, `rgb()`, `rgba()`, `hsl()`, `hsla()` (comma or space syntax), `hwb()`, `lab()`, `lch()`, `oklab()`,
`oklch()`, `color()` (sRGB, Display P3 and the other predefined spaces, mapped to sRGB), `color-mix()`,
`light-dark()` (the light value) and `currentColor`.

**Pagination**: lines, images and table rows are never cut in half at a page boundary; table header rows repeat on
every page a table spans; backgrounds and borders continue across pages. Flex lines and grid rows move to the next
page as a whole when they don't fit. A paragraph that splits leaves at least `orphans` lines (default 2) at the bottom
of a page and carries at least `widows` lines (default 2) to the next. Headings keep with the content that follows
them (`break-after: avoid` in the default styles), and `break-before`/`break-after: avoid` does the same for any block.
`break-inside: avoid` keeps a block or a whole table on one page when it fits.

**Links and bookmarks**: `<a href>` becomes a clickable link, whether inline or a block (a card wrapped in a link),
and stays clickable on both pages when a page break splits it. External URLs open in the viewer's browser and `#id`
links jump to the element. Headings `h1`-`h6` become the PDF's bookmarks, nested by level; set
`PdfOptions.Bookmarks = false` to leave them out.

**Running header and footer**: an element with the `data-sticky-pdf-header` or `data-sticky-pdf-footer` attribute is
taken out of the flow and drawn at the top or bottom of every page, and the page content flows in the space between.
Inside it, `{page}` and `{total-pages}` (or `{pages}`) are replaced with the page number and page count. An optional
`height` attribute (`height="17mm"`) reserves at least that much space.

```html
<header data-sticky-pdf-header height="17mm">ACME Ltd · Payslip</header>
<main>...</main>
<footer data-sticky-pdf-footer height="12mm">Page {page} of {total-pages}</footer>
```

### Paged media

`@page` rules set the page size, margins and background, and their margin boxes (`@top-left`, `@top-center`,
`@bottom-right` and the other thirteen) print headers and footers with `counter(page)` and `counter(pages)`, chosen
per page with `:first`, `:left` and `:right`. On top of that:

- **Named pages**: `page: wide` puts an element on pages styled by `@page wide { ... }`, with their own size,
  orientation, margins, background and margin boxes. Content on another named page starts a new page, so a landscape
  table or a full-bleed cover can sit between portrait pages.
- **Running headers**: `string-set` copies text from the document (`content()`, `attr()`, strings and counters) into
  a named string, and `string(name)` shows it in a margin box: the chapter title in the page header, say. As in the
  spec, `string(name, first | start | last | first-except)` picks which value a page shows.
- **Tables of contents**: `target-counter(attr(href), page)` shows the page number of the element a link points to,
  and `leader(dotted)` (or `solid`, `space`, or a string) fills the space before it. `target-text()` shows the
  linked element's text. The document is laid out again with the numbers it finds, so they are always right.

```css
@page { size: A4; margin: 20mm;
  @top-center { content: string(chapter) }
  @bottom-right { content: "Page " counter(page) " of " counter(pages) } }
@page cover { margin: 0; background: #1d3557; @top-center { content: none } @bottom-right { content: none } }
@page wide { size: A4 landscape }
.cover { page: cover }
.wide-table { page: wide }
h2 { string-set: chapter content(); break-before: page }
nav a::after { content: leader(dotted) target-counter(attr(href), page) }
```

Chrome supports named pages and margin boxes, but not running strings, `target-counter()` or `leader()`.

**SVG**: inline `<svg>` and SVG files (in `<img>` and as `url()` backgrounds, including `data:image/svg+xml` URIs) with `path` (all commands, arcs included), `rect` (incl. rounded), `circle`, `ellipse`, `line`,
`polyline`, `polygon`, `text` and `tspan` (`x`, `y`, `dx`, `dy`, `text-anchor`, `dominant-baseline`, standard fonts),
`g`, `use`, nested `svg`, `viewBox`, `preserveAspectRatio` and `transform`, drawn as vector paths. `linearGradient` and
`radialGradient` (both units, `gradientTransform`, `spreadMethod`, `href` inheritance) become PDF shadings, and
`pattern` fills and strokes (both units, `viewBox`, `patternTransform`, `href` inheritance) become PDF tiling patterns.
`clipPath` clips the elements that reference it, and `mask` (luminance or alpha, both units) becomes a PDF soft mask.
`marker` draws arrowheads and dots at the vertices of paths, lines, polylines and polygons (`orient` incl. `auto` and
`auto-start-reverse`, `markerUnits`, `viewBox`, `refX`/`refY`, clipped to the marker unless `overflow="visible"`).
`filter` is applied in pixels, at 144 dpi, and drawn as an image: `feGaussianBlur`, `feOffset`, `feDropShadow`,
`feFlood`, `feMerge`, `feComposite` (all operators), `feBlend` (separable modes), `feColorMatrix`,
`feComponentTransfer` and `feMorphology`, in linearRGB or sRGB. As in browsers, an SVG file is styled only by its own `<style>`
elements and attributes, not by the page's CSS, and loads nothing else.

**Images**: JPEG (embedded as-is, RGB, grayscale and CMYK), PNG (all colour types and bit depths, transparency,
interlacing), GIF (transparency, interlacing) and WebP (lossy, lossless and with alpha), from files, `data:` URIs or
your `ResourceResolver`. Animated GIF and WebP files show their first frame. SVG files are drawn as vectors (see SVG
above). Opaque PNGs are passed through without re-encoding. Images wider than the page are scaled down to fit.

**Fonts**: registered and `@font-face` TrueType/OpenType fonts are embedded as subsets (Type0/CIDFontType2 for
TrueType outlines, CIDFontType0 with an OpenType program for CFF outlines) with a ToUnicode map, so text can be
searched and copied. Without them, the PDF standard fonts are used: `font-family` maps to Helvetica (sans-serif,
Arial, Verdana, ...), Times (serif, Georgia, ...) or Courier (monospace, Consolas, ...), each in regular, bold,
italic and bold italic. With `EmbedStandardFonts` or PDF/A, these are replaced by embedded subsets of the bundled,
metric-compatible Liberation Sans, Serif and Mono.

## Limitations

PdfPecker is built for fast conversion of HTML templates you control, not for rendering arbitrary web pages. These are
the known gaps; most are scope choices for speed and size, and they are the first candidates for future work. Anything
not listed in [What is supported](#what-is-supported) should be assumed missing.

**By design**

- No JavaScript. Scripts are never run, so content must be in the HTML before conversion (render it server-side first).
- No network access: `http:`/`https:` stylesheets and images are not downloaded. Supply them through your
  `ResourceResolver`, as files under `BaseDirectory`, or as `data:` URIs.
- `<canvas>`, `<iframe>`, `<video>`, `<audio>`, `<object>` and `<embed>` are not drawn, nor are file, range, colour and
  image inputs. Form fields have no JavaScript (no calculations, formats or submit actions), and a field inside a
  transformed box, a running header or footer, or a `position: fixed` box is drawn but not fillable.

**Text and fonts**

- Without registered fonts or `EmbedStandardFonts`, text uses the standard PDF fonts, so characters are limited to
  the Windows-1252 set (Western European languages); other characters are replaced with `?`. The bundled Liberation
  fonts add Central European, Greek and Cyrillic, but not CJK or other scripts: register fonts for those.
- Complex-script shaping covers Arabic and Hebrew (and other scripts written with Arabic letters, such as Persian and
  Urdu). Devanagari and other Indic scripts, Thai, Khmer, Myanmar and Syriac are not shaped, so their vowel signs
  and conjuncts come out wrong, and Thai text breaks only at spaces. There is no vertical text (`writing-mode`).
- In Latin and other simple scripts, shaping is limited to pair kerning and standard ligatures; no contextual
  alternates or other OpenType features (`small-caps` is synthesized from scaled capitals).
- Right-to-left text in SVG `<text>` is not reordered or shaped, and `text-overflow: ellipsis` is always placed at the
  right end of a line.
- Variable fonts use their default instance (`font-variation-settings` and `font-stretch` are ignored).
- No automatic hyphenation (`hyphens: auto`); words break only at spaces, hyphens, `&shy;` and `&#8203;`.
- SVG `<text>` uses the standard fonts (embedded as Liberation for PDF/A), only the first value of position lists and
  no `textPath`.

**CSS**

- A table column width that mixes a percentage with other units (`calc(50% - 10px)`) is sized as `auto`.
- `@import` is ignored (use `<link rel="stylesheet">`), and rules inside `@container` blocks are skipped. `@page`
  rules for `:first`, `:left` and `:right` pages can style margin boxes but not change the page size or margins.
  A named page's `:first` is the document's first page, as in Chrome, not the first page of the named run.
- Selectors: no `::first-letter`/`::first-line`. A rule with an unsupported selector is skipped. `:hover`, `:focus`
  and similar interaction states never match. Selectors inside `:is()`, `:where()` and `:not()` may have at most eight
  compound parts (one when nested in another of them).
- Properties not supported: `filter`, `mix-blend-mode`, `clip-path` on HTML elements (only in SVG), `border-image`,
  `list-style-image`, `font-variant-numeric` and inset `box-shadow`. No relative colours (`rgb(from …)`).
- `text-shadow` blur is approximated with faint copies of the text rather than a true Gaussian blur, and decorations
  (underlines) get no shadow. `text-overflow` only supports `ellipsis` at the end of the line (`clip` and custom
  strings do nothing). `aspect-ratio` does not take part in min-/max-content sizing (shrink-to-fit widths) and is
  ignored on tables. `object-fit` applies to raster images; an SVG file in `<img>` keeps its own
  `preserveAspectRatio` fit.
- Borders: `groove`, `ridge`, `inset` and `outset` draw as solid.
- `position: sticky` is drawn as `relative`; `background-attachment: fixed` scrolls with the box.
- Lists: `list-style-type` covers disc, circle, square, decimal, decimal-leading-zero, alpha, roman and strings only
  (no Greek, CJK or `@counter-style`).

**Backgrounds and transparency**

- No `background-clip: text`; backgrounds of inline elements (`<span>`) show only their colour, not images or
  gradients.
- Blurred box shadows and repeating gradients with translucent stops are blended onto the background colour rather
  than truly see-through; SVG gradient stops with transparency are blended onto white.

**Layout**

- Grid: no subgrid.
- Multi-column: children are kept whole and balanced between columns; a single block is never split across columns.
- Overflow: a positioned child with a `z-index` inside a clipping box stays stacked within that box.

**Images and SVG**

- Images: no AVIF, BMP or TIFF. Animated GIF and WebP files show only their first frame.
- SVG (inline and files): no embedded `<image>`. Filters have no `feTurbulence`, `feImage`, `feTile`,
  `feConvolveMatrix`, `feDisplacementMap` or lighting (these pass their input through unchanged) and ignore primitive
  subregions; filtered content becomes an image (text in it is no longer selectable). Watermark images must be raster
  images (PNG, JPEG, GIF or WebP).

**Markdown and the PDF renderer**

- Markdown: no footnotes, definition lists, math or front matter.
- `PdfRenderer` (PDF to image): no predefined CJK CMaps or non-embedded CJK fonts, and ICC profiles are not applied
  (ICC-based colours use their device equivalents).

## Performance

Measured with `bench/PdfPecker.Benchmarks` on a 4-core cloud VM (.NET 10, Release, after JIT warm-up):

| Document | Pages | Median | Throughput |
| --- | --- | --- | --- |
| Invoice with a PNG logo and a styled table | 1 | 0.55 ms | about 1,800 docs/s on one core |
| Feature tour (text, lists, tables, JPEG and PNG) | 5 | 2.5 ms | about 1,700 docs/s on 4 cores |
| Dense report with 1,000 table rows | 30 | 19.5 ms | about 1,500 pages/s on one core |
| Markdown guide (`guide.md`, headings, lists, code, a table) to PDF | 2 | 0.9 ms | about 1,000 docs/s on one core |

Parsing Markdown to HTML runs at roughly 20 to 45 MB/s, so it is a small part of a Markdown to PDF conversion.

The first conversion in a process also pays for JIT compilation (about 150 ms); later ones run at the speeds above.

Rendering PDF pages to images, same machine, one core per page (`PdfRenderer`, first render of each page of a freshly
opened document, so fonts and images are decoded too):

| Document | Render at 96 DPI | Render at 150 DPI | PNG encoding at 150 DPI |
| --- | --- | --- | --- |
| Invoice (1 page) | 13 ms | 19 ms | 24 ms |
| Feature tour (5 pages) | 7 ms per page | 12 ms per page | 26 ms per page |
| Large report (30 pages) | 5 ms per page | 10 ms per page | 29 ms per page |

Rendering the same page again (fonts and glyphs cached) takes about 5 ms at 150 DPI. Multi-page conversions render
pages in parallel.

## Building

```
dotnet build
dotnet test
dotnet run --project samples/PdfPecker.Sample -c Release      # converts samples/PdfPecker.Sample/html/*.html and *.md into ./output
dotnet run --project bench/PdfPecker.Benchmarks -c Release
dotnet run --project bench/PdfPecker.Benchmarks -c Release -- --requirements    # memory and CPU per conversion
dotnet pack src/PdfPecker -c Release -o ./nupkg
```

## How it works

Markdown is first turned into HTML: `Markdown/BlockParser` finds the block structure line by line, following the
CommonMark reference algorithm, `Markdown/InlineParser` handles emphasis (delimiter stack) and links (bracket stack),
and `Markdown/HtmlRenderer` writes the HTML, which then takes the same path as any other document:

1. `Html/HtmlParser` builds a DOM in a single pass, applying the browser rules for implied end tags.
2. `Css/StyleResolver` cascades a user-agent stylesheet, presentational attributes, document stylesheets and inline
   styles. Rules are indexed by id, class and tag, and elements with identical inputs share one computed style.
   For a tagged PDF, `Pdf/StructureTree` then maps the elements to structure types and gives every text node, image and
   list marker a slot in that tree.
3. `Layout/LayoutEngine` lays out blocks, inline text (line breaking, justification), lists and tables
   (automatic column widths), paginating as it goes, and produces a flat list of draw operations. Inline content with
   right-to-left text gets its embedding levels from `Text/Bidi` (UAX #9), and each line is put in visual order after
   it is broken. `Fonts/ComplexShaper` shapes Arabic and Hebrew runs with the font's GSUB and GPOS tables
   (`Fonts/OpenTypeLayout`).
4. `Pdf/PdfDocumentWriter` writes PDF 1.4: content streams, fonts, image XObjects, link annotations,
   the bookmark outline and the cross-reference table. A tagged PDF's content is wrapped in marked-content sequences
   for its slots (or as artifacts), and the structure tree and its parent tree are written last. `Fonts/FontSubsetter` and `Fonts/CffSubsetter` cut embedded
   fonts down to the glyphs used (with their composite components and the CFF subroutines they call).

The renderer (`Rendering/`) works the other way round: `Parsing/PdfFile` reads the object graph, `PageRenderer`
interprets content streams, `Fonts/` turns glyphs into outlines (cached as anti-aliased bitmaps), `Images/` decodes
images, and `Graphics/Rasterizer` fills paths with exact area coverage before `PngEncoder` writes the result.

The third-party fonts, font data and code built into the library are listed, with their licenses, in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
