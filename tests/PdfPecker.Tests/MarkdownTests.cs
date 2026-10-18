using System.Diagnostics;
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
    };

}
