# PdfPecker Markdown guide

PdfPecker turns **Markdown** into HTML and PDF with its own parser: *CommonMark* plus the
GitHub extensions. No browser, ~~no native binaries~~, no third-party packages.

![PdfPecker logo](logo.png)

## Contents

1. [Text](#text)
2. [Lists](#lists)
3. [Code](#code)
4. [Tables](#tables)

## Text

Paragraphs wrap automatically. End a line with two spaces  
for a hard break. Inline `code`, [links](https://github.com/Moonwalker1307/PdfPecker),
bare URLs like https://example.com and e-mail addresses like hello@example.com all work.

> Block quotes can hold *any* other block,
> including lists:
>
> - one
> - two

---

## Lists

- Bullets with `-`, `*` or `+`
  - nested
    - and nested again
- Loose and tight lists

3. Ordered lists can start anywhere
4. Like this one

### Tasks

- [x] Write the parser
- [x] Render to PDF
- [ ] Take over the world

## Code

```csharp
var pdf = MarkdownToPdf.Convert("# Hello\n\nWorld");
File.WriteAllBytes("hello.pdf", pdf);
```

    Indented code blocks work too.

## Tables

| Feature        | Supported | Notes                 |
|:---------------|:---------:|----------------------:|
| Headings       | yes       | `#` and underlined    |
| Emphasis       | yes       | `*`, `_`, `~~`        |
| Tables         | yes       | with alignment        |
| Footnotes      | no        | maybe later           |

Raw <abbr title="HyperText Markup Language">HTML</abbr> passes through, so you can still use a
<span style="color: #c53030">red word</span> when you need one.
