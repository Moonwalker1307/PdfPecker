using System.Text;

namespace PdfPecker.Markdown;

// Definition lists (PHP Markdown Extra and markdown-it-deflist syntax): a line of its own is the term, and each line that
// starts with ": " after it begins one of its definitions, which continue like list items.
internal sealed partial class BlockParser
{
    /// <summary>The last line that was added to a paragraph as a lazy continuation line.</summary>
    private int _lazyLine;

    private BlockStart DefinitionStart(MdNode container)
    {
        if (_cursor.IsIndented || _cursor.NextNonspaceChar != ':') return BlockStart.None;
        // The marker needs a space after it and a definition after that.
        int afterMarker = _cursor.NextNonspace + 1;
        if (!_cursor.PeekIsSpaceOrTab(afterMarker) || MdUtil.IsBlank(_cursor.Line, afterMarker)) return BlockStart.None;

        MdNode? list = null, paragraph = null;
        if (container.Type == MdNodeType.DefinitionList)
        {
            list = container;
            // A line that lazily continued the last definition is the next term when a marker follows it.
            if (_lazyLine == _lineNumber - 1 && _tip is { Type: MdNodeType.Paragraph } last
                && last.Parent is { Type: MdNodeType.DefinitionDetails } details && details.Parent == list)
            {
                paragraph = last;
            }
        }
        else if (container.Type == MdNodeType.Paragraph) paragraph = container;
        // One blank line may separate a term from its first definition, which makes the list loose.
        else if (container.LastChild is { Type: MdNodeType.Paragraph, Open: false } closed && closed.EndLine == _lineNumber - 2) paragraph = closed;
        else return BlockStart.None;

        MdNode? term = null;
        if (paragraph != null)
        {
            // An open paragraph may still start with link reference definitions, which are not part of the term.
            var content = paragraph.Open ? StripReferenceDefinitions(paragraph.Content!.ToString()) : paragraph.Content!.ToString();
            if (!MdUtil.IsBlank(content)) term = SplitTerm(paragraph, content, closeParagraph: paragraph == container);
            else if (list == null) return BlockStart.None;
        }

        int markerOffset = _cursor.Indent;
        CloseUnmatchedBlocks();
        // When the whole paragraph was the term, a list just before it takes the term, so that the blank lines that
        // usually separate the entries of a list do not split it.
        if (list == null && term != null && paragraph!.Parent == null && _tip.LastChild is { Type: MdNodeType.DefinitionList } previous)
            list = previous;
        if (list == null)
        {
            list = AddChild(MdNodeType.DefinitionList);
            list.ListData = new ListData();
        }
        else
        {
            list.Open = true;
            _tip = list;
        }
        if (term != null) list.AppendChild(term);

        var data = new ListData { MarkerOffset = markerOffset };
        ConsumeMarker(data, markerLength: 1);
        var definition = AddChild(MdNodeType.DefinitionDetails);
        definition.ListData = data;
        return BlockStart.Container;
    }

    /// <summary>
    /// Makes the last line of <paramref name="content"/>, the paragraph's text, a term, and leaves the lines before it in
    /// the paragraph, or removes the paragraph when nothing is left. A term is one line, as in markdown-it.
    /// </summary>
    private MdNode SplitTerm(MdNode paragraph, string content, bool closeParagraph)
    {
        int end = content.Length;
        while (end > 0 && content[end - 1] is '\n' or ' ' or '\t') end--;
        int lineStart = content.LastIndexOf('\n', end - 1) + 1;
        int termLine = paragraph.Open ? _lineNumber - 1 : paragraph.EndLine;
        var term = new MdNode(MdNodeType.DefinitionTerm)
        {
            Open = false,
            StartLine = termLine,
            EndLine = termLine,
            Content = new StringBuilder(content, lineStart, end - lineStart, end - lineStart),
        };

        if (content.AsSpan(0, lineStart).Trim(" \t\n").IsEmpty)
        {
            var parent = paragraph.Parent!;
            if (_tip == paragraph) _tip = parent;
            if (_previousTip == paragraph) _previousTip = parent;
            paragraph.Unlink();
            return term;
        }

        paragraph.Content = new StringBuilder(content, 0, lineStart, lineStart);
        if (closeParagraph)
        {
            // The paragraph ends before its last line. Its reference definitions are taken already, so it is closed
            // here rather than finalized again.
            paragraph.Open = false;
            paragraph.EndLine = termLine - 1;
            _tip = paragraph.Parent!;
        }
        return term;
    }

    /// <summary>
    /// A definition list is loose when a blank line separates a term from its definition, two definitions, or two blocks
    /// inside a definition; blank lines between entries are usual and leave it tight.
    /// </summary>
    private static void FinalizeDefinitionList(MdNode list)
    {
        var data = list.ListData!;
        // A list that takes more entries after a blank line is closed and reopened each time, so the check goes on from
        // the last entry it saw rather than starting again, which would take quadratic time. Entries only make a list
        // loose, never tight again.
        for (var child = data.CheckedUntil ?? list.FirstChild; child != null && data.Tight; child = child.Next)
        {
            if (child.Next is { Type: MdNodeType.DefinitionDetails } && EndsWithBlankLine(child)) data.Tight = false;
            if (child.Type != MdNodeType.DefinitionDetails) continue;
            for (var block = child.FirstChild; block != null; block = block.Next)
            {
                if (block.Next != null && EndsWithBlankLine(block)) { data.Tight = false; break; }
            }
        }
        data.CheckedUntil = list.LastChild;
        if (list.LastChild != null) list.EndLine = list.LastChild.EndLine;
    }
}
