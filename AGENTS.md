# AGENTS.md

Guidance for coding agents (and people) working in this repository. `CLAUDE.md` imports this file, so Claude Code and
other agents read the same rules.

PdfPecker is a dependency-free .NET library: HTML/Markdown to PDF, and PDF to PNG, JPEG, WebP or TIFF. It has its own
HTML parser, Markdown parser, CSS engine, layout engine, PDF writer and PDF renderer, and the NuGet package depends only
on the .NET base class library.

The repository also holds PdfPecker.Fluent (`src/PdfPecker.Fluent`), a fluent C# API in the style of QuestPDF that
writes HTML and CSS and renders it with PdfPecker. It is a second NuGet package, released with every PdfPecker release
under the same version, and its only dependency is the PdfPecker package of that version. It has no layout engine of
its own: anything about how a page looks is PdfPecker's job, and the fluent package's job is to write the smallest
HTML that says what the caller built.

PdfPecker.Testing (`src/PdfPecker.Testing`) is the third package, released the same way: snapshot tests for PDFs
(`PdfAssert`), with a normalised comparison of the files' objects, pixel diffs of the rendered pages, and text and
page count checks. It depends on no test framework: a failed check throws `PdfAssertException`.

The repository also holds PdfPecker.Cli (`src/PdfPecker.Cli`), the `pdfpecker` dotnet tool: a thin command line over the
library's public API (convert, watch, merge, split, sign, ...) with wkhtmltopdf-compatible options. It is a fourth NuGet
package, released with every PdfPecker release under the same version; it carries the PdfPecker assembly inside it and
has no dependencies of its own. A .NET 10 SDK installs it as a Native AOT executable from `PdfPecker.Cli.<rid>`, packed
per platform in CI, and older SDKs install the framework-dependent build inside the package itself.

PdfPecker.Razor (`src/PdfPecker.Razor`) is the fifth package, released the same way. It renders Razor components to
HTML with Microsoft's `HtmlRenderer` and converts the HTML with PdfPecker. It is the only project that may depend on a
package at run time, and only on Microsoft's `Microsoft.AspNetCore.Components.Web`, which holds that renderer.

## Read first

Before non-trivial work, read:

- `README.md` – what the library does and promises; it is also the nuget.org page.
- `BUILD.md` – SDKs, target frameworks, build commands, warnings as errors, Native AOT.
- `TESTING.md` – the test suites, golden baselines, hostile input, conformance, fuzzing and benchmarks.
- `CONFIG.md` – the repository's build settings, environment variables and CI settings.
- `RELEASING.md` – versioning, the API baseline and how a release reaches nuget.org.

`CONTRIBUTING.md` is the short version for outside contributors, `fuzz/README.md` covers both fuzzers, and
`src/PdfPecker.Fluent/README.md`, `src/PdfPecker.Testing/README.md` and `src/PdfPecker.Razor/README.md` are the other
packages' READMEs and nuget.org pages.

## Core rules

