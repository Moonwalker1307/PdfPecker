## What changes

<!-- What a user of the library sees before and after this change. Link the issue it fixes, if any. -->

## How

<!-- A short note on the approach, if it isn't obvious from the diff. -->

## Checklist

- [ ] `dotnet build -c Release` and `dotnet test -c Release --no-build` pass
- [ ] No new `PackageReference` in `src/PdfPecker`, and no reflection
- [ ] Golden baselines unchanged, or regenerated on purpose and listed here with the reason
- [ ] New public members have XML doc comments
- [ ] Bug fixes for malformed input have a regression test in `HostileInputTests`
- [ ] `README.md` updated if a feature was added or changed
