# PdfPecker.Fluent

A fluent C# API for building PDFs with [PdfPecker](https://www.nuget.org/packages/PdfPecker). You describe pages,
headers, footers, columns, rows, tables, text, images and barcodes in code, in the style of QuestPDF. PdfPecker.Fluent
turns that into compact HTML and CSS and renders it with PdfPecker, so it needs no browser, no native binaries and no
package other than PdfPecker.

```
dotnet add package PdfPecker.Fluent
```

PdfPecker.Fluent is released together with PdfPecker and always has the same version, and it depends on the PdfPecker
release of that version.

```csharp
using PdfPecker.Fluent;

Document.Create(container =>
{
    container.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(2, Unit.Centimetre);
        page.DefaultTextStyle(x => x.FontSize(11));

        page.Header().Text("Hello PdfPecker").FontSize(20).Bold().FontColor(Colors.Blue.Darken2);

        page.Content().PaddingVertical(1, Unit.Centimetre).Column(column =>
        {
            column.Spacing(10);
            column.Item().Text("A paragraph of text.");
            column.Item().Image(File.ReadAllBytes("logo.png")).FitWidth();
        });

        page.Footer().AlignCenter().Text(x =>
        {
            x.Span("Page ");
            x.CurrentPageNumber();
            x.Span(" of ");
            x.TotalPages();
        });
    });
})
.GeneratePdf("hello.pdf");
```

## Features

- **Layout**: `Column`, `Row` (relative, constant and auto items), `Table` (constant and relative columns, row and
  column spans, explicit placement, header and footer rows that repeat on every page), padding, borders, corner
  radius, background, width and height with minimum and maximum, alignment, page breaks, `PreventPageBreak`,
  `ShowIf` and right-to-left content.
- **More layout**: `Layers` (watermarks under and stamps over content), `Inlined` (items that wrap like words, such as
  tags), `MultiColumn` (newspaper columns), `EnsureSpace` (keeps a heading off the bottom of a page), `Unconstrained`,
  `ZIndex`, `AspectRatio`, `Shadow`, rounded single corners, `Shrink`, `Placeholder`, `DebugArea` and `Lazy`.
- **Repeating and lists**: `Decoration` (content with a part before and after it that repeat on every page it
  crosses) and `List` (bulleted, numbered or custom-marker lists, tagged as lists in PDF/UA).
- **Transforms**: `Rotate`, `RotateLeft`, `RotateRight`, `Scale`, `ScaleHorizontal`, `ScaleVertical`, `FlipHorizontal`,
  `FlipVertical`, `FlipOver`, `TranslateX` and `TranslateY`.
- **Text**: spans with font family, size, weight, colour, background, italic, underline, strikethrough (solid, double,
  wavy, dotted or dashed, in their own colour and thickness), line height, letter and word spacing, superscript and
  subscript; OpenType features (`EnableFontFeature`, `DisableFontFeature`); justified text, `ClampLines` with an
  ellipsis, a first-line indent and `ParagraphSpacing`; small slots inside the text for icons and badges
  (`text.Element()`); headings, which become PDF bookmarks and tags; links and links to named sections, and the page a
  section starts on (`BeginPageNumberOfSection`).
- **Pages**: size, margins, page colour, a header and footer on every page, and `CurrentPageNumber` / `TotalPages`
  in them. Several `Page` calls give page sets with their own size and margins.
- **Images and graphics**: PNG, JPEG, WebP, BMP, TIFF and GIF images, SVG, solid, dashed and dotted lines, and QR,
  EPC payment QR, Code 128 and EAN-13 barcodes.
- **Accessibility**: `SemanticSection`, `SemanticArticle`, `SemanticHeader1` to `6`, `SemanticList`,
  `SemanticBlockQuotation`, `SemanticCaption`, `SemanticImage`, `SemanticIgnore` and the rest of QuestPDF's semantic
  calls write the HTML elements PdfPecker tags a PDF/UA document from.
- **Everything else in PdfPecker** through the normal `PdfOptions`: PDF/A, PDF/UA, signatures, encryption,
  watermarks, e-invoices (ZUGFeRD, Factur-X, XRechnung), attachments and fonts. `GeneratePdf(options)` copies the
  options, so one options object can be shared.
- **An escape hatch**: `container.Html("...")` places raw HTML anywhere, and `documentContainer.Css("...")` adds
  stylesheet rules, for anything the fluent calls do not cover yet (form fields, floats, grid, footnotes).
- **Output**: `GeneratePdf` (bytes, file or stream), `GeneratePdfAsync`, `GenerateImages` (PNG, JPEG, WebP, TIFF)
  and `GenerateHtml`, which shows the HTML PdfPecker renders and opens in a browser. `GeneratePdfAndShow` and
  `GenerateHtmlAndShow` write a temporary file and open it, and `Placeholders` makes up text, numbers, dates,
  colours and images (the same ones on every run) for trying out a layout.
