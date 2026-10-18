using System.Text;

namespace PdfPecker.Markdown;

// Block starts: does a new block begin at the next non-space character of the line?
internal sealed partial class BlockParser
{
    private const int MinFenceLength = 3;
    private const int MaxHeadingLevel = 6;
    private const int MinThematicBreakMarks = 3;

    /// <summary>The seven kinds of HTML block of the CommonMark spec, stored in <see cref="MdNode.HtmlBlockType"/>.</summary>
    private static class HtmlBlockKind
    {
        /// <summary>script, pre, style and textarea; ends at the closing tag.</summary>
        public const int RawText = 1;

        /// <summary>A comment; ends at "--&gt;".</summary>
        public const int Comment = 2;

        /// <summary>A processing instruction; ends at "?&gt;".</summary>
        public const int ProcessingInstruction = 3;

        /// <summary>A declaration such as DOCTYPE; ends at "&gt;".</summary>
        public const int Declaration = 4;

        /// <summary>A CDATA section; ends at "]]&gt;".</summary>
        public const int CData = 5;

        /// <summary>A block-level tag; ends at a blank line.</summary>
        public const int BlockTag = 6;

        /// <summary>Any other complete tag on a line of its own; ends at a blank line and cannot interrupt a paragraph.</summary>
        public const int AnyTag = 7;
    }

    private BlockStart TryBlockStarts(MdNode container)
    {
        var start = BlockQuoteStart();
        if (start == BlockStart.None) start = AtxHeadingStart();
        if (start == BlockStart.None) start = FencedCodeStart();
        if (start == BlockStart.None) start = HtmlBlockStart(container);
        if (start == BlockStart.None && _gfm) start = TableStart(container);
        if (start == BlockStart.None) start = SetextHeadingStart(container);
        if (start == BlockStart.None) start = ThematicBreakStart();
        if (start == BlockStart.None && _gfm) start = FootnoteDefinitionStart();
        if (start == BlockStart.None && _gfm) start = DefinitionStart(container);
        if (start == BlockStart.None) start = ListItemStart(container);
        if (start == BlockStart.None) start = IndentedCodeStart();
        return start;
    }

    private BlockStart BlockQuoteStart()
    {
        if (_cursor.IsIndented || _cursor.NextNonspaceChar != '>') return BlockStart.None;
        ConsumeBlockQuoteMarker();
        CloseUnmatchedBlocks();
        AddChild(MdNodeType.BlockQuote);
        return BlockStart.Container;
    }

    private BlockStart AtxHeadingStart()
    {
        if (_cursor.IsIndented) return BlockStart.None;
        // One more than the maximum is enough to tell that the run is too long.
        int level = _cursor.CountRun(_cursor.NextNonspace, '#', MaxHeadingLevel + 1);
        if (level is 0 or > MaxHeadingLevel) return BlockStart.None;
        int afterMarker = _cursor.NextNonspace + level;
        if (afterMarker < _cursor.Line.Length && !MdUtil.IsSpaceOrTab(_cursor.Line[afterMarker])) return BlockStart.None;

        _cursor.AdvanceToNextNonspace();
        _cursor.Advance(level, columns: false);
        CloseUnmatchedBlocks();
        var heading = AddChild(MdNodeType.Heading);
        heading.Level = level;
        heading.Content = new StringBuilder(HeadingText(_cursor.Line.AsSpan(Math.Min(_cursor.Offset, _cursor.Line.Length))).ToString());
        _cursor.AdvanceToEnd();
        return BlockStart.Leaf;
    }

    /// <summary>The text of an ATX heading without the optional closing sequence of #s, which must be preceded by a space or be all there is.</summary>
    private static ReadOnlySpan<char> HeadingText(ReadOnlySpan<char> text)
    {
        text = text.TrimEnd(" \t");
        int end = text.Length;
        while (end > 0 && text[end - 1] == '#') end--;
        if (end < text.Length && (end == 0 || MdUtil.IsSpaceOrTab(text[end - 1]))) text = text.Slice(0, end);
        return text.Trim(" \t");
    }

    private BlockStart FencedCodeStart()
    {
        if (_cursor.IsIndented) return BlockStart.None;
        char fenceChar = _cursor.NextNonspaceChar;
        if (fenceChar != '`' && fenceChar != '~') return BlockStart.None;
        int length = _cursor.CountRun(_cursor.NextNonspace, fenceChar);
        if (length < MinFenceLength) return BlockStart.None;
        // A backtick fence's info string cannot contain backticks.
        if (fenceChar == '`' && _cursor.Line.IndexOf('`', _cursor.NextNonspace + length) >= 0) return BlockStart.None;

        CloseUnmatchedBlocks();
        var block = AddChild(MdNodeType.CodeBlock);
        block.IsFenced = true;
        block.FenceLength = length;
        block.FenceChar = fenceChar;
        block.FenceOffset = _cursor.Indent;
        _cursor.AdvanceToNextNonspace();
        _cursor.Advance(length, columns: false);
        return BlockStart.Leaf;
    }

