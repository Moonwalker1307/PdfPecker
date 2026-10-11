using System.Text;

namespace PdfPecker.Markdown;

/// <summary>
/// Block structure phase of the Markdown parser. Follows the CommonMark reference algorithm: each line is matched against
/// the open container blocks, then against block starts, and what is left is added to the innermost open block.
/// GFM tables and task list items are layered on top.
/// </summary>
internal sealed class BlockParser
{
    private const int CodeIndent = 4;
    private const int MaxTableColumns = 256;
    // Short rows are padded with empty cells; cap the total so a few bytes of input cannot create millions of cells.
    private const int MaxPaddedCells = 100_000;

    private readonly bool _gfm;
    private readonly MdNode _doc = new(MdNodeType.Document) { StartLine = 1 };
    private readonly Dictionary<string, LinkReference> _refmap = new(StringComparer.Ordinal);
    private readonly InlineParser _inlines;

    private MdNode _tip, _oldtip, _lastMatchedContainer;
    private string _line = "";
    private int _lineNumber;
    private int _offset, _column, _nextNonspace, _nextNonspaceColumn, _indent;
    private bool _indented, _blank, _partiallyConsumedTab, _allClosed;
    private int _paddedCells;

    public BlockParser(bool gfm)
    {
        _gfm = gfm;
        _tip = _doc;
        _oldtip = _doc;
        _lastMatchedContainer = _doc;
        _inlines = new InlineParser(_refmap, gfm);
    }

    public MdNode Parse(string input)
    {
        int pos = 0, len = input.Length;
        while (pos < len)
        {
            int eol = input.AsSpan(pos).IndexOfAny('\n', '\r');
            if (eol < 0)
            {
                IncorporateLine(input.Substring(pos));
                break;
            }
            IncorporateLine(input.Substring(pos, eol));
            pos += eol + 1;
            if (input[pos - 1] == '\r' && pos < len && input[pos] == '\n') pos++;
        }
        while (_tip != _doc) Finalize(_tip, _lineNumber);
        Finalize(_doc, _lineNumber);
        ProcessInlines(_doc);
        return _doc;
    }

    // ---- Line handling -------------------------------------------------------------------------------------------

    private void IncorporateLine(string line)
    {
        var container = _doc;
        _oldtip = _tip;
        _offset = 0;
        _column = 0;
        _blank = false;
        _partiallyConsumedTab = false;
        _lineNumber++;
        if (line.Contains('\0')) line = line.Replace('\0', '�');
        _line = line;

        // Match the line against each open container, innermost last.
        bool allMatched = true;
        MdNode? last;
        while ((last = container.LastChild) != null && last.Open)
        {
            container = last;
            FindNextNonspace();
            switch (Continue(container))
            {
                case 0: break;
                case 1: allMatched = false; break;
                default: return; // a closing code fence consumed the line
            }
            if (!allMatched)
            {
                container = container.Parent!;
                break;
            }
        }

        _allClosed = container == _oldtip;
        _lastMatchedContainer = container;

        bool matchedLeaf = container.Type is MdNodeType.CodeBlock or MdNodeType.HtmlBlock;
        while (!matchedLeaf)
        {
            FindNextNonspace();
            if (!_indented && !MaybeSpecial(_nextNonspace < _line.Length ? _line[_nextNonspace] : '\0'))
            {
                AdvanceNextNonspace();
                break;
            }
            int res = TryBlockStarts(container);
            if (res == 0)
            {
                AdvanceNextNonspace();
                break;
            }
            container = _tip;
            if (res == 2) matchedLeaf = true;
        }

        // What is left is text for the innermost block; a paragraph may continue lazily.
        if (!_allClosed && !_blank && _tip.Type == MdNodeType.Paragraph)
        {
            AddLine();
        }
        else
        {
            CloseUnmatchedBlocks();
            var t = container.Type;
            if (t is MdNodeType.CodeBlock or MdNodeType.HtmlBlock or MdNodeType.Paragraph)
            {
                AddLine();
                if (t == MdNodeType.HtmlBlock && container.HtmlBlockType is >= 1 and <= 5
                    && HtmlBlockCloses(container.HtmlBlockType, _line.AsSpan(Math.Min(_offset, _line.Length))))
                {
                    Finalize(container, _lineNumber);
                }
            }
            else if (t == MdNodeType.Table)
            {
                if (!MdUtil.IsBlank(_line, _offset)) AddTableRow(container, _line.Substring(_offset), header: false);
            }
            else if (_offset < _line.Length && !_blank)
            {
                AddChild(MdNodeType.Paragraph);
                AdvanceNextNonspace();
                AddLine();
            }
        }
    }

