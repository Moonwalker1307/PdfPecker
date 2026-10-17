using System.Text;

namespace PdfPecker.Css;

internal static partial class CssParser
{
    /// <summary>Removes the /* */ comments; a comment left open runs to the end of the text.</summary>
    public static string StripComments(string css)
    {
        int open = css.IndexOf("/*", StringComparison.Ordinal);
        if (open < 0) return css;
        var sb = new StringBuilder(css.Length);
        int kept = 0; // the start of the text not yet copied
        while (open >= 0)
        {
            sb.Append(css, kept, open - kept);
            int close = css.IndexOf("*/", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                kept = css.Length;
                break;
            }
            kept = close + 2;
            open = css.IndexOf("/*", kept, StringComparison.Ordinal);
        }
        if (kept < css.Length) sb.Append(css, kept, css.Length - kept);
        return sb.ToString();
    }

    /// <summary>Splits on a separator that is not inside parentheses, brackets or quotes.</summary>
    public static List<string> SplitTopLevel(string s, char separator)
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                // Inside a string only its closing quote matters, and a backslash escapes the next character.
                if (c == quote) quote = '\0';
                else if (c == '\\') i++;
            }
            else if (c is '"' or '\'') quote = c;
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == separator && depth == 0)
            {
                if (i > start) result.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        if (start < s.Length) result.Add(s.Substring(start));
        return result;
    }

    /// <summary>Splits a value into whitespace-separated tokens, keeping functions and quoted strings intact.</summary>
    public static List<string> Tokens(string s)
    {
        var result = new List<string>(4);
        int depth = 0, start = -1;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (IsTokenSpace(c) && depth == 0)
            {
                if (start >= 0)
                {
                    result.Add(s.Substring(start, i - start));
                    start = -1;
                }
                continue;
            }
            if (start < 0) start = i;
            if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
        }
        if (start >= 0) result.Add(s.Substring(start));
        return result;
    }

    /// <summary>The white space between value tokens: the CSS white-space characters, not the Unicode set.</summary>
    private static bool IsTokenSpace(char c) => c is ' ' or '\t' or '\n' or '\r';
}

/// <summary>
/// A read position in a range of CSS text, with the scans the rule parser needs. The scans step over quoted strings,
/// so a brace or a semicolon inside a string does not end a block or a statement; a string left open hides everything
/// after it to the end of the range, as browsers treat it.
/// </summary>
internal ref struct CssReader
{
    private readonly string _text;
    private readonly int _end;
    private int _position;

    public CssReader(string text, int start, int end)
    {
        _text = text;
        _position = start;
        _end = end;
    }

    public readonly int Position => _position;

    public readonly int End => _end;

    public readonly bool AtEnd => _position >= _end;

    public void SkipWhitespaceAndSemicolons()
    {
        string text = _text;
        int i = _position, end = _end;
        while (i < end && (char.IsWhiteSpace(text[i]) || text[i] == ';')) i++;
        _position = i;
    }

    /// <summary>Steps over the character when it is the next one.</summary>
    public bool Skip(char c)
    {
        if (_position >= _end || _text[_position] != c) return false;
        _position++;
        return true;
    }

    /// <summary>Steps over the marker when the text continues with it.</summary>
    public bool Skip(string marker)
    {
        if (_position >= _end || _text[_position] != marker[0] || !_text.AsSpan(_position).StartsWith(marker, StringComparison.Ordinal)) return false;
        _position += marker.Length;
        return true;
    }

    /// <summary>Reads the name of an at-rule after its "@" (letters, digits and hyphens), lowercased.</summary>
    public string ReadAtRuleName()
    {
        string text = _text;
        int start = _position, i = start, end = _end;
        while (i < end && (char.IsLetterOrDigit(text[i]) || text[i] == '-')) i++;
        _position = i;
        return text.Substring(start, i - start).ToLowerInvariant();
    }

    /// <summary>The next <paramref name="target"/> outside quotes, or -1 when none comes before the end or a string is left open.</summary>
    public readonly int IndexOfOutsideQuotes(char target) => IndexOfOutsideQuotes(target, _position, _end);

    /// <summary>The first <paramref name="target"/> outside quotes in [<paramref name="from"/>, <paramref name="to"/>), or -1 when none comes before it or a string is left open.</summary>
    public readonly int IndexOfOutsideQuotes(char target, int from, int to)
    {
        string text = _text;
        for (int i = from; i < to; i++)
        {
            char c = text[i];
            if (c == target) return i;
            if (c is '"' or '\'')
            {
                int close = text.IndexOf(c, i + 1, to - i - 1);
                if (close < 0) return -1;
                i = close;
            }
        }
        return -1;
    }

    /// <summary>The "}" closing the block that opens at <paramref name="open"/>, or the end of the range when the block or a string in it is left open.</summary>
    public readonly int MatchingBrace(int open)
    {
        string text = _text;
        int end = _end, depth = 0;
        for (int i = open; i < end; i++)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                int close = text.IndexOf(c, i + 1, end - i - 1);
                if (close < 0) return end;
                i = close;
            }
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return end;
    }

    /// <summary>
    /// Where the prelude of a nested rule starts: after the last ";" or "}" before its "{", and not before
    /// <paramref name="floor"/>, the start of the declarations it follows.
    /// </summary>
    public readonly int StartOfPrelude(int brace, int floor)
    {
        string text = _text;
        int start = brace;
        while (start > floor && text[start - 1] != ';' && text[start - 1] != '}') start--;
        return start;
    }

    public readonly string Text(int from, int to) => _text.Substring(from, to - from);

    public readonly string RemainingText() => Text(_position, _end);

    /// <summary>A reader over the inside of a block, between its braces.</summary>
    public readonly CssReader Inside(int open, int close) => new(_text, open + 1, close);

    /// <summary>Moves to the position, or to the end when it lies beyond (the block had no closing brace).</summary>
    public void MoveTo(int position) => _position = Math.Min(position, _end);
}
