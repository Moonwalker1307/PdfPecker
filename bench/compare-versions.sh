#!/usr/bin/env bash
# Benchmarks PdfPecker from the working tree against another version (a tag, branch or commit; the latest release tag
# by default) on this machine, running the two in turns. Usage: bench/compare-versions.sh [git-ref] [rounds]
set -euo pipefail
repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
ref=${1:-$(git -C "$repo" describe --tags --abbrev=0 origin/main 2>/dev/null || git -C "$repo" describe --tags --abbrev=0)}
rounds=${2:-3}
other=$(mktemp -d)
trap 'git -C "$repo" worktree remove --force "$other" >/dev/null 2>&1 || rm -rf "$other"' EXIT

git -C "$repo" worktree add --detach "$other" "$ref" >/dev/null
# The other version is measured with this version's benchmark program, so both run exactly the same code around the
# conversion; only the library differs.
rm -rf "$other/bench/PdfPecker.Comparison"
cp -r "$repo/bench/PdfPecker.Comparison" "$other/bench/PdfPecker.Comparison"
rm -rf "$other/bench/PdfPecker.Comparison/bin" "$other/bench/PdfPecker.Comparison/obj"
cp "$repo/bench/PdfPecker.Benchmarks/Documents.cs" "$other/bench/PdfPecker.Benchmarks/Documents.cs"

dotnet build -c Release "$other/bench/PdfPecker.Comparison" -v q -nologo >&2
dotnet build -c Release "$repo/bench/PdfPecker.Comparison" -v q -nologo >&2
dotnet "$repo/bench/PdfPecker.Comparison/bin/Release/net10.0/PdfPecker.Comparison.dll" versions \
  "$other/bench/PdfPecker.Comparison/bin/Release/net10.0/PdfPecker.Comparison.dll" "$rounds" | sed "s|other = .*|other = $ref|; s/Other build/$ref/; s/This build/working tree/"
