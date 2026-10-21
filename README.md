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

// Any PDF as one self-contained HTML page: page images with selectable, searchable text and working links
string html = PdfToHtml.Convert(File.ReadAllBytes("statement.pdf"));

// Open a PDF once and render its pages one at a time, or in parallel
var renderer = PdfRenderer.Open(pdf);
renderer.RenderPage(0, new ImageOptions { Dpi = 150 }).Save("page1.png");
```

The conversion methods are static and thread-safe, so you can convert many documents in parallel. The few instance types are `FontCollection` and `PdfRenderer`, which can be shared by parallel work, and `StreamingPdfMerger`, which is not thread-safe: it builds one output and is used from one thread at a time.

## Features

- **HTML and CSS**: flexbox, grid and subgrid (whose items keep their children's margins inside them, as in Chrome), `min-content`, `max-content` and `fit-content` widths (boxes that shrink to fit, such as floats, `inline-flex` and table columns, leave out a trailing space as Chrome does), units measured from the font (`ex`, `ch`, `cap`, `lh`) and the viewport units `vi`, `vb`, `svh`, `lvh` and `dvh`, logical `inset-inline`, `inset-block` and `inline-size`, logical borders (`border-inline-start`, `border-block-end`, `border-start-start-radius` and the rest, on the side `direction` and `writing-mode` give them), CSS tables on any element (`display: table`, `inline-table` and lone cells) with columns sized as in Chrome, also under cells that span several, hold a long URL, ask for more than 100%, have a `max-width` or hold an image with a percentage width, and collapsed borders resolved as Chrome does (on rows and row groups too, and `hidden`), `display: contents` wrappers whose children join the flex, grid or table around them, multi-column layout, floats (cleared by an empty `clear: both` clearfix div or `<br clear=all>` as in Chrome, and with text wrapping round `shape-outside` circles, polygons and image outlines), drop caps with `initial-letter`, positioning (also of badges and ribbons placed absolutely in flex and grid boxes or in `position: relative` table cells), transforms (3D ones drawn flat, with the perspective taken at the box's centre), gradients and background images (also clipped to text), outlined text with `-webkit-text-stroke` and `paint-order`, backgrounds and borders on inline elements across line breaks with `box-decoration-break`, balanced and pretty line breaks with `text-wrap`, filters and `backdrop-filter`, masks (`mask-image` and `-webkit-mask`), media and container queries, vertical text labels (`writing-mode`), superscripts and subscripts that nest and set line heights as in Chrome, `vertical-align: top`, `middle` and `text-top` on smaller inline text (such as a currency sign in a price), underlines that skip descenders (`text-decoration-skip-ink`) and dashed, dotted, double and wavy decorations drawn the way Chrome draws them, inline SVG, `details`, `progress` and `meter` drawn as Chrome prints them, and PNG, JPEG, GIF, WebP, BMP and TIFF images (an image that is missing prints its alt text, as in Chrome). A fragment without `html`, `head` or `body` tags gets them, as in a browser, so `body { ... }` and `html { ... }` rules apply (an implied body has no default margin, so fragments keep their layout). [More](https://pdfpecker.net/docs/html-and-css/)
- **Paged media**: `@page` sizes and margin boxes, running headers, footnotes, tables of contents with page numbers, break control, table headers and footers repeated on every page, pages too narrow for their content shrunk to fit as Chrome prints them (`ShrinkToFit`), and box shadows that, as in Chrome, never add a page of their own. [More](https://pdfpecker.net/docs/paged-media/)
- **Content scaled to fit a box**: `-pdfpecker-scale-to-fit: shrink` on a box with a `height` or `max-height` draws its content smaller, after reflowing it wider, until it fits the box, for labels, badges and certificates with a fixed space for text of any length.
- **Markdown**: CommonMark 0.31 and GitHub Flavored Markdown to PDF or HTML, with footnotes (at the bottom of the page in a PDF), definition lists and YAML front matter. [More](https://pdfpecker.net/docs/markdown/)
- **Fonts**: TrueType, OpenType, WOFF and WOFF2, subset and embedded, with kerning (`font-kerning`), ligatures, variable fonts, OpenType features (numerals, fractions, super and subscripts, small capitals, east asian forms), `font-synthesis`, `font-size-adjust` and `@font-face` `font-stretch` matching. Characters no font of the document has fall back to built-in fonts, as browsers fall back to system fonts: Central European, Greek and Cyrillic letters to the bundled Liberation fonts, and arrows, check marks, stars, dingbats, math and box drawing to a bundled symbol font. [More](https://pdfpecker.net/docs/fonts/)
- **Languages**: Arabic and Hebrew with bidi and shaping, Indic scripts (Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada, Malayalam), Thai and Lao shaped as HarfBuzz shapes them, CJK line breaking, justification (`text-justify`) and punctuation trimming (`text-spacing-trim`), and hyphenation in English, Danish, German, Dutch, Norwegian, Swedish, Spanish, French, Italian, Portuguese and Polish. [More](https://pdfpecker.net/docs/languages/)
- **PDF/A, passwords and watermarks**: PDF/A-2, 3 and 4, AES-256 encryption, and text, image and SVG watermarks. [More](https://pdfpecker.net/docs/pdfa-and-passwords/)
- **Accessibility**: tagged PDF, PDF/UA-1 and PDF/UA-2 built from your HTML. [More](https://pdfpecker.net/docs/accessibility/)
- **Reproducible output**: a fixed `CreationDate` or `SOURCE_DATE_EPOCH` gives byte-identical PDFs for the same input, also when merging, splitting, stamping or compacting, for build pipelines and output caches.
- **Images to PDF**: `ImageToPdf` puts PNG, JPEG, GIF, WebP, BMP or TIFF images on pages of their own size, or scaled to fit a page size, for scans and receipts.
- **Barcodes**: QR codes, EPC payment QR codes (GiroCode), Code 128 and EAN-13 as vector SVG for your template, e.g. `Barcode.QrCode("https://example.com")` or `<img src="@Barcode.ToDataUri(Barcode.Ean13("590123412345"))">`.
- **Forms**: HTML inputs become fillable PDF fields; read, fill and flatten the fields of existing PDFs. [More](https://pdfpecker.net/docs/forms/)
- **Digital signatures**: PAdES signing, timestamps, external signers, verification and long-term validation. [More](https://pdfpecker.net/docs/signatures/)
- **E-invoices and attachments**: Factur-X, ZUGFeRD, XRechnung and Peppol BIS 3.0 XML built from invoice data, with the totals and VAT calculated and checked against the EN 16931 rules (`InvoiceData.ToCii()` and `ToUbl()`); a ready-made invoice layout with your logo in English, German or Danish (`InvoiceTemplate.ToPdf(invoice)`); Factur-X embedded in the PDF or read back from a received one, PDFs attached to Peppol and OIOUBL invoices, and any embedded file. [More](https://pdfpecker.net/docs/e-invoices/)
- **Existing PDFs**: merge (also streamed), split, reorder, rotate, stamp and extract text with positions. [More](https://pdfpecker.net/docs/merging/)
- **Compacting**: `PdfCompactor` makes any PDF smaller without changing how it looks: unused objects and old revisions go, identical fonts and images are stored once, streams are recompressed and objects packed into compressed object streams, keeping bookmarks, forms, tags and PDF/A. Optional settings also downsample images and recompress them as JPEG. Signed and encrypted PDFs are refused, since rewriting would break or drop the signature or protection.
- **PDF to image**: renders any PDF page, including JPEG 2000, JBIG2 and CCITT images, to PNG, JPEG or WebP, or a whole PDF to one multi-page TIFF (colour, greyscale or black and white with CCITT Group 4). `HtmlToImage` goes from HTML to images in one call. [More](https://pdfpecker.net/docs/pdf-to-image/)
- **PDF to HTML**: `PdfToHtml` turns any PDF into one HTML file with no scripts that looks like the PDF: each page is an image (WebP, PNG or JPEG) with the page's text laid invisibly over it, word by word, so it can be selected, copied, searched and read by screen readers, plus the PDF's web links and links between pages. Pages scale down to a phone screen and print on paper of their own size. The HTML keeps the PDF's layout rather than reflowing into paragraphs, and text zooms as an image does.
- **Safe failures**: the library's own errors share the base class `PdfPeckerException`, and the `*File` methods write through a temporary file, so a failure never leaves a half-written output.

PdfPecker is built for HTML templates you control: it runs no JavaScript, and conversion never fetches anything from
the network (remote images and stylesheets load only through a `ResourceResolver` you supply). Only signing goes online,
and only when you ask: a timestamp from the server you name in `TimestampUrl`, OCSP and CRL data for long-term
validation when you turn on `FetchRevocationData`, and online revocation checks when verification uses
`X509RevocationMode.Online`. See the [limitations](https://pdfpecker.net/docs/limitations/) for what it does not do yet.

## How it compares

The same three documents converted to A4 PDF by PdfPecker and six .NET alternatives, on one 4-core cloud VM
(Intel Xeon @ 2.8 GHz, 33 MB L3, 16 GB, Linux, .NET 10.0.12). Times are medians after a 3-second warm-up, averaged
over two runs; "first PDF" includes starting the browser where there is one. Parallel runs one conversion per core at
once on the 5-page document. Peak memory is the proportional set size (PSS) of the whole process tree, so a browser's
helper processes are included once and shared pages are not double-counted; .NET processes run with a 16 MB GC gen0
budget, as .NET would otherwise size it from the CPU cache.

PdfPecker is 60 to 220 times faster than the Chromium-based converters on the small documents and uses a tenth of their
memory. How to read the table, how it was measured and how to run it yourself is in
[Performance](https://pdfpecker.net/docs/performance/).

| Converter | Engine | To deploy | First PDF | Invoice, 1 page | Feature tour, 5 pages | Report, 30 pages | Parallel | Peak memory |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **PdfPecker** | own, managed | 5.0 MB DLL | 0.40 s | **0.86 ms** | **3.5 ms** | **45 ms** | **818 docs/s** | **90 MB** |
| HtmlRenderer.PdfSharp 1.6.1 | own, managed (CSS 2) | 2.4 MB of DLLs | 0.58 s | 6.3 ms | 21 ms | 639 ms | 72 docs/s | 236 MB |
| iText pdfHTML 6.3.4 | own, managed | 11 MB of DLLs | 1.44 s | 34 ms | 209 ms | 3.4 s | 9 docs/s | 624 MB |
| wkhtmltopdf 0.12.6 | Qt WebKit, native | about 90 MB with Qt | 0.36 s | 310 ms | 356 ms | 780 ms | 11 docs/s | 164 MB |
| PuppeteerSharp 25.13 | Chromium 141 | about 600 MB browser | 1.43 s | 153 ms | 217 ms | 792 ms | 11 docs/s | 829 MB |
| Microsoft.Playwright 1.63 | Chromium 141 | browser + 135 MB driver | 1.36 s | 194 ms | 247 ms | 726 ms | 8 docs/s | 967 MB |
| Syncfusion HTML to PDF 35.1.40 | Chrome (Blink), bundled | about 545 MB with Chrome | 2.83 s | 264 ms | 320 ms | 1.1 s | 2 docs/s | 2,193 MB |

## Building

```
dotnet build
dotnet test
dotnet run --project samples/PdfPecker.Sample -c Release      # converts samples/PdfPecker.Sample/html/*.html and *.md into ./output
dotnet run --project bench/PdfPecker.Benchmarks -c Release
dotnet run --project bench/PdfPecker.Benchmarks -c Release -- --requirements    # memory and CPU per conversion
bench/compare-versions.sh v1.13.0                              # this checkout against a release, in turns on this machine
dotnet pack src/PdfPecker -c Release -o ./nupkg                # also checks the public API against the last release
```

Requirements and details are in [BUILD.md](https://github.com/Moonwalker1307/PdfPecker/blob/main/BUILD.md) and
[TESTING.md](https://github.com/Moonwalker1307/PdfPecker/blob/main/TESTING.md); how to fuzz the parsers is in
[fuzz/README.md](https://github.com/Moonwalker1307/PdfPecker/blob/main/fuzz/README.md).

To contribute, see [CONTRIBUTING.md](https://github.com/Moonwalker1307/PdfPecker/blob/main/CONTRIBUTING.md). Report security problems privately as described in
[SECURITY.md](https://github.com/Moonwalker1307/PdfPecker/blob/main/SECURITY.md).

## License

MIT. The third-party fonts, font data and code built into the library are listed, with their licenses, in
[THIRD-PARTY-NOTICES.md](https://github.com/Moonwalker1307/PdfPecker/blob/main/THIRD-PARTY-NOTICES.md).