- **No dependencies.** Never add a `PackageReference` to `src/PdfPecker`. Everything the library does is its own code
  on top of the base class library. No project in this repository uses a third-party package at run time: the
  comparisons with other converters and with QuestPDF live in
  [PdfPecker.Benchmarks](https://github.com/Moonwalker1307/PdfPecker.Benchmarks). `src/PdfPecker.Fluent` and
  `src/PdfPecker.Testing` reference `src/PdfPecker` as a project and nothing else. The one exception is
  `src/PdfPecker.Razor`, which may reference `Microsoft.AspNetCore.Components.Web` (the release of each target
  framework's .NET version) and nothing else besides `src/PdfPecker`; Razor rendering is Microsoft's code and cannot
  be written on the base class library. No other project takes that package or any other.
- **No JavaScript and no network.** PdfPecker is for HTML templates the caller controls. It runs no scripts, and
  conversion never fetches anything itself: remote resources load only through a `ResourceResolver` the caller
  supplies. Only signing goes online, and only when the caller asks for it.
- **No breaking API changes before 2.0**, in any of the packages. `dotnet pack` checks the public API against the last
  release; a change it rejects needs a new overload or type instead, not a suppression. The fluent package keeps
  QuestPDF's names and signatures where they fit, so QuestPDF code ports by changing `using` lines.
- **The command line is the CLI's API.** Before 2.0, `src/PdfPecker.Cli` does not remove or rename a command or an
  option, and keeps the exit codes (0 success, 1 the work failed, 2 a wrong command line). It uses only PdfPecker's
  public API, never reads a web address, and takes no reflection-based argument parser: `Options.cs` is its own.
- **Caller data never becomes markup in the fluent package.** Text, links, colours, names and image data are encoded
  or validated; only `Html()` and `Css()` insert trusted markup, and their docs say so. It throws only
  `ArgumentException` (and subclasses) for bad arguments, `InvalidOperationException` for an invalid document structure
  and `PdfPeckerException` from the library.
- **Trimmable and AOT-compatible.** No reflection-based code.
- **Sealed, static and span-based.** Classes are `sealed` (or `static`) unless something derives from them, and
  methods that use no instance data are `static`. `src/PdfPecker/.globalconfig` turns the .NET performance analyzers
  (CA18xx) into build errors, so these and span-based string handling are checked on every build.
- **Culture-independent output.** Parse and format numbers with `CultureInfo.InvariantCulture`
  (`Invariant.TryParseInt` for integers) and compare strings ordinally.
- **Malformed input fails only with documented exceptions**, within bounded time and memory.
- **Every library PR checks `README.md`** (features, limitations, examples, benchmark table) and updates it in the same
  PR when the change is visible to users; a fluent package change checks `src/PdfPecker.Fluent/README.md` (features,
  the QuestPDF porting notes, the benchmark table) the same way, and a testing or Razor package change checks
  `src/PdfPecker.Testing/README.md` or `src/PdfPecker.Razor/README.md`.
- **Never name a user's private application or employer** in code, tests, commits, the README or PR descriptions. Use
  neutral names such as "MyApp" or "a production app".

## Repo map

- `src/PdfPecker/` – the library. The public API sits at the top level (`HtmlToPdf`, `MarkdownToPdf`, `PdfToImage`,
  `PdfToHtml`, `PdfMerger`, `PdfSigner`, `PdfOptions`, ...). The pipeline in `HtmlToPdf.cs` is `Html/` parse → `Css/`
  `StyleResolver` → `Layout/` `LayoutEngine` → `Pdf/` `PdfDocumentWriter`. `Rendering/` is the separate PDF-to-image
  renderer, which `Rendering/Html/` also turns into PDF-to-HTML pages; `Markdown/`, `Fonts/`, `Text/` (bidi, line
  breaking, hyphenation), `Images/`, `Svg/` and `Signing/` hold the rest.
- Large classes are split into partial-class files by concern (`PdfDocumentWriter.Text.cs`,
  `StyleResolver.Cascade.cs`, ...). Shared low-level code that new code must reuse instead of copying:
  - `Pdf/PdfObjectWriter.cs`, `PdfXref.cs` and `PdfUpdateWriter.cs` write PDF objects, xref sections and incremental
    updates for every writer (merge, stamp, sign, LTV, forms, attachments). `PdfObjectSyntax` presets keep each
    writer's exact bytes.
  - `Fonts/Cff/` reads CFF and CFF2 for the subsetter, the CFF2 instancer and the renderer.
- `src/PdfPecker.Fluent/` – the fluent package. The public API sits at the top level: `Document`, `PageDescriptor`,
  `ContainerExtensions` (decorators; transforms, placement and shadows in `ContainerExtensions.Layout.cs`, semantic
  calls in `ContainerExtensions.Semantics.cs`), `ContentExtensions` (text, column, row, table, layers, lists, images,
  barcodes, lines; form fields in `ContentExtensions.Forms.cs` with `FormDescriptors.cs`), `Descriptors.cs`,
  `TextDescriptors.cs`, `TextStyle`, `Color`, `Colors`, `PageSizes`, `Unit`, `FontManager`, `Placeholders` and
  `GenerateExtensions`. `Model/Elements.cs` is the tree the calls build; `Html/HtmlWriter.cs` turns it into HTML,
  merging decorator chains into as few elements as the CSS box model allows (see the comments on `Box`), with one class
  per distinct set of declarations; `Html/TableLayout.cs` places table cells, `Html/ImageTable.cs` hands images to
  PdfPecker through a resource resolver, and `Html/CssValue.cs` formats and escapes CSS values.
- `src/PdfPecker.Cli/` – the tool. `CliApp.cs` finds the command; `ConvertCommand.cs` and `ConvertOptions.cs` hold
  `convert`, `watch` and the wkhtmltopdf option mapping (header and footer texts become CSS margin boxes);
  `PageCommands.cs`, `FileCommands.cs`, `SecurityCommands.cs` and `ExportCommands.cs` hold the PDF commands; `Options.cs`
  parses arguments and `Units.cs` lengths and paper sizes.
- `tests/PdfPecker.Tests/` – xUnit. `Fixtures/` holds inputs, `Golden/` the baseline PDFs.
- `tests/PdfPecker.Cli.Tests/` – xUnit for the tool; it runs commands in process with in-memory streams.
- `tests/PdfPecker.Fluent.Tests/` – xUnit for the fluent package. `Golden/` holds the baseline HTML and PDFs of the
  examples.
- `src/PdfPecker.Testing/` – the snapshot testing package: `PdfAssert`, `PdfSnapshotOptions` and `PdfAssertException`
  are public; `PdfNormalizer` reads a PDF into normalised objects and `PixelDiff` compares rendered pages and writes the
  diff PNGs. `tests/PdfPecker.Testing.Tests/` holds its tests.
- `src/PdfPecker.Razor/` – the Razor package: `RazorToPdf` renders a component with `HtmlRenderer`, in a renderer of
  its own per call, and hands the HTML to `HtmlToPdf`. `tests/PdfPecker.Razor.Tests/` holds its xUnit tests and test
  components, and `samples/PdfPecker.Razor.Sample` an invoice template that CI also publishes with Native AOT.
- `samples/PdfPecker.Sample` – converts the sample documents; CI also publishes it with Native AOT.
- `samples/PdfPecker.Fluent.Sample` – writes the fluent examples as PDF and HTML; CI also publishes it with Native AOT.
  Its `Examples.cs` and `assets/` (invoice, report, catalogue, layout and text examples) are shared with the fluent
  tests and benchmarks.
- `bench/PdfPecker.Benchmarks`, `bench/PdfPecker.Fluent.Benchmarks` and `bench/PdfPecker.Versions`, which
  `bench/compare-versions.sh` runs. The comparisons with other converters and QuestPDF are in the
  [PdfPecker.Benchmarks](https://github.com/Moonwalker1307/PdfPecker.Benchmarks) repository.
- `fuzz/PdfPecker.Fuzz` – the dependency-free mutation fuzzer; `fuzz/PdfPecker.Fluent.Fuzz` – the generative fuzzer of
  the fluent package.
- `tools/` – generators for checked-in data (see "Change-specific guidance"), the veraPDF conformance samples, the
  HarfBuzz shaping reference and the third-party PDF fixtures.
- `.github/workflows/` – `nuget.yml`, `dotnet-preview.yml`, `conformance.yml`, `codeql.yml`, `perf.yml`, `fuzz.yml`,
  `dependabot-auto-merge.yml`; see `CONFIG.md`.
- The user manual at pdfpecker.net/docs is not here: it is generated from `docs/*.md` in the PdfPecker.Web repository.

## Build and verify

```
dotnet build -c Release
dotnet test -c Release --no-build
dotnet test -f net10.0 --filter "FullyQualifiedName~MarkdownTests"   # one class, one runtime
```

Before pushing, the build must be warning-free (warnings are errors), the tests must pass, and the golden baselines
must be unchanged unless the output change is intended. If only one runtime is installed, pass `-f net10.0`; CI runs
`net8.0`, `net9.0` and `net10.0` on Linux and `net10.0` on Windows. A change to the public API also needs
`dotnet pack src/PdfPecker -c Release -o ./nupkg` (or `src/PdfPecker.Fluent`, `src/PdfPecker.Testing`,
`src/PdfPecker.Razor`), which runs package validation. The tool has no assembly API to validate; pack it with
`src/PdfPecker.Cli` when its csproj changes. Details
are in `BUILD.md` and `TESTING.md`.

## Change-specific guidance

- **Output changes** (layout, CSS, fonts, PDF bytes): regenerate the goldens with `PDFPECKER_UPDATE_GOLDEN=1` (command
  in `TESTING.md`) and list in the commit and PR which baselines changed and why. A refactor must leave every golden
  unchanged. The fluent goldens render through the library too, so a library output change can change their PDFs.
- **A new CLI command or option**: a test in `PdfPecker.Cli.Tests`, an entry in `Help.cs`, a line in
  `src/PdfPecker.Cli/README.md`, and the root README's "Command line" section when it is a new kind of job. A new
  wkhtmltopdf option is either mapped in `ConvertOptions.cs` or listed there as ignored with a warning, never rejected.
- **A new fluent call**: XML doc comment, a `LayoutTests` test of the HTML it writes, a line in its README, and a
  place in the fluent fuzzer's generator. Prefer QuestPDF's name and signature when QuestPDF has the call. Look at the
  PDFs, and compare with Chrome printing the `GenerateHtml` output when layout is involved.
- **Something PdfPecker cannot render for the fluent package**: don't work around it in the writer with fragile
  markup; fix the library in the same repository, in its own PR first.
- **Public API**: code is `internal` unless it is meant for callers (tests can see internals). Every public member
  needs an XML doc comment (CS1591 is an error). Add overloads rather than changing signatures, add a README line for
  a new feature, and remember the manual in PdfPecker.Web gets a docs change that goes live with the next release.
- **Newer .NET APIs**: the library also targets .NET 8, which lacks `Dictionary` alternate lookups, span `Split` and
  some other APIs. Guard them with `#if NET9_0_OR_GREATER` and keep a .NET 8 path.
- **Parsers of untrusted input** (PDF, images, fonts, HTML, CSS, SVG, Markdown): add resource limits, a fuzz target in
  `fuzz/PdfPecker.Fuzz`, and for each bug fix a regression test in the `HostileInputCollection`.
- **Generated data**: change the generator, never its output.
  - `tools/UnicodeTables` → `src/PdfPecker/Text/UnicodeTables.g.cs`
  - `tools/FontPackBuilder` → `Rendering/Fonts/StandardFonts.bin` and `Fonts/Bundled/*.ttf.br`
  - `tools/Hyphenation/build.cs` → `src/PdfPecker/Text/Hyphenation/*.br`
  - `tools/IndicTables/build.cs` → `src/PdfPecker/Fonts/IndicTables.g.cs`
  - `tools/ShapingReference/generate.py` → `tests/PdfPecker.Tests/Fixtures/shaping/harfbuzz.txt`
  - `tools/ThirdPartyPdfs/generate.sh` → `tests/PdfPecker.Tests/Fixtures/third-party/*.pdf`
  - FontPackBuilder and Hyphenation pin their inputs by SHA-256; a deliberate update changes the hashes, the versions
    named in the tool and `THIRD-PARTY-NOTICES.md` together.
- **Bundled fonts or data sources**: update `THIRD-PARTY-NOTICES.md`.
- **PDF/A and PDF/UA**: run the veraPDF check from `conformance.yml`; a new failure that is not in
  `tools/ConformanceSamples/known-failures.txt` fails CI.
- **Performance-sensitive code**: compare with `bench/compare-versions.sh <base-ref>` on one machine, not with the
  README table.
- **Releases**: follow `RELEASING.md`; only the repository owner publishes a release, and it publishes all five
  packages.

## Avoid

- Adding a package, a native binary, a browser or a JavaScript engine (PdfPecker.Razor's Microsoft package is the only
  exception).
- Regenerating goldens or the CommonMark pass list just to make a failing test pass.
- Skipping, disabling or loosening a test, a time bound or a known-failures list to get CI green.
- Reflection, `dynamic`, runtime code generation, or anything else that breaks trimming or Native AOT.
- `ToString()`/`Parse` without an invariant culture, and culture-sensitive string comparison.
- Letting malformed input escape as `IndexOutOfRangeException`, `NullReferenceException`, `OutOfMemoryException` or
  a hang.
- Copying low-level PDF or CFF code instead of reusing the shared writers and readers.
- Hand-editing generated files, or committing the CommonMark spec text (it is CC-BY-SA and downloaded at test time).
- Putting a licence that is not OSI- or FSF-approved in `PackageLicenseExpression`: nuget.org rejects the package.
- Publishing, tagging or creating a GitHub release; that is the owner's step.

## Working mindset

- Read the code around a change first and match it: naming, comment density, partial-class layout, error handling.
- Prefer the smallest change that fixes the cause, and keep a pull request to one change.
- Treat byte-identical output as a feature: callers cache and compare PDFs, so any output change is deliberate and
  documented.
- Measure before claiming a speed-up, on one machine, against the base version.
- When a browser and PdfPecker disagree, Chromium is the usual reference for layout and HarfBuzz for shaping.
- Write down what you checked: the tests run, the baselines changed, the benchmark compared.

## Commit guidance

- Branch from the latest `origin/main`, one branch per piece of work.
- Name the branch after the work it carries, in lowercase words joined by hyphens, with a `claude/` prefix when an
  agent creates it (e.g. `claude/barcode-qr-codes`). Never use a generic or generated name such as
  `claude/project-thread-xxxx`, even when a tool suggests one. Don't rename a branch that already has an open pull
  request.
- Never reuse a branch name. Once a pull request is merged or closed, its branch is finished: follow-up work goes on a
  new branch with a new name, even when it continues the same feature.
- Commit subjects are short imperative or descriptive sentences without prefixes, e.g. "Right-to-left text in SVG and
  RTL ellipsis".
- The body explains why, and names any golden baselines that changed and the reason.
- Fill in `.github/pull_request_template.md`; CI must be green before review.

## Wisdom

- Code comments and docs are plain, full sentences explaining why, not what.
- `GoldenPdfTests` renders every golden under ar-SA and fa-IR, whose minus and plus signs differ from ASCII; that is
  where culture bugs show up.
- Cloud VMs and GitHub runners differ too much in CPU to compare a single benchmark run with an earlier one; only
  version-against-version runs on one machine are meaningful.
- The fuzzer has no coverage feedback. It finds hangs, huge allocations, deep recursion and wrong exception types, not
  logic bugs; damaged-input limits belong in the decoder's own work budget, counted before allocating.
- `SOURCE_DATE_EPOCH` or a fixed `CreationDate` makes output byte-identical, which is what makes golden tests and
  callers' output caches possible; signed and encrypted output stay unique on purpose.
- The website (PdfPecker.Web) uses the NuGet package, so a library feature reaches pdfpecker.net only after a release
  and a `PackageReference` bump there.
- In the fluent package, a table cell's painted content fills the row as in QuestPDF through `height: 1px` on the cell
  and `height: 100%` on the box inside it: as in Chrome, a percentage height resolves against a cell only when the
  cell has a height of its own. Cells with an alignment keep their content's size.
- Fluent page numbers are PdfPecker's `{page}` and `{total-pages}` tokens in the sticky header and footer. Caller text
  with braces in those bands is written so the library leaves it alone. In the content, a page number is a span that
  refers to its own id with `target-counter()`, and the page count is `counter(pages)`. Section page numbers use
  `-pdfpecker-target-pages()` in the content and the `{section-page:name}` token in the bands.
- Several fluent `Page` calls become named pages (`page: p0`, `p1`, ...) of one HTML document, each with its own
  `@page` rule and bands marked `data-sticky-pdf-page`, so page numbers, links, PDF/A, PDF/UA and signatures work across
  them. A single `Page` call writes no page names, which keeps its output as it was.
- The fluent golden PDFs are compared after inflating streams, because .NET 9's zlib compresses differently from .NET 8
  and 10.
- The QR and Code 128 encoders dominate the fluent layer's own time in documents that have barcodes.
- Fluent `Unconstrained` is a box of no size rather than `position: absolute`, because a positioned box paints over
  the in-flow content after it, and QuestPDF draws that content on top.
- `Microsoft.AspNetCore.Components` reports trim warnings from its own code (it reads component members through
  `GetType()`), so the Razor sample folds only that assembly's warnings into one ignored `IL2104` for the AOT publish;
  `@layout` and `LayoutView` warn in the caller's project, so the sample's layout is a component with `ChildContent`.
- The tool's per-platform packages are a .NET 10 SDK feature: .NET 8 and 9 SDKs refuse a package whose settings file
  names platform packages, so only the package's net10.0 folder does, and its net8.0 and net9.0 folders keep the
  framework-dependent tool. SDK 10.0.4xx writes that file to `tools/any/any`, where a .NET 10 SDK would prefer the net9.0
  folder, so a target in the project moves it to `tools/net10.0/any`. Alpine needs the musl packages: without them the SDK picks `linux-x64`, whose executable
  does not run there. The native tool writes slightly larger PDFs than a Linux distribution's own .NET build, because
  Native AOT links zlib-ng as Microsoft's runtime builds do; the inflated content is identical.