    private static bool MaybeSpecial(char c)
        => c is '#' or '`' or '~' or '*' or '+' or '_' or '=' or '<' or '>' or '-' or '|' or ':' or (>= '0' and <= '9');

    private static bool HtmlBlockCloses(int type, ReadOnlySpan<char> s) => type switch
    {
        1 => MdRegex.HtmlBlockClose1().IsMatch(s),
        2 => s.IndexOf("-->".AsSpan()) >= 0,
        3 => s.IndexOf("?>".AsSpan()) >= 0,
        4 => s.IndexOf('>') >= 0,
        5 => s.IndexOf("]]>".AsSpan()) >= 0,
        _ => false,
    };

    private void FindNextNonspace()
    {
        int i = _offset, cols = _column;
        char c = '\0';
        while (i < _line.Length)
        {
            c = _line[i];
            if (c == ' ') { i++; cols++; }
            else if (c == '\t') { i++; cols += 4 - cols % 4; }
            else break;
        }
        _blank = i >= _line.Length;
        _nextNonspace = i;
        _nextNonspaceColumn = cols;
        _indent = _nextNonspaceColumn - _column;
        _indented = _indent >= CodeIndent;
    }

    private void AdvanceNextNonspace()
    {
        _offset = _nextNonspace;
        _column = _nextNonspaceColumn;
        _partiallyConsumedTab = false;
    }

    private void AdvanceOffset(int count, bool columns)
    {
        while (count > 0 && _offset < _line.Length)
        {
            char c = _line[_offset];
            if (c == '\t')
            {
                int charsToTab = 4 - _column % 4;
                if (columns)
                {
                    _partiallyConsumedTab = charsToTab > count;
                    int charsToAdvance = Math.Min(charsToTab, count);
                    _column += charsToAdvance;
                    _offset += _partiallyConsumedTab ? 0 : 1;
                    count -= charsToAdvance;
                }
                else
                {
                    _partiallyConsumedTab = false;
                    _column += charsToTab;
                    _offset++;
                    count--;
                }
            }
            else
            {
                _partiallyConsumedTab = false;
                _offset++;
                _column++;
                count--;
            }
        }
    }

    private char Peek(int pos) => pos < _line.Length ? _line[pos] : '\0';

    private void AddLine()
    {
        var sb = _tip.Content ??= new StringBuilder();
        if (_partiallyConsumedTab)
        {
            _offset++; // skip over the tab and add the columns it still covers as spaces
            sb.Append(' ', 4 - _column % 4);
        }
        if (_offset < _line.Length) sb.Append(_line, _offset, _line.Length - _offset);
        sb.Append('\n');
    }

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

    private void CloseUnmatchedBlocks()
    {
        if (_allClosed) return;
        while (_oldtip != _lastMatchedContainer)
        {
            var parent = _oldtip.Parent!;
            Finalize(_oldtip, _lineNumber - 1);
            _oldtip = parent;
        }
        _allClosed = true;
    }

    // ---- Continuation ----------------------------------------------------------------------------------------------

