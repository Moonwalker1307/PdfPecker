![PdfPecker](https://raw.githubusercontent.com/Moonwalker1307/PdfPecker/main/assets/logo.png)

# PdfPecker

[![NuGet](https://img.shields.io/nuget/v/PdfPecker.svg)](https://www.nuget.org/packages/PdfPecker)

Fast HTML and Markdown to PDF conversion, and PDF to PNG, JPEG and WebP rendering, for .NET, with no dependencies.

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

// Markdown to PDF, merging, and PDF pages to PNG
byte[] guide = MarkdownToPdf.Convert(File.ReadAllText("README.md"));
byte[] merged = PdfMerger.Merge(invoicePdf, termsPdf);
byte[][] pngs = PdfToImage.Convert(pdf, new ImageOptions { Dpi = 150 });
```

All methods are static and thread-safe, so you can convert many documents in parallel.

## Features

- **HTML and CSS**: flexbox, grid, multi-column, floats and positioning, transforms, transparency, gradients,
  `var()` and `calc()`, relative colours, media queries, `@import`, nesting and cascade layers, `filter`, `clip-path`,
  blend modes, `border-image`, inset shadows, `::first-letter` and `::first-line`, `@counter-style` and list images,
  inline SVG and SVG files (with embedded images and every SVG filter), PNG, JPEG, GIF, WebP, BMP and TIFF. [More](https://pdfpecker.net/docs/html-and-css/)
- **Paged media**: `@page` sizes and margin boxes, its own margins for the first page and mirrored left and right pages,
  named pages, running headers, tables of contents with page numbers, repeated table headers, orphans, widows and
  break control. [More](https://pdfpecker.net/docs/paged-media/)
- **Markdown**: CommonMark 0.31 and GitHub Flavored Markdown to PDF or HTML. [More](https://pdfpecker.net/docs/markdown/)
- **Fonts**: TrueType, OpenType, WOFF and WOFF2, subset and embedded, with `@font-face`, kerning, ligatures, variable fonts
  (`font-weight`, `font-stretch`, `font-variation-settings`) and OpenType features such as tabular figures
  (`font-variant-numeric`, `font-feature-settings`).
  [More](https://pdfpecker.net/docs/fonts/)
- **Right-to-left and hyphenation**: Arabic and Hebrew with bidi and shaping, and `hyphens: auto`.
  [More](https://pdfpecker.net/docs/languages/)
- **PDF/A, passwords and watermarks**: PDF/A-2, 3 and 4, AES-256 encryption and permissions, also for existing PDFs;
  text, image and SVG watermarks.
  [More](https://pdfpecker.net/docs/pdfa-and-passwords/)
- **Accessibility**: tagged PDF, PDF/UA-1 and PDF/UA-2 built from your HTML. [More](https://pdfpecker.net/docs/accessibility/)
- **Fillable forms**: HTML inputs, check boxes, radio buttons, selects and text areas become PDF form fields.
  [More](https://pdfpecker.net/docs/forms/)
- **Digital signatures**: PAdES signatures, visible boxes, timestamps and existing signature fields; verification of
  signed PDFs, and long-term validation (DSS with OCSP and CRLs, document timestamps).
  [More](https://pdfpecker.net/docs/signatures/)
- **E-invoices and attachments**: Factur-X and ZUGFeRD, PDFs in Peppol and OIOUBL invoices, and any embedded file.
  [More](https://pdfpecker.net/docs/e-invoices/)
- **Existing PDFs**: merge (also streamed), extract, reorder, rotate and split pages, and stamp or underlay pages.
  [More](https://pdfpecker.net/docs/merging/)
- **PDF to image**: PdfPecker's own renderer turns any PDF page into PNG, JPEG or WebP. [More](https://pdfpecker.net/docs/pdf-to-image/)

PdfPecker is built for HTML templates you control: it runs no JavaScript and never fetches anything from the network on
its own. See the [limitations](https://pdfpecker.net/docs/limitations/) for what it does not do yet.

## How it compares

The same three documents converted to A4 PDF by PdfPecker and six .NET alternatives, on one 4-core cloud VM
(Intel Xeon @ 2.8 GHz, 16 GB, Linux, .NET 10.0.12). Times are medians after a 3-second warm-up; "first PDF" includes
starting the browser where there is one. Parallel runs one conversion per core at once on the 5-page document. Peak
memory is the proportional set size (PSS) of the whole process tree, so a browser's helper processes are included
once and shared pages are not double-counted.

PdfPecker is 60 to 150 times faster than the Chromium-based converters on the small documents and uses a tenth of their
memory. How to read the table, how it was measured and how to run it yourself is in
[Performance](https://pdfpecker.net/docs/performance/).

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

## Building

```
dotnet build
dotnet test
dotnet run --project samples/PdfPecker.Sample -c Release      # converts samples/PdfPecker.Sample/html/*.html and *.md into ./output
dotnet run --project bench/PdfPecker.Benchmarks -c Release
dotnet run --project bench/PdfPecker.Benchmarks -c Release -- --requirements    # memory and CPU per conversion
dotnet pack src/PdfPecker -c Release -o ./nupkg
```

## License

MIT. The third-party fonts, font data and code built into the library are listed, with their licenses, in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
