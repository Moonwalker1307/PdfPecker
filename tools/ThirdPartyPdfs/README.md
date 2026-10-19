# Third-party PDF fixtures

`generate.sh` writes the PDFs in `tests/PdfPecker.Tests/Fixtures/third-party` with tools other than PdfPecker, so
`ThirdPartyPdfTests` can check rendering, text extraction, merging, compacting, stamping, form filling and decryption
on files PdfPecker did not write. The sources are in this folder; the PDFs are checked in because their bytes depend
on the tool versions, and CI does not install these tools.

| Fixture | Made by | What it covers |
| --- | --- | --- |
| `chromium.pdf` | Chromium 141 printing `chromium.html` | TrueType subsets, Greek and Cyrillic fallback fonts, gradients, SVG, a link |
| `chromium-qpdf.pdf` | qpdf 11.9 rewriting `chromium.pdf` | object streams, cross-reference stream, linearization |
| `libreoffice.pdf` | LibreOffice Writer 24.2 exporting `libreoffice.html` | tagged PDF, bookmarks, AcroForm text, check box and list fields |
| `libreoffice-rc4-128.pdf` | qpdf, from `libreoffice.pdf` | standard security handler revision 3 (RC4, 128-bit); passwords `user` and `owner` |
| `libreoffice-aes256.pdf` | qpdf, from `libreoffice.pdf` | revision 6 (AES-256); passwords `user` and `owner` |
| `reportlab.pdf` | ReportLab 5.0 running `reportlab_doc.py` | standard 14 fonts not embedded (including Symbol and ZapfDingbats), shading, inline image, outline |
| `ghostscript.pdf` | Ghostscript 10.02 `pdfwrite` of `ghostscript.ps` | CFF font subsets, a Type 2 shading, an image with a decode matrix |

All text in the fixtures is written for this purpose. The embedded fonts are subsets of fonts that allow it: Liberation
(SIL OFL 1.1) and DejaVu (Bitstream Vera licence) in the Chromium and LibreOffice files, which PdfPecker also bundles,
and the URW base 35 fonts in the Ghostscript file, whose licence (AGPL 3.0 with a font exception) allows embedding them
in a PDF regardless of the document's own licence.

To regenerate on Ubuntu 24.04: `apt install libreoffice-writer ghostscript qpdf python3-reportlab python3-pil`, a
Chromium build (the script uses Playwright's in `/opt/pw-browsers` unless `CHROME` is set), then run `./generate.sh`
and refresh the baselines as `TESTING.md` describes.