    /// <summary>0 = matched, 1 = not matched, 2 = matched and the line is fully consumed.</summary>
    private int Continue(MdNode container)
    {
        switch (container.Type)
        {
            case MdNodeType.BlockQuote:
                if (!_indented && Peek(_nextNonspace) == '>')
                {
                    AdvanceNextNonspace();
                    AdvanceOffset(1, false);
                    if (MdUtil.IsSpaceOrTab(Peek(_offset))) AdvanceOffset(1, true);
                    return 0;
                }
                return 1;

            case MdNodeType.Item:
            {
                var data = container.ListData!;
                if (_blank)
                {
                    if (container.FirstChild == null) return 1; // a blank line after an empty item ends it
                    AdvanceNextNonspace();
                }
                else if (_indent >= data.MarkerOffset + data.Padding)
                {
                    AdvanceOffset(data.MarkerOffset + data.Padding, true);
                }
                else return 1;
                return 0;
            }

            case MdNodeType.Heading:
            case MdNodeType.ThematicBreak:
                return 1;

            case MdNodeType.CodeBlock:
                if (container.IsFenced)
                {
                    if (_indent <= 3 && Peek(_nextNonspace) == container.FenceChar)
                    {
                        int n = 0, p = _nextNonspace;
                        while (p < _line.Length && _line[p] == container.FenceChar) { n++; p++; }
                        if (n >= container.FenceLength && MdUtil.IsBlank(_line, p))
                        {
                            Finalize(container, _lineNumber);
                            return 2;
                        }
                    }
                    // Skip the optional spaces of the fence's own indentation.
                    for (int i = container.FenceOffset; i > 0 && MdUtil.IsSpaceOrTab(Peek(_offset)); i--) AdvanceOffset(1, true);
                }
                else
                {
                    if (_indent >= CodeIndent) AdvanceOffset(CodeIndent, true);
                    else if (_blank) AdvanceNextNonspace();
                    else return 1;
                }
                return 0;

            case MdNodeType.HtmlBlock:
                return _blank && container.HtmlBlockType is 6 or 7 ? 1 : 0;

            case MdNodeType.Paragraph:
            case MdNodeType.Table:
                return _blank ? 1 : 0;

            default:
                return 0;
        }
    }

    // ---- Block starts ----------------------------------------------------------------------------------------------

    /// <summary>0 = no match, 1 = matched a container start, 2 = matched a leaf start.</summary>
    private int TryBlockStarts(MdNode container)
    {
        int r;
        if ((r = BlockQuoteStart()) != 0) return r;
        if ((r = AtxHeadingStart()) != 0) return r;
        if ((r = FencedCodeStart()) != 0) return r;
        if ((r = HtmlBlockStart(container)) != 0) return r;
        if (_gfm && (r = TableStart(container)) != 0) return r;
        if ((r = SetextHeadingStart(container)) != 0) return r;
        if ((r = ThematicBreakStart()) != 0) return r;
        if ((r = ListItemStart(container)) != 0) return r;
        return IndentedCodeStart();
    }

    private int BlockQuoteStart()
    {
        if (_indented || Peek(_nextNonspace) != '>') return 0;
        AdvanceNextNonspace();
        AdvanceOffset(1, false);
        if (MdUtil.IsSpaceOrTab(Peek(_offset))) AdvanceOffset(1, true);
        CloseUnmatchedBlocks();
        AddChild(MdNodeType.BlockQuote);
        return 1;
    }

    private int AtxHeadingStart()
    {
        if (_indented) return 0;
        int p = _nextNonspace, level = 0;
        while (p < _line.Length && _line[p] == '#' && level < 7) { p++; level++; }
        if (level is 0 or > 6) return 0;
        if (p < _line.Length && !MdUtil.IsSpaceOrTab(_line[p])) return 0;

        AdvanceNextNonspace();
        AdvanceOffset(level, false);
        CloseUnmatchedBlocks();
        var heading = AddChild(MdNodeType.Heading);
        heading.Level = level;

        // Strip the optional closing sequence of #s (it must be preceded by a space, or be all there is).
        var text = _line.AsSpan(Math.Min(_offset, _line.Length)).TrimEnd(" \t");
        int end = text.Length;
        while (end > 0 && text[end - 1] == '#') end--;
        if (end < text.Length && (end == 0 || MdUtil.IsSpaceOrTab(text[end - 1]))) text = text.Slice(0, end);
        heading.Content = new StringBuilder(text.Trim(" \t").ToString());
        AdvanceOffset(_line.Length - _offset, false);
        return 2;
    }

