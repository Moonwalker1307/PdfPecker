# PdfPecker

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

## PDF to image

`PdfToImage` and `PdfRenderer` render PDF pages to PNG (or raw RGBA pixels). This works for any PDF, not only
PdfPecker's own output, and also uses no third-party code: the PDF parser, font engines, JPEG and fax decoders,
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
(`PdfPasswordException` otherwise); unreadable files throw `InvalidDataException`.

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
  baseline and progressive JPEG, CCITT fax, stencil masks, colour-key masks and soft masks.

Not supported yet: JPEG 2000 (`JPXDecode`) and JBIG2 images, which are left blank; predefined CJK CMaps and
non-embedded CJK fonts; ICC profiles (ICC-based colours use their device equivalents).

## Website

`web/PdfPecker.Web` is a small ASP.NET Core site for converting HTML and Markdown in the browser: paste or open an
`.html` or `.md` file (or drag one onto the page), pick page size, orientation, margins and page numbers, and preview or
download the PDF. Markdown can also be converted to HTML.

```
dotnet run --project web/PdfPecker.Web -c Release        # then open the URL it prints
docker build -f web/PdfPecker.Web/Dockerfile -t pdfpecker-web . && docker run -p 8080:8080 pdfpecker-web
```

It also exposes the converter as an HTTP API:

```
curl -X POST "http://localhost:8080/api/convert?pageSize=Letter&pageNumbers=true" \
     -H "Content-Type: text/html" --data-binary @invoice.html -o invoice.pdf
```

Or upload a file as a form:

```
curl -X POST http://localhost:8080/api/convert -F "file=@invoice.html" -F pageSize=A4 -F pageNumbers=true -o invoice.pdf
```

`POST /api/pdf-to-image` turns a PDF into PNGs: one page with `?page=N` (or when the PDF has one page), otherwise a
zip with a PNG per page. Options: `dpi` (10 to 300, default 150), `width`, `height` and `password`.

```
curl -X POST "http://localhost:8080/api/pdf-to-image?page=1&dpi=200" --data-binary @report.pdf -o page1.png
curl -X POST http://localhost:8080/api/pdf-to-image -F "file=@report.pdf" -o pages.zip
```

`POST /api/convert` also accepts JSON: `{ "html": "...", "pageSize": "A4", "landscape": false, "marginMm": 12.7, "pageNumbers": true }`.

Markdown works the same way. The input is read as Markdown when it is uploaded as a `.md`/`.markdown` file, sent as
`"markdown"` in JSON or as a `markdown` form field, posted with `Content-Type: text/markdown`, or marked with
`format=markdown`. Add `output=html` to get the HTML page instead of a PDF:

```
curl -X POST http://localhost:8080/api/convert -F "file=@README.md" -o README.pdf
curl -X POST http://localhost:8080/api/convert -F "file=@README.md" -F output=html -o README.html
curl -X POST http://localhost:8080/api/convert -H "Content-Type: application/json" -d '{"markdown":"# Hi","output":"html"}'
```

The site never reads files from the server (`AllowFileAccess = false`), limits requests to 5 MB, 15 seconds and
120 conversions per minute per IP, and stores nothing.

## What is supported

**HTML**: the common document elements: headings, paragraphs, `div`/`section`-style blocks, `span`, `a` (external links
and `#id` links inside the document), `b`/`strong`, `i`/`em`, `u`, `s`, `sub`, `sup`, `small`, `big`, `mark`, `code`,
`pre`, `blockquote`, `hr`, `br`, `ul`/`ol`/`li` (nested, with `start` and `value`), `dl`, `table` (with `thead`,
`tbody`, `tfoot`, `caption`, `colspan`, `rowspan`, `col width`), `img`, inline `<svg>`, and legacy attributes such as `align`,
`valign`, `bgcolor`, `width`, `height`, `border`, `cellpadding`, `cellspacing` and `<font>`. The parser is forgiving
about unclosed and misnested tags, the way browsers are.

**Markdown**: [CommonMark 0.31](https://spec.commonmark.org/0.31.2/) (651 of the 652 spec examples pass; the one
miss is an exotic HTML5 entity name) plus the GitHub Flavored Markdown extensions: tables with column alignment,
`~~strikethrough~~`, task list items (`- [x]`) and bare `www.`/`https://`/e-mail autolinks. Headings get GitHub-style
ids, so `[see below](#heading-text)` links work in the PDF too. The parser runs in linear time on hostile input
(deep nesting, unclosed brackets, long delimiter runs). Not supported: footnotes, definition lists, math and
front matter.

**CSS**: `<style>` blocks, `<link rel="stylesheet">`, inline `style` attributes, `@media print`, and `@page`.
Selectors: type, `.class`, `#id`, `*`, attribute selectors (`[a]`, `=`, `~=`, `|=`, `^=`, `$=`, `*=`), descendant,
`>`, `+`, `~`, `:first-child`, `:last-child`, `:only-child`, `:nth-child()`, `:nth-last-child()`, `:not()`, `:root`,
`:link`, `:empty`, with specificity and `!important` handled as in browsers. Properties:

| Area | Properties |
| --- | --- |
| Box model | `display` (block, inline, inline-block, flex, inline-flex, grid, inline-grid, list-item, table parts, none), `margin`, `padding`, `border` (solid, dashed, dotted, double), `width`, `height`, `min-`/`max-width`, `min-height`, `box-sizing` |
| Text | `color`, `font`, `font-family`, `font-size`, `font-weight`, `font-style`, `line-height`, `text-align` (incl. justify), `text-decoration`, `text-transform`, `text-indent`, `letter-spacing`, `word-spacing`, `white-space`, `vertical-align` |
| Backgrounds | `background-color`, `background` (colour part), `opacity` on SVG |
| Flexbox | `flex-direction`, `flex-wrap`, `flex-flow`, `flex`, `flex-grow`/`-shrink`/`-basis`, `order`, `justify-content`, `align-items`, `align-self`, `align-content`, `gap`, `margin: auto` |
| Grid | `grid-template-columns`/`-rows` (lengths, `%`, `fr`, `auto`, `min-content`, `max-content`, `minmax()`, `fit-content()`, `repeat()` incl. `auto-fill`/`auto-fit`), `grid-auto-rows`/`-columns`, `grid-auto-flow`, `grid-column`/`-row` (line numbers and `span`), `grid-area`, `gap`, `justify-items`/`-self`, `align-items`/`-self`, `justify-content`, `align-content`, `place-*` |
| Columns | `column-count`, `column-width`, `columns`, `column-gap` |
| Variables | custom properties (`--name: value`) and `var(--name, fallback)`, anywhere including shorthands |
| SVG | `fill`, `stroke`, `stroke-width`, `fill-opacity`, `stroke-opacity`, `fill-rule`, `stroke-linecap`, `stroke-linejoin`, `stroke-miterlimit` (as CSS or presentation attributes) |
| Lists | `list-style-type` (disc, circle, square, decimal, decimal-leading-zero, alpha, roman, none), `list-style` |
| Tables | `border-collapse`, `border-spacing`, cell `vertical-align`, automatic column widths |
| Paging | `page-break-before`/`after`/`inside`, `break-before`/`after`/`inside`, `@page` size and margins |

Units: `px`, `pt`, `em`, `rem`, `%`, `mm`, `cm`, `in`, `pc`, `ex`, `ch`, `vw`, `vh`, `vmin`, `vmax` (the viewport is
the page's content area, minus any running header and footer). Colours: names, `#rgb`, `#rrggbb`,
`#rrggbbaa`, `rgb()`, `rgba()`, `hsl()`, `hsla()`.

**Pagination**: lines, images and table rows are never cut in half at a page boundary; table header rows repeat on
every page a table spans; backgrounds and borders continue across pages. Flex lines and grid rows move to the next
page as a whole when they don't fit.

**Running header and footer**: an element with the `data-sticky-pdf-header` or `data-sticky-pdf-footer` attribute is
taken out of the flow and drawn at the top or bottom of every page, and the page content flows in the space between.
Inside it, `{page}` and `{total-pages}` (or `{pages}`) are replaced with the page number and page count. An optional
`height` attribute (`height="17mm"`) reserves at least that much space.

```html
<header data-sticky-pdf-header height="17mm">ACME Ltd · Payslip</header>
<main>...</main>
<footer data-sticky-pdf-footer height="12mm">Page {page} of {total-pages}</footer>
```

**SVG**: inline `<svg>` with `path` (all commands, arcs included), `rect` (incl. rounded), `circle`, `ellipse`, `line`,
`polyline`, `polygon`, `g`, `use`, nested `svg`, `viewBox`, `preserveAspectRatio` and `transform`, drawn as vector
paths.

**Images**: JPEG (embedded as-is, RGB, grayscale and CMYK) and PNG (all colour types and bit depths, transparency,
interlacing), from files, `data:` URIs or your `ResourceResolver`. Opaque PNGs are passed through without
re-encoding. Images wider than the page are scaled down to fit.

**Fonts**: the PDF standard fonts. `font-family` maps to Helvetica (sans-serif, Arial, Verdana, ...),
Times (serif, Georgia, ...) or Courier (monospace, Consolas, ...), each in regular, bold, italic and bold italic.

## Limitations

These are deliberate scope choices for speed and size; they are the first candidates for future work.

- Text uses the standard PDF fonts, so characters are limited to the Windows-1252 set (Western European languages).
  Other characters are replaced with `?`. Embedding TrueType fonts is not supported yet.
- No JavaScript, forms, `float`, `position`, `::before`/`::after`, background images, gradients, `border-radius`,
  shadows, transforms or GIF/WebP images. SVG files in `<img>` are not drawn.
- Inline SVG: no `<text>`, embedded images, clipping, masks, patterns or filters; gradients are drawn in their average
  colour.
- Grid: no named lines or `grid-template-areas`, no `dense` packing, no subgrid.
- Multi-column: children are kept whole and balanced between columns; a single block is never split across columns.
- Translucent colours (`rgba`) are blended onto white instead of using real transparency.

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
dotnet pack src/PdfPecker -c Release -o ./nupkg
```

## How it works

Markdown is first turned into HTML: `Markdown/BlockParser` finds the block structure line by line, following the
CommonMark reference algorithm, `Markdown/InlineParser` handles emphasis (delimiter stack) and links (bracket stack),
and `Markdown/HtmlRenderer` writes the HTML, which then takes the same path as any other document:

1. `Html/HtmlParser` builds a DOM in a single pass, applying the browser rules for implied end tags.
2. `Css/StyleResolver` cascades a user-agent stylesheet, presentational attributes, document stylesheets and inline
   styles. Rules are indexed by id, class and tag, and elements with identical inputs share one computed style.
3. `Layout/LayoutEngine` lays out blocks, inline text (line breaking, justification), lists and tables
   (automatic column widths), paginating as it goes, and produces a flat list of draw operations.
4. `Pdf/PdfDocumentWriter` writes PDF 1.4: content streams, standard fonts, image XObjects, link annotations and
   the cross-reference table.

The renderer (`Rendering/`) works the other way round: `Parsing/PdfFile` reads the object graph, `PageRenderer`
interprets content streams, `Fonts/` turns glyphs into outlines (cached as anti-aliased bitmaps), `Images/` decodes
images, and `Graphics/Rasterizer` fills paths with exact area coverage before `PngEncoder` writes the result.

Third-party font data built into the renderer is listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
