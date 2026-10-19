#!/usr/bin/env bash
# Regenerates the third-party PDF fixtures in tests/PdfPecker.Tests/Fixtures/third-party from the sources in this
# folder, with tools other than PdfPecker. The tests use them as inputs (rendering, text extraction, merge, stamp,
# compact, forms and decryption), so a change here changes their baselines too: regenerate those afterwards with
# PDFPECKER_UPDATE_GOLDEN=1 (see TESTING.md).
#
# Needs: Chromium (CHROME, default the Playwright build), LibreOffice (soffice), Python 3 with ReportLab and Pillow,
# Ghostscript (gs) and qpdf. The outputs depend on the tool versions, so they are checked in rather than built in CI;
# the versions used last are listed in README.md next to this script.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="$here/../../tests/PdfPecker.Tests/Fixtures/third-party"
chrome="${CHROME:-$(ls -d /opt/pw-browsers/chromium-*/chrome-linux/chrome 2>/dev/null | head -1)}"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$out"

# Fixed dates, so a rerun with the same tool versions changes as little as possible.
export SOURCE_DATE_EPOCH=1767225600 # 2026-01-01T00:00:00Z

"$chrome" --headless --no-sandbox --disable-gpu --no-pdf-header-footer --run-all-compositor-stages-before-draw \
  --print-to-pdf="$out/chromium.pdf" "file://$here/chromium.html" 2>/dev/null

cp "$here/libreoffice.html" "$work/"
soffice --headless -env:UserInstallation="file://$work/profile" --infilter="HTML (StarWriter)" \
  --convert-to 'pdf:writer_pdf_Export:{"UseTaggedPDF":{"type":"boolean","value":"true"},"ExportFormFields":{"type":"boolean","value":"true"},"ExportBookmarks":{"type":"boolean","value":"true"}}' \
  --outdir "$work" "$work/libreoffice.html"
cp "$work/libreoffice.pdf" "$out/libreoffice.pdf"

python3 "$here/reportlab_doc.py" "$out/reportlab.pdf"

gs -q -dNOPAUSE -dBATCH -dSAFER -sDEVICE=pdfwrite -dCompatibilityLevel=1.7 -sOutputFile="$out/ghostscript.pdf" \
  "$here/ghostscript.ps"

# The same Writer document behind the older and newer standard security handlers, and Chromium's PDF rewritten with
# object streams, a cross-reference stream and linearization.
qpdf --allow-weak-crypto --encrypt --user-password=user --owner-password=owner --bits=128 --use-aes=n -- \
  "$out/libreoffice.pdf" "$out/libreoffice-rc4-128.pdf"
qpdf --encrypt --user-password=user --owner-password=owner --bits=256 -- \
  "$out/libreoffice.pdf" "$out/libreoffice-aes256.pdf"
qpdf --object-streams=generate --linearize --deterministic-id "$out/chromium.pdf" "$out/chromium-qpdf.pdf"

ls -l "$out"
