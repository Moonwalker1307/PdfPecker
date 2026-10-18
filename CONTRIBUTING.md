# Contributing to PdfPecker

Thanks for helping. Bug reports, small fixes and feature ideas are all welcome. For anything larger than a bug fix,
please open an issue first so we can agree on the approach before you spend time on it.

By contributing you agree that your contributions are licensed under the [MIT license](LICENSE), and to follow the
[code of conduct](CODE_OF_CONDUCT.md). Security problems go through the [security policy](SECURITY.md), not public
issues.

## Reporting a bug

The most useful report is a small HTML, Markdown or PDF file that shows the problem, the options you passed, what you
expected and what you got (a screenshot of the PDF helps). Please trim the input down to the few elements that matter
and leave out anything private.

## The one hard rule: no dependencies

The NuGet package depends only on the .NET base class library. It has its own HTML, CSS, Markdown and PDF parsers,
layout engine, PDF writer and renderer, and that is the point of the project. A pull request that adds a
`PackageReference` to `src/PdfPecker` cannot be merged. The library must also stay trimmable and Native AOT
compatible, so no reflection-based code.

## Building and testing

You need the .NET 10 SDK. The library and tests target `net8.0`, `net9.0` and `net10.0`, and CI runs all three.

```
dotnet build -c Release
dotnet test -c Release --no-build
dotnet test -f net10.0 --filter "FullyQualifiedName~MarkdownTests"   # one class, one runtime
```

If you only have one runtime installed, pass `-f net10.0` (or the one you have). [BUILD.md](BUILD.md) and
[TESTING.md](TESTING.md) have the details, and [AGENTS.md](AGENTS.md) the full set of rules the project works by.

Things that will fail the build or CI:

- Warnings are errors in the library.
- Every public member needs an XML doc comment.
- `dotnet pack` checks the public API against the last release, so a breaking change belongs only in a major version.
- **Golden tests** (`tests/PdfPecker.Tests/Golden/`) compare output byte for byte with checked-in `*.expected.pdf`
  files, and `RendererGoldenTests` compare the renderer's PNGs of them. If your change is meant to alter output,
  regenerate them and say in the pull request which baselines changed and why:

  ```
  PDFPECKER_UPDATE_GOLDEN=1 dotnet test --filter "FullyQualifiedName~GoldenPdfTests|FullyQualifiedName~RendererGoldenTests" -f net10.0
  ```

  Please don't regenerate just to make a failing test pass, and a refactor should leave every baseline unchanged.
- `MarkdownSpecTests` runs the CommonMark examples and fails when one that passed before regresses.
- Malformed input may only surface as the exceptions the public API documents. A fix for a crash or hang on bad input
  gets a regression test in `HostileInputTests`, and a new parser of untrusted input also needs resource limits and a
  fuzz target (see [fuzz/README.md](fuzz/README.md)).

To check that a change does not slow things down, run `bench/compare-versions.sh <base-ref>`, which benchmarks both
versions in turns on the same machine.

## Code style

- Nullable enabled, file-scoped namespaces, `internal` unless it is part of the public API.
- Large classes are split into partial-class files by concern (`PdfDocumentWriter.Text.cs`, ...); put new code in the
  file for its concern.
- Reuse the shared low-level code instead of copying it: `Pdf/PdfObjectWriter.cs`, `PdfXref.cs` and
  `PdfUpdateWriter.cs` for writing PDF objects, and `Fonts/Cff/` for CFF fonts.
- Files under `tools/` generate checked-in data (Unicode tables, the bundled font pack); change the generator, not
  its output.
- Comments are plain, full sentences that explain why, not what.
- Adding or changing a bundled font or data source means updating `THIRD-PARTY-NOTICES.md`.

## Pull requests

- Branch from the latest `main` and keep a pull request to one change.
- Commit subjects are short sentences without prefixes, e.g. "Right-to-left text in SVG and RTL ellipsis".
- If the change adds or changes a feature, update `README.md` too. The full manual at
  [pdfpecker.net/docs](https://pdfpecker.net/docs/) lives in a separate repository and is updated with each release.
- Fill in the pull request template; CI must be green before review.