    private int FencedCodeStart()
    {
        if (_indented) return 0;
        char c = Peek(_nextNonspace);
        if (c != '`' && c != '~') return 0;
        int p = _nextNonspace, n = 0;
        while (p < _line.Length && _line[p] == c) { p++; n++; }
        if (n < 3) return 0;
        if (c == '`' && _line.IndexOf('`', p) >= 0) return 0; // a backtick fence's info string cannot contain backticks

        CloseUnmatchedBlocks();
        var block = AddChild(MdNodeType.CodeBlock);
        block.IsFenced = true;
        block.FenceLength = n;
        block.FenceChar = c;
        block.FenceOffset = _indent;
        AdvanceNextNonspace();
        AdvanceOffset(n, false);
        return 2;
    }

    private int HtmlBlockStart(MdNode container)
    {
        if (_indented || Peek(_nextNonspace) != '<') return 0;
        var s = _line.AsSpan(_nextNonspace);
        int type = 0;
        if (MdRegex.HtmlBlockOpen1().IsMatch(s)) type = 1;
        else if (s.StartsWith("<!--")) type = 2;
        else if (s.StartsWith("<?")) type = 3;
        else if (s.Length > 2 && s[1] == '!' && char.IsAsciiLetter(s[2])) type = 4;
        else if (s.StartsWith("<![CDATA[")) type = 5;
        else if (MdRegex.HtmlBlockOpen6().IsMatch(s)) type = 6;
        else if (container.Type != MdNodeType.Paragraph && !(!_allClosed && !_blank && _tip.Type == MdNodeType.Paragraph)
                 && MdRegex.HtmlBlockOpen7().IsMatch(s)) type = 7;
        if (type == 0) return 0;

        CloseUnmatchedBlocks();
        // The offset is not advanced: leading spaces belong to the HTML block.
        var block = AddChild(MdNodeType.HtmlBlock);
        block.HtmlBlockType = type;
        return 2;
    }

    private int SetextHeadingStart(MdNode container)
    {
        if (_indented || container.Type != MdNodeType.Paragraph) return 0;
        char c = Peek(_nextNonspace);
        if (c != '=' && c != '-') return 0;
        int p = _nextNonspace;
        while (p < _line.Length && _line[p] == c) p++;
        if (!MdUtil.IsBlank(_line, p)) return 0;

        CloseUnmatchedBlocks();
        var content = StripReferenceDefinitions(container.Content!.ToString());
        if (content.Length == 0) return 0;
        var heading = new MdNode(MdNodeType.Heading)
        {
            StartLine = container.StartLine,
            Level = c == '=' ? 1 : 2,
            Content = new StringBuilder(content),
        };
        container.InsertAfter(heading);
        container.Unlink();
        _tip = heading;
        AdvanceOffset(_line.Length - _offset, false);
        return 2;
    }

    private int ThematicBreakStart()
    {
        if (_indented) return 0;
        char c = Peek(_nextNonspace);
        if (c != '*' && c != '-' && c != '_') return 0;
        int count = 0;
        for (int i = _nextNonspace; i < _line.Length; i++)
        {
            char d = _line[i];
            if (d == c) count++;
            else if (!MdUtil.IsSpaceOrTab(d)) return 0;
        }
        if (count < 3) return 0;
        CloseUnmatchedBlocks();
        AddChild(MdNodeType.ThematicBreak);
        AdvanceOffset(_line.Length - _offset, false);
        return 2;
    }

