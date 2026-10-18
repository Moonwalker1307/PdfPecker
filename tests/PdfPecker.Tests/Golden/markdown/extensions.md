---
title: Markdown extensions
author: PdfPecker
---
# Footnotes and definition lists

Footnotes[^note] go to the bottom of the page where they are referenced, numbered in the order of first use.
A footnote can hold several blocks[^blocks], and a second reference[^note] links to the same note.

[^note]: A short footnote, which cites another[^inner].
[^blocks]: The first paragraph of a longer footnote.

    Its second paragraph, indented by four spaces, and a list:

    - one
    - two
[^inner]: A footnote that only another footnote cites is listed after the text.

## Glossary

CommonMark
: The Markdown specification that PdfPecker follows.

GFM
: GitHub Flavored Markdown, which adds tables, task lists, strikethrough, autolinks and footnotes.

Front matter
:   The YAML block at the start of this file. It is not rendered, and its `title` becomes the PDF title.

    A definition can have several paragraphs.
