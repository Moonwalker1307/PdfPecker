namespace PdfPecker.Markdown;

// Footnote definitions (GitHub and markdown-it syntax): "[^label]: text", continued by lines indented four columns.
internal sealed partial class BlockParser
{
    /// <summary>A footnote label is at most as long as a link label.</summary>
    internal const int MaxFootnoteLabelLength = 999;

    /// <summary>Lines indented this far continue a footnote definition.</summary>
    private const int FootnoteIndent = 4;

    /// <summary>The footnote definitions by normalized label; the first definition of a label wins, as for links.</summary>
    public Dictionary<string, MdNode> Footnotes { get; } = new(StringComparer.Ordinal);

    /// <summary>The kept definitions in document order, for the inline pass: they are taken out of the document tree.</summary>
    private readonly List<MdNode> _footnoteDefinitions = new();

    /// <summary>
    /// Whether <paramref name="c"/> may appear in a footnote label. Whitespace and brackets may not; leaving out "[" also
    /// keeps the search for the closing "]" from running over the same text again from each "[^".
    /// </summary>
    private static bool IsFootnoteLabelChar(char c) => c is not (' ' or '\t' or '\n' or '\r' or '[' or ']' or '\0');

    /// <summary>
    /// The length of the label of the "[^label]" that starts at <paramref name="start"/> in <paramref name="s"/>, or 0
    /// when there is none there.
    /// </summary>
    public static int FootnoteLabelLength(ReadOnlySpan<char> s, int start)
    {
        if (start + 2 >= s.Length || s[start] != '[' || s[start + 1] != '^') return 0;
        int labelStart = start + 2, end = labelStart;
        while (end < s.Length && end - labelStart < MaxFootnoteLabelLength && IsFootnoteLabelChar(s[end])) end++;
        return end > labelStart && end < s.Length && s[end] == ']' ? end - labelStart : 0;
    }

    private BlockStart FootnoteDefinitionStart()
    {
        if (_cursor.IsIndented || _cursor.NextNonspaceChar != '[') return BlockStart.None;
        var line = _cursor.Line.AsSpan();
        int start = _cursor.NextNonspace;
        int length = FootnoteLabelLength(line, start);
        int colon = start + 2 + length + 1;
        if (length == 0 || colon >= line.Length || line[colon] != ':') return BlockStart.None;

        CloseUnmatchedBlocks();
        var definition = AddChild(MdNodeType.FootnoteDefinition);
        definition.Label = MdUtil.NormalizeFootnoteLabel(line.Slice(start + 2, length));
        if (Footnotes.TryAdd(definition.Label, definition)) _footnoteDefinitions.Add(definition);
        _cursor.AdvanceToNextNonspace();
        _cursor.Advance(colon + 1 - start, columns: false);
        while (_cursor.PeekIsSpaceOrTab(_cursor.Offset)) _cursor.Advance(1, columns: false);
        return BlockStart.Container;
    }

    private Continuation ContinueFootnoteDefinition()
    {
        if (_cursor.IsBlank) _cursor.AdvanceToNextNonspace();
        else if (_cursor.Indent >= FootnoteIndent) _cursor.Advance(FootnoteIndent, columns: true);
        else return Continuation.NotMatched;
        return Continuation.Matched;
    }

    /// <summary>Takes a closed footnote definition out of the tree: footnotes render where they are referenced.</summary>
    private static void FinalizeFootnoteDefinition(MdNode definition)
    {
        definition.EndLine = definition.LastChild?.EndLine ?? definition.StartLine;
        definition.Unlink();
    }
}
