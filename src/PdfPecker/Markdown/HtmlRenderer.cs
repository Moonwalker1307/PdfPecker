using System.Text;

namespace PdfPecker.Markdown;

/// <summary>Renders a Markdown syntax tree as HTML, in the same shape as the CommonMark reference renderer.</summary>
internal sealed class HtmlRenderer
{
    private readonly StringBuilder _sb;
    private readonly bool _headingIds;
    private readonly bool _allowRawHtml;
    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nextSuffix = new(StringComparer.Ordinal);
    private int _disableTags; // > 0 while rendering image alt text, which is plain text

    public HtmlRenderer(StringBuilder sb, bool headingIds, bool allowRawHtml)
    {
        _sb = sb;
        _headingIds = headingIds;
        _allowRawHtml = allowRawHtml;
    }

    private void Cr()
    {
        if (_sb.Length > 0 && _sb[^1] != '\n') _sb.Append('\n');
    }

    private void Tag(string s)
    {
        if (_disableTags == 0) _sb.Append(s);
    }

    private void Esc(string s) => MdUtil.EscapeHtml(_sb, s);

    private static bool InTightList(MdNode paragraph)
    {
        var grandparent = paragraph.Parent?.Parent;
        return grandparent is { Type: MdNodeType.List } && grandparent.ListData!.Tight;
    }

    public void Render(MdNode root)
    {
        foreach (var (node, entering) in root.Walk())
        {
            switch (node.Type)
            {
                case MdNodeType.Document:
                    break;

                case MdNodeType.Text:
                    Esc(node.Literal!);
                    break;

                case MdNodeType.SoftBreak:
                    _sb.Append('\n');
                    break;

                case MdNodeType.LineBreak:
                    if (_disableTags > 0) _sb.Append(' ');
                    else _sb.Append("<br />\n");
                    break;

                case MdNodeType.Code:
                    Tag("<code>");
                    Esc(node.Literal!);
                    Tag("</code>");
                    break;

                case MdNodeType.HtmlInline:
                    if (_disableTags > 0) break;
                    if (_allowRawHtml) _sb.Append(node.Literal);
                    else Esc(node.Literal!);
                    break;

                case MdNodeType.Emph:
                    Tag(entering ? "<em>" : "</em>");
                    break;

                case MdNodeType.Strong:
                    Tag(entering ? "<strong>" : "</strong>");
                    break;

                case MdNodeType.Strikethrough:
                    Tag(entering ? "<del>" : "</del>");
                    break;

                case MdNodeType.Link:
                    if (entering)
                    {
                        if (_disableTags > 0) break;
                        _sb.Append("<a href=\"");
                        Esc(node.Destination ?? "");
                        _sb.Append('"');
                        if (!string.IsNullOrEmpty(node.Title))
                        {
                            _sb.Append(" title=\"");
                            Esc(node.Title);
                            _sb.Append('"');
                        }
                        _sb.Append('>');
                    }
                    else Tag("</a>");
                    break;

                case MdNodeType.Image:
                    if (entering)
                    {
                        if (_disableTags == 0)
                        {
                            _sb.Append("<img src=\"");
                            Esc(node.Destination ?? "");
                            _sb.Append("\" alt=\"");
                        }
                        _disableTags++;
                    }
                    else
                    {
                        _disableTags--;
                        if (_disableTags == 0)
                        {
                            _sb.Append('"');
                            if (!string.IsNullOrEmpty(node.Title))
                            {
                                _sb.Append(" title=\"");
                                Esc(node.Title);
                                _sb.Append('"');
                            }
                            _sb.Append(" />");
                        }
                    }
                    break;

                case MdNodeType.Paragraph:
                    if (InTightList(node)) break;
                    if (entering)
                    {
                        Cr();
                        _sb.Append("<p>");
                    }
                    else
                    {
                        _sb.Append("</p>");
                        Cr();
                    }
                    break;

                case MdNodeType.Heading:
                    if (entering)
                    {
                        Cr();
                        _sb.Append("<h").Append(node.Level);
                        if (_headingIds)
                        {
                            var id = UniqueId(Slug(PlainText(node)));
                            if (id.Length > 0) _sb.Append(" id=\"").Append(id).Append('"');
                        }
                        _sb.Append('>');
                    }
                    else
                    {
                        _sb.Append("</h").Append(node.Level).Append('>');
                        Cr();
                    }
                    break;

                case MdNodeType.CodeBlock:
                {
                    Cr();
                    _sb.Append("<pre><code");
                    var info = node.Info;
                    if (!string.IsNullOrEmpty(info))
                    {
                        int sp = info.IndexOfAny([' ', '\t']);
                        var lang = sp < 0 ? info : info.Substring(0, sp);
                        _sb.Append(" class=\"language-");
                        Esc(lang);
                        _sb.Append('"');
                    }
                    _sb.Append('>');
                    Esc(node.Literal ?? "");
                    _sb.Append("</code></pre>");
                    Cr();
                    break;
                }

                case MdNodeType.HtmlBlock:
                    Cr();
                    if (_allowRawHtml) _sb.Append(node.Literal);
                    else
                    {
                        _sb.Append("<p>");
                        Esc(node.Literal ?? "");
                        _sb.Append("</p>");
                    }
                    Cr();
                    break;

                case MdNodeType.ThematicBreak:
                    Cr();
                    _sb.Append("<hr />");
                    Cr();
                    break;

                case MdNodeType.BlockQuote:
                    Cr();
                    _sb.Append(entering ? "<blockquote>" : "</blockquote>");
                    Cr();
                    break;

                case MdNodeType.List:
                {
                    var data = node.ListData!;
                    if (entering)
                    {
                        Cr();
                        if (data.Ordered)
                        {
                            _sb.Append("<ol");
                            if (data.Start != 1) _sb.Append(" start=\"").Append(data.Start).Append('"');
                            _sb.Append('>');
                        }
                        else _sb.Append(IsTaskList(node) ? "<ul class=\"contains-task-list\">" : "<ul>");
                        Cr();
                    }
                    else
                    {
                        Cr();
                        _sb.Append(data.Ordered ? "</ol>" : "</ul>");
                        Cr();
                    }
                    break;
                }

                case MdNodeType.Item:
                    if (entering)
                    {
                        if (node.Checked is bool done)
                        {
                            _sb.Append("<li class=\"task-list-item\"><input type=\"checkbox\" disabled=\"\"");
                            if (done) _sb.Append(" checked=\"\"");
                            _sb.Append(" /> ");
                        }
                        else _sb.Append("<li>");
                    }
                    else
                    {
                        _sb.Append("</li>");
                        Cr();
                    }
                    break;

                case MdNodeType.Table:
                    Cr();
                    if (entering)
                    {
                        _sb.Append("<table>");
                        Cr();
                    }
                    else
                    {
                        if (node.LastChild != node.FirstChild) _sb.Append("</tbody>\n");
                        _sb.Append("</table>");
                        Cr();
                    }
                    break;

                case MdNodeType.TableRow:
                    if (entering)
                    {
                        if (node.IsHeader) _sb.Append("<thead>\n");
                        else if (node.Prev == node.Parent!.FirstChild) _sb.Append("<tbody>\n");
                        _sb.Append("<tr>\n");
                    }
                    else
                    {
                        _sb.Append("</tr>\n");
                        if (node.IsHeader) _sb.Append("</thead>\n");
                    }
                    break;

                case MdNodeType.TableCell:
                    if (entering)
                    {
                        _sb.Append(node.IsHeader ? "<th" : "<td");
                        switch (node.Align)
                        {
                            case TableAlign.Left: _sb.Append(" align=\"left\""); break;
                            case TableAlign.Center: _sb.Append(" align=\"center\""); break;
                            case TableAlign.Right: _sb.Append(" align=\"right\""); break;
                        }
                        _sb.Append('>');
                    }
                    else
                    {
                        _sb.Append(node.IsHeader ? "</th>\n" : "</td>\n");
                    }
                    break;
            }
        }
    }