    private int ListItemStart(MdNode container)
    {
        if (_indented && container.Type != MdNodeType.List) return 0;
        var data = ParseListMarker(container);
        if (data == null) return 0;
        CloseUnmatchedBlocks();
        if (_tip.Type != MdNodeType.List || !ListsMatch(container.ListData!, data))
        {
            var list = AddChild(MdNodeType.List);
            list.ListData = data;
        }
        var item = AddChild(MdNodeType.Item);
        item.ListData = data;
        return 1;
    }

    private static bool ListsMatch(ListData a, ListData b)
        => a.Ordered == b.Ordered && a.Delimiter == b.Delimiter && a.BulletChar == b.BulletChar;

    private ListData? ParseListMarker(MdNode container)
    {
        if (_indent >= 4) return null;
        var data = new ListData { MarkerOffset = _indent };
        int p = _nextNonspace;
        char c = Peek(p);
        int markerLength;
        if (c is '*' or '+' or '-')
        {
            data.BulletChar = c;
            markerLength = 1;
        }
        else
        {
            int digits = 0, start = 0;
            while (digits < 10 && char.IsAsciiDigit(Peek(p + digits)))
            {
                start = start * 10 + (Peek(p + digits) - '0');
                digits++;
            }
            if (digits is 0 or > 9) return null;
            char delim = Peek(p + digits);
            if (delim != '.' && delim != ')') return null;
            // An ordered list can only interrupt a paragraph when it starts with 1.
            if (container.Type == MdNodeType.Paragraph && start != 1) return null;
            data.Ordered = true;
            data.Start = start;
            data.Delimiter = delim;
            markerLength = digits + 1;
        }

        char next = Peek(p + markerLength);
        if (!(next == '\0' || next == '\t' || next == ' ')) return null;
        // A list item interrupting a paragraph cannot start with a blank line.
        if (container.Type == MdNodeType.Paragraph && MdUtil.IsBlank(_line, p + markerLength)) return null;

        AdvanceNextNonspace();
        AdvanceOffset(markerLength, true);
        int spacesStartCol = _column, spacesStartOffset = _offset;
        do
        {
            AdvanceOffset(1, true);
            next = Peek(_offset);
        } while (_column - spacesStartCol < 5 && MdUtil.IsSpaceOrTab(next));
        bool blankItem = _offset >= _line.Length;
        int spacesAfterMarker = _column - spacesStartCol;
        if (spacesAfterMarker >= 5 || spacesAfterMarker < 1 || blankItem)
        {
            data.Padding = markerLength + 1;
            _column = spacesStartCol;
            _offset = spacesStartOffset;
            if (MdUtil.IsSpaceOrTab(Peek(_offset))) AdvanceOffset(1, true);
        }
        else data.Padding = markerLength + spacesAfterMarker;
        return data;
    }

    private int IndentedCodeStart()
    {
        if (!_indented || _tip.Type == MdNodeType.Paragraph || _blank) return 0;
        AdvanceOffset(CodeIndent, true);
        CloseUnmatchedBlocks();
        AddChild(MdNodeType.CodeBlock);
        return 2;
    }

    // ---- GFM tables ------------------------------------------------------------------------------------------------

