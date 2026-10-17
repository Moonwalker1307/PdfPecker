using System.Text;

namespace PdfPecker.Markdown;

// GFM tables: a header row, a delimiter row of dashes and colons, then body rows.
internal sealed partial class BlockParser
{
    private const int MaxTableColumns = 256;

    // Short rows are padded with empty cells; cap the total so a few bytes of input cannot create millions of cells.
    private const int MaxPaddedCells = 100_000;

    private int _paddedCells;

    private BlockStart TableStart(MdNode container)
    {
        if (_cursor.IsIndented || container.Type != MdNodeType.Paragraph) return BlockStart.None;
        var aligns = ParseDelimiterRow(_cursor.Line.AsSpan(_cursor.NextNonspace));
        if (aligns == null) return BlockStart.None;

        // The header row is the paragraph's last line (after any link reference definitions).
        var content = container.Content!.ToString();
        var rest = StripReferenceDefinitions(content).TrimEnd('\n');
        if (rest.Length == 0) return BlockStart.None;
        int lastNewline = rest.LastIndexOf('\n');
        var headerLine = rest.Substring(lastNewline + 1);
        var headerCells = SplitRow(headerLine);
        if (headerCells.Count != aligns.Length || aligns.Length > MaxTableColumns) return BlockStart.None;
        if (!headerLine.Contains('|') && !_cursor.Line.Contains('|')) return BlockStart.None;

        CloseUnmatchedBlocks();
        var table = new MdNode(MdNodeType.Table) { StartLine = _lineNumber - 1, Aligns = aligns };
        container.InsertAfter(table);
        if (lastNewline < 0)
        {
            container.Unlink();
        }
        else
        {
            // Lines before the header stay a paragraph (its reference definitions are already registered).
            container.Content = new StringBuilder(rest.Substring(0, lastNewline + 1));
            container.Open = false;
            container.EndLine = _lineNumber - 2;
        }
        _tip = table;
        AddTableRow(table, headerLine, header: true);
        _cursor.AdvanceToEnd();
        return BlockStart.Leaf;
    }

    /// <summary>Reads a delimiter row such as "|:--|:-:|--:|" into the alignment of each column; null if the line is not one.</summary>
    private static TableAlign[]? ParseDelimiterRow(ReadOnlySpan<char> line)
    {
        line = line.Trim(" \t");
        if (line.Length == 0) return null;
        bool hasPipe = line.Contains('|');
        if (line[0] == '|') line = line.Slice(1);
        if (line.Length > 0 && line[^1] == '|' && hasPipe) line = line.Slice(0, line.Length - 1);

        var aligns = new List<TableAlign>();
        while (true)
        {
            int bar = line.IndexOf('|');
            var cell = (bar < 0 ? line : line.Slice(0, bar)).Trim(" \t");
            if (!TryParseDelimiterCell(cell, out var align)) return null;
            aligns.Add(align);
            if (bar < 0) break;
            line = line.Slice(bar + 1);
        }
        return aligns.ToArray();
    }

    /// <summary>A delimiter cell is dashes with an optional colon at either end, which sets the alignment.</summary>
    private static bool TryParseDelimiterCell(ReadOnlySpan<char> cell, out TableAlign align)
    {
        align = TableAlign.None;
        if (cell.Length == 0) return false;
        bool left = cell[0] == ':', right = cell[^1] == ':';
        // A lone ":" is both the left and the right colon; it has no dashes and is not a delimiter cell.
        int start = left ? 1 : 0, end = cell.Length - (right ? 1 : 0);
        if (end <= start) return false;
        foreach (char c in cell[start..end])
            if (c != '-') return false;

        align = left && right ? TableAlign.Center : left ? TableAlign.Left : right ? TableAlign.Right : TableAlign.None;
        return true;
    }

    /// <summary>Splits a table row on unescaped pipes. "\|" becomes a literal pipe in the cell text.</summary>
    private static List<string> SplitRow(string line)
    {
        var cells = new List<string>();
        var text = line.AsSpan().Trim(" \t");
        if (text.Length > 0 && text[0] == '|') text = text.Slice(1);
        var cell = new StringBuilder();
        bool endsWithPipe = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            endsWithPipe = false;
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (c == '|')
            {
                cells.Add(cell.ToString().Trim(' ', '\t'));
                cell.Clear();
                endsWithPipe = true;
            }
            else cell.Append(c);
        }
        if (!endsWithPipe || cell.Length > 0) cells.Add(cell.ToString().Trim(' ', '\t'));
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
}
