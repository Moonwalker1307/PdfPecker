namespace PdfPecker.Markdown;

// Code spans: a run of backticks up to the next run of the same length.
internal sealed partial class InlineParser
{
    // The start of the last backtick run of each length seen so far. Once a search for a closer has reached the end of
    // the text, the table holds every run, so an opener whose length has no later run is known to stay literal without
    // searching again: a text of many unmatched runs then takes linear rather than quadratic time.
    private readonly Dictionary<int, int> _lastBacktickRun = new();
    private bool _allBacktickRunsSeen;

    private void ResetCodeSpanCache()
    {
        _lastBacktickRun.Clear();
        _allBacktickRunsSeen = false;
    }

    private void ParseCodeSpan(MdNode block)
    {
        int length = _cursor.ConsumeRun('`');
        int contentStart = _cursor.Position;
        if (!KnownToHaveNoCloser(length, contentStart) && TryFindCloser(length, contentStart, out int closerStart))
        {
            block.AppendChild(new MdNode(MdNodeType.Code) { Literal = CodeSpanContent(contentStart, closerStart) });
            _cursor.Position = closerStart + length;
            return;
        }
        // Without a closer the opening run is literal text, and parsing goes on right after it.
        block.AppendChild(Text(new string('`', length)));
    }

    private bool KnownToHaveNoCloser(int length, int contentStart)
        => _allBacktickRunsSeen && (!_lastBacktickRun.TryGetValue(length, out int lastRun) || lastRun < contentStart);

    /// <summary>Finds the first backtick run of exactly <paramref name="length"/> at or after <paramref name="from"/>, noting every run it passes.</summary>
    private bool TryFindCloser(int length, int from, out int closerStart)
    {
        var text = _cursor.Text;
        int position = from;
        while (position < text.Length)
        {
            int runStart = text.IndexOf('`', position);
            if (runStart < 0) break;
            int runLength = text.AsSpan(runStart).IndexOfAnyExcept('`');
            if (runLength < 0) runLength = text.Length - runStart;
            _lastBacktickRun[runLength] = runStart;
            if (runLength == length)
            {
                closerStart = runStart;
                return true;
            }
            position = runStart + runLength;
        }
        _allBacktickRunsSeen = true;
        closerStart = -1;
        return false;
    }

    /// <summary>
    /// The text between the backtick runs: line endings become spaces, and one space comes off each side when both sides
    /// have one and the content is not all spaces, so that a span can begin or end with a backtick.
    /// </summary>
    private string CodeSpanContent(int start, int end)
    {
        var content = _cursor.Text.Substring(start, end - start).Replace('\n', ' ');
        if (content.Length > 1 && content[0] == ' ' && content[^1] == ' ' && content.AsSpan().ContainsAnyExcept(' '))
            content = content.Substring(1, content.Length - 2);
        return content;
    }
}
