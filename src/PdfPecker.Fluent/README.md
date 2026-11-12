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
  column spans, explicit placement, cell backgrounds and borders that fill the row in any call order, header and footer
  rows that repeat on every page, last cells extended to the bottom), padding, borders, corner radius, background,
  width and height with minimum and maximum, alignment, page breaks, `PreventPageBreak`, `StopPaging` (content kept to
  one page or to its height, the rest left out), `ShowIf` and right-to-left content.
- **Gradients and opacity**: `BackgroundLinearGradient` (QuestPDF's call, with its angles), `BackgroundRadialGradient`
  (from the middle or a centre you choose, for a glow on a cover page) and `Opacity`, which fades a slot and everything
  in it, such as a watermark over the content. Paint called before `Opacity` stays as it is.
- **More layout**: `Repeat` (content drawn again on each later page of its row item or table cell, such as a label
  beside a long text), `Layers` (watermarks under and stamps over content), `Inlined` (items that wrap like words, such as
  tags), `MultiColumn` (newspaper columns), `EnsureSpace` (keeps a heading off the bottom of a page), `Unconstrained`,
  `ZIndex`, `AspectRatio`, `Shadow`, rounded single corners, `Shrink`, `ScaleToFit` (draws content smaller until it
  fits a box of fixed size, for badges and labels), `Placeholder`, `DebugArea` and `Lazy`.
- **Repeating and lists**: `Decoration` (content with a part before and after it that repeat on every page it
  crosses) and `List` (bulleted, numbered or custom-marker lists, tagged as lists in PDF/UA).
- **Table of contents**: `TableOfContents()` lists the headings of the content, indented by level, each with a dot
  leader and the page it lands on, and links every line to its heading. `MaxLevel`, `Indent`, `Spacing`, `Leader`,
  `Style` and `LevelStyle` set it out. Give the table's own title no `Heading`, or it lists itself.
- **Transforms**: `Rotate`, `RotateLeft`, `RotateRight`, `Scale`, `ScaleHorizontal`, `ScaleVertical`, `FlipHorizontal`,
  `FlipVertical`, `FlipOver`, `TranslateX` and `TranslateY`.
- **Text**: spans with font family, size, weight, colour, background, italic, underline, overline, strikethrough
  (solid, double, wavy, dotted or dashed, in their own colour and thickness), line height, letter and word spacing, superscript and
  subscript; OpenType features (`EnableFontFeature`, `DisableFontFeature`); justified text, `ClampLines` with an
  ellipsis, `BreakAnywhere` (lines that break between any two characters, for long links and identifiers), a direction
  of their own (`DirectionFromRightToLeft`, `DirectionFromLeftToRight`, `DirectionAuto`), a first-line indent and `ParagraphSpacing`; `Hyphenate` (hyphenation of long words in eleven languages,
  which QuestPDF does not have) with the language of the document or of a container (`Language("da")`); small slots inside the text for icons and badges
  (`text.Element()`); dot leaders that fill the rest of a line (`Leader`), for tables of contents and price lists; headings, which become PDF bookmarks and tags; links and links to named sections, and the page a
  section starts on and ends on, its number of pages and the page within it (`BeginPageNumberOfSection`,
  `EndPageNumberOfSection`, `TotalPagesWithinSection`, `PageNumberWithinSection`).
- **Pages**: size, margins, page colour, direction (per page, or for the whole document with
  `DocumentSettings.ContentDirection`), `ContinuousSize` (a page as tall as its content, for receipts on a roll), a header and footer on every page, and `CurrentPageNumber` / `TotalPages`
  in them or anywhere in the content, as 1, i, I, a, A or 001 with `Format(PageNumberFormat.UpperRoman)` and the other formats. `ShowOnce` and `SkipOnce` in the header or footer give the first page a band of its own, such as a
  letterhead followed by a slim header. `EvenPageHeader` and `EvenPageFooter` give the even pages bands of their own, for printing on both sides. `Background` and `Foreground` cover the whole page, margins included, under or over the header, content and footer, for letterheads, frames and watermarks. Several `Page` calls give page sets with their own size, margins, header and
  footer in one document, and the page numbers run on through all of them.
- **Merging**: `Document.Merge(invoice1, invoice2, ...)` generates several documents as one PDF or HTML document, such
  as a batch of invoices. Each keeps its own page numbers, totals and section names, or `UseContinuousPageNumbers()`
  numbers the pages through all of them.
- **Working on existing PDFs**: `DocumentOperation`, with QuestPDF's names, works on any PDF, also ones PdfPecker did
  not write. `LoadFile` (or `Load` from bytes), then `TakePages` and `MergeFile` choose, reorder and append pages in
  qpdf's page syntax (`"1-3,z"`, `"r3-r1"`, `"1-10,x4"`, `"1-z:even"`), `OverlayFile` and `UnderlayFile` draw the
  pages of another PDF over or under chosen pages (a "COPY" stamp, letterhead with a different first page),
  `AddAttachment` attaches files, and `Encrypt`, `Decrypt` and `Linearize` protect the result or prepare it for Fast
  Web View, also both at once. `Save` writes a file or stream, and `GeneratePdf` returns the bytes:

  ```csharp
  DocumentOperation.LoadFile("invoice.pdf")
      .MergeFile("terms.pdf")
      .UnderlayFile(new DocumentOperation.LayerConfiguration { FilePath = "letterhead.pdf", SourcePages = "1", RepeatSourcePages = "2" })
      .AddAttachment(new DocumentOperation.DocumentAttachment { FilePath = "invoice.xml", Relationship = DocumentOperation.DocumentAttachmentRelationship.Alternative })
      .Encrypt(new DocumentOperation.Encryption256Bit { UserPassword = "secret", AllowModification = false })
      .Linearize()
      .Save("invoice-final.pdf");
  ```
- **Form fields and footnotes**, which QuestPDF does not have: `TextField` (one line, multiline or password),
  `CheckBox`, `RadioButton`, `ComboBox` and `ListBox` become fillable PDF fields, `SignatureField("Buyer")` is an
  empty signature field as large as its slot that `PdfSigner` signs later with `FieldName = "Buyer"`, and
  `text.Footnote("...")` numbers a note and sets it at the bottom of its page. Browsers show footnotes in the text, where `GenerateHtml` puts them.
- **Markdown**: `container.Markdown("...")` places Markdown text anywhere, converted by PdfPecker's own parser
  (CommonMark with GitHub tables, task lists, strikethrough, autolinks and footnotes, and definition lists). It takes
  the surrounding font, size and colour, styles headings, code, quotes and tables as on GitHub, and is safe for text
  from users: HTML in it stays text and only web, mail and in-document links keep their target.
- **Images and graphics**: PNG, JPEG, WebP, BMP, TIFF and GIF images, SVG, solid, dashed and dotted lines, and QR,
  EPC payment QR, Swiss QR-bill, Data Matrix, GS1 Data Matrix, Code 128 and EAN-13 barcodes. `Image.FromFile` and `SvgImage.FromText` load an image once for every
  page and document that draws it, and `WithCompressionQuality`, `WithRasterDpi` and `UseOriginalImage`, or
  `ImageCompressionQuality` and `ImageRasterDpi` in `DocumentSettings`, write photos as JPEGs and downsample them to
  the size they are drawn at.
- **Swiss QR-bill**: `SwissQrBill(bill, language)` lays out the 210 x 105 mm payment part with its receipt as the
  QR-bill style guide sets it, in English, German, French or Italian, with blank fields for a missing amount or payer.
- **Charts**: `Chart` draws column, bar, line and pie charts from your series as vector SVG, with a legend, data
  labels, your own colours and a .NET number format for the values; MigraDoc users will find the usual report charts here.
- **Accessibility**: `SemanticSection`, `SemanticArticle`, `SemanticHeader1` to `6` (and `SemanticHeading1` to `6`),
  `SemanticList` with `SemanticListLabel` and `SemanticListItemBody`, `SemanticTableOfContents` with its
  `SemanticTableOfContentsItem` entries, `SemanticFormula`, `SemanticIndex`, `SemanticBlockQuotation`, `SemanticQuote`,
  `SemanticCode`, `SemanticCaption`, `SemanticImage`, `SemanticLanguage`, `SemanticIgnore`, `SemanticHorizontalHeader`
  on a table cell (a row's header) and the rest of QuestPDF's semantic calls write the HTML elements and ARIA roles
  PdfPecker tags a PDF/UA document from.
- **Everything else in PdfPecker** through the normal `PdfOptions`: PDF/A, PDF/UA, signatures, encryption,
  watermarks, e-invoices (ZUGFeRD, Factur-X, XRechnung), attachments, fonts, viewer settings and page labels (which
  number the pages across every `Page` call) and linearized output for Fast Web View, also for documents with several
  `Page` calls. `GeneratePdf(options)` copies the options, so one options object can be shared.
- **An escape hatch**: `container.Html("...")` places raw HTML anywhere, and `documentContainer.Css("...")` adds
  stylesheet rules, for anything the fluent calls do not cover yet (floats, grid).
- **Output**: `GeneratePdf` (bytes, file or stream), `GeneratePdfAsync`, `GenerateImages` (PNG, JPEG, WebP, TIFF)
  and `GenerateHtml`, which shows the HTML PdfPecker renders and opens in a browser. `GeneratePdfAndShow` and
  `GenerateHtmlAndShow` write a temporary file and open it, and `Placeholders` makes up text, numbers, dates,
  colours and images (the same ones on every run) for trying out a layout.
- **.NET 8, 9, 10 and 11**, trimmable and Native AOT compatible, culture-independent output. 1.25.0 is the
  last release for .NET 8 and 9; the next release targets .NET 10 and later only.

## Safety

Text, links, colours and names you pass are encoded, so caller data never turns into markup: the fuzzer and the
hostile-input tests check that. Links must be absolute `http`, `https` or `mailto` addresses. `Markdown` shows HTML
written in the Markdown as text and drops the target of any other link, such as `javascript:` or a file. Its images load
as any other resource would, from data URIs or through your `ResourceResolver`. `Html` and `Css` are the exceptions:
they insert trusted markup as it is, so never pass user input to them.

Invalid arguments throw `ArgumentException` (or `ArgumentOutOfRangeException`), an invalid document structure such
as an overlapping table cell throws `InvalidOperationException`, and PdfPecker's own limits throw
`PdfPeckerException`.

## Coming from QuestPDF

Most QuestPDF code ports by changing the `using` lines: `Document.Create`, `Page`, `Header`, `Content`, `Footer`,
`Column`, `Row`, `Table`, `Text`, `Span`, `Padding`, `Border`, `Background`, `Align*`, `Width`, `Height`, `Element`,
`Component` and `GeneratePdf` have the same names. The differences:

- `PageSizes.A4.Landscape` is a property, not a method.
- `Color.FromHex` reads CSS order, so `#rrggbbaa` puts alpha last, where QuestPDF reads `#aarrggbb`.
- Transforms change how content is drawn, not the space it takes: `Scale(2)` draws twice as large from the top-left
  corner without making room. `RotateLeft` and `RotateRight` turn the layout itself (CSS `writing-mode`), so give
  longer rotated content a height.
- `MultiColumn` balances the columns unless `BalanceHeight(false)`. `ClampLines` always ends with
  an ellipsis. `Lazy` builds its content straight away, since PdfPecker lays out the document in one pass.
- `Decoration` is a table underneath, so PDF/UA tags it as one, and its before and after parts repeat only where the
  page breaks between items of a `Column` content, not inside one item taller than a page.
- `text.Element` with `BelowBaseline` sits like `Top`. Font features work with embedded fonts that have them.
- `ShowOnce` and `SkipOnce` act on the first page of the header and footer. Elsewhere content appears only once, so
  `ShowOnce` changes nothing and `SkipOnce` leaves the content out, also in a `Decoration` or a table header, whose
  repeats are all the same.
- `ScaleToFit` fits the content into a `Height` or `MaxHeight` set before it, or into the height of the slot around it,
  such as a row or a box with a `Height`.
- `Markdown` is built in, with the same call as the community QuestPDF.Markdown package's `container.Markdown(text)`;
  its look comes from CSS rules on the `md` class, which `documentContainer.Css("...")` can change.
- Page numbers take `Format(PageNumberFormat.LowerRoman)`, `UpperRoman`, `LowerLetters`, `UpperLetters` or
  `ZeroPadded(digits)` instead of QuestPDF's `Format(number => ...)` function, because PdfPecker numbers the pages while
  it lays them out and runs no code of yours then.
- `ExtendLastCellsToTableBottom` grows the last cell of each column of the table's body down to its last row. A cell
  spanning several columns grows only when it is the last cell in all of them, since HTML table cells cannot overlap.
- `BackgroundLinearGradient` runs the way its angle points: 0 left to right, 90 top to bottom, 180 right to left and
  270 bottom to top. QuestPDF 2026.9 runs the angles between 90 and 270 other than 180 rightwards instead, so a gradient
  ported from such an angle is reversed. `BackgroundRadialGradient` and `Opacity` are not in QuestPDF.
- `Document.Merge` composes the documents into one HTML document, so the result has one set of metadata and settings:
  those of `WithMetadata` and `WithSettings` on the merge, or the first document's. With original page numbers,
  `BeginPageNumberOfSection` and `EndPageNumberOfSection` still give pages of the whole PDF.
- `SemanticDivision`, `SemanticSpan` and `SemanticTable` write nothing: PdfPecker tags content by what it is, a
  `Table` is tagged as a table already, and plain groups stay out of the tag tree. `SemanticCode` and `SemanticQuote`
  keep the surrounding font and add no quotation marks. In a `SemanticListItem`, everything but the
  `SemanticListLabel` is the item's body, so `SemanticListItemBody` writes nothing; `List` tags its labels itself.
- Images keep their data unless `ImageCompressionQuality` or `ImageRasterDpi` is set, where QuestPDF compresses at
  `High` and 288 dpi by default; set those to match it. A compressed image replaces the original only when it is
  smaller, and transparency stays lossless. `Image.Size` is not available.
- A page number in `Background` or `Foreground` shows the number of the page set's first page on every page; put
  page numbers in the header or footer.
- `DocumentOperation` encrypts with AES-256 for every `Encrypt` call, also `Encryption40Bit` and `Encryption128Bit`,
  keeping their permissions, and always encrypts the metadata. An encrypted PDF whose pages change or that is
  linearized needs `Decrypt()` or an `Encrypt` call, where QuestPDF keeps the old encryption; adding attachments keeps
  it. An attachment is named by its `AttachmentName` (by default the file name), not by `Key`, and one the loaded PDF
  already has cannot be replaced. Overlays, underlays, encryption and linearization refuse digitally signed PDFs, whose
  signatures they would break; attachments keep the signatures valid.
  `ExtendMetadata` and `RemoveRestrictions` are not available.
- Not available: `Dynamic`, `Canvas`, the Companion previewer and the `MultiColumn` `Spacer`.

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

Both invoices come from the same code, call for call, in the
[PdfPecker.Benchmarks](https://github.com/Moonwalker1307/PdfPecker.Benchmarks) repository. Run
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
