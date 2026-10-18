using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

public class MarkdownTests
{
    private static readonly MarkdownOptions Plain = new() { HeadingIds = false };

    private static string Html(string markdown, MarkdownOptions? options = null)
        => MarkdownToHtml.Convert(markdown, options ?? Plain);

    [Theory]
    [InlineData("# One", "<h1>One</h1>\n")]
    [InlineData("###### Six ######", "<h6>Six</h6>\n")]
    [InlineData("Title\n=====", "<h1>Title</h1>\n")]
    [InlineData("Sub\n---", "<h2>Sub</h2>\n")]
    [InlineData("*em* **strong** ***both***", "<p><em>em</em> <strong>strong</strong> <em><strong>both</strong></em></p>\n")]
    [InlineData("snake_case_word", "<p>snake_case_word</p>\n")]
    [InlineData("a `b <c>` d", "<p>a <code>b &lt;c&gt;</code> d</p>\n")]
    [InlineData("line one  \nline two", "<p>line one<br />\nline two</p>\n")]
    [InlineData("\\*not em\\*", "<p>*not em*</p>\n")]
    [InlineData("&copy; &#169; &#xA9;", "<p>© © ©</p>\n")]
    [InlineData("***", "<hr />\n")]
    [InlineData("> quote", "<blockquote>\n<p>quote</p>\n</blockquote>\n")]
    [InlineData("    code", "<pre><code>code\n</code></pre>\n")]
    public void Converts_core_commonmark(string markdown, string expected)
        => Assert.Equal(expected, Html(markdown));

    [Fact]
    public void Tight_and_loose_lists()
    {
        Assert.Equal("<ul>\n<li>a</li>\n<li>b</li>\n</ul>\n", Html("- a\n- b"));
        Assert.Equal("<ul>\n<li>\n<p>a</p>\n</li>\n<li>\n<p>b</p>\n</li>\n</ul>\n", Html("- a\n\n- b"));
        Assert.Equal("<ol start=\"3\">\n<li>three</li>\n<li>four</li>\n</ol>\n", Html("3. three\n4. four"));
    }

    [Fact]
    public void Nested_lists()
    {
        Assert.Equal("<ul>\n<li>a\n<ul>\n<li>b</li>\n</ul>\n</li>\n</ul>\n", Html("- a\n  - b"));
    }

    [Fact]
    public void Fenced_code_keeps_language_and_escapes()
    {
        Assert.Equal("<pre><code class=\"language-cs\">if (a &lt; b) { }\n</code></pre>\n", Html("```cs\nif (a < b) { }\n```"));
        Assert.Equal("<pre><code>~~~ still code\n</code></pre>\n", Html("~~~~\n~~~ still code\n~~~~"));
    }

    [Fact]
    public void Links_images_and_references()
    {
        Assert.Equal("<p><a href=\"/url\" title=\"t\">x</a></p>\n", Html("[x](/url \"t\")"));
        Assert.Equal("<p><a href=\"https://a.b/c%20d\">ref</a></p>\n", Html("[ref]\n\n[REF]: <https://a.b/c d>"));
        Assert.Equal("<p><img src=\"cat.png\" alt=\"a cat\" /></p>\n", Html("![a *cat*](cat.png)"));
        Assert.Equal("<p><a href=\"https://x.org\">https://x.org</a></p>\n", Html("<https://x.org>"));
        Assert.Equal("<p><a href=\"mailto:me@x.org\">me@x.org</a></p>\n", Html("<me@x.org>"));
    }

    [Fact]
    public void Raw_html_passes_through_unless_disabled()
    {
        Assert.Equal("<div class=\"x\">\n<p>hi</p>\n</div>\n", Html("<div class=\"x\">\n\nhi\n\n</div>"));
        Assert.Equal("<p>a <span>b</span></p>\n", Html("a <span>b</span>"));
        var noHtml = new MarkdownOptions { HeadingIds = false, AllowRawHtml = false };
        Assert.Equal("<p>a &lt;b&gt;x&lt;/b&gt;</p>\n", Html("a <b>x</b>", noHtml));
    }

    [Fact]
    public void Gfm_table_with_alignment_and_escaped_pipe()
    {
        var html = Html("| a | b | c |\n|:--|:-:|--:|\n| 1 | `x\\|y` | 3 |\n| short |");
        Assert.Equal(
            "<table>\n<thead>\n<tr>\n<th align=\"left\">a</th>\n<th align=\"center\">b</th>\n<th align=\"right\">c</th>\n</tr>\n</thead>\n" +
            "<tbody>\n<tr>\n<td align=\"left\">1</td>\n<td align=\"center\"><code>x|y</code></td>\n<td align=\"right\">3</td>\n</tr>\n" +
            "<tr>\n<td align=\"left\">short</td>\n<td align=\"center\"></td>\n<td align=\"right\"></td>\n</tr>\n</tbody>\n</table>\n",
            html);
    }

