# Building PdfPecker

## Requirements

- The .NET 11 SDK, which the `net11.0` target needs. `global.json` pins `11.0.100` with `rollForward: latestFeature`,
  so any 11.0 feature band works.
- The .NET 8, 9 and 10 runtimes to run the tests on every target framework, as CI does. With only .NET 11 installed,
  pass `-f net11.0`.
- Nothing else: the library, tests, fuzzer, sample and generators need no packages beyond the test framework, and
  no native libraries.

## Commands

```
dotnet build -c Release                                         # library, tests, fuzzer, sample, benchmarks, tools
dotnet test -c Release --no-build                               # all tests on net8.0 to net11.0
dotnet test -f net10.0 --filter "FullyQualifiedName~MarkdownTests"   # one class, one runtime
dotnet pack src/PdfPecker -c Release -o ./nupkg                 # the package, with API validation
dotnet pack src/PdfPecker.Fluent -c Release -o ./nupkg          # PdfPecker.Fluent, with the same version
dotnet pack src/PdfPecker.Testing -c Release -o ./nupkg         # PdfPecker.Testing, with the same version
dotnet run --project samples/PdfPecker.Sample -c Release        # converts the sample HTML and Markdown into ./output
dotnet publish samples/PdfPecker.Sample -c Release -p:PublishAot=true -o aot   # Native AOT, as CI checks it
dotnet run --project samples/PdfPecker.Fluent.Sample -c Release -- output   # writes the fluent examples as PDF and HTML
dotnet run --project src/PdfPecker.Cli -c Release -- convert in.html out.pdf   # the pdfpecker command-line tool
dotnet pack src/PdfPecker.Cli -c Release -o ./nupkg                 # the dotnet tool package, same version as the others
dotnet pack src/PdfPecker.Cli -c Release -r linux-x64 -o ./nupkg    # its Native AOT package for this machine's platform
dotnet pack src/PdfPecker.Cli -c Release -r any -o ./nupkg          # and the framework-dependent fallback
dotnet run --project samples/PdfPecker.Razor.Sample -c Release -- output    # writes the Razor invoice as PDF and HTML
```

## What breaks the build

- **Warnings are errors.** `Directory.Build.props` turns on `TreatWarningsAsErrors` and nullable reference types for
  every project.
- **Missing XML doc comments.** The library generates documentation, so a public member without a comment is CS1591,
  an error.
- **Trimming and AOT analyzers.** `IsTrimmable` and `IsAotCompatible` are on, so reflection-based code shows up as a
  warning, and therefore an error.
- **File-scoped namespaces** are required by `.editorconfig` (`csharp_style_namespace_declarations = file_scoped:warning`).
- **Breaking API changes.** `dotnet pack` runs package validation against `PackageValidationBaselineVersion`; see
  `RELEASING.md`.

## Target frameworks

The libraries and tests target `net8.0`, `net9.0`, `net10.0` and `net11.0`. APIs that .NET 8 lacks, such as `Dictionary`
alternate lookups and span `Split`, go behind `#if NET9_0_OR_GREATER` with a .NET 8 path next to them.

The next .NET is tried before it ships: `-p:PreviewTargetFramework=net12.0` adds it as an extra target to the library
and tests, and `.github/workflows/dotnet-preview.yml` builds and tests that on the latest .NET 12 preview SDK once one
is out. The released package gets the new target only once that .NET is generally available.

The command-line tool stays on `net8.0`, `net9.0` and `net10.0`: its Native AOT packages are named in the package's
`net10.0` folder, which a .NET 11 SDK also reads, and a `net11.0` folder would make a .NET 11 SDK install a
framework-dependent build instead.

## Line endings

`.gitattributes` checks text out with LF on every platform and marks fonts, images, PDFs and compressed data as
binary, so tests that compare text or bytes behave the same on Windows.
