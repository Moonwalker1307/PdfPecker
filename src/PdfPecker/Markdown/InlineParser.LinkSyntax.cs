namespace PdfPecker.Markdown;

// Link destinations, titles and labels, shared by inline links, reference links and link reference definitions.
internal sealed partial class InlineParser
{
    // Like cmark, limit nesting so a run of "(" cannot make every link attempt rescan the rest of the text.
    private const int MaxLinkParenDepth = 32;

    // A link label holds at most 999 characters between its brackets.
    private const int MaxLinkLabelLength = 999;

    // The length of "[]", the empty label of a collapsed reference link.
    private const int EmptyLabelLength = 2;

    /// <summary>Parses a link reference definition at <paramref name="start"/> in <paramref name="s"/>; returns the characters consumed, or 0.</summary>
    public int ParseReference(string s, int start)
    {
        _cursor.Start(s, start);
        int labelLength = ParseLinkLabel();
        if (labelLength == 0) return 0;
        var rawLabel = s.Substring(start, labelLength);
        if (!_cursor.TryConsume(':')) return 0;
        SkipLinkWhitespace();
        var destination = ParseLinkDestination();
        if (destination == null || !TryParseDefinitionTitle(out var title)) return 0;

        var label = MdUtil.NormalizeReference(rawLabel);
        if (label.Length == 0) return 0;
        // The first definition of a label wins.
        _refmap.TryAdd(label, new LinkReference(destination, title));
        return _cursor.Position - start;
    }

    /// <summary>
    /// Reads the optional title of a definition up to the end of its line. A title followed by more text is not part of
    /// the definition, which is then valid when the destination ends its line.
    /// </summary>
    private bool TryParseDefinitionTitle(out string title)
    {
        int beforeTitle = _cursor.Position;
        SkipLinkWhitespace();
        // Whitespace must separate the title from the destination.
        var parsed = _cursor.Position != beforeTitle ? ParseLinkTitle() : null;
        if (parsed == null) _cursor.Position = beforeTitle;
        title = parsed ?? "";
        if (TrySkipToNextLine()) return true;
        if (title.Length == 0) return false;
        title = "";
        _cursor.Position = beforeTitle;
        return TrySkipToNextLine();
    }

    /// <summary>Consumes the spaces and tabs that end the line and its line ending; returns false, consuming nothing, when anything else comes first.</summary>
    private bool TrySkipToNextLine()
    {
        var rest = _cursor.Rest;
        int end = rest.IndexOfAnyExcept(' ', '\t');
        if (end < 0)
        {
            _cursor.Advance(rest.Length);
            return true;
        }
        if (rest[end] != '\n') return false;
        _cursor.Advance(end + 1);
        return true;
    }

    /// <summary>Skips spaces and tabs with at most one line ending among them.</summary>
    private void SkipLinkWhitespace()
    {
        _cursor.SkipSpacesAndTabs();
        if (_cursor.TryConsume('\n')) _cursor.SkipSpacesAndTabs();
    }

    private static bool IsEscapedPunctuation(string s, int index)
        => s[index] == '\\' && index + 1 < s.Length && MdUtil.IsAsciiPunctuation(s[index + 1]);

    /// <summary>Reads a link destination, unescaped and percent-encoded, or returns null when there is none.</summary>
    private string? ParseLinkDestination() => _cursor.Current == '<' ? ParseBracketedDestination() : ParseBareDestination();

    /// <summary>A destination in angle brackets, which may hold anything but line endings and unescaped angle brackets.</summary>
    private string? ParseBracketedDestination()
    {
        var text = _cursor.Text;
        int contentStart = _cursor.Position + 1;
        for (int p = contentStart; p < text.Length; p++)
        {
            if (IsEscapedPunctuation(text, p))
            {
                p++;
                continue;
            }
            char c = text[p];
            if (c is '<' or '\n') return null;
            if (c == '>')
            {
                _cursor.Position = p + 1;
                return MdUtil.NormalizeUri(MdUtil.Unescape(text.Substring(contentStart, p - contentStart)));
            }
        }
        return null;
    }

    /// <summary>
    /// A destination without angle brackets: no whitespace or control characters, and parentheses only balanced and
    /// nested at most <see cref="MaxLinkParenDepth"/> deep. It may be empty only right before the closing parenthesis.
    /// </summary>
    private string? ParseBareDestination()
    {
        int start = _cursor.Position, depth = 0;
        while (!_cursor.AtEnd)
        {
            char c = _cursor.Current;
            if (IsEscapedPunctuation(_cursor.Text, _cursor.Position)) _cursor.Advance(2);
            else if (c == '(')
            {
                if (depth >= MaxLinkParenDepth) return null;
                _cursor.Advance();
                depth++;
            }
            else if (c == ')')
            {
                if (depth == 0) break;
                _cursor.Advance();
                depth--;
            }
            else if (c <= ' ') break;
            else _cursor.Advance();
        }
        if (_cursor.Position == start && _cursor.Current != ')') return null;
        if (depth != 0) return null;
        return MdUtil.NormalizeUri(MdUtil.Unescape(_cursor.Text.Substring(start, _cursor.Position - start)));
    }

    /// <summary>
    /// Reads a title in double quotes, single quotes or parentheses, unescaped, or returns null when there is none. A
    /// backslash escapes any character, and a title in parentheses may not contain "(".
    /// </summary>
    private string? ParseLinkTitle()
    {
        char open = _cursor.Current;
        char close = open switch { '"' => '"', '\'' => '\'', '(' => ')', _ => '\0' };
        if (close == '\0') return null;
        var text = _cursor.Text;
        int contentStart = _cursor.Position + 1;
        for (int p = contentStart; p < text.Length; p++)
        {
            char c = text[p];
            if (c == '\\' && p + 1 < text.Length)
            {
                p++;
                continue;
            }
            if (c == close)
            {
                _cursor.Position = p + 1;
                return MdUtil.Unescape(text.Substring(contentStart, p - contentStart));
            }
            if (open == '(' && c == '(') return null;
        }
        return null;
    }

    /// <summary>
    /// Reads a link label at the cursor and returns its length with the brackets, or 0, consuming nothing, when there is
    /// none. A label may not contain unescaped brackets.
    /// </summary>
    private int ParseLinkLabel()
    {
        if (_cursor.Current != '[') return 0;
        var text = _cursor.Text;
        int start = _cursor.Position;
        int lastClose = start + MaxLinkLabelLength + 1; // the furthest the closing bracket may be
        for (int p = start + 1; p < text.Length && p <= lastClose; p++)
        {
            char c = text[p];
            if (c == '\\' && p + 1 < text.Length)
            {
                p++;
                continue;
            }
            if (c == '[') return 0;
            if (c == ']')
            {
                _cursor.Position = p + 1;
                return p + 1 - start;
            }
        }
        return 0;
    }
}