    [Fact]
    public void Table_needs_matching_header_and_delimiter_row()
    {
        Assert.Equal("<p>| a | b |\n| --- |</p>\n", Html("| a | b |\n| --- |"));
        // Without any pipe, "---" under text is a setext heading.
        Assert.Equal("<h2>a</h2>\n", Html("a\n---"));
    }

    [Fact]
    public void Gfm_strikethrough_tasks_and_autolinks()
    {
        Assert.Equal("<p><del>gone</del> ~~~kept~~~</p>\n", Html("~~gone~~ ~~~kept~~~"));
        Assert.Equal(
            "<ul class=\"contains-task-list\">\n<li class=\"task-list-item\"><input type=\"checkbox\" disabled=\"\" checked=\"\" /> done</li>\n" +
            "<li class=\"task-list-item\"><input type=\"checkbox\" disabled=\"\" /> todo</li>\n</ul>\n",
            Html("- [x] done\n- [ ] todo"));
        Assert.Equal("<p>see <a href=\"http://www.example.com\">www.example.com</a>, <a href=\"https://x.org/a_(b)\">https://x.org/a_(b)</a>.</p>\n",
            Html("see www.example.com, https://x.org/a_(b)."));
        Assert.Equal("<p>mail <a href=\"mailto:a.b@c.de\">a.b@c.de</a>.</p>\n", Html("mail a.b@c.de."));
    }

    [Fact]
    public void Strict_commonmark_mode_has_no_extensions()
    {
        var strict = new MarkdownOptions { GitHubExtensions = false, HeadingIds = false };
        Assert.Equal("<p>~~a~~ https://x.org</p>\n", Html("~~a~~ https://x.org", strict));
        Assert.Equal("<p>| a |\n| - |</p>\n", Html("| a |\n| - |", strict));
    }

    [Fact]
    public void Heading_ids_are_slugs_and_unique()
    {
        var html = MarkdownToHtml.Convert("# Hello, World!\n## Hello, World!\n### `Code` & more");
        Assert.Contains("<h1 id=\"hello-world\">", html);
        Assert.Contains("<h2 id=\"hello-world-1\">", html);
        Assert.Contains("<h3 id=\"code--more\">", html);
    }

    [Fact]
    public void Document_has_title_styles_and_body()
    {
        var doc = MarkdownToHtml.ConvertDocument("Intro\n\n# The *Real* Title\n\ntext");
        Assert.StartsWith("<!DOCTYPE html>", doc);
        Assert.Contains("<title>The Real Title</title>", doc);
        Assert.Contains("<style>", doc);
        Assert.Contains("<p>text</p>", doc);

        var bare = MarkdownToHtml.ConvertDocument("x", new MarkdownOptions { StyleSheet = "", Title = "T<1>" });
        Assert.DoesNotContain("<style>", bare);
        Assert.Contains("<title>T&lt;1&gt;</title>", bare);
    }

    [Fact]
    public void Line_endings_and_tabs_are_normalized()
    {
        Assert.Equal("<p>a\nb</p>\n<pre><code>c\n</code></pre>\n", Html("a\r\nb\r\n\r\n\tc\r\n"));
        Assert.Equal("<p>a\nb</p>\n", Html("a\rb"));
    }

    [Fact]
    public void Markdown_converts_to_pdf()
    {
        var md = "# Report\n\nSome **bold** text.\n\n| Item | Qty |\n|------|----:|\n| Apples | 3 |\n\n- [x] checked\n\n```\ncode line\n```";
        var pdf = MarkdownToPdf.Convert(md, new PdfOptions { Compress = false });
        AssertStructurallyValid(pdf);
        var content = AllContent(pdf);
        Assert.Contains("(Report) Tj", content);
        Assert.Contains("(Apples) Tj", content);
        Assert.Contains("(code line) Tj", content);
        Assert.Contains("/Title (Report)", Latin1(pdf));
        Assert.Contains("/BaseFont /Courier", Latin1(pdf));
    }

