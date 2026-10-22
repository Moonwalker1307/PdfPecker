#!/usr/bin/env bash
# Benchmarks PdfPecker from the working tree against another version (a tag, branch or commit; the latest release tag
# by default) on this machine, running the two in turns. Usage: bench/compare-versions.sh [git-ref] [rounds]
# PDFPECKER_BENCH_WARMUP=15 warms up for 15 seconds instead of 3, so that JIT tiering has finished in both builds.
set -euo pipefail
repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
ref=${1:-$(git -C "$repo" describe --tags --abbrev=0 origin/main 2>/dev/null || git -C "$repo" describe --tags --abbrev=0)}
rounds=${2:-3}
other=$(mktemp -d)
trap 'git -C "$repo" worktree remove --force "$other" >/dev/null 2>&1 || rm -rf "$other"' EXIT

git -C "$repo" worktree add --detach "$other" "$ref" >/dev/null
# The other version is measured with this version's benchmark program, so both run exactly the same code around the
# conversion; only the library differs.
rm -rf "$other/bench/PdfPecker.Versions"
cp -r "$repo/bench/PdfPecker.Versions" "$other/bench/PdfPecker.Versions"
rm -rf "$other/bench/PdfPecker.Versions/bin" "$other/bench/PdfPecker.Versions/obj"
cp "$repo/bench/PdfPecker.Benchmarks/Documents.cs" "$other/bench/PdfPecker.Benchmarks/Documents.cs"

dotnet build -c Release "$other/bench/PdfPecker.Versions" -v q -nologo >&2
dotnet build -c Release "$repo/bench/PdfPecker.Versions" -v q -nologo >&2
# The ref is escaped before it goes into the sed replacements, because a branch such as origin/main or claude/foo
# carries a slash that would otherwise end the s command.
label=$(printf '%s' "$ref" | sed 's/[\\/|&]/\\&/g')
dotnet "$repo/bench/PdfPecker.Versions/bin/Release/net10.0/PdfPecker.Versions.dll" versions \
  "$other/bench/PdfPecker.Versions/bin/Release/net10.0/PdfPecker.Versions.dll" "$rounds" |
  sed "s|other = .*|other = $label|; s/Other build/$label/; s/This build/working tree/"