    private int TableStart(MdNode container)
    {
        if (_indented || container.Type != MdNodeType.Paragraph) return 0;
        var aligns = ParseDelimiterRow(_line.AsSpan(_nextNonspace));
        if (aligns == null) return 0;

        // The header row is the paragraph's last line (after any link reference definitions).
        var content = container.Content!.ToString();
        var rest = StripReferenceDefinitions(content).TrimEnd('\n');
        if (rest.Length == 0) return 0;
        int lastNl = rest.LastIndexOf('\n');
        var headerLine = rest.Substring(lastNl + 1);
        var headerCells = SplitRow(headerLine);
        if (headerCells.Count != aligns.Length || aligns.Length > MaxTableColumns) return 0;
        if (!headerLine.Contains('|') && !_line.Contains('|')) return 0;

        CloseUnmatchedBlocks();
        var table = new MdNode(MdNodeType.Table) { StartLine = _lineNumber - 1, Aligns = aligns };
        container.InsertAfter(table);
        if (lastNl < 0)
        {
            container.Unlink();
        }
        else
        {
            // Lines before the header stay a paragraph (its reference definitions are already registered).
            container.Content = new StringBuilder(rest.Substring(0, lastNl + 1));
            container.Open = false;
            container.EndLine = _lineNumber - 2;
        }
        _tip = table;
        AddTableRow(table, headerLine, header: true);
        AdvanceOffset(_line.Length - _offset, false);
        return 2;
    }

    private static TableAlign[]? ParseDelimiterRow(ReadOnlySpan<char> line)
    {
        line = line.Trim(" \t");
        if (line.Length == 0) return null;
        bool hasPipe = line.Contains('|');
        if (line[0] == '|') line = line.Slice(1);
        if (line.Length > 0 && line[^1] == '|' && hasPipe) line = line.Slice(0, line.Length - 1);
        var aligns = new List<TableAlign>();
        foreach (var range in line.Split('|'))
        {
            var cell = line[range].Trim(" \t");
            if (cell.Length == 0) return null;
            bool left = cell[0] == ':', right = cell[^1] == ':';
            var dashes = cell.Slice(left ? 1 : 0, cell.Length - (left ? 1 : 0) - (right ? 1 : 0));
            if (dashes.Length == 0) return null;
            foreach (var c in dashes) if (c != '-') return null;
            aligns.Add(left && right ? TableAlign.Center : left ? TableAlign.Left : right ? TableAlign.Right : TableAlign.None);
        }
        return aligns.ToArray();
    }

