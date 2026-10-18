# Releasing PdfPecker

Only the repository owner publishes a release. Agents and contributors prepare the pull requests around it, but never
create tags or GitHub releases.

## Versions

- Semantic versioning. New features are minor versions, fixes are patches, and a breaking change to the public API
  belongs only in a major version. No breaking changes are planned before 2.0.
- Package validation enforces this: `dotnet pack` compares the public API with `PackageValidationBaselineVersion` in
  `src/PdfPecker/PdfPecker.csproj` and fails on a break. A deliberate break, in a major version only, needs a
  `CompatibilitySuppressions.xml` entry.
- Local builds pack as `<next patch>-dev` (the `Version` in the project file). CI builds of main and pull requests
  are `<next patch>-preview.<run number>`, computed from the latest `v*` tag, so they sort between two releases.

## Before a release

1. Make sure main is green in `nuget.yml` and `conformance.yml`.
2. Write the release notes: what is new, and a separate list of intended output changes, since callers who compare
   PDFs byte for byte need to know.

## Publishing

1. The owner creates a GitHub release with a tag `vX.Y.Z` (or `vX.Y.Z-suffix` for a pre-release) and the notes.
2. `nuget.yml` builds and tests with that version, attaches the `.nupkg` to the release, and pushes it to nuget.org
   with Trusted Publishing: `NuGet/login` swaps the workflow's OIDC token for a short-lived key, in the `nuget`
   environment with the `NUGET_USER` repository variable. There is no stored API key.

## After a release

Open one pull request on the library that:

- raises `PackageValidationBaselineVersion` to the new version and `Version` to the next patch's `-dev`
  (CI warns while the baseline lags behind the latest tag);
- updates the version placeholder in `.github/ISSUE_TEMPLATE/bug_report.yml`;
- updates `README.md`: the DLL size in the comparison table and the release in the `bench/compare-versions.sh`
  example.

Then update the website repository, PdfPecker.Web, which uses the NuGet package: bump its `PackageReference`, add the
version's docs and changelog entry, and refresh any feature cards, the compare page or converter settings the release
affects. The site gets library changes only this way.
