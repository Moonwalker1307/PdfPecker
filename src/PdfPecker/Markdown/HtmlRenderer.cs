using System.Globalization;
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

    /// <summary>A footnote that is referenced, with its number in the order of first reference.</summary>
    private sealed class Footnote(MdNode definition, int number)
    {
        public readonly MdNode Definition = definition;
        public readonly int Number = number;
        public readonly string Text = number.ToString(CultureInfo.InvariantCulture);

        /// <summary>The references to it, and how many of them are rendered so far.</summary>
        public int References, Rendered;

        /// <summary>True once its text is placed at a reference, to float to the bottom of the page.</summary>
        public bool Floated;

        public string Id => "fn-" + Text;

        /// <summary>The id of its <paramref name="k"/>th reference, counted from 1, as GitHub names them.</summary>
        public string ReferenceId(int k) => k == 1 ? "fnref-" + Text : "fnref-" + Text + "-" + k.ToString(CultureInfo.InvariantCulture);
    }

    private readonly Dictionary<string, MdNode>? _definitions;
    private readonly bool _pagedFootnotes;
    private readonly Dictionary<string, Footnote> _footnotes = new(StringComparer.Ordinal);
    private readonly List<Footnote> _footnoteOrder = new();
    private int _textFootnotes; // the footnotes that the text references, not only other footnotes: numbers 1 to this
    private int _footnoteDepth; // > 0 while rendering the text of a footnote
    private MdNode? _bareParagraph; // a floating footnote's only paragraph, which needs no <p>
    private Footnote? _backrefNote; // the footnote whose links back to its references end _backrefParagraph
    private MdNode? _backrefParagraph;

    /// <param name="sb">Receives the HTML.</param>
    /// <param name="headingIds">Whether headings get id attributes.</param>
    /// <param name="allowRawHtml">Whether raw HTML passes through; otherwise it is escaped.</param>
    /// <param name="footnotes">The footnote definitions by label, or null without footnotes.</param>
    /// <param name="pagedFootnotes">
    /// Whether footnotes float to the bottom of their pages (CSS <c>float: footnote</c>) in a PDF, rather than going to a
    /// list at the end as on GitHub, which is what a browser can show.
    /// </param>
    public HtmlRenderer(StringBuilder sb, bool headingIds, bool allowRawHtml, Dictionary<string, MdNode>? footnotes = null, bool pagedFootnotes = false)
    {
        _sb = sb;
        _headingIds = headingIds;
        _allowRawHtml = allowRawHtml;
        _definitions = footnotes;
        _pagedFootnotes = pagedFootnotes;
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
        return grandparent is { Type: MdNodeType.List or MdNodeType.DefinitionList } && grandparent.ListData!.Tight;
    }

    /// <summary>Renders the document, and after it the footnotes that do not float to the bottom of their pages.</summary>
    public void Render(MdNode root)
    {
        NumberFootnotes(root);
        RenderNodes(root);
        RenderFootnoteList();
    }

    private void RenderNodes(MdNode root)
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
                    if (InTightList(node) || node == _bareParagraph) break;
                    if (entering)
                    {
                        Cr();
                        _sb.Append("<p>");
                    }
                    else
                    {
                        if (node == _backrefParagraph) AppendBackrefs(_backrefNote!, leadingSpace: true);
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

                case MdNodeType.DefinitionList:
                    Cr();
                    _sb.Append(entering ? "<dl>" : "</dl>");
                    Cr();
                    break;

                case MdNodeType.DefinitionTerm:
                    if (entering)
                    {
                        Cr();
                        _sb.Append("<dt>");
                    }
                    else
                    {
                        _sb.Append("</dt>");
                        Cr();
                    }
                    break;

                case MdNodeType.DefinitionDetails:
                    if (entering)
                    {
                        Cr();
                        _sb.Append("<dd>");
                    }
                    else
                    {
                        _sb.Append("</dd>");
                        Cr();
                    }
                    break;

                case MdNodeType.FootnoteReference:
                    RenderFootnoteReference(node);
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

    /// <summary>
    /// Numbers the referenced footnotes in the order of their first reference: those of the text first, then those that
    /// only other footnotes reference, in the order the footnote list shows them. Each footnote's text is visited once,
    /// so footnotes that reference each other cost no more than any other text.
    /// </summary>
    private void NumberFootnotes(MdNode root)
    {
        if (_definitions == null || _definitions.Count == 0) return;
        CountReferences(root);
        _textFootnotes = _footnoteOrder.Count;
        for (int i = 0; i < _footnoteOrder.Count; i++) CountReferences(_footnoteOrder[i].Definition);
        if (!_headingIds) return;
        // Headings must not take the ids of the footnotes and their references.
        foreach (var note in _footnoteOrder)
        {
            _usedIds.Add(note.Id);
            for (int k = 1; k <= note.References; k++) _usedIds.Add(note.ReferenceId(k));
        }
    }

    private void CountReferences(MdNode root)
    {
        int images = 0; // alt text is plain text, so a reference in it links nowhere
        foreach (var (node, entering) in root.Walk())
        {
            if (node.Type == MdNodeType.Image) images += entering ? 1 : -1;
            if (node.Type != MdNodeType.FootnoteReference || images > 0) continue;
            if (!_footnotes.TryGetValue(node.Label!, out var note))
            {
                if (!_definitions!.TryGetValue(node.Label!, out var definition)) continue;
                note = new Footnote(definition, _footnoteOrder.Count + 1);
                _footnotes.Add(node.Label!, note);
                _footnoteOrder.Add(note);
            }
            note.References++;
        }
    }

    private void RenderFootnoteReference(MdNode node)
    {
        if (!_footnotes.TryGetValue(node.Label!, out var note)) return;
        if (_disableTags > 0)
        {
            _sb.Append(note.Text);
            return;
        }
        if (_pagedFootnotes && note.Number <= _textFootnotes && !note.Floated && _footnoteDepth == 0)
        {
            FloatFootnote(note);
            return;
        }
        if (_pagedFootnotes)
        {
            // Further references to a floating footnote, and references in the text of other footnotes, link to it.
            // The link goes around the superscript, because the layout raises only the text that the element with
            // vertical-align holds itself. A PDF has no links back, so the references need no ids.
            _sb.Append("<a class=\"footnote-ref\" href=\"#").Append(note.Id).Append("\"><sup>").Append(note.Text).Append("</sup></a>");
            return;
        }
        _sb.Append("<sup class=\"footnote-ref\"><a href=\"#").Append(note.Id).Append("\" id=\"").Append(note.ReferenceId(++note.Rendered))
            .Append("\">").Append(note.Text).Append("</a></sup>");
    }

    /// <summary>
    /// Places the footnote's text at its first reference, in an element that floats to the bottom of the page, where the
    /// layout numbers it with the footnote counter in the same order. The element is a &lt;small&gt;, an inline element
    /// whose block content does not end the paragraph around it, and the float is in its style attribute so that it
    /// works with any style sheet.
    /// </summary>
    private void FloatFootnote(Footnote note)
    {
        note.Floated = true;
        var definition = note.Definition;
        _sb.Append("<small class=\"footnote\" id=\"").Append(note.Id).Append("\" style=\"float: footnote\">");
        var bare = _bareParagraph;
        _bareParagraph = definition.FirstChild is { Type: MdNodeType.Paragraph } only && only.Next == null ? only : null;
        _footnoteDepth++;
        for (var child = definition.FirstChild; child != null; child = child.Next) RenderNodes(child);
        _footnoteDepth--;
        _bareParagraph = bare;
        _sb.Append("</small>");
    }

    /// <summary>The footnotes that do not float, as GitHub shows them: a numbered list with links back to the references.</summary>
    private void RenderFootnoteList()
    {
        int first = _pagedFootnotes ? _textFootnotes : 0;
        if (_footnoteOrder.Count <= first) return;
        Cr();
        _sb.Append("<section class=\"footnotes\">\n<ol");
        if (first > 0) _sb.Append(" start=\"").Append(first + 1).Append('"');
        _sb.Append(">\n");
        _footnoteDepth++;
        for (int i = first; i < _footnoteOrder.Count; i++)
        {
            var note = _footnoteOrder[i];
            var definition = note.Definition;
            _sb.Append("<li id=\"").Append(note.Id).Append("\">\n");
            // A PDF has no links back, as its footnotes at the page bottom have none, and its fonts have no "↩".
            _backrefNote = _pagedFootnotes ? null : note;
            _backrefParagraph = _backrefNote != null && definition.LastChild is { Type: MdNodeType.Paragraph } last ? last : null;
            for (var child = definition.FirstChild; child != null; child = child.Next) RenderNodes(child);
            if (_backrefNote != null && _backrefParagraph == null)
            {
                Cr();
                _sb.Append("<p>");
                AppendBackrefs(note, leadingSpace: false);
                _sb.Append("</p>\n");
            }
            _backrefParagraph = null;
            Cr();
            _sb.Append("</li>\n");
        }
        _footnoteDepth--;
        _sb.Append("</ol>\n</section>\n");
    }

    /// <summary>The links from a footnote back to each of its references, as GitHub shows them.</summary>
    private void AppendBackrefs(Footnote note, bool leadingSpace)
    {
        for (int k = 1; k <= note.References; k++)
        {
            if (k > 1 || leadingSpace) _sb.Append(' ');
            var id = note.ReferenceId(k);
            _sb.Append("<a href=\"#").Append(id).Append("\" class=\"footnote-backref\" aria-label=\"Back to reference ")
                .Append(id, "fnref-".Length, id.Length - "fnref-".Length).Append("\">\u21A9");
            if (k > 1) _sb.Append("<sup class=\"footnote-ref\">").Append(k.ToString(CultureInfo.InvariantCulture)).Append("</sup>");
            _sb.Append("</a>");
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