    /// <summary>Splits a table row on unescaped pipes. "\|" becomes a literal pipe in the cell text.</summary>
    private static List<string> SplitRow(string line)
    {
        var cells = new List<string>();
        var s = line.AsSpan().Trim(" \t");
        if (s.Length > 0 && s[0] == '|') s = s.Slice(1);
        var sb = new StringBuilder();
        bool trailingPipe = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            trailingPipe = false;
            if (c == '\\' && i + 1 < s.Length && s[i + 1] == '|')
            {
                sb.Append('|');
                i++;
            }
            else if (c == '|')
            {
                cells.Add(sb.ToString().Trim(' ', '\t'));
                sb.Clear();
                trailingPipe = true;
            }
            else sb.Append(c);
        }
        if (!trailingPipe || sb.Length > 0) cells.Add(sb.ToString().Trim(' ', '\t'));
        return cells;
    }

    private void AddTableRow(MdNode table, string line, bool header)
    {
        var cells = SplitRow(line);
        var row = new MdNode(MdNodeType.TableRow) { IsHeader = header, Open = false };
        var aligns = table.Aligns!;
        int count = aligns.Length;
        if (cells.Count < count)
        {
            _paddedCells += count - cells.Count;
            if (_paddedCells > MaxPaddedCells) count = Math.Max(cells.Count, 1);
        }
        for (int i = 0; i < count; i++)
        {
            var cell = new MdNode(MdNodeType.TableCell)
            {
                IsHeader = header,
                Align = aligns[i],
                Content = new StringBuilder(i < cells.Count ? cells[i] : ""),
                Open = false,
            };
            row.AppendChild(cell);
        }
        table.AppendChild(row);
    }

    // ---- Finalization ----------------------------------------------------------------------------------------------

    private void Finalize(MdNode block, int lineNumber)
    {
        var above = block.Parent;
        block.Open = false;
        block.EndLine = lineNumber;
        switch (block.Type)
        {
            case MdNodeType.Paragraph:
            {
                var content = block.Content?.ToString() ?? "";
                var rest = StripReferenceDefinitions(content);
                if (rest.Length != content.Length)
                {
                    if (MdUtil.IsBlank(rest)) block.Unlink();
                    else block.Content = new StringBuilder(rest);
                }
                break;
            }
            case MdNodeType.CodeBlock:
            {
                var content = block.Content?.ToString() ?? "";
                if (block.IsFenced)
                {
                    int nl = content.IndexOf('\n');
                    var firstLine = nl < 0 ? content : content.Substring(0, nl);
                    block.Info = MdUtil.Unescape(firstLine.Trim(' ', '\t'));
                    block.Literal = nl < 0 ? "" : content.Substring(nl + 1);
                }
                else
                {
                    // Trailing blank lines are not part of an indented code block.
                    var lines = content.Split('\n');
                    int n = lines.Length, blankLines = 0;
                    while (n > 0 && MdUtil.IsBlank(lines[n - 1])) n--;
                    for (int i = n; i < lines.Length - 1; i++) blankLines++;
                    block.Literal = string.Join("\n", lines, 0, n) + "\n";
                    block.EndLine = Math.Max(block.StartLine, lineNumber - blankLines);
                }
                block.Content = null;
                break;
            }
            case MdNodeType.HtmlBlock:
            {
                var content = block.Content?.ToString() ?? "";
                // Drop the trailing newlines; spaces on the last line are kept.
                int end = content.Length;
                while (end > 0 && content[end - 1] == '\n') end--;
                block.Literal = content.Substring(0, end);
                block.Content = null;
                break;
            }
            case MdNodeType.Item:
                block.EndLine = block.LastChild?.EndLine ?? block.StartLine;
                break;
            case MdNodeType.List:
            {
                var data = block.ListData!;
                for (var item = block.FirstChild; item != null && data.Tight; item = item.Next)
                {
                    if (item.Next != null && EndsWithBlankLine(item)) { data.Tight = false; break; }
                    for (var sub = item.FirstChild; sub != null; sub = sub.Next)
                    {
                        if (sub.Next != null && EndsWithBlankLine(sub)) { data.Tight = false; break; }
                    }
                }
                if (block.LastChild != null) block.EndLine = block.LastChild.EndLine;
                break;
            }
        }
        _tip = above!;
    }

    private static bool EndsWithBlankLine(MdNode block)
        => block.Next != null && block.EndLine != block.Next.StartLine - 1;

    private string StripReferenceDefinitions(string content)
    {
        int pos = 0;
        while (pos < content.Length && content[pos] == '[')
        {
            int consumed = _inlines.ParseReference(content, pos);
            if (consumed == 0) break;
            pos += consumed;
        }
        return pos == 0 ? content : content.Substring(pos);
    }

    // ---- Inlines ---------------------------------------------------------------------------------------------------

    private void ProcessInlines(MdNode root)
    {
        // Depth-first walk without recursion, so deeply nested input cannot overflow the stack.
        var stack = new Stack<MdNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node.Type)
            {
                case MdNodeType.Paragraph:
                    if (_gfm && node.Parent?.Type == MdNodeType.Item && node.Parent.FirstChild == node) ExtractTaskMarker(node);
                    goto case MdNodeType.Heading;
                case MdNodeType.Heading:
                case MdNodeType.TableCell:
                    _inlines.Parse(node);
                    continue;
            }
            for (var child = node.LastChild; child != null; child = child.Prev) stack.Push(child);
        }
    }

    private static void ExtractTaskMarker(MdNode paragraph)
    {
        var sb = paragraph.Content;
        if (sb == null || sb.Length < 4 || sb[0] != '[' || sb[2] != ']' || !(sb[3] is ' ' or '\t' or '\n')) return;
        char mark = sb[1];
        if (mark is not (' ' or 'x' or 'X')) return;
        paragraph.Parent!.Checked = mark != ' ';
        sb.Remove(0, 4);
    }
}

internal readonly record struct LinkReference(string Destination, string Title);
