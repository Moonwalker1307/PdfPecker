using System.Text;

namespace PdfPecker.Markdown;

/// <summary>
/// Block structure phase of the Markdown parser. Follows the CommonMark reference algorithm: each line is matched against
/// the open container blocks, then against block starts, and what is left is added to the innermost open block.
/// GFM tables and task list items are layered on top.
/// </summary>
internal sealed partial class BlockParser
{
    /// <summary>The deepest nesting of block containers (block quotes, lists, list items).</summary>
    internal const int MaxNesting = 100;

    private const int CodeIndent = LineCursor.CodeIndent;

    private readonly bool _gfm;
    private readonly MdNode _doc = new(MdNodeType.Document) { StartLine = 1 };
    private readonly Dictionary<string, LinkReference> _refmap = new(StringComparer.Ordinal);
    private readonly InlineParser _inlines;
    private LineCursor _cursor;

    // The innermost open block, the one that was innermost before the current line, and the deepest open container
    // that the current line continues.
    private MdNode _tip, _previousTip, _lastMatchedContainer;
    private int _lineNumber;
    private bool _allClosed;

    /// <summary>How a line relates to an open block: whether the block continues, and whether the line is used up.</summary>
    private enum Continuation { Matched, NotMatched, LineConsumed }

    /// <summary>What a block start recognizer found.</summary>
    private enum BlockStart { None, Container, Leaf }

    public BlockParser(bool gfm)
    {
        _gfm = gfm;
        _tip = _doc;
        _previousTip = _doc;
        _lastMatchedContainer = _doc;
        _inlines = new InlineParser(_refmap, gfm);
    }

    public MdNode Parse(string input)
    {
        int position = 0;
        while (TryReadLine(input, ref position, out string line)) IncorporateLine(line);

        while (_tip != _doc) Finalize(_tip, _lineNumber);
        Finalize(_doc, _lineNumber);
        ProcessInlines(_doc);
        return _doc;
    }

    /// <summary>Reads the next line; "\n", "\r" and "\r\n" all end one.</summary>
    private static bool TryReadLine(string input, ref int position, out string line)
    {
        line = "";
        if (position >= input.Length) return false;

        int length = input.AsSpan(position).IndexOfAny('\n', '\r');
        if (length < 0)
        {
            line = input.Substring(position);
            position = input.Length;
            return true;
        }
        line = input.Substring(position, length);
        position += length + 1;
        if (input[position - 1] == '\r' && position < input.Length && input[position] == '\n') position++;
        return true;
    }

    // ---- Line handling -------------------------------------------------------------------------------------------

    private void IncorporateLine(string line)
    {
        _previousTip = _tip;
        _lineNumber++;
        _cursor.Start(line.Contains('\0') ? line.Replace('\0', '�') : line);

        if (!TryMatchOpenContainers(out var container)) return;
        container = StartNewBlocks(container);
        AddRemainder(container);
    }

    /// <summary>
    /// Matches the line against each open container, outermost first, and returns the innermost one that continues.
    /// Returns false when the line closed a fenced code block and has nothing left to add.
    /// </summary>
    private bool TryMatchOpenContainers(out MdNode container)
    {
        container = _doc;
        while (container.LastChild is { Open: true } child)
        {
            container = child;
            _cursor.FindNextNonspace();
            var result = Continue(container);
            if (result == Continuation.LineConsumed) return false;
            if (result == Continuation.NotMatched)
            {
                container = container.Parent!;
                break;
            }
        }

        _allClosed = container == _previousTip;
        _lastMatchedContainer = container;
        return true;
    }

    /// <summary>Opens the new blocks that start on this line, as many as there are, and returns the innermost.</summary>
    private MdNode StartNewBlocks(MdNode container)
    {
        bool matchedLeaf = container.Type is MdNodeType.CodeBlock or MdNodeType.HtmlBlock;
        int depth = 0;
        for (var p = container; p != _doc; p = p.Parent!) depth++;
        while (!matchedLeaf)
        {
            // Past the nesting limit, further block markers are plain text: every line walks all open containers,
            // so unbounded nesting ("- - - - ...", "> > > ...") would make parsing quadratic.
            if (depth >= MaxNesting)
            {
                _cursor.FindNextNonspace();
                _cursor.AdvanceToNextNonspace();
                break;
            }
            _cursor.FindNextNonspace();
            if (!_cursor.IsIndented && !MaybeSpecial(_cursor.NextNonspaceChar))
            {
                _cursor.AdvanceToNextNonspace();
                break;
            }
            var start = TryBlockStarts(container);
            if (start == BlockStart.None)
            {
                _cursor.AdvanceToNextNonspace();
                break;
            }
            container = _tip;
            depth++;
            if (start == BlockStart.Leaf) matchedLeaf = true;
        }
        return container;
    }

    /// <summary>Adds what is left of the line to the innermost block; a paragraph may continue lazily.</summary>
    private void AddRemainder(MdNode container)
    {
        if (IsLazyContinuation)
        {
            AddLine();
            return;
        }

        CloseUnmatchedBlocks();
        var type = container.Type;
        if (type is MdNodeType.CodeBlock or MdNodeType.HtmlBlock or MdNodeType.Paragraph)
        {
            AddLine();
            if (type == MdNodeType.HtmlBlock && container.HtmlBlockType is >= HtmlBlockKind.RawText and <= HtmlBlockKind.CData
                && HtmlBlockCloses(container.HtmlBlockType, _cursor.Line.AsSpan(Math.Min(_cursor.Offset, _cursor.Line.Length))))
            {
                Finalize(container, _lineNumber);
            }
        }
        else if (type == MdNodeType.Table)
        {
            if (!MdUtil.IsBlank(_cursor.Line, _cursor.Offset)) AddTableRow(container, _cursor.Line.Substring(_cursor.Offset), header: false);
        }
        else if (!_cursor.AtEnd && !_cursor.IsBlank)
        {
            AddChild(MdNodeType.Paragraph);
            _cursor.AdvanceToNextNonspace();
            AddLine();
        }
    }

    /// <summary>
    /// True when the line does not continue every open block but the innermost one is a paragraph, which then takes
    /// the line as a lazy continuation line.
    /// </summary>
    private bool IsLazyContinuation => !_allClosed && !_cursor.IsBlank && _tip.Type == MdNodeType.Paragraph;

    private static bool MaybeSpecial(char c)
        => c is '#' or '`' or '~' or '*' or '+' or '_' or '=' or '<' or '>' or '-' or '|' or ':' or (>= '0' and <= '9');

    private void AddLine() => _cursor.AppendRemainderTo(_tip.Content ??= new StringBuilder());

    private MdNode AddChild(MdNodeType type)
    {
        while (!CanContain(_tip.Type, type)) Finalize(_tip, _lineNumber - 1);
        var block = new MdNode(type) { StartLine = _lineNumber };
        _tip.AppendChild(block);
        _tip = block;
        return block;
    }

    private static bool CanContain(MdNodeType parent, MdNodeType child) => parent switch
    {
        MdNodeType.Document or MdNodeType.BlockQuote or MdNodeType.Item => child != MdNodeType.Item,
        MdNodeType.List => child == MdNodeType.Item,
        _ => false,
    };

    /// <summary>Closes the blocks that were open before this line and do not continue on it.</summary>
    private void CloseUnmatchedBlocks()
    {
        if (_allClosed) return;
        while (_previousTip != _lastMatchedContainer)
        {
            var parent = _previousTip.Parent!;
            Finalize(_previousTip, _lineNumber - 1);
            _previousTip = parent;
        }
        _allClosed = true;
    }
}
