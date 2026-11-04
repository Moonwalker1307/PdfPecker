# Releasing PdfPecker

Only the repository owner publishes a release. Agents and contributors prepare the pull requests around it, but never
create tags or GitHub releases.

A release publishes three packages from this repository, PdfPecker, PdfPecker.Fluent and PdfPecker.Razor, always with
the same version. PdfPecker.Fluent and PdfPecker.Razor depend on the PdfPecker package of their own version, as a
minimum. PdfPecker.Razor also depends on Microsoft's `Microsoft.AspNetCore.Components.Web`, the release of each target
framework's .NET version.

## Versions

- Semantic versioning. New features are minor versions, fixes are patches, and a breaking change to the public API
  belongs only in a major version. No breaking changes are planned before 2.0.
- Package validation enforces this: `dotnet pack` compares the public API with `PackageValidationBaselineVersion` in
  `src/PdfPecker/PdfPecker.csproj` and fails on a break. A deliberate break, in a major version only, needs a
  `CompatibilitySuppressions.xml` entry.
- Local builds pack as `<next patch>-dev` (the `Version` in `Directory.Build.props`, which all packages share). CI builds of main and pull requests
  are `<next patch>-preview.<run number>`, computed from the latest `v*` tag, so they sort between two releases.

## Before a release

1. Make sure main is green in `nuget.yml` and `conformance.yml`.
2. Check that `Microsoft.AspNetCore.Components.Web` in `src/PdfPecker.Razor/PdfPecker.Razor.csproj` is on the latest
   patch of each .NET version (Dependabot proposes those patches weekly), with a line for every target framework the
   library has.
3. Write the release notes: what is new, and a separate list of intended output changes, since callers who compare
   PDFs byte for byte need to know.

## Publishing

1. The owner creates a GitHub release with a tag `vX.Y.Z` (or `vX.Y.Z-suffix` for a pre-release) and the notes.
2. `nuget.yml` builds and tests with that version, attaches the three `.nupkg` files to the release, and pushes them to
   nuget.org with Trusted Publishing: `NuGet/login` swaps the workflow's OIDC token for a short-lived key, in the
   `nuget` environment with the `NUGET_USER` repository variable. There is no stored API key. The Trusted Publishing
   policy covers every package its owner owns, so PdfPecker.Fluent and PdfPecker.Razor need no policy of their own; a
   package's first push creates it on nuget.org under that account.

## After a release

Open one pull request on the library that:

- raises `PackageValidationBaselineVersion` to the new version in `src/PdfPecker/PdfPecker.csproj`,
  `src/PdfPecker.Fluent/PdfPecker.Fluent.csproj` and `src/PdfPecker.Razor/PdfPecker.Razor.csproj` (the Razor package
  gets the line after its first release; until then package validation checks only its target frameworks), and
  `Version` in `Directory.Build.props` to the next patch's `-dev` (CI warns while the library's baseline lags behind
  the latest tag);
- updates the version placeholder in `.github/ISSUE_TEMPLATE/bug_report.yml`;
- updates `README.md`: the DLL size in the comparison table and the release in the `bench/compare-versions.sh`
  example.

Once nuget.org serves the new version, raise `PdfPeckerVersion` in `Directory.Build.props` of
[PdfPecker.Benchmarks](https://github.com/Moonwalker1307/PdfPecker.Benchmarks), which measures the released package.

Then update the website repository, PdfPecker.Web, which uses the NuGet package: bump its `PackageReference`, add the
version's docs and changelog entry, and refresh any feature cards, the compare page or converter settings the release
affects. The site gets library changes only this way.
