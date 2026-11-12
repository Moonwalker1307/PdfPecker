#!/usr/bin/env bash
# Runs Stryker.NET mutation testing on one area of the library, with only that area's tests.
#
#   tools/MutationTesting/run.sh <area> [output directory]
#
# It edits the working tree (one target framework, and a "Stryker disable all" comment at the top of every library
# file outside the area), so run it in CI or in a scratch clone, and `git checkout -- .` afterwards. Stryker's
# --mutate option only filters the report: without the comments every mutant of the library is compiled in, and the
# coverage pass over hot loops elsewhere (layout, the barcode reader) takes hours instead of a minute.
set -euo pipefail

area="${1:-}"
out="${2:-$PWD/mutation-report}"
root="$(cd "$(dirname "$0")/../.." && pwd)"

# Each area names the library paths to mutate (relative to src/PdfPecker) and the tests that cover them. The tests are
# a filter for speed, so a mutant that only another test class kills shows as survived; read survivors with that in mind.
case "$area" in
  markdown)   paths="Markdown/";                                           filter="FullyQualifiedName~Markdown" ;;
  css-values) paths="Css/CssMath.cs Css/ColorFunctions.cs Css/CssColor.cs Css/CssUnits.cs"
              filter="FullyQualifiedName~Css|FullyQualifiedName~Color|FullyQualifiedName~Unit" ;;
  barcodes)   paths="Barcodes/";                                           filter="FullyQualifiedName~Barcode" ;;
  text)       paths="Text/Bidi.cs Text/LineBreaker.cs Text/Hyphenator.cs Text/JustificationCharacters.cs"
              filter="FullyQualifiedName~Bidi|FullyQualifiedName~LineBreak|FullyQualifiedName~TextWrap|FullyQualifiedName~Hyphenation|FullyQualifiedName~Justify" ;;
  *) echo "Unknown area '$area'; use markdown, css-values, barcodes or text." >&2; exit 2 ;;
esac

cd "$root"
sed -i 's#<TargetFrameworks>[^<]*</TargetFrameworks>#<TargetFrameworks>net10.0</TargetFrameworks>#' \
  src/PdfPecker/PdfPecker.csproj tests/PdfPecker.Tests/PdfPecker.Tests.csproj

mutate=()
cd src/PdfPecker
while IFS= read -r -d '' file; do
  rel="${file#./}"
  inside=false
  for p in $paths; do
    case "$rel" in "$p"*) inside=true ;; esac
  done
  if [ "$inside" = false ]; then
    sed -i '1i // Stryker disable all' "$file"
  fi
done < <(find . -name '*.cs' -not -path './obj/*' -not -path './bin/*' -print0)
for p in $paths; do
  case "$p" in */) mutate+=(--mutate "**/${p}**/*.cs") ;; *) mutate+=(--mutate "**/$p") ;; esac
done

cd "$root/tests/PdfPecker.Tests"
dotnet stryker --project PdfPecker.csproj "${mutate[@]}" --test-case-filter "$filter" \
  --configuration Release --concurrency "${STRYKER_CONCURRENCY:-3}" --break-at 0 \
  --reporter html --reporter json --reporter markdown --output "$out" --skip-version-check
