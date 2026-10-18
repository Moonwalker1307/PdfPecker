---
title: Project report
author: MyApp team
---
# Project report

This report is written in Markdown and uses most of the syntax PdfPecker supports: headings, emphasis,
**strong text**, `inline code`, ~~strikethrough~~, links such as <https://example.com>, footnotes[^method]
and definition lists.

## Summary

| Area        | Status      | Owner   |  Done |
|:------------|:-----------:|---------|------:|
| Parser      | Complete    | Alice   |  100% |
| Layout      | In progress | Bob     |   80% |
| Rendering   | Planned     | Carol   |   10% |
| Packaging   | Complete    | Dave    |  100% |

> **Note**
> Figures are estimates[^estimates]. A quote can hold several paragraphs.
>
> - and lists
> - with **formatting**

## Tasks

- [x] Write the parser
- [x] Add tables
- [ ] Hyphenation for more languages
  - [ ] Spanish
  - [x] Swedish
- [ ] Release 2.0

1. First step
2. Second step, which has a long description that wraps onto a second line to show how list items wrap
   1. A nested step
   2. Another nested step
3. Third step

## Code

```csharp
var options = new PdfOptions { Title = "Report" };
byte[] pdf = HtmlToPdf.Convert(html, options);
File.WriteAllBytes("report.pdf", pdf);
```

    An indented code block
    keeps its spacing.

## Terms

Golden test
: A test that compares output with a saved baseline file.

Baseline
: The expected output.
: A second definition of the same term.

***

## Long section

Paragraph 1. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam.

Paragraph 2. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam.

Paragraph 3. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam.

Paragraph 4. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam.

Paragraph 5. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam.

Paragraph 6. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam.

Paragraph 7. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam.

Paragraph 8. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam.

Paragraph 9. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam.

Paragraph 10. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam.

Paragraph 11. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Integer nec odio praesent libero sed cursus ante dapibus diam.


Final paragraph with a footnote at the end[^end].

[^method]: The method is described in the appendix.
[^estimates]: Estimates were made on 1 October.
[^end]: The last footnote, on the last page.
