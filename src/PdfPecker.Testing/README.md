# PdfPecker.Testing

Snapshot tests for PDFs. `PdfAssert.MatchesSnapshot` compares the PDF your code makes with a stored one, leaving out
what changes on every run (creation dates, file IDs, the compressed bytes of streams), and when they differ it writes
page images with the changed pixels marked. It also checks for text and page counts.

```
dotnet add package PdfPecker.Testing
```

It works with xUnit, NUnit, MSTest or any other test framework, because a failed check throws `PdfAssertException`,
which every framework reports as a failed test. Its only dependency is [PdfPecker](https://www.nuget.org/packages/PdfPecker),
which reads and renders the PDFs; the PDFs under test can come from PdfPecker, PdfPecker.Fluent or anything else.
PdfPecker.Testing is released together with PdfPecker and always has the same version.

```csharp
using PdfPecker.Testing;

[Fact]
public void Invoice_looks_as_before()
{
    byte[] pdf = InvoiceService.Render(SampleData.Invoice);

    PdfAssert.MatchesSnapshot(pdf, "invoice");          // Snapshots/invoice.pdf next to this test file
    PdfAssert.ContainsText(pdf, "Total due: 1,500.00");
    PdfAssert.ContainsText(pdf, "Payment terms", pageNumber: 2);
    PdfAssert.DoesNotContainText(pdf, "{{");             // no template placeholder left
    PdfAssert.HasPageCount(pdf, 2);
}
```

## Snapshots

The first run has no snapshot, so it fails and writes the PDF as `Snapshots/invoice.received.pdf`. Look at it, and
rename it to `invoice.pdf` to make it the snapshot. Commit the snapshots with the tests.

When a later PDF differs, the check fails with the first difference it found, for example

```
The PDF doesn't match the snapshot …/Snapshots/invoice.pdf: the stream of object 8 differs (260 bytes in the
snapshot, 265 in the PDF): line 7 is "1 0 0 1 38.25 486.53 Tm (Consulting) Tj" in the snapshot and
"1 0 0 1 38.25 486.53 Tm (Design work) Tj" in the PDF. Page 1 (1,561 pixels) looks different; the diff images are
next to the snapshot. …
```

and writes next to the snapshot:

- `invoice.received.pdf`, the new PDF;
- for each page that looks different, `invoice.page-1.expected.png` and `invoice.page-1.actual.png`, and
  `invoice.page-1.diff.png`: the new page faded to grey with the changed pixels in magenta.

To accept a change, replace the snapshot with the received file, or run the tests with the environment variable
`PDFPECKER_UPDATE_SNAPSHOTS=1`, which writes every missing or changed snapshot and passes. A check that passes deletes
the files an earlier failure left. Add the failure files to `.gitignore`:

```
*.received.pdf
*.page-*.expected.png
*.page-*.actual.png
*.page-*.diff.png
```

## What has to match

By default the comparison is of the files' content: every object in the same order, with streams inflated, and
without dates, file IDs and XMP document IDs. PdfPecker writes the same objects for the same input, so this catches
every change, including those that don't show on the page, such as a link target, a bookmark, the title or the text
that copy and paste and screen readers get. A PDF made again from the same input matches without a fixed
`CreationDate`.

`PdfSnapshotComparison.Appearance` compares the rendered pages instead. Use it for PDFs from other producers, or when
only the look matters, for example across a PdfPecker update that writes the same pages with different objects:

```csharp
var options = new PdfSnapshotOptions
{
    Comparison = PdfSnapshotComparison.Appearance,
    ColorTolerance = 2,         // a channel may differ by up to 2 of 255
    MaxDifferentPixels = 50,    // per page
};
PdfAssert.MatchesSnapshot(pdf, "invoice", options);
```

| `PdfSnapshotOptions` | Default | What it does |
|---|---|---|
| `Directory` | `Snapshots` next to the test's source file | Where the snapshots and failure files are |
| `Comparison` | `Content` | `Content` or `Appearance`, as above |
| `Dpi` | 72 | Resolution of the compared and written page images |
| `ColorTolerance` | 0 | How far a pixel's channel may differ before it counts as different |
| `MaxDifferentPixels` | 0 | How many different pixels a page may have in `Appearance` comparisons |
| `UpdateSnapshots` | false | Write the PDF as the snapshot instead of failing, like `PDFPECKER_UPDATE_SNAPSHOTS=1` |
| `WriteDiffImages` | true | Write the page images on a failure |
| `Password` | none | Password of encrypted PDFs |

The default folder comes from the source file path the compiler records for the calling test. A build with
`ContinuousIntegrationBuild` or `PathMap` records other paths, and the check then asks for `Directory`; for example
`Directory = Path.Combine(AppContext.BaseDirectory, "Snapshots")` with the snapshots copied to the output.

## Text and pages

`ContainsText`, `DoesNotContainText` and `HasPageCount` read the PDF with PdfPecker's text extraction. Line breaks
and other runs of white space count as one space, so text that wraps differently still matches. A PDF that can't be
read fails the check with a `PdfAssertException` that holds the reason.

## Limitations

- The content comparison reads objects in file order, so a PDF whose producer orders or numbers its objects
  differently on every run needs `Appearance`.
- Signed and encrypted PDFs change on every run by design (the signature and the key are new each time); compare them
  with `Appearance`, giving `Password` for encrypted ones.
- Rendering uses PdfPecker's renderer, so `Appearance` compares PdfPecker's picture of the pages, not a particular
  viewer's.
