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

// Markdown to PDF or HTML, merging, and PDF pages to PNG
byte[] guide = MarkdownToPdf.Convert(File.ReadAllText("README.md"));
string guideHtml = MarkdownToHtml.Convert(File.ReadAllText("README.md"));
byte[] merged = PdfMerger.Merge(invoicePdf, termsPdf);
byte[][] pngs = PdfToImage.Convert(pdf, new ImageOptions { Dpi = 150 });

// Open a PDF once and render its pages one at a time, or in parallel
var renderer = PdfRenderer.Open(pdf);
renderer.RenderPage(0, new ImageOptions { Dpi = 150 }).Save("page1.png");
```

The conversion methods are static and thread-safe, so you can convert many documents in parallel. The few instance types are `FontCollection` and `PdfRenderer`, which can be shared by parallel work, and `StreamingPdfMerger`, which builds one output and is used from one thread at a time.

## Features

- **HTML and CSS**: flexbox, grid, floats, positioning, transforms, gradients, filters, media and container queries, inline SVG, and PNG, JPEG, GIF, WebP, BMP and TIFF images. [More](https://pdfpecker.net/docs/html-and-css/)
- **Paged media**: `@page` sizes and margin boxes, running headers, footnotes, tables of contents with page numbers, and break control. [More](https://pdfpecker.net/docs/paged-media/)
- **Markdown**: CommonMark 0.31 and GitHub Flavored Markdown to PDF or HTML. [More](https://pdfpecker.net/docs/markdown/)
- **Fonts**: TrueType, OpenType, WOFF and WOFF2, subset and embedded, with kerning, ligatures, variable fonts and OpenType features. [More](https://pdfpecker.net/docs/fonts/)
- **Languages**: Arabic and Hebrew with bidi and shaping, CJK line breaking, and hyphenation. [More](https://pdfpecker.net/docs/languages/)
- **PDF/A, passwords and watermarks**: PDF/A-2, 3 and 4, AES-256 encryption, and text, image and SVG watermarks. [More](https://pdfpecker.net/docs/pdfa-and-passwords/)
- **Accessibility**: tagged PDF, PDF/UA-1 and PDF/UA-2 built from your HTML. [More](https://pdfpecker.net/docs/accessibility/)
- **Forms**: HTML inputs become fillable PDF fields; read, fill and flatten the fields of existing PDFs. [More](https://pdfpecker.net/docs/forms/)
- **Digital signatures**: PAdES signing, timestamps, external signers, verification and long-term validation. [More](https://pdfpecker.net/docs/signatures/)
- **E-invoices and attachments**: Factur-X and ZUGFeRD, PDFs in Peppol and OIOUBL invoices, and any embedded file. [More](https://pdfpecker.net/docs/e-invoices/)
- **Existing PDFs**: merge (also streamed), split, reorder, rotate, stamp and extract text with positions. [More](https://pdfpecker.net/docs/merging/)
- **PDF to image**: renders any PDF page, including JPEG 2000, JBIG2 and CCITT images, to PNG, JPEG or WebP. [More](https://pdfpecker.net/docs/pdf-to-image/)

PdfPecker is built for HTML templates you control: it runs no JavaScript and never fetches anything from the network on
its own. See the [limitations](https://pdfpecker.net/docs/limitations/) for what it does not do yet.

## How it compares

The same three documents converted to A4 PDF by PdfPecker and six .NET alternatives, on one 4-core cloud VM
(Intel Xeon @ 2.1 GHz, 16 GB, Linux, .NET 10.0.12), with PdfPecker 1.9.0. Times are medians after a 3-second warm-up,
averaged over two runs; "first PDF" includes starting the browser where there is one. Parallel runs one conversion per
core at once on the 5-page document. Peak memory is the proportional set size (PSS) of the whole process tree, so a
browser's helper processes are included once and shared pages are not double-counted.

PdfPecker is 50 to 150 times faster than the Chromium-based converters on the small documents and uses a fifth of their
memory. How to read the table, how it was measured and how to run it yourself is in
[Performance](https://pdfpecker.net/docs/performance/).

| Converter | Engine | To deploy | First PDF | Invoice, 1 page | Feature tour, 5 pages | Report, 30 pages | Parallel | Peak memory |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **PdfPecker** | own, managed | 4.0 MB DLL | 0.34 s | **0.77 ms** | **2.5 ms** | **22 ms** | **1,466 docs/s** | **195 MB** |
| HtmlRenderer.PdfSharp 1.6.1 | own, managed (CSS 2) | 2.4 MB of DLLs | 0.45 s | 4.2 ms | 13 ms | 281 ms | 202 docs/s | 352 MB |
| iText pdfHTML 6.3.4 | own, managed | 11 MB of DLLs | 1.19 s | 21 ms | 127 ms | 2.0 s | 16 docs/s | 711 MB |
| wkhtmltopdf 0.12.6 | Qt WebKit, native | about 90 MB with Qt | 0.31 s | 279 ms | 308 ms | 576 ms | 13 docs/s | 280 MB |
| PuppeteerSharp 25.13 | Chromium 141 | about 600 MB browser | 1.15 s | 102 ms | 135 ms | 431 ms | 17 docs/s | 872 MB |
| Microsoft.Playwright 1.63 | Chromium 141 | browser + 135 MB driver | 0.96 s | 116 ms | 159 ms | 466 ms | 12 docs/s | 1,016 MB |
| Syncfusion HTML to PDF 35.1.40 | Chrome (Blink), bundled | about 545 MB with Chrome | 2.48 s | 159 ms | 209 ms | 0.87 s | 4 docs/s | 2,212 MB |

Most of PdfPecker's peak memory is the .NET garbage collector's allocation budget, which .NET sizes from the CPU cache
(260 MB of L3 on this VM). With `DOTNET_GCgen0size=0x1000000` (16 MB) the same run peaks at 88 MB.

## Building

```
dotnet build
dotnet test
dotnet run --project samples/PdfPecker.Sample -c Release      # converts samples/PdfPecker.Sample/html/*.html and *.md into ./output
dotnet run --project bench/PdfPecker.Benchmarks -c Release
dotnet run --project bench/PdfPecker.Benchmarks -c Release -- --requirements    # memory and CPU per conversion
dotnet pack src/PdfPecker -c Release -o ./nupkg                # also checks the public API against the last release
```

How to fuzz the parsers is in [fuzz/README.md](fuzz/README.md).

## License

MIT. The third-party fonts, font data and code built into the library are listed, with their licenses, in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
