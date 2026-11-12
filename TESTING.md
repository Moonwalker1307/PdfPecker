# Testing PdfPecker

```
dotnet test -c Release --no-build                                    # everything, every runtime
dotnet test -f net10.0 --filter "FullyQualifiedName~MarkdownTests"   # one class, one runtime
```

The tests are xUnit in `tests/PdfPecker.Tests`, one file per feature area. Inputs live in `Fixtures/`, baselines in
`Golden/`. Tests can see the library's internals.

## Golden baselines

`GoldenPdfTests` convert each `Golden/**/*.html` (and Markdown) input and compare the result byte for byte with its
`*.expected.pdf`. `RendererGoldenTests` render those PDFs and compare the PNGs. Every golden is also converted under
the ar-SA and fa-IR cultures, whose minus and plus signs differ from ASCII, to catch culture-dependent output.

`GoldenPdfTests` also keeps baselines for merging, stamping and encrypting under `Golden/operations/`. Encrypted output
changes on every run (random keys), so it is compared after decryption, and its encryption dictionary and object
layout with strings and stream data left out.

`ThirdPartyPdfTests` cover PDFs that other tools wrote: Chromium, LibreOffice Writer, ReportLab and Ghostscript, and
qpdf copies with object streams, RC4-128 and AES-256. It renders every page against PNGs, compares extracted text, and
keeps baselines for merging, compacting, stamping, form filling and decryption, all under `Golden/third-party/`. The
inputs in `Fixtures/third-party/` come from `tools/ThirdPartyPdfs/generate.sh`; regenerating them changes their
baselines too.

`PdfToHtmlGoldenTests` convert a few of those PDFs to HTML and compare it with `Golden/pdf-to-html/*.expected.html`:
the page structure, the text layer and the links. The page images and the text layer's web font are blanked first:
`RendererGoldenTests` cover the renderer, whose pixels can move by a rounding step between runtimes, and the font holds
the characters the runtime's Unicode tables know, which grow with each .NET version.
A change to `footnotes-toc/demo` or `features/images` changes these baselines too.

`Golden/features/` holds one page per group of features that came with the Chrome comparison work (text wrapping,
decorations, drop caps and float shapes, masks and filters, units and fonts, selectors, tables, CSS tables, images,
and documents without `html` or `body`), and since 1.22.0 for the newer ones (tables split across pages, list markers,
text styling, borders, outlines and clipping, the page box, sizing keywords and newer syntax, vertical writing, and page
numbers and fixed boxes in the content). Each was compared with Chromium's print to PDF before it was checked in.

When a change is meant to alter output, regenerate them and say in the commit and PR which baselines changed and why:

```
PDFPECKER_UPDATE_GOLDEN=1 dotnet test --filter "FullyQualifiedName~GoldenPdfTests|FullyQualifiedName~RendererGoldenTests|FullyQualifiedName~ThirdPartyPdfTests|FullyQualifiedName~PdfToHtmlGoldenTests" -f net10.0
```

Never regenerate just to make a failing test pass. A refactor must leave every golden unchanged.

## Hostile input

`HostileInputTests`, `FuzzFindingTests`, `TimeBoundTests` and `SignerInputTests` run in the `HostileInputCollection`
and must finish within their time bounds. Malformed input may only surface as the exceptions the public API
documents. A fix for a crash, hang or huge allocation on bad input gets a regression test there; a new parser of
untrusted input also gets resource limits and a fuzz target.

### Swallowed exceptions

To keep going on damaged input, the library catches `IndexOutOfRangeException`, `NullReferenceException` and
`InvalidCastException` in its parsers, decoders and renderer. On well-formed input the same catch would hide a bug,
so every such catch calls `SwallowedErrors.Record`, which passes the exception to `SwallowedErrors.Sink` when one is
set. The sink is per execution context (an `AsyncLocal`), so a test sees only its own work and the hostile-input tests,
which never set it, are unaffected; release behaviour does not change. `SwallowedErrorTests` sets it and fails when
anything is swallowed while converting every golden sample, rendering and reading back the golden PDFs and the
third-party fixtures, and embedding the fixture fonts and images. A new catch of these types must call
`SwallowedErrors.Record`, and a new well-formed fixture belongs in that test.

## CommonMark

