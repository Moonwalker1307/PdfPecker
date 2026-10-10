using System.Text;
using PdfPecker.Markdown;

namespace PdfPecker;

/// <summary>
/// Converts Markdown to HTML with PdfPecker's own parser: CommonMark plus the GitHub extensions (tables, strikethrough,
/// task lists, autolinks). No dependencies beyond the .NET base class library.
/// </summary>
/// <remarks>All methods are static and thread-safe.</remarks>
public static class MarkdownToHtml
{
    /// <summary>The stylesheet <see cref="ConvertDocument"/> and <see cref="MarkdownToPdf"/> use unless <see cref="MarkdownOptions.StyleSheet"/> is set.</summary>
    public const string DefaultStyleSheet = """
body { font-family: Helvetica, Arial, sans-serif; font-size: 10.5pt; line-height: 1.5; color: #1f2328; margin: 0; }
h1, h2, h3, h4, h5, h6 { margin: 1.4em 0 0.6em; line-height: 1.25; font-weight: bold; page-break-after: avoid; }
h1 { font-size: 2em; padding-bottom: 0.25em; border-bottom: 1px solid #d1d9e0; }
h2 { font-size: 1.5em; padding-bottom: 0.25em; border-bottom: 1px solid #d1d9e0; }
h3 { font-size: 1.25em; }
h4 { font-size: 1em; }
h5 { font-size: 0.875em; }
h6 { font-size: 0.85em; color: #59636e; }
body > h1:first-child, body > h2:first-child, body > h3:first-child { margin-top: 0; }
p, blockquote, ul, ol, dl, table, pre { margin: 0 0 1em; }
a { color: #0969da; text-decoration: none; }
strong { font-weight: bold; }
del { color: #59636e; }
ul, ol { padding-left: 2em; }
ul ul, ul ol, ol ol, ol ul { margin: 0; }
li + li { margin-top: 0.25em; }
li > p { margin: 0.5em 0; }
ul.contains-task-list { list-style-type: none; padding-left: 1.2em; }
input[type=checkbox] { display: inline-block; width: 0.75em; height: 0.75em; border: 1px solid #59636e; margin-right: 0.4em; }
input[checked] { background-color: #0969da; border-color: #0969da; }
blockquote { margin-left: 0; margin-right: 0; padding: 0 1em; color: #59636e; border-left: 0.25em solid #d1d9e0; }
code { font-family: Courier, monospace; font-size: 0.9em; background-color: #eff1f3; }
pre { font-family: Courier, monospace; font-size: 0.85em; line-height: 1.45; background-color: #f6f8fa; padding: 10px 12px; white-space: pre-wrap; }
pre code { font-size: 1em; background-color: transparent; }
hr { border: 0; border-top: 2px solid #d1d9e0; margin: 1.5em 0; }
table { border-collapse: collapse; }
th, td { border: 1px solid #d1d9e0; padding: 4px 10px; }
th { font-weight: bold; background-color: #f6f8fa; }
tr:nth-child(even) td { background-color: #f6f8fa; }
img { max-width: 100%; }
@media screen { body { max-width: 860px; margin: 0 auto; padding: 32px 24px; } }
""";

    /// <summary>Converts Markdown to an HTML fragment (the body content only, no &lt;html&gt; or styles).</summary>
    public static string Convert(string markdown, MarkdownOptions? options = null)
    {
        if (markdown == null) throw new ArgumentNullException(nameof(markdown));
        options ??= new MarkdownOptions();
        var doc = new BlockParser(options.GitHubExtensions).Parse(markdown);
        var sb = new StringBuilder(markdown.Length + markdown.Length / 4 + 64);
        new HtmlRenderer(sb, options.HeadingIds, options.AllowRawHtml).Render(doc);
        return sb.ToString();
    }

    /// <summary>
    /// Converts Markdown to a complete HTML document with a stylesheet, ready to open in a browser or to pass to <see cref="HtmlToPdf"/>.
    /// </summary>
    public static string ConvertDocument(string markdown, MarkdownOptions? options = null)
    {
        if (markdown == null) throw new ArgumentNullException(nameof(markdown));
        options ??= new MarkdownOptions();
        var doc = new BlockParser(options.GitHubExtensions).Parse(markdown);

        var title = options.Title;
        if (title == null)
        {
            foreach (var (node, entering) in doc.Walk())
            {
                if (entering && node.Type == MdNodeType.Heading)
                {
                    title = HtmlRenderer.PlainText(node).Trim();
                    break;
                }
            }
        }

        var sb = new StringBuilder(markdown.Length + markdown.Length / 4 + DefaultStyleSheet.Length + 256);
        sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n");
        if (!string.IsNullOrEmpty(title))
        {
            sb.Append("<title>");
            MdUtil.EscapeHtml(sb, title);
            sb.Append("</title>\n");
        }
        var css = options.StyleSheet ?? DefaultStyleSheet;
        if (css.Length > 0)
        {
            // "</" cannot appear inside <style>; a CSS escape keeps the meaning.
            sb.Append("<style>\n").Append(css.Replace("</", "<\\/")).Append("\n</style>\n");
        }
        sb.Append("</head>\n<body class=\"markdown-body\">\n");
        new HtmlRenderer(sb, options.HeadingIds, options.AllowRawHtml).Render(doc);
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>Converts a Markdown file to a complete HTML document file.</summary>
    public static void ConvertFile(string markdownPath, string htmlPath, MarkdownOptions? options = null)
        => File.WriteAllText(htmlPath, ConvertDocument(File.ReadAllText(markdownPath), options));
}
