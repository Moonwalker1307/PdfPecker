# PdfPecker.Cli

The `pdfpecker` command-line tool: HTML and Markdown to PDF, and the everyday jobs on existing PDFs. It is a thin layer
over the [PdfPecker](https://www.nuget.org/packages/PdfPecker) library, so it needs no browser, no JavaScript engine and no
native binaries, and it never fetches anything over the network.

```
dotnet tool install -g PdfPecker.Cli
pdfpecker convert invoice.html            # writes invoice.pdf
pdfpecker watch invoice.html              # converts again on every save
pdfpecker merge a.pdf b.pdf:1-3 -o all.pdf
```

Run `pdfpecker help` for the commands and `pdfpecker help <command>` for the options of one.

## Commands

| Command | What it does |
| --- | --- |
| `convert` | HTML or Markdown file (or `-` for standard input) to PDF |
| `watch` | `convert`, again each time the file, its folder or the style sheet changes |
| `merge`, `split`, `extract`, `remove`, `rotate` | join, cut, keep, drop and turn pages |
| `stamp` | lay a PDF or an HTML page over or under every page |
| `compact`, `linearize`, `sanitize` | smaller files, Fast Web View, no scripts or launch actions |
| `sign`, `verify`, `encrypt`, `decrypt` | PAdES signatures, AES-256 passwords |
| `to-image`, `to-html`, `text`, `info` | PNG/JPEG/WebP/TIFF, one HTML file, plain text, a summary |

`-` stands for standard input or output, so PDFs can be piped. Progress and errors go to standard error. The exit code is
0 on success, 1 when the work failed (a damaged file, a wrong password, an invalid signature) and 2 when the command line
is wrong.

## Replacing wkhtmltopdf

A call without a command word is read like wkhtmltopdf's, with its 10 mm default margins:

```
pdfpecker -s A4 -O Landscape -T 15mm --footer-center "Page [page] of [topage]" in.html out.pdf
```

The page size and orientation, margins, `--title`, `--zoom`, `--disable-smart-shrinking`, `--user-style-sheet`,
`--no-outline`, `--no-pdf-compression`, `--enable-local-file-access`, `-q` and the `--header-left/-center/-right` and
`--footer-left/-center/-right` texts (with `[page]`, `[topage]`, `[date]`, `[time]` and `[title]`) work as they do there.
Options that exist only because wkhtmltopdf runs a browser (`--javascript-delay`, `--cookie`, `--header-html`,
`--grayscale`, ...) are accepted and ignored with a warning, so a script keeps running. Web addresses are refused: save the
page to a file first.

## Signing

```
export PDFPECKER_CERT_PASSWORD=...
pdfpecker sign contract.pdf signed.pdf --cert me.pfx --reason "I approve" --page 1 --rect 36,36,200,60
pdfpecker verify signed.pdf --trusted-root ca.cer
```

Passwords given on the command line are visible to other users of the machine; prefer the `-env` options and
`PDFPECKER_CERT_PASSWORD`.

## License

MIT. The tool is released with every PdfPecker release under the same version.
