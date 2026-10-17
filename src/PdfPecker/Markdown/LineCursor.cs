using System.Text;

namespace PdfPecker.Markdown;

/// <summary>
/// The position within the line the block parser is reading. Block markers are matched at the next non-space character,
/// but indentation counts in columns (a tab advances to the next multiple of four), so the cursor tracks the character
/// offset and the column side by side, and whether a tab was only partly consumed. It is a mutable struct that lives in
/// a field of the parser, so that parsing a document allocates nothing for it.
/// </summary>
internal struct LineCursor
{
    /// <summary>Four columns of indentation start an indented code block.</summary>
    public const int CodeIndent = 4;

    private const int TabStop = 4;

    /// <summary>The current line; <see cref="Start"/> must have been called.</summary>
    public string Line { get; private set; }

    /// <summary>The index of the first character that has not been consumed.</summary>
    public int Offset { get; private set; }

    /// <summary>The column of <see cref="Offset"/>.</summary>
    public int Column { get; private set; }

    /// <summary>True when a tab was split: some of its columns are consumed, so <see cref="Offset"/> still points at the tab.</summary>
    public bool PartiallyConsumedTab { get; private set; }

    /// <summary>The index of the first non-space character at or after <see cref="Offset"/>, as of the last <see cref="FindNextNonspace"/>.</summary>
    public int NextNonspace { get; private set; }

    public int NextNonspaceColumn { get; private set; }

    /// <summary>The columns of whitespace between <see cref="Column"/> and <see cref="NextNonspace"/>.</summary>
    public int Indent { get; private set; }

    public bool IsIndented { get; private set; }

    /// <summary>True when only whitespace is left on the line.</summary>
    public bool IsBlank { get; private set; }

    public char NextNonspaceChar => Peek(NextNonspace);

    /// <summary>True when everything on the line has been consumed.</summary>
    public bool AtEnd => Offset >= Line.Length;

    /// <summary>Begins a new line. The scan results of the previous line stay until <see cref="FindNextNonspace"/> runs again.</summary>
    public void Start(string line)
    {
        Line = line;
        Offset = 0;
        Column = 0;
        IsBlank = false;
        PartiallyConsumedTab = false;
    }

    /// <summary>The character at <paramref name="position"/>, or NUL past the end of the line.</summary>
    public char Peek(int position) => position < Line.Length ? Line[position] : '\0';

    public bool PeekIsSpaceOrTab(int position) => MdUtil.IsSpaceOrTab(Peek(position));

    /// <summary>The number of consecutive <paramref name="c"/> characters from <paramref name="start"/>, counting at most <paramref name="limit"/>.</summary>
    public int CountRun(int start, char c, int limit = int.MaxValue)
    {
        int end = start;
        while (end < Line.Length && Line[end] == c && end - start < limit) end++;
        return end - start;
    }

    public void FindNextNonspace()
    {
        int i = Offset, columns = Column;
        while (i < Line.Length)
        {
            char c = Line[i];
            if (c == ' ') { i++; columns++; }
            else if (c == '\t') { i++; columns += TabStop - columns % TabStop; }
            else break;
        }
        IsBlank = i >= Line.Length;
        NextNonspace = i;
        NextNonspaceColumn = columns;
        Indent = NextNonspaceColumn - Column;
        IsIndented = Indent >= CodeIndent;
    }

    public void AdvanceToNextNonspace()
    {
        Offset = NextNonspace;
        Column = NextNonspaceColumn;
        PartiallyConsumedTab = false;
    }

    /// <summary>Consumes the rest of the line.</summary>
    public void AdvanceToEnd() => Advance(Line.Length - Offset, columns: false);

    /// <summary>Moves back to a position saved earlier, for a marker whose following spaces turn out to belong to its content.</summary>
    public void Rewind(int offset, int column)
    {
        Offset = offset;
        Column = column;
    }

    /// <summary>
    /// Consumes <paramref name="count"/> characters, or with <paramref name="columns"/> that many columns, which may end in
    /// the middle of a tab.
    /// </summary>
    public void Advance(int count, bool columns)
    {
        while (count > 0 && Offset < Line.Length)
        {
            if (Line[Offset] == '\t')
            {
                int columnsToTab = TabStop - Column % TabStop;
                if (columns)
                {
                    PartiallyConsumedTab = columnsToTab > count;
                    int columnsToAdvance = Math.Min(columnsToTab, count);
                    Column += columnsToAdvance;
                    if (!PartiallyConsumedTab) Offset++;
                    count -= columnsToAdvance;
                }
                else
                {
                    PartiallyConsumedTab = false;
                    Column += columnsToTab;
                    Offset++;
                    count--;
                }
            }
            else
            {
                PartiallyConsumedTab = false;
                Offset++;
                Column++;
                count--;
            }
        }
    }

    /// <summary>Appends the rest of the line and a newline; a partly consumed tab turns into the spaces it still covers.</summary>
    public void AppendRemainderTo(StringBuilder builder)
    {
        if (PartiallyConsumedTab)
        {
            Offset++;
            builder.Append(' ', TabStop - Column % TabStop);
        }
        if (Offset < Line.Length) builder.Append(Line, Offset, Line.Length - Offset);
        builder.Append('\n');
    }
}
