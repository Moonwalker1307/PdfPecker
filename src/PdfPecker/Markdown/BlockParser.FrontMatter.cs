namespace PdfPecker.Markdown;

// YAML front matter: a block between "---" lines at the very start of the document, which holds metadata and is not
// rendered, as on GitHub, in Jekyll and in markdown-it-front-matter.
internal sealed partial class BlockParser
{
    /// <summary>The <c>title</c> of the front matter, or null when there is none.</summary>
    public string? FrontMatterTitle { get; private set; }

    /// <summary>
    /// Skips the front matter at the start of <paramref name="input"/> and returns where the Markdown begins. Without a
    /// closing "---" (or "...") line there is no front matter, and the opening line is a thematic break as usual.
    /// </summary>
    private int SkipFrontMatter(string input)
    {
        int position = input.StartsWith('﻿') ? 1 : 0;
        if (!TryReadLine(input, ref position, out string line) || !IsFrontMatterFence(line, closing: false)) return 0;

        int lines = 1;
        string? title = null;
        while (TryReadLine(input, ref position, out line))
        {
            lines++;
            if (IsFrontMatterFence(line, closing: true))
            {
                _lineNumber = lines;
                FrontMatterTitle = title;
                return position;
            }
            if (title == null && line.StartsWith("title:", StringComparison.Ordinal)) title = YamlScalar(line.AsSpan("title:".Length));
        }
        return 0;
    }

    private static bool IsFrontMatterFence(string line, bool closing)
    {
        var s = line.AsSpan().TrimEnd(" \t");
        return s is "---" || (closing && s is "...");
    }

    /// <summary>
    /// The value of a one-line YAML scalar: a quoted string, or plain text up to a comment. Null for an empty value or a
    /// block scalar ("|" or "&gt;"), whose text is on the following lines.
    /// </summary>
    private static string? YamlScalar(ReadOnlySpan<char> s)
    {
        s = s.Trim(" \t");
        if (s.IsEmpty) return null;
        string value;
        if (s[0] is '"' or '\'')
        {
            char quote = s[0];
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                if (quote == '\'' && c == '\'' && i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i++; }
                else if (quote == '"' && c == '\\' && i + 1 < s.Length) sb.Append(s[++i] switch { 'n' => '\n', 't' => '\t', var e => e });
                else if (c == quote) break;
                else sb.Append(c);
            }
            value = sb.ToString();
        }
        else
        {
            if (s[0] is '|' or '>') return null;
            int comment = s.IndexOf(" #".AsSpan());
            value = (comment < 0 ? s : s.Slice(0, comment)).TrimEnd(" \t").ToString();
        }
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
