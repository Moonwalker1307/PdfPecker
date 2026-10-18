using System.Text;

namespace PdfPecker.Markdown;

/// <summary>
/// Inline phase of the Markdown parser: code spans, emphasis (delimiter-run algorithm), links and images (bracket stack),
/// autolinks, raw HTML, entities, escapes and line breaks, plus GFM strikethrough and extended autolinks. One instance
/// parses every block of a document, and the link reference definitions it collects serve all of them.
/// </summary>
internal sealed partial class InlineParser
{
    private readonly Dictionary<string, LinkReference> _refmap;
    private readonly bool _gfm;
    private SubjectCursor _cursor;

    public InlineParser(Dictionary<string, LinkReference> refmap, bool gfm)
    {
        _refmap = refmap;
        _gfm = gfm;
    }

    /// <summary>Replaces the text that a paragraph, heading or table cell collected with the inlines parsed from it.</summary>
    public void Parse(MdNode block)
    {
        _cursor.Start(TrimmedText(block.Content));
        block.Content = null;
        _delimiters = null;
        _brackets = null;
        _linksInactiveBefore = 0;
        ResetCodeSpanCache();
        while (!_cursor.AtEnd) ParseInline(block);
        ProcessEmphasis(null);
        ShortenPartlyUsedDelimiters();
        MergeAdjacentText(block);
        if (_gfm) Linkify(block);
    }

    /// <summary>
    /// The collected text without the whitespace around it. Paragraph text always ends in a line ending, so trimming the
    /// builder instead of its string saves copying every paragraph twice.
    /// </summary>
    private static string TrimmedText(StringBuilder? content)
    {
        if (content == null) return "";
        int start = 0, end = content.Length;
        while (end > start && IsTrimmedWhitespace(content[end - 1])) end--;
        while (start < end && IsTrimmedWhitespace(content[start])) start++;
        return content.ToString(start, end - start);
    }

    private static bool IsTrimmedWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r';

    /// <summary>Parses the inline that starts at the cursor and appends it to <paramref name="block"/>.</summary>
    private void ParseInline(MdNode block)
    {
        char c = _cursor.Current;
        switch (c)
        {
            case '\n': ParseLineEnding(block); return;
            case '\\': ParseBackslash(block); return;
            case '`': ParseCodeSpan(block); return;
            case '*' or '_': ParseDelimiterRun(c, block); return;
            case '~' when _gfm: ParseDelimiterRun(c, block); return;
            case '[': ParseOpenBracket(block); return;
            case '!': ParseBang(block); return;
            case ']': ParseCloseBracket(block); return;
            case '<' when TryParseAutolink(block) || TryParseHtmlTag(block): return;
            case '&' when TryParseEntity(block): return;
            case '~' or '<' or '&':
                // The character starts nothing after all, so it is literal text.
                _cursor.Advance();
                block.AppendChild(Text(c.ToString()));
                return;
            default: ParseText(block); return;
        }
    }

    private static MdNode Text(string s) => new(MdNodeType.Text) { Literal = s };

    /// <summary>Moves <paramref name="first"/> and the siblings after it, up to but not including <paramref name="stop"/>, into <paramref name="container"/>.</summary>
    private static void MoveSiblingsInto(MdNode container, MdNode? first, MdNode? stop)
    {
        var node = first;
        while (node != null && node != stop)
        {
            var next = node.Next;
            container.AppendChild(node);
            node = next;
        }
    }

    /// <summary>
    /// Joins neighbouring text nodes, which delimiters and brackets that stayed literal leave behind, and drops the empty
    /// ones, so that the renderer and the GFM autolinker see each run of text whole.
    /// </summary>
    private static void MergeAdjacentText(MdNode root)
    {
        var containers = new List<MdNode>();
        foreach (var (n, entering) in root.Walk())
            if (entering && n.FirstChild != null) containers.Add(n);
        foreach (var parent in containers)
        {
            var node = parent.FirstChild;
            while (node != null)
            {
                var next = node.Next;
                if (node.Type == MdNodeType.Text)
                {
                    if (next is { Type: MdNodeType.Text }) next = AppendFollowingText(node, next);
                    if (node.Literal!.Length == 0) node.Unlink();
                }
                node = next;
            }
        }
    }

    /// <summary>Appends the text nodes from <paramref name="next"/> on to <paramref name="node"/> and returns the node after them.</summary>
    private static MdNode? AppendFollowingText(MdNode node, MdNode? next)
    {
        var sb = new StringBuilder(node.Literal);
        while (next is { Type: MdNodeType.Text })
        {
            sb.Append(next.Literal);
            var after = next.Next;
            next.Unlink();
            next = after;
        }
        node.Literal = sb.ToString();
        return next;
    }
}
