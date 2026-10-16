# CLAUDE.md

PdfPecker is a dependency-free .NET library: HTML/Markdown to PDF, and PDF to PNG, JPEG or WebP. It has its own HTML parser, Markdown
parser, CSS engine, layout engine, PDF writer and PDF renderer. The NuGet package may depend only on the .NET base
class library: never add a PackageReference to `src/PdfPecker`.

## Git

- Always create a new branch for each piece of work, from the latest `origin/main`. Never reuse a branch name, even
  one whose pull request is merged.
- Name the branch after the feature or fix, in lowercase words joined by hyphens, e.g. `add-claude-md` or
  `rtl-svg-text`, never a random or generic name.

## Build and test

```
dotnet build -c Release
dotnet test -c Release --no-build
dotnet test -f net10.0 --filter "FullyQualifiedName~MarkdownTests"   # one class, one runtime
```

- Library and tests target `net8.0`, `net9.0` and `net10.0`; CI runs all three. If only one SDK/runtime is installed
  locally, pass `-f net10.0` (or whichever is installed).
- `TreatWarningsAsErrors` is on in the library, so a warning breaks the build.
- Every public member needs an XML doc comment (CS1591 is an error).
- `dotnet pack` validates the public API against the last release (`PackageValidationBaselineVersion` in
  `src/PdfPecker/PdfPecker.csproj`). Raise it to the new version after each release; a breaking change belongs only in a
  major version.
- Two external conformance checks: `MarkdownSpecTests` runs the CommonMark 0.31.2 examples (downloaded into the temp
  folder, skipped offline) and fails when an example listed in `Fixtures/commonmark-passing.txt` regresses; record
  improvements with `PDFPECKER_UPDATE_COMMONMARK=1`. `.github/workflows/conformance.yml` validates PDF/A and PDF/UA
  samples from `tools/ConformanceSamples` with veraPDF (run it locally as the workflow does); rules that fail today
  are listed in `tools/ConformanceSamples/known-failures.txt`, and a new failure fails the job.
- Golden tests (`tests/PdfPecker.Tests/Golden/**`) compare output against `*.expected.pdf`. When an output change is
  intended, regenerate with `PDFPECKER_UPDATE_GOLDEN=1 dotnet test --filter GoldenPdfTests -f net10.0` and say in the
  commit/PR which baselines changed and why. Never regenerate just to make a failing test pass.

## Layout

- `src/PdfPecker/` – public API at the top level (`HtmlToPdf`, `MarkdownToPdf`, `PdfToImage`, `PdfMerger`,
  `PdfSigner`, `PdfOptions`, ...). Pipeline in `HtmlToPdf.cs`: `Html/` parse → `Css/` `StyleResolver` →
  `Layout/` `LayoutEngine` → `Pdf/` `PdfDocumentWriter`. `Rendering/` is the separate PDF-to-image renderer.
- `tests/PdfPecker.Tests/` – xUnit; `Fixtures/` holds input files, `Golden/` the baseline PDFs.
- `bench/PdfPecker.Benchmarks` – in the solution. `bench/PdfPecker.Comparison` – deliberately not in the solution so
  its third-party packages never reach the library or tests.
- `fuzz/PdfPecker.Fuzz` – dependency-free mutation fuzzer for the parsers of untrusted input (nightly in
  `.github/workflows/fuzz.yml`); how to run it and to turn a finding into a test is in `fuzz/README.md`.
- `tools/` – generators for checked-in data; don't hand-edit their output:
  - `tools/UnicodeTables` → `src/PdfPecker/Text/UnicodeTables.g.cs`
  - `tools/FontPackBuilder` → `Rendering/Fonts/StandardFonts.bin` and `Fonts/Bundled/*.ttf.br`

## Conventions

- Nullable enabled, file-scoped namespaces, `internal` by default; tests can see internals.
- Library must stay trimmable and AOT-compatible: no reflection-based code.
- Code comments and docs are plain, full sentences explaining why, not what.
- Adding or changing a bundled font or data source: update `THIRD-PARTY-NOTICES.md`.
- `README.md` is the short overview (also shown on nuget.org): pitch, install, quick start, a one-line feature list and
  the comparison table. The full manual is at pdfpecker.net/docs, generated from `docs/*.md` in the PdfPecker.Web
  repository; a new feature gets a README line and a docs change there, which goes live with the next NuGet release.
- Commit subjects are short imperative or descriptive sentences, no prefixes (e.g. "Right-to-left text in SVG and
  RTL ellipsis").