    private static bool IsTaskList(MdNode list)
    {
        for (var item = list.FirstChild; item != null; item = item.Next)
            if (item.Checked != null) return true;
        return false;
    }

    /// <summary>The text content of a node, without markup.</summary>
    public static string PlainText(MdNode node)
    {
        var sb = new StringBuilder();
        foreach (var (n, entering) in node.Walk())
        {
            if (!entering) continue;
            switch (n.Type)
            {
                case MdNodeType.Text:
                case MdNodeType.Code:
                    sb.Append(n.Literal);
                    break;
                case MdNodeType.SoftBreak:
                case MdNodeType.LineBreak:
                    sb.Append(' ');
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>GitHub-style heading anchor: lower case, punctuation removed, spaces become hyphens.</summary>
    private static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(char.ToLowerInvariant(c));
            else if (c == ' ') sb.Append('-');
        }
        return sb.ToString();
    }

    private string UniqueId(string slug)
    {
        if (slug.Length == 0) return slug;
        if (_usedIds.Add(slug)) return slug;
        // Remember where numbering stopped, so many identical headings stay linear.
        int i = _nextSuffix.GetValueOrDefault(slug, 1);
        string id;
        while (!_usedIds.Add(id = slug + "-" + i)) i++;
        _nextSuffix[slug] = i + 1;
        return id;
    }
}