- **.NET 8, 9 and 10**, trimmable and Native AOT compatible, culture-independent output.

## Safety

Text, links, colours and names you pass are encoded, so caller data never turns into markup: the fuzzer and the
hostile-input tests check that. Links must be absolute `http`, `https` or `mailto` addresses. `Html` and `Css` are
the exceptions: they insert trusted markup as it is, so never pass user input to them.

Invalid arguments throw `ArgumentException` (or `ArgumentOutOfRangeException`), an invalid document structure such
as an overlapping table cell throws `InvalidOperationException`, and PdfPecker's own limits throw
`PdfPeckerException`.

## Coming from QuestPDF

Most QuestPDF code ports by changing the `using` lines: `Document.Create`, `Page`, `Header`, `Content`, `Footer`,
`Column`, `Row`, `Table`, `Text`, `Span`, `Padding`, `Border`, `Background`, `Align*`, `Width`, `Height`, `Element`,
`Component` and `GeneratePdf` have the same names. The differences:

- `PageSizes.A4.Landscape` is a property, not a method.
- `Color.FromHex` reads CSS order, so `#rrggbbaa` puts alpha last, where QuestPDF reads `#aarrggbb`.
- Page numbers work in the header and footer, not in the content.
- In a table cell, put `Background` before `Padding` (`.Background(...).Padding(...)`) to fill the whole cell.
- Each `Page` call is converted on its own and the results are merged, so page numbers restart for each page set.
  PDF/A, PDF/UA and signatures need a single `Page` call.
- Transforms change how content is drawn, not the space it takes: `Scale(2)` draws twice as large from the top-left
  corner without making room. `RotateLeft` and `RotateRight` turn the layout itself (CSS `writing-mode`), so give
  longer rotated content a height.
- `MultiColumn` balances the columns unless `BalanceHeight(false)`, and has no `Spacer`. `ClampLines` always ends with
  an ellipsis. `Lazy` builds its content straight away, since PdfPecker lays out the document in one pass.
- `Decoration` is a table underneath, so PDF/UA tags it as one, and its before and after parts repeat only where the
  page breaks between items of a `Column` content, not inside one item taller than a page.
- `text.Element` with `BelowBaseline` sits like `Top`. Font features work with embedded fonts that have them.
- Of the section page numbers, only `BeginPageNumberOfSection` is available.
- Not available: `Dynamic`, `Canvas`, `StopPaging`, `ShowOnce`, `SkipOnce`, `RepeatContent`, `ScaleToFit`,
  `ExtendLastCellsToTableBottom`, `EndPageNumberOfSection`, `PageNumberWithinSection`, `TotalPagesWithinSection` and
  the Companion previewer. PdfPecker can now scale a box's content to fit it and give the first page a header of its
  own, so `ScaleToFit`, `ShowOnce` and `SkipOnce` are the next calls planned.

## Performance

The fluent layer costs well under a millisecond for a typical document: composing an invoice and writing its HTML
takes about 0.8 ms, against about 1.2 ms for PdfPecker to render that HTML. Measured on a 4-core 2.1 GHz cloud VM with
.NET 10 after a 12 s warm-up, medians:

| | PdfPecker.Fluent | QuestPDF 2026.9.1 |
|---|---|---|
| Invoice, 1 page | 3.5 ms | 14.0 ms |
| Invoice, 400 lines (14 pages) | 18.6 ms | 90.2 ms |
| Invoices per second, 4 cores | 947 | 249 |
| Package size | 0.2 MB + PdfPecker 11.8 MB | QuestPDF with native Skia binaries |

Both invoices come from the same code, call for call: `bench/PdfPecker.Fluent.Comparison`. Run
`bench/PdfPecker.Fluent.Benchmarks` to see the fluent layer's own cost.

## Source

PdfPecker.Fluent lives in the [PdfPecker repository](https://github.com/Moonwalker1307/PdfPecker), next to the
library:

- `src/PdfPecker.Fluent` – the package.
- `samples/PdfPecker.Fluent.Sample` – writes each example as a PDF and as HTML. Its `Examples.cs` holds an invoice, a
  report, a product catalogue, a layout tour and a text tour, shared by the sample, the tests and the benchmarks.
- `tests/PdfPecker.Fluent.Tests`, `fuzz/PdfPecker.Fluent.Fuzz` and `bench/PdfPecker.Fluent.*` – see `TESTING.md`.

## License

MIT. PdfPecker, the one dependency, is MIT too, with the licenses of the fonts and data it bundles in its
`THIRD-PARTY-NOTICES.md`.
