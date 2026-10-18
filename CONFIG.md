# Configuration

This file covers how the repository is configured: build settings, environment variables and CI. How callers
configure the library itself (`PdfOptions`, `ImageOptions`, fonts, resource resolvers and so on) is in the manual at
[pdfpecker.net/docs](https://pdfpecker.net/docs/) and the XML doc comments.

## Build settings

| File | What it sets |
| --- | --- |
| `global.json` | .NET SDK 10.0.100, rolling forward to the latest feature band. |
| `Directory.Build.props` | Nullable and `TreatWarningsAsErrors` for every project except `bench/PdfPecker.Comparison`. |
| `src/PdfPecker/PdfPecker.csproj` | Target frameworks, trimming and AOT analyzers, XML docs, package metadata, the local `-dev` version, package validation and `PackageValidationBaselineVersion`, embedded font and hyphenation data, and `InternalsVisibleTo` for the tests, fuzzer, benchmarks and FontPackBuilder. On GitHub Actions it also sets `ContinuousIntegrationBuild` for reproducible PDBs. |
| `.editorconfig` | UTF-8, four-space indents (two for YAML and JSON), file-scoped namespaces, `System` usings first. |
| `.gitattributes` | LF line endings everywhere and binary handling for fonts, images, PDFs and compressed data. |

The package licence is given as `PackageLicenseFile` (`THIRD-PARTY-NOTICES.md`), not as an expression, because
nuget.org only accepts OSI- or FSF-approved licence IDs and some bundled font licences are neither.

## Environment variables

| Variable | Used by | Effect |
| --- | --- | --- |
| `PDFPECKER_UPDATE_GOLDEN=1` | `GoldenPdfTests`, `RendererGoldenTests` | Rewrites the golden baselines instead of comparing. |
| `PDFPECKER_UPDATE_COMMONMARK=1` | `MarkdownSpecTests` | Records newly passing CommonMark examples in `Fixtures/commonmark-passing.txt`. |
| `PDFPECKER_COMMONMARK_SPEC` | `MarkdownSpecTests` | Path to a local `spec.txt`, for running the spec tests offline. |
| `SOURCE_DATE_EPOCH` | The library at run time | When no `CreationDate` is set, fixes the dates and file identifier, so the same input gives a byte-identical PDF. |

## CI

| Workflow | When | What |
| --- | --- | --- |
| `nuget.yml` | Every PR, pushes to main, published releases | Builds and tests net8.0, net9.0 and net10.0 on Linux and net10.0 on Windows, publishes the sample with Native AOT, reports coverage and packs. On a release it attaches the package and publishes it to nuget.org. |
| `conformance.yml` | PRs and pushes touching `src`, `tests` or the samples | veraPDF validation of the PDF/A and PDF/UA samples. |
| `codeql.yml` | PRs, pushes and a schedule | CodeQL analysis. |
| `perf.yml` | Nightly | Benchmark on main against the previous night's run. |
| `fuzz.yml` | Nightly | Fuzzes every target. |

Actions are pinned to commit SHAs, and jobs get read-only permissions unless they need more: the release job may
attach files, and the publish job may request an OIDC token.

Repository settings the release relies on: the `nuget` environment, and the `NUGET_USER` variable naming the
nuget.org account that owns the Trusted Publishing policy. No NuGet API key is stored. See `RELEASING.md`.