    [Fact]
    public void Markdown_file_resolves_relative_images()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pdfpecker-md-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "dot.png"), RgbPng());
            var md = Path.Combine(dir, "doc.md");
            File.WriteAllText(md, "# Pic\n\n![dot](dot.png)\n");
            var pdfPath = Path.Combine(dir, "doc.pdf");
            MarkdownToPdf.ConvertFile(md, pdfPath);
            var pdf = File.ReadAllBytes(pdfPath);
            AssertStructurallyValid(pdf);
            Assert.Contains("/Subtype /Image", Latin1(pdf));

            var htmlPath = Path.Combine(dir, "doc.html");
            MarkdownToHtml.ConvertFile(md, htmlPath);
            Assert.Contains("<img src=\"dot.png\" alt=\"dot\" />", File.ReadAllText(htmlPath));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Footnotes_render_as_on_github()
    {
        Assert.Equal(
            "<p>Text<sup class=\"footnote-ref\"><a href=\"#fn-1\" id=\"fnref-1\">1</a></sup> and more<sup class=\"footnote-ref\"><a href=\"#fn-2\" id=\"fnref-2\">2</a></sup>.</p>\n" +
            "<section class=\"footnotes\">\n<ol>\n" +
            "<li id=\"fn-1\">\n<p>First. <a href=\"#fnref-1\" class=\"footnote-backref\" aria-label=\"Back to reference 1\">↩</a></p>\n</li>\n" +
            "<li id=\"fn-2\">\n<p>Second. <a href=\"#fnref-2\" class=\"footnote-backref\" aria-label=\"Back to reference 2\">↩</a></p>\n</li>\n" +
            "</ol>\n</section>\n",
            Html("Text[^a] and more[^B].\n\n[^b]: Second.\n[^a]: First."));
    }

    [Fact]
    public void Footnotes_have_blocks_repeated_references_and_numbers_in_order_of_use()
    {
        var html = Html("x[^n] y[^n] z[^m]\n\n[^m]: M, which cites[^deep].\n[^n]: One.\n\n    Two,\ncontinued lazily.\n\n    - item\n\n[^deep]: Deep.\n[^unused]: Unused.");
        // References number the footnotes in the order of first use; a second reference gets its own id and link back.
        Assert.Contains("x<sup class=\"footnote-ref\"><a href=\"#fn-1\" id=\"fnref-1\">1</a></sup> y<sup class=\"footnote-ref\"><a href=\"#fn-1\" id=\"fnref-1-2\">1</a></sup>", html);
        Assert.Contains("<li id=\"fn-1\">\n<p>One.</p>\n<p>Two,\ncontinued lazily.</p>\n<ul>\n<li>item</li>\n</ul>\n"
                        + "<p><a href=\"#fnref-1\" class=\"footnote-backref\" aria-label=\"Back to reference 1\">↩</a> "
                        + "<a href=\"#fnref-1-2\" class=\"footnote-backref\" aria-label=\"Back to reference 1-2\">↩<sup class=\"footnote-ref\">2</sup></a></p>\n</li>", html);
        // A footnote that only another footnote references comes after those of the text; unused ones are left out.
        Assert.Contains("<li id=\"fn-2\">\n<p>M, which cites<sup class=\"footnote-ref\"><a href=\"#fn-3\" id=\"fnref-3\">3</a></sup>.", html);
        Assert.Contains("<li id=\"fn-3\">\n<p>Deep.", html);
        Assert.DoesNotContain("Unused", html);
    }

    [Fact]
    public void Footnote_syntax_without_a_definition_stays_text()
    {
        Assert.Equal("<p>a [^none] [^ b] [^]</p>\n", Html("a [^none] [^ b] [^]"));
        // A code span and an inline link keep their meaning; the first definition of a label wins.
        Assert.Equal("<p><code>[^1]</code> <a href=\"/u\">x</a><sup class=\"footnote-ref\"><a href=\"#fn-1\" id=\"fnref-1\">1</a></sup></p>\n"
                     + "<section class=\"footnotes\">\n<ol>\n<li id=\"fn-1\">\n<p>A <a href=\"#fnref-1\" class=\"footnote-backref\" aria-label=\"Back to reference 1\">↩</a></p>\n</li>\n</ol>\n</section>\n",
            Html("`[^1]` [x](/u)[^1]\n\n[^1]: A\n[^1]: B"));
    }

    [Fact]
    public void Footnote_ids_are_kept_from_headings()
    {
        var html = MarkdownToHtml.Convert("# fn 1\n\nText[^x]\n\n[^x]: Note");
        Assert.Contains("<h1 id=\"fn-1-1\">", html);
        Assert.Contains("<li id=\"fn-1\">", html);
    }

    [Fact]
    public void Definition_lists()
    {
        Assert.Equal("<dl>\n<dt>Term</dt>\n<dd>First <em>one</em></dd>\n<dd>Second</dd>\n<dt>Other</dt>\n<dd>Third</dd>\n</dl>\n",
            Html("Term\n: First *one*\n: Second\n\nOther\n: Third"));
        // A term line right after a definition, which would otherwise continue its paragraph, starts the next entry.
        Assert.Equal("<dl>\n<dt>A</dt>\n<dd>a</dd>\n<dt>B</dt>\n<dd>b</dd>\n</dl>\n", Html("A\n: a\nB\n: b"));
        // Only the line before the marker is the term; the lines before it stay a paragraph.
        Assert.Equal("<p>Intro line</p>\n<dl>\n<dt>Term</dt>\n<dd>def</dd>\n</dl>\n", Html("Intro line\nTerm\n: def"));
    }

    [Fact]
    public void Loose_definition_lists_and_indented_continuation()
    {
        Assert.Equal("<dl>\n<dt>Term</dt>\n<dd>\n<p>One.</p>\n<p>Two.</p>\n<pre><code>code\n</code></pre>\n</dd>\n</dl>\n",
            Html("Term\n\n:   One.\n\n    Two.\n\n        code"));
        Assert.Equal("<dl>\n<dt>Term</dt>\n<dd>\n<ul>\n<li>a</li>\n<li>b</li>\n</ul>\n</dd>\n</dl>\n<p>After</p>\n",
            Html("Term\n: - a\n  - b\n\nAfter"));
    }

    [Fact]
    public void Definition_markers_need_a_term_and_a_space()
    {
        Assert.Equal("<p>: no term</p>\n", Html(": no term"));
        Assert.Equal("<p>Term\n:no space</p>\n", Html("Term\n:no space"));
        Assert.Equal("<p>Term\n:</p>\n", Html("Term\n:"));
        Assert.Equal("<h1>Head</h1>\n<p>: def</p>\n", Html("# Head\n: def"));
        // Two blank lines are too many between a term and its definition.
        Assert.Equal("<p>Term</p>\n<p>: def</p>\n", Html("Term\n\n\n: def"));
    }

    [Fact]
    public void Front_matter_is_not_rendered_and_gives_the_title()
    {
        const string md = "---\ntitle: \"Annual: report\" \nauthor: Me\n---\nBody\n";
        Assert.Equal("<p>Body</p>\n", Html(md));
        var doc = MarkdownToHtml.ConvertDocument("---\ntitle: Plain title # comment\n...\n# Heading\n");
        Assert.Contains("<title>Plain title</title>", doc);
        Assert.Contains("<title>Annual: report</title>", MarkdownToHtml.ConvertDocument(md));
        Assert.Contains("<title>Given</title>", MarkdownToHtml.ConvertDocument(md, new MarkdownOptions { Title = "Given" }));
        Assert.Contains("<title>Heading</title>", MarkdownToHtml.ConvertDocument("---\nauthor: x\n---\n# Heading\n"));
        Assert.Contains("/Title (Annual: report)", Latin1(MarkdownToPdf.Convert(md, new PdfOptions { Compress = false })));
    }

    [Fact]
    public void Front_matter_needs_its_closing_line_at_the_start()
    {
        Assert.Equal("<hr />\n<p>title: x</p>\n", Html("---\ntitle: x\n"));
        Assert.Equal("<p>Text</p>\n<hr />\n<h2>title: x</h2>\n", Html("Text\n\n---\ntitle: x\n---\n"));
    }

    [Fact]
    public void Strict_commonmark_mode_has_no_footnotes_definition_lists_or_front_matter()
    {
        var strict = new MarkdownOptions { GitHubExtensions = false, HeadingIds = false };
        Assert.Equal("<p>a<a href=\"b\">^1</a></p>\n", Html("a[^1]\n\n[^1]: b", strict));
        Assert.Equal("<p>Term\n: def</p>\n", Html("Term\n: def", strict));
        Assert.Equal("<hr />\n<h2>a: b</h2>\n", Html("---\na: b\n---", strict));
    }

    [Fact]
    public void Markdown_to_pdf_puts_footnotes_at_the_page_bottom()
    {
        var md = "Body text[^a] and more[^b], again[^a].\n\n[^a]: Note A cites[^c].\n[^b]: Note B.\n\n    Second paragraph of B.\n[^c]: Note C.";
        var html = MarkdownToHtml.RenderDocument(md, null, pagedFootnotes: true);
        Assert.Contains("Body text<small class=\"footnote\" id=\"fn-1\" style=\"float: footnote\">Note A cites<a class=\"footnote-ref\" href=\"#fn-3\"><sup>3</sup></a>.</small>", html);
        Assert.Contains("<small class=\"footnote\" id=\"fn-2\" style=\"float: footnote\">\n<p>Note B.</p>\n<p>Second paragraph of B.</p>\n</small>", html);
        Assert.Contains("again<a class=\"footnote-ref\" href=\"#fn-1\"><sup>1</sup></a>.", html);
        // Only a footnote that other footnotes alone reference goes to a list at the end, numbered on.
        Assert.Contains("<section class=\"footnotes\">\n<ol start=\"3\">\n<li id=\"fn-3\">\n<p>Note C.</p>\n</li>", html);
        Assert.DoesNotContain("footnote-backref", html);

        var pdf = MarkdownToPdf.Convert(md, new PdfOptions { Compress = false });
        AssertStructurallyValid(pdf);
        var content = AllContent(pdf);
        float Y(string text) => float.Parse(Regex.Match(content, @"1 0 0 1 [\d.]+ ([\d.]+) Tm \(" + Regex.Escape(text)).Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(Y("1. ") < 100 && Y("Note B.") < Y("1. ") && Y("Second paragraph of B.") < Y("Note B."), "The footnotes are at the bottom of the page, in order");
        Assert.True(Y("Body text") > 700 && Y("Note C.") > 700, "The listed footnote follows the text");
    }

    [Fact]
    public void Markdown_to_pdf_floats_footnotes_with_any_style_sheet()
    {
        var pdf = MarkdownToPdf.Convert("Text[^1]\n\n[^1]: The note.", new PdfOptions { Compress = false }, new MarkdownOptions { StyleSheet = "" });
        var content = AllContent(pdf);
        var y = float.Parse(Regex.Match(content, @"1 0 0 1 [\d.]+ ([\d.]+) Tm \(The note").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(y < 100, "The footnote is at the bottom of the page");
    }

    public static TheoryData<string, string> Pathological => new()
    {
        { "nested quotes", new string('>', 50_000) + " a" },
        { "open brackets", new string('[', 50_000) + "a" },
        { "unclosed links", string.Concat(Enumerable.Repeat("[a](", 20_000)) },
        { "emphasis openers", string.Concat(Enumerable.Repeat("*a ", 30_000)) },
        { "long delimiter runs", new string('*', 30_000) + "a" + new string('*', 30_000) },
        { "backtick runs", string.Concat(Enumerable.Range(1, 1500).Select(i => new string('`', i) + "a")) },
        { "tildes", string.Concat(Enumerable.Repeat("~a ~~b ", 30_000)) },
        { "html starts", string.Concat(Enumerable.Repeat("<a ", 30_000)) },
        { "link destination parens", "[a](" + new string('(', 50_000) + ")" },
        { "reference definitions", string.Concat(Enumerable.Range(0, 10_000).Select(i => $"[r{i}]: /u{i}\n")) + "[r1]" },
        { "wide table", "|" + string.Concat(Enumerable.Repeat("a|", 200)) + "\n|" + string.Concat(Enumerable.Repeat("-|", 200)) + "\n" + string.Concat(Enumerable.Repeat("|b\n", 5000)) },
        { "bare urls", string.Concat(Enumerable.Repeat("www.a.b ", 30_000)) },
        { "footnote references", string.Concat(Enumerable.Repeat("a[^1] ", 30_000)) + "\n\n[^1]: note" },
        { "footnotes", string.Concat(Enumerable.Range(0, 10_000).Select(i => $"x[^{i}]\n")) + string.Concat(Enumerable.Range(0, 10_000).Select(i => $"[^{i}]: n{i} [^{i + 1}]\n")) },
        { "unclosed footnote labels", string.Concat(Enumerable.Repeat("[^" + new string('a', 990), 200)) + "\n\n[^a]: x" },
        { "footnote openers", string.Concat(Enumerable.Repeat("[^a", 30_000)) + "\n\n[^a]: x" },
        { "nested footnote definitions", string.Concat(Enumerable.Repeat("[^a]: ", 30_000)) + "x [^a]\n\nText [^a]" },
        { "definition markers", "Term\n" + string.Concat(Enumerable.Repeat(": d\n", 30_000)) },
        { "definition entries", string.Concat(Enumerable.Repeat("t\n: d\n", 20_000)) },
        { "nested definitions", string.Concat(Enumerable.Range(0, 300).Select(i => new string(' ', 2 * i) + "t\n" + new string(' ', 2 * i) + ": d\n")) },
        { "unclosed front matter", "---\n" + string.Concat(Enumerable.Repeat("title: a\n", 50_000)) },
    };

}
