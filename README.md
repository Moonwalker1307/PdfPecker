![PdfPecker](https://raw.githubusercontent.com/Moonwalker1307/PdfPecker/main/assets/logo.png)

# PdfPecker

[![NuGet](https://img.shields.io/nuget/v/PdfPecker.svg)](https://www.nuget.org/packages/PdfPecker)

Fast HTML and Markdown to PDF conversion, PDF to PNG, JPEG, WebP and TIFF rendering, and PDF to HTML, for .NET, with no
dependencies.

PdfPecker has its own HTML parser, Markdown parser, CSS engine, layout engine and PDF writer, and its own PDF renderer.
It does not start a browser, ship native binaries or pull in third-party packages; the NuGet package depends only on
the .NET base class library.

```csharp
using PdfPecker;

byte[] pdf = HtmlToPdf.Convert("<h1>Hello</h1><p>World</p>");
File.WriteAllBytes("hello.pdf", pdf);
```

**Documentation: [pdfpecker.net/docs](https://pdfpecker.net/docs/)** · [HTML to PDF in C#: a step-by-step guide](https://pdfpecker.net/docs/html-to-pdf-csharp/)

## Install

```
dotnet add package PdfPecker
```

The package targets `net8.0`, `net9.0` and `net10.0`, so it works on .NET 8 and later, on Windows, Linux and macOS.

## Quick start

```csharp
// HTML string to PDF bytes, or file to file (relative images and stylesheets resolve against the HTML file's folder)
byte[] pdf = HtmlToPdf.Convert(html);
HtmlToPdf.ConvertFile("invoice.html", "invoice.pdf");

// ASP.NET Core
app.MapGet("/invoice", () => Results.File(HtmlToPdf.Convert(html), "application/pdf", "invoice.pdf"));

// Page size, margins, footer and fonts
var options = new PdfOptions
{
    PageSize = PageSize.Letter,
    Margins = PageMargins.FromMillimeters(20, 15, 20, 15),
    FooterText = "Page {page} of {pages}",
};
options.Fonts.AddFile("fonts/OpenSans-Regular.ttf");
byte[] letter = HtmlToPdf.Convert(html, options);

// Markdown to PDF or HTML, merging, PDF pages to PNG, and a whole PDF to one black and white fax TIFF
byte[] guide = MarkdownToPdf.Convert(File.ReadAllText("README.md"));
string guideHtml = MarkdownToHtml.Convert(File.ReadAllText("README.md"));
byte[] merged = PdfMerger.Merge(invoicePdf, termsPdf);
byte[][] pngs = PdfToImage.Convert(pdf, new ImageOptions { Dpi = 150 });
byte[] tiff = PdfToImage.ConvertToTiff(pdf, new ImageOptions { Dpi = 200, TiffColorMode = TiffColorMode.BlackAndWhite });

// Make any existing PDF smaller: losslessly, or with images downsampled to 150 dpi and recompressed as JPEG
byte[] smaller = PdfCompactor.Compact(File.ReadAllBytes("scan.pdf"));
byte[] smallest = PdfCompactor.Compact(pdf, new PdfCompactOptions { MaxImageDpi = 150, ImageQuality = 75 });

// Fast Web View: the first page shows while the rest of a large PDF is still downloading
byte[] web = HtmlToPdf.Convert(html, new PdfOptions { Linearize = true });
byte[] mergedForWeb = PdfLinearizer.Linearize(PdfMerger.Merge(invoicePdf, termsPdf));

// Title, author, PDF version, page sizes, encryption and PDF/A claim of any PDF
var properties = PdfDocumentProperties.Read(File.ReadAllBytes("received.pdf"));
Console.WriteLine($"{properties.Title} by {properties.Author}: PDF {properties.Version}, {properties.PageCount} pages, {properties.PdfAConformance}");

// Any PDF as one self-contained HTML page: page images with selectable, searchable text and working links
string html = PdfToHtml.Convert(File.ReadAllBytes("statement.pdf"));

// Or its text as headings, paragraphs, lists and real <table> elements in reading order, for search and language models
string structured = PdfToHtml.Convert(File.ReadAllBytes("statement.pdf"), new PdfHtmlOptions { Mode = PdfHtmlMode.Structured });

// The same structure with the PDF's fonts, colours, backgrounds, borders and pictures
string styled = PdfToHtml.Convert(File.ReadAllBytes("statement.pdf"),
    new PdfHtmlOptions { Mode = PdfHtmlMode.Structured, IncludeStyles = true, IncludeImages = true });

// Open a PDF once and render its pages one at a time, or in parallel
var renderer = PdfRenderer.Open(pdf);
renderer.RenderPage(0, new ImageOptions { Dpi = 150 }).Save("page1.png");
```

The conversion methods are static and thread-safe, so you can convert many documents in parallel. The few instance types are `FontCollection` and `PdfRenderer`, which can be shared by parallel work, and `StreamingPdfMerger`, which is not thread-safe: it builds one output and is used from one thread at a time.

## Fluent API

[PdfPecker.Fluent](https://www.nuget.org/packages/PdfPecker.Fluent) builds the same PDFs from C# code instead of HTML,
in the style of QuestPDF, so most QuestPDF documents port by changing the `using` lines. It is a separate package from
this repository, released with every PdfPecker release under the same version, and its only dependency is PdfPecker.

```
dotnet add package PdfPecker.Fluent
```

```csharp
using PdfPecker.Fluent;

Document.Create(container => container.Page(page =>
{
    page.Size(PageSizes.A4);
    page.Margin(2, Unit.Centimetre);
    page.Header().Text("Invoice 2026-104").FontSize(20).Bold();
    page.Content().Table(table =>
    {
        table.ColumnsDefinition(columns => { columns.RelativeColumn(3); columns.RelativeColumn(); });
        table.Cell().Text("Consulting");
        table.Cell().AlignRight().Text("1,200.00");
    });
    page.Footer().AlignCenter().Text(x => { x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
}))
.GeneratePdf("invoice.pdf");
```

It writes compact HTML and CSS and renders it with PdfPecker, so every `PdfOptions` feature (PDF/A, PDF/UA,
signatures, e-invoices) works with it. What it covers and how it differs from QuestPDF is in its
[README](https://github.com/Moonwalker1307/PdfPecker/blob/main/src/PdfPecker.Fluent/README.md).

## Features

- **HTML and CSS**: flexbox, grid and subgrid, CSS tables, floats with `shape-outside`, multi-column layout,
  positioning (also the legacy `clip: rect()`, and absolute boxes inside a positioned inline element, such as
  tooltips and badges), transforms, gradients (also `in oklch` and other colour spaces, and as
  list markers), fixed background images (placed on each page's paper, as Chrome prints them), filters, masks,
  media and container queries, logical properties, `writing-mode` (with floats, sized boxes and vertical-lr line
  order), `text-wrap`, `line-clamp` with your own `block-ellipsis`, underlines from the font or below the descenders
  (`text-underline-position`), drop caps, `::marker` styles (a larger marker font makes its line taller, and
  `content: none` hides the marker, as in Chrome), images and gradients in `::before` and `::after` content, inline SVG (with
  HTML in `<foreignObject>`) and PNG, JPEG, GIF, WebP, BMP and TIFF images (also chosen by `srcset` and `<picture>`,
  shown by `<embed>` and `<object>`, and photos turned upright by their EXIF orientation), and CSS-only charts such as
  Charts.css (column, bar, area, line and pie charts), laid out as Chrome lays them out. Older CSS works too: the
  `-webkit-` property names Chrome still takes (`-webkit-transform`, `-webkit-flex`, ...), the legacy
  `display: -webkit-box` and its `-webkit-box-` properties, and the `-webkit-gradient()` and prefixed gradient
  syntaxes. As in browsers, a length without a unit (`width: 200`) counts as pixels only in a page without a doctype;
  with `<!DOCTYPE html>` it is ignored.
  [More](https://pdfpecker.net/docs/html-and-css/)
- **Paged media**: `@page` sizes, borders, padding and background images (which cover the whole sheet) and margin boxes, running headers and footers (also a first page of its own, and bands
  of their own for named pages), footnotes, tables of contents with page numbers, break control (also table row groups
  and group header rows kept together, as reports keep a group on one page, and forced breaks between table rows and between flex lines), table rows that break inside at a page's end as in Chrome, unless a cell would have nothing before the break, table
  headers and footers repeated on every page (also around a row split across pages), and
  bookmarks from the headings that `bookmark-level`, `bookmark-label` and `bookmark-state` can extend, rename or close.
  [More](https://pdfpecker.net/docs/paged-media/)
- **Content scaled to fit a box**: `-pdfpecker-scale-to-fit: shrink` fits text of any length into a fixed space, for
  labels, badges and certificates. [More](https://pdfpecker.net/docs/html-and-css/#scale-content-to-fit-a-box)
- **Markdown**: CommonMark 0.31 and GitHub Flavored Markdown to PDF or HTML, with footnotes, definition lists and YAML
  front matter. [More](https://pdfpecker.net/docs/markdown/)
- **Fonts**: TrueType, OpenType, WOFF and WOFF2, subset and embedded, with kerning, ligatures, variable fonts and
  OpenType features, and bundled fallback fonts for letters and symbols a document's fonts lack. COLR colour fonts,
  versions 0 and 1 (such as Noto Color Emoji), print in colour as Chrome prints them, with the default palette.
  [More](https://pdfpecker.net/docs/fonts/)
- **Languages**: Arabic and Hebrew with bidi and shaping, Indic scripts, Thai and Lao, CJK line breaking and
  justification, and hyphenation in eleven languages. [More](https://pdfpecker.net/docs/languages/)
- **PDF/A, passwords and watermarks**: PDF/A-2, 3 and 4, AES-256 encryption, and text, image and SVG watermarks.
  [More](https://pdfpecker.net/docs/pdfa-and-passwords/)
- **Accessibility**: tagged PDF, PDF/UA-1 and PDF/UA-2 built from your HTML.
  [More](https://pdfpecker.net/docs/accessibility/)
- **Forms**: HTML inputs become fillable PDF fields; read, fill and flatten the fields of existing PDFs.
  [More](https://pdfpecker.net/docs/forms/)
- **Digital signatures**: PAdES signing, certification signatures, timestamps, external signers, verification and
  long-term validation. [More](https://pdfpecker.net/docs/signatures/)
- **E-invoices and attachments**: Factur-X, ZUGFeRD, XRechnung and Peppol BIS 3.0 XML built from invoice data and
  checked against EN 16931, a ready-made invoice layout, and embedded files.
  [More](https://pdfpecker.net/docs/e-invoices/)
- **Barcodes**: QR codes, EPC payment QR codes, Code 128 and EAN-13 as vector SVG, e.g.
  `Barcode.QrCode("https://example.com")`. [More](https://pdfpecker.net/docs/barcodes/)
- **Existing PDFs**: merge (also streamed), split, reorder, rotate, stamp, extract text with positions, extract images
  and read document properties. [More](https://pdfpecker.net/docs/merging/)
- **Compacting and Fast Web View**: `PdfCompactor` makes any PDF smaller without changing how it looks, and
  `PdfOptions.Linearize` or `PdfLinearizer` writes a linearized PDF whose first page shows before the rest arrives.
  [More](https://pdfpecker.net/docs/compacting/)
- **Images to PDF**: `ImageToPdf` puts scans and photos on pages of their own size or fitted to a page size, with
  photos turned upright by their EXIF orientation.
  [More](https://pdfpecker.net/docs/images-to-pdf/)
- **Photo privacy**: `PdfOptions.StripImageMetadata` removes EXIF (GPS location included), XMP, IPTC and comments
  from embedded JPEGs while keeping their pixels and colours.
- **PDF to image**: renders any PDF page to PNG, JPEG or WebP, or a whole PDF to a multi-page TIFF, and
  `HtmlToImage` goes from HTML to images in one call. [More](https://pdfpecker.net/docs/pdf-to-image/)
- **PDF to HTML**: `PdfToHtml` turns any PDF into one script-free HTML file that looks like the PDF, with text you can
  select, search and copy. `PdfHtmlMode.Structured` writes the text instead as flowing headings, paragraphs, lists and
  real `<table>` elements in reading order, with nothing placed at a fixed position, for search indexes and language
  models. A tagged PDF (PDF/UA, or any PDF with a structure tree) is read from its tags, which say exactly what each
  heading, list item and table cell is. Other PDFs are read from their layout: tables come from the rules and borders
  the PDF draws and from text that lines up in columns, with header rows, spanned cells and wrapped cells. Reading the
  layout can turn unusual layouts, floats and charts into plain paragraphs, and scanned pages have no text to give.
  `IncludeStyles` keeps how the page looks in that structure: fonts, sizes, colours, backgrounds, rounded boxes, table
  borders, column widths and alignment, as CSS classes. `IncludeImages` adds the page's pictures as `<img>` elements.
  [More](https://pdfpecker.net/docs/pdf-to-html/)
- **Viewer settings and page labels**: the page, zoom and panel a viewer opens with, print settings, and page numbers
  such as "i" to "iv" for front matter. [More](https://pdfpecker.net/docs/options/)
- **Reproducible output**: a fixed `CreationDate` or `SOURCE_DATE_EPOCH` gives byte-identical PDFs, for build pipelines
  and output caches. [More](https://pdfpecker.net/docs/options/#reproducible-output)
- **Safe failures**: errors share the base class `PdfPeckerException`, and the `*File` methods never leave a
  half-written output. [More](https://pdfpecker.net/docs/options/#errors-cancellation-and-output-files)

PdfPecker is built for HTML templates you control: it runs no JavaScript, and conversion never fetches anything from
the network (remote images and stylesheets load only through a `ResourceResolver` you supply; those in `data:` URLs need
none). Only signing goes online, and only when you ask: a timestamp from the server you name in `TimestampUrl`, OCSP and CRL data for long-term
validation when you turn on `FetchRevocationData`, and online revocation checks when verification uses
`X509RevocationMode.Online`. See the [limitations](https://pdfpecker.net/docs/limitations/) for what it does not do yet.

## How it compares

The same three documents converted to A4 PDF by PdfPecker and six .NET alternatives, on one 4-core cloud VM
(Intel Xeon @ 2.1 GHz, 260 MB L3, 16 GB, Linux, .NET 10.0.13). Times are medians after a 3-second warm-up, averaged
over two runs; "first PDF" includes starting the browser where there is one. Parallel runs one conversion per core at
once on the 5-page document. Peak memory is the proportional set size (PSS) of the whole process tree, so a browser's
helper processes are included once and shared pages are not double-counted; .NET processes run with a 16 MB GC gen0
budget, as .NET would otherwise size it from the CPU cache.

PdfPecker is 50 to 200 times faster than the Chromium-based converters on the small documents and uses an eighth or less
of their memory. How to read the table and how it was measured is in
[Performance](https://pdfpecker.net/docs/performance/); the benchmark itself, to run on your own machine, is in
[PdfPecker.Benchmarks](https://github.com/Moonwalker1307/PdfPecker.Benchmarks).

| Converter | Engine | To deploy | First PDF | Invoice, 1 page | Feature tour, 5 pages | Report, 30 pages | Parallel | Peak memory |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **PdfPecker** | own, managed | 5.1 MB DLL | 0.40 s | **0.92 ms** | **2.9 ms** | **33 ms** | **1,132 docs/s** | **92 MB** |
| HtmlRenderer.PdfSharp 1.6.1 | own, managed (CSS 2) | 5.0 MB of DLLs | 0.54 s | 4.9 ms | 16 ms | 360 ms | 114 docs/s | 255 MB |
| iText pdfHTML 6.3.4 | own, managed | 11 MB of DLLs | 1.18 s | 24 ms | 132 ms | 2.2 s | 15 docs/s | 707 MB |
| wkhtmltopdf 0.12.6 | Qt WebKit, native | about 90 MB with Qt | 0.33 s | 282 ms | 309 ms | 600 ms | 12 docs/s | 165 MB |
| PuppeteerSharp 25.13 | Chromium 141 | about 600 MB browser | 1.15 s | 106 ms | 142 ms | 496 ms | 15 docs/s | 813 MB |
| Microsoft.Playwright 1.63 | Chromium 141 | browser + 135 MB driver | 0.98 s | 131 ms | 167 ms | 517 ms | 12 docs/s | 1,027 MB |
| Syncfusion HTML to PDF 35.1.40 | Chrome (Blink), bundled | about 545 MB with Chrome | 2.46 s | 183 ms | 236 ms | 968 ms | 3 docs/s | 2,239 MB |

## Building

```
dotnet build
dotnet test
dotnet run --project samples/PdfPecker.Sample -c Release      # converts samples/PdfPecker.Sample/html/*.html and *.md into ./output
dotnet run --project samples/PdfPecker.Fluent.Sample -c Release   # writes the PdfPecker.Fluent examples into ./output
dotnet run --project bench/PdfPecker.Benchmarks -c Release
dotnet run --project bench/PdfPecker.Benchmarks -c Release -- --requirements    # memory and CPU per conversion
bench/compare-versions.sh v1.21.0                              # this checkout against a release, in turns on this machine
dotnet pack src/PdfPecker -c Release -o ./nupkg                # also checks the public API against the last release
dotnet pack src/PdfPecker.Fluent -c Release -o ./nupkg         # the fluent package, with the same version
```

Requirements and details are in [BUILD.md](https://github.com/Moonwalker1307/PdfPecker/blob/main/BUILD.md) and
[TESTING.md](https://github.com/Moonwalker1307/PdfPecker/blob/main/TESTING.md); how to fuzz the parsers is in
[fuzz/README.md](https://github.com/Moonwalker1307/PdfPecker/blob/main/fuzz/README.md).

To contribute, see [CONTRIBUTING.md](https://github.com/Moonwalker1307/PdfPecker/blob/main/CONTRIBUTING.md). Report security problems privately as described in
[SECURITY.md](https://github.com/Moonwalker1307/PdfPecker/blob/main/SECURITY.md).

## License

MIT. The third-party fonts, font data and code built into the library are listed, with their licenses, in
[THIRD-PARTY-NOTICES.md](https://github.com/Moonwalker1307/PdfPecker/blob/main/THIRD-PARTY-NOTICES.md).
