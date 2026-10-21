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
the page structure, the text layer, its web font and the links. The page images are blanked before the comparison,
because `RendererGoldenTests` already cover the renderer and its pixels can move by a rounding step between runtimes.
A change to `footnotes-toc/demo` or `features/images` changes these baselines too.

`Golden/features/` holds one page per group of features that came with the Chrome comparison work (text wrapping,
decorations, drop caps and float shapes, masks and filters, units and fonts, selectors, tables, CSS tables, images,
and documents without `html` or `body`). Each was compared with Chromium's print to PDF before it was checked in.

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

## CommonMark

`MarkdownSpecTests` runs the CommonMark 0.31.2 examples. The spec is downloaded into the temp folder (or read from
`PDFPECKER_COMMONMARK_SPEC`) and the test is skipped offline. It fails when an example listed in
`Fixtures/commonmark-passing.txt` regresses; record improvements with `PDFPECKER_UPDATE_COMMONMARK=1`. Don't commit
the spec text, which is CC-BY-SA.

## Shaping

`HarfBuzzShapingTests` compare PdfPecker's Arabic, Hebrew, Indic, Thai and Lao shaping with HarfBuzz's output in
`Fixtures/shaping/harfbuzz.txt`, which `tools/ShapingReference/generate.py` regenerates from `corpus.txt`.

## PDF/A and PDF/UA conformance

`.github/workflows/conformance.yml` generates samples with `tools/ConformanceSamples` and validates them with veraPDF
(pinned in `tools/ConformanceSamples/verapdf/pom.xml`, fetched from Maven Central). Rules that fail today are listed
in `tools/ConformanceSamples/known-failures.txt`; a new failure fails the job. To run it locally, follow the workflow's
steps: fetch veraPDF with Maven, `dotnet run -c Release --project tools/ConformanceSamples -- <dir>`, then
`python3 -I tools/ConformanceSamples/check.py <dir> <verapdf-dir>`.

## Fuzzing

`fuzz/PdfPecker.Fuzz` mutates seeds from the repository against every parser of untrusted input, and runs nightly in
`fuzz.yml`. How to run a target, reproduce a finding and turn it into a test is in `fuzz/README.md`.

## Performance

Nightly, `perf.yml` runs the benchmark on main and compares it with the previous night's run. For a change of your
own, run both versions in turns on one machine:

```
bench/compare-versions.sh v1.11.0     # the working tree against a release tag, branch or commit
```

Cloud VMs differ too much to compare a run with the README table.

## Coverage

CI collects line coverage on `net10.0` in a job of its own, alongside the build job, without the time-bound tests
(instrumentation slows them down), and never fails on it.