    private BlockStart HtmlBlockStart(MdNode container)
    {
        if (_cursor.IsIndented || _cursor.NextNonspaceChar != '<') return BlockStart.None;
        int kind = HtmlBlockKindAt(_cursor.Line.AsSpan(_cursor.NextNonspace), container);
        if (kind == 0) return BlockStart.None;

        CloseUnmatchedBlocks();
        // The offset is not advanced: leading spaces belong to the HTML block.
        var block = AddChild(MdNodeType.HtmlBlock);
        block.HtmlBlockType = kind;
        return BlockStart.Leaf;
    }

    /// <summary>The kind of HTML block that starts with <paramref name="s"/>, or 0 if none does.</summary>
    private int HtmlBlockKindAt(ReadOnlySpan<char> s, MdNode container)
    {
        if (MdRegex.HtmlBlockOpen1().IsMatch(s)) return HtmlBlockKind.RawText;
        if (s.StartsWith("<!--")) return HtmlBlockKind.Comment;
        if (s.StartsWith("<?")) return HtmlBlockKind.ProcessingInstruction;
        if (s.Length > 2 && s[1] == '!' && char.IsAsciiLetter(s[2])) return HtmlBlockKind.Declaration;
        if (s.StartsWith("<![CDATA[")) return HtmlBlockKind.CData;
        if (MdRegex.HtmlBlockOpen6().IsMatch(s)) return HtmlBlockKind.BlockTag;
        if (container.Type != MdNodeType.Paragraph && !IsLazyContinuation && MdRegex.HtmlBlockOpen7().IsMatch(s)) return HtmlBlockKind.AnyTag;
        return 0;
    }

    /// <summary>Whether the line contains the marker that ends an HTML block of a kind that has one.</summary>
    private static bool HtmlBlockCloses(int kind, ReadOnlySpan<char> s) => kind switch
    {
        HtmlBlockKind.RawText => MdRegex.HtmlBlockClose1().IsMatch(s),
        HtmlBlockKind.Comment => s.IndexOf("-->".AsSpan()) >= 0,
        HtmlBlockKind.ProcessingInstruction => s.IndexOf("?>".AsSpan()) >= 0,
        HtmlBlockKind.Declaration => s.IndexOf('>') >= 0,
        HtmlBlockKind.CData => s.IndexOf("]]>".AsSpan()) >= 0,
        _ => false,
    };

    private BlockStart SetextHeadingStart(MdNode container)
    {
        if (_cursor.IsIndented || container.Type != MdNodeType.Paragraph) return BlockStart.None;
        char underline = _cursor.NextNonspaceChar;
        if (underline != '=' && underline != '-') return BlockStart.None;
        int length = _cursor.CountRun(_cursor.NextNonspace, underline);
        if (!MdUtil.IsBlank(_cursor.Line, _cursor.NextNonspace + length)) return BlockStart.None;

        CloseUnmatchedBlocks();
        var content = StripReferenceDefinitions(container.Content!.ToString());
        if (content.Length == 0) return BlockStart.None;
        var heading = new MdNode(MdNodeType.Heading)
        {
            StartLine = container.StartLine,
            Level = underline == '=' ? 1 : 2,
            Content = new StringBuilder(content),
        };
        container.InsertAfter(heading);
        container.Unlink();
        _tip = heading;
        _cursor.AdvanceToEnd();
        return BlockStart.Leaf;
    }

    private BlockStart ThematicBreakStart()
    {
        if (_cursor.IsIndented) return BlockStart.None;
        char mark = _cursor.NextNonspaceChar;
        if (mark != '*' && mark != '-' && mark != '_') return BlockStart.None;
        int count = 0;
        for (int i = _cursor.NextNonspace; i < _cursor.Line.Length; i++)
        {
            char c = _cursor.Line[i];
            if (c == mark) count++;
            else if (!MdUtil.IsSpaceOrTab(c)) return BlockStart.None;
        }
        if (count < MinThematicBreakMarks) return BlockStart.None;
        CloseUnmatchedBlocks();
        AddChild(MdNodeType.ThematicBreak);
        _cursor.AdvanceToEnd();
        return BlockStart.Leaf;
    }

    private BlockStart IndentedCodeStart()
    {
        if (!_cursor.IsIndented || _tip.Type == MdNodeType.Paragraph || _cursor.IsBlank) return BlockStart.None;
        _cursor.Advance(CodeIndent, columns: true);
        CloseUnmatchedBlocks();
        AddChild(MdNodeType.CodeBlock);
        return BlockStart.Leaf;
    }
}
