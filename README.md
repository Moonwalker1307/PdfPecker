# PdfPecker

Fast HTML to PDF conversion for .NET, with no dependencies.

PdfPecker has its own HTML parser, CSS engine, layout engine and PDF writer. It does not start a browser, ship native
binaries or pull in third-party packages; the NuGet package depends only on the .NET base class library.

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

The package targets `net10.0` and works on .NET 10 and later.

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

### Options

```csharp
var options = new PdfOptions
{
    PageSize = PageSize.Letter.Landscape,            // A3, A4 (default), A5, Letter, Legal or new PageSize(w, h) in points
    Margins = PageMargins.FromMillimeters(20, 15, 20, 15),
    BaseDirectory = "/var/app/templates",             // where relative image/stylesheet paths are resolved
    ResourceResolver = url => myCache.Get(url),       // serve http(s) or embedded resources yourself; null = default handling
    UserStyleSheet = "body { font-size: 10pt }",      // extra CSS applied after the document's own
    FooterText = "Page {page} of {pages}",            // also HeaderText
    Title = "Invoice 42", Author = "Accounts",        // document properties (Title defaults to <title>)
    Compress = true,                                  // Flate-compress page content (default)
    AllowFileAccess = false,                          // block local file reads when converting untrusted HTML
};
byte[] pdf = HtmlToPdf.Convert(html, options);
```

A CSS `@page { size: A4 landscape; margin: 15mm }` rule overrides the page size and margins unless
`IgnoreCssPageRules` is set. PdfPecker never makes network requests on its own: `http(s)` images and stylesheets are only
loaded through `ResourceResolver`.

## Website

`web/PdfPecker.Web` is a small ASP.NET Core site for converting HTML to PDF in the browser: paste or open an HTML
file, pick page size, orientation, margins and page numbers, and preview or download the PDF.

```
dotnet run --project web/PdfPecker.Web -c Release        # then open the URL it prints
docker build -f web/PdfPecker.Web/Dockerfile -t pdfpecker-web . && docker run -p 8080:8080 pdfpecker-web
```

It also exposes the converter as an HTTP API:

```
curl -X POST "http://localhost:8080/api/convert?pageSize=Letter&pageNumbers=true" \
     -H "Content-Type: text/html" --data-binary @invoice.html -o invoice.pdf
```

`POST /api/convert` also accepts JSON: `{ "html": "...", "pageSize": "A4", "landscape": false, "marginMm": 12.7, "pageNumbers": true }`.
The site never reads files from the server (`AllowFileAccess = false`), limits requests to 5 MB, 15 seconds and
120 conversions per minute per IP, and stores nothing.

## What is supported

**HTML**: the common document elements: headings, paragraphs, `div`/`section`-style blocks, `span`, `a` (external links
and `#id` links inside the document), `b`/`strong`, `i`/`em`, `u`, `s`, `sub`, `sup`, `small`, `big`, `mark`, `code`,
`pre`, `blockquote`, `hr`, `br`, `ul`/`ol`/`li` (nested, with `start` and `value`), `dl`, `table` (with `thead`,
`tbody`, `tfoot`, `caption`, `colspan`, `rowspan`, `col width`), `img`, and legacy attributes such as `align`,
`valign`, `bgcolor`, `width`, `height`, `border`, `cellpadding`, `cellspacing` and `<font>`. The parser is forgiving
about unclosed and misnested tags, the way browsers are.

**CSS**: `<style>` blocks, `<link rel="stylesheet">`, inline `style` attributes, `@media print`, and `@page`.
Selectors: type, `.class`, `#id`, `*`, attribute selectors (`[a]`, `=`, `~=`, `|=`, `^=`, `$=`, `*=`), descendant,
`>`, `+`, `~`, `:first-child`, `:last-child`, `:only-child`, `:nth-child()`, `:nth-last-child()`, `:not()`, `:root`,
`:link`, `:empty`, with specificity and `!important` handled as in browsers. Properties:

| Area | Properties |
| --- | --- |
| Box model | `display` (block, inline, inline-block, list-item, table parts, none), `margin`, `padding`, `border` (solid, dashed, dotted, double), `width`, `height`, `min-`/`max-width`, `min-height`, `box-sizing` |
| Text | `color`, `font`, `font-family`, `font-size`, `font-weight`, `font-style`, `line-height`, `text-align` (incl. justify), `text-decoration`, `text-transform`, `text-indent`, `letter-spacing`, `word-spacing`, `white-space`, `vertical-align` |
| Backgrounds | `background-color`, `background` (colour part) |
| Lists | `list-style-type` (disc, circle, square, decimal, decimal-leading-zero, alpha, roman, none), `list-style` |
| Tables | `border-collapse`, `border-spacing`, cell `vertical-align`, automatic column widths |
| Paging | `page-break-before`/`after`/`inside`, `break-before`/`after`/`inside`, `@page` size and margins |

Units: `px`, `pt`, `em`, `rem`, `%`, `mm`, `cm`, `in`, `pc`, `ex`, `ch`. Colours: names, `#rgb`, `#rrggbb`,
`#rrggbbaa`, `rgb()`, `rgba()`, `hsl()`, `hsla()`.

**Pagination**: lines, images and table rows are never cut in half at a page boundary; table header rows repeat on
every page a table spans; backgrounds and borders continue across pages.

**Images**: JPEG (embedded as-is, RGB, grayscale and CMYK) and PNG (all colour types and bit depths, transparency,
interlacing), from files, `data:` URIs or your `ResourceResolver`. Opaque PNGs are passed through without
re-encoding. Images wider than the page are scaled down to fit.

**Fonts**: the PDF standard fonts. `font-family` maps to Helvetica (sans-serif, Arial, Verdana, ...),
Times (serif, Georgia, ...) or Courier (monospace, Consolas, ...), each in regular, bold, italic and bold italic.

## Limitations

These are deliberate scope choices for speed and size; they are the first candidates for future work.

- Text uses the standard PDF fonts, so characters are limited to the Windows-1252 set (Western European languages).
  Other characters are replaced with `?`. Embedding TrueType fonts is not supported yet.
- No JavaScript, forms, `float`, `position`, flexbox or grid (flex and grid containers lay out as normal blocks),
  `::before`/`::after`, background images, gradients, `border-radius`, shadows, transforms or SVG/GIF/WebP images.
- Translucent colours (`rgba`) are blended onto white instead of using real transparency.

## Performance

Measured with `bench/PdfPecker.Benchmarks` on a 4-core cloud VM (.NET 10, Release, after JIT warm-up):

| Document | Pages | Median | Throughput |
| --- | --- | --- | --- |
| Invoice with a PNG logo and a styled table | 1 | 0.55 ms | about 1,800 docs/s on one core |
| Feature tour (text, lists, tables, JPEG and PNG) | 5 | 2.5 ms | about 1,700 docs/s on 4 cores |
| Dense report with 1,000 table rows | 30 | 19.5 ms | about 1,500 pages/s on one core |

The first conversion in a process also pays for JIT compilation (about 150 ms); later ones run at the speeds above.

## Building

```
dotnet build
dotnet test
dotnet run --project samples/PdfPecker.Sample -c Release      # converts samples/PdfPecker.Sample/html/*.html into ./output
dotnet run --project bench/PdfPecker.Benchmarks -c Release
dotnet pack src/PdfPecker -c Release -o ./nupkg
```

## How it works

1. `Html/HtmlParser` builds a DOM in a single pass, applying the browser rules for implied end tags.
2. `Css/StyleResolver` cascades a user-agent stylesheet, presentational attributes, document stylesheets and inline
   styles. Rules are indexed by id, class and tag, and elements with identical inputs share one computed style.
3. `Layout/LayoutEngine` lays out blocks, inline text (line breaking, justification), lists and tables
   (automatic column widths), paginating as it goes, and produces a flat list of draw operations.
4. `Pdf/PdfDocumentWriter` writes PDF 1.4: content streams, standard fonts, image XObjects, link annotations and
   the cross-reference table.
