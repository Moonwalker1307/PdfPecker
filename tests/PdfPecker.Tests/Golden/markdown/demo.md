# Markdown sample

A paragraph with *emphasis*, **strong text**, `inline code`, ~~strikethrough~~ and a [link](https://example.com).
A hard line break follows  
this line.

## Lists

1. First item
2. Second item with a nested list:
   - nested bullet
   - another one
3. Third item

- [x] A finished task
- [ ] An open task

## Table

| Product | Category | Qty | Price |
|:--------|:--------:|----:|------:|
| Widget  | Tools    |   3 | 12.50 |
| Gadget  | Toys     |  12 |  4.99 |

## Code and quotes

```csharp
var pdf = MarkdownToPdf.Convert(File.ReadAllText("README.md"));
File.WriteAllBytes("readme.pdf", pdf);
```

> A blockquote with **bold** text,
> over two lines.

---

Setext heading
--------------

Text after a thematic break, with an autolink: <https://pdfpecker.net>.