`MarkdownSpecTests` runs the CommonMark 0.31.2 examples. The spec is downloaded into the temp folder (or read from
`PDFPECKER_COMMONMARK_SPEC`) and the test is skipped offline. It fails when an example listed in
`Fixtures/commonmark-passing.txt` regresses; record improvements with `PDFPECKER_UPDATE_COMMONMARK=1`. Don't commit
the spec text, which is CC-BY-SA.

## Shaping

`HarfBuzzShapingTests` compare PdfPecker's Arabic, Hebrew, Indic, Khmer, Myanmar, Sinhala, Tibetan, Thai and Lao shaping with HarfBuzz's output in
`Fixtures/shaping/harfbuzz.txt`, which `tools/ShapingReference/generate.py` regenerates from `corpus.txt`.

## Unicode normalization

Shaping, hyphenation and AES-256 passwords normalize text with the library's own tables (`Text/UnicodeNormalization.cs`),
not `string.Normalize`, which needs ICU and does nothing with invariant globalization. `UnicodeNormalizationTests`
compare it with the host's normalization across the characters of the Basic Multilingual Plane assigned in the tables'
Unicode version (from its `DerivedAge.txt`, since a newer ICU knows more) and run Unicode's `NormalizationTest.txt` for
that version, which is the authority. Both files are downloaded once into the temp folder like the CommonMark spec (or
read from the folder of `PDFPECKER_NORMALIZATION_TEST`; skipped offline). `SaslPrepTests` check the password preparation
(SASLprep, RFC 4013) against the RFC's examples and the edges of its tables. `nuget.yml` runs the normalization, shaping, hyphenation and
encryption tests a second time with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true`, as the command-line tool and the
Native AOT packages run.

## PDF/A and PDF/UA conformance

`.github/workflows/conformance.yml` generates samples with `tools/ConformanceSamples` and validates them with veraPDF
(pinned in `tools/ConformanceSamples/verapdf/pom.xml`, fetched from Maven Central). Rules that fail today are listed
in `tools/ConformanceSamples/known-failures.txt`; a new failure fails the job. To run it locally, follow the workflow's
steps: fetch veraPDF with Maven, `dotnet run -c Release --project tools/ConformanceSamples -- <dir>`, then
`python3 -I tools/ConformanceSamples/check.py <dir> <verapdf-dir>`.

## Fuzzing

`fuzz/PdfPecker.Fuzz` mutates seeds from the repository against every parser of untrusted input, and
`fuzz/PdfPecker.Fluent.Fuzz` builds random documents from random fluent calls; both run nightly in `fuzz.yml`. How to
run a target, reproduce a finding and turn it into a test is in `fuzz/README.md`.

## Mutation testing

`.github/workflows/mutation.yml` runs [Stryker.NET](https://stryker-mutator.io) every Sunday, one job per area
(`markdown`, `css-values`, `barcodes`, `text`), through `tools/MutationTesting/run.sh <area>`. Stryker changes the
area's code one mutant at a time (a `<` for a `<=`, a removed statement) and runs the area's tests against each; a
mutant that no test notices points at behaviour nothing pins down. The job never fails: the score and a per-file table
go to the job summary, and the HTML report (an artifact) shows each surviving mutant in its source. Turn the ones that
matter into tests; some are equivalent (`>>` and `>>>` on a `uint`) and stay.

The script edits the working tree, so run it locally only in a scratch clone: it builds net10.0 only and puts
`// Stryker disable all` at the top of every library file outside the area, because Stryker's `--mutate` only filters
the report and otherwise compiles all 160,000 mutants of the library into one assembly, which makes the coverage pass
take hours. The tests are filtered to the area's classes for speed, so a mutant killed only by another class shows as a
survivor. An area takes about an hour on four cores. The Markdown score needs the CommonMark spec (see above); offline
it drops by about 12 points.

## Performance

Nightly, `perf.yml` runs the benchmark on main and compares it with the previous night's run. For a change of your
own, run both versions in turns on one machine:

```
bench/compare-versions.sh v1.11.0     # the working tree against a release tag, branch or commit
PDFPECKER_BENCH_WARMUP=15 bench/compare-versions.sh v1.11.0   # a 15-second warm-up, so JIT tiering has finished
```

Cloud VMs differ too much to compare a run with the README table.

## PdfPecker.Cli

The command-line tool's tests are in `tests/PdfPecker.Cli.Tests`. They run the tool in process (`CliRunner` gives each test a
folder of its own and in-memory standard streams) and check exit codes, the files written and, for PDFs, their text and
page count:

- `ConvertTests` – `convert`, the wkhtmltopdf-style call, header and footer tokens, lengths and paper sizes, refusals
  (web addresses, an output that would overwrite an input) and `watch`, which is started, given a changed file and
  stopped through its cancellation token.
- `PdfCommandTests` – merge, split, extract, remove, rotate, stamp, compact, linearize, sanitize, encrypt, decrypt,
  sign and verify (with a throwaway self-signed certificate), to-image, to-html, text and info.

`nuget.yml` also installs the packed tool with the .NET 8 and 9 SDKs, which take its framework-dependent build, and
runs it; its `tool-packages` job packs the Native AOT package of each platform on that platform, installs the tool from
the full set of packages and checks that the native executable runs.

## PdfPecker.Fluent

The fluent package's tests are in `tests/PdfPecker.Fluent.Tests` and run with the rest:

- `LayoutTests` and `ExtrasTests` – the HTML each fluent call writes: which elements, which declarations, how
  decorators merge. `QuestPdfCallsTests` covers the calls named after QuestPDF's.
- `ApiTests` – argument checks, options handling (the caller's `PdfOptions` are copied, never changed), metadata,
  several page sets, images and the generate methods.
- `GoldenTests` – the examples in `samples/PdfPecker.Fluent.Sample/Examples.cs`, compared with
  `Golden/*.expected.html` exactly and with `Golden/*.expected.pdf` after inflating every stream (`PdfCompare.cs`),
  because .NET 9's zlib compresses the same bytes differently. They also run under ar-SA, fa-IR and da-DK.
- `HostileInputTests` – hostile strings in every text, link, name and CSS value, deep nesting, huge spans and damaged
  images. Caller text must never become markup, and only documented exceptions may come out.
- `PerformanceTests` – the HTML writer stays linear and writing HTML stays a small part of producing a PDF, both
  measured in allocated bytes, which do not depend on the machine's load; 100,000 rows finish within a loose time
  bound that only guards against a hang.

Their goldens use the same switch, and render through the library, so a library change of output can change them:

```
PDFPECKER_UPDATE_GOLDEN=1 dotnet test -f net10.0 tests/PdfPecker.Fluent.Tests --filter "FullyQualifiedName~GoldenTests"
```

The benchmarks:

```
dotnet run --project bench/PdfPecker.Fluent.Benchmarks -c Release                 # the fluent layer's own cost
```

## PdfPecker.Testing

The snapshot testing package's tests are in `tests/PdfPecker.Testing.Tests` and run with the rest. They make small
PDFs with `HtmlToPdf` and keep their snapshots in a temporary folder of their own, so they have no checked-in
baselines and a library output change doesn't change them.

- `SnapshotTests` – missing, matching and changed snapshots, the files a failure writes and a pass deletes, the
  content and appearance comparisons, tolerances, extra pages, damaged snapshots, file names and the
  `PDFPECKER_UPDATE_SNAPSHOTS` variable (in a collection that doesn't run in parallel, because the variable is
  process-wide).
- `TextAndPageTests` – `ContainsText`, `DoesNotContainText` and `HasPageCount`, and unreadable input.

The `pdf-snapshot` fuzz target runs the snapshot normaliser on damaged PDFs, because it reads snapshot files without
the library's parser.

## PdfPecker.Razor

The Razor package's tests are in `tests/PdfPecker.Razor.Tests`, a Razor SDK project with its own small components,
and run with the rest. `RazorToPdfTests` checks parameters, encoding of caller text, `MarkupString`, injected services,
asynchronous data, layouts, the errors for unknown parameters, missing services and component exceptions,
cancellation, parallel conversions, and that the PDF is byte for byte the one `HtmlToPdf` makes from the rendered HTML.
The package has no goldens of its own: its output is PdfPecker's output for the HTML Razor writes.

CI's `native-aot` job publishes `samples/PdfPecker.Razor.Sample` with Native AOT and checks that it writes the invoice
PDF.

It times composing and writing HTML, `GeneratePdf`, and `HtmlToPdf` on the same HTML, after a 12-second warm-up. The
comparison with QuestPDF, the same invoice built with both, is in the
[PdfPecker.Benchmarks](https://github.com/Moonwalker1307/PdfPecker.Benchmarks) repository.

## Coverage

CI collects line coverage on `net10.0` in a job of its own, alongside the build job, without the time-bound tests
(instrumentation slows them down), and never fails on it.
