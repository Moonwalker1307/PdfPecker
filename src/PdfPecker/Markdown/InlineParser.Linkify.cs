namespace PdfPecker.Markdown;

// GFM extended autolinks: bare www., http(s):// and ftp:// URLs and e-mail addresses in text become links. They are
// found in the finished text nodes, outside links and images.
internal sealed partial class InlineParser
{
    private const string SchemeSeparator = "://";

    private static void Linkify(MdNode root)
    {
        var texts = new List<MdNode>();
        int linkDepth = 0;
        foreach (var (n, entering) in root.Walk())
        {
            if (n.Type is MdNodeType.Link or MdNodeType.Image) linkDepth += entering ? 1 : -1;
            else if (n.Type == MdNodeType.Text && linkDepth == 0) texts.Add(n);
        }
        foreach (var text in texts) LinkifyText(text);
    }

    /// <summary>Replaces a text node that holds URLs or e-mail addresses with the text and links it splits into.</summary>
    private static void LinkifyText(MdNode node)
    {
        var s = node.Literal!;
        int searchFrom = 0, textStart = 0;
        // The next URL and the next e-mail address are each looked for again only once the search has passed the one
        // found last; when there is none, later searches would find none either. Searching again after every link
        // would scan the rest of the text once per link.
        bool urlSearched = false, emailSearched = false, www = false;
        int urlStart = -1, emailStart = -1, emailEnd = 0;
        var insertAfter = node;
        while (searchFrom < s.Length)
        {
            if (!urlSearched || (urlStart >= 0 && urlStart < searchFrom))
            {
                urlStart = FindUrlStart(s, searchFrom, out www);
                urlSearched = true;
            }
            if (!emailSearched || (emailStart >= 0 && emailStart < searchFrom))
            {
                emailStart = FindEmail(s, searchFrom, out emailEnd);
                emailSearched = true;
            }

            int start, end;
            string destination;
            if (emailStart >= 0 && (urlStart < 0 || emailStart < urlStart))
            {
                start = emailStart;
                end = emailEnd;
                destination = "mailto:" + s.Substring(start, end - start);
            }
            else if (urlStart < 0) break;
            else
            {
                start = urlStart;
                end = FindUrlEnd(s, start);
                int hostStart = www ? start : s.IndexOf(SchemeSeparator, start, StringComparison.Ordinal) + SchemeSeparator.Length;
                if (!ValidDomain(s, hostStart, end))
                {
                    searchFrom = start + 1;
                    continue;
                }
                destination = www ? "http://" + s.Substring(start, end - start) : s.Substring(start, end - start);
            }

            if (start > textStart) insertAfter = InsertTextAfter(insertAfter, s.Substring(textStart, start - textStart));
            var link = AutolinkNode(MdUtil.NormalizeUri(destination), s.Substring(start, end - start));
            insertAfter.InsertAfter(link);
            insertAfter = link;
            textStart = searchFrom = end;
        }
        if (insertAfter == node) return; // nothing found
        if (textStart < s.Length) InsertTextAfter(insertAfter, s.Substring(textStart));
        node.Unlink();
    }

    private static MdNode InsertTextAfter(MdNode sibling, string text)
    {
        var node = Text(text);
        sibling.InsertAfter(node);
        return node;
    }

    private static bool IsEmailLocalChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+';

    /// <summary>A domain character of an e-mail address; a dot only when a letter or digit follows it.</summary>
    private static bool IsEmailDomainChar(string s, int index)
    {
        char c = s[index];
        return char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || (c == '.' && index + 1 < s.Length && char.IsAsciiLetterOrDigit(s[index + 1]));
    }

    /// <summary>Finds a bare e-mail address at or after <paramref name="from"/>: its start, and its end in <paramref name="end"/>; -1 when there is none.</summary>
    private static int FindEmail(string s, int from, out int end)
    {
        end = -1;
        for (int at = s.IndexOf('@', from); at >= 0; at = s.IndexOf('@', at + 1))
        {
            int start = at;
            while (start > from && IsEmailLocalChar(s[start - 1])) start--;
            if (start == at) continue;
            int p = at + 1, dots = 0;
            while (p < s.Length && IsEmailDomainChar(s, p))
            {
                if (s[p] == '.') dots++;
                p++;
            }
            // The domain needs a dot and may not end in "-" or "_".
            if (dots == 0 || p == at + 1 || s[p - 1] is '-' or '_') continue;
            end = p;
            return start;
        }
        return -1;
    }

    /// <summary>Finds where a www. or http(s):// or ftp:// URL starts at or after <paramref name="from"/>, or returns -1.</summary>
    private static int FindUrlStart(string s, int from, out bool www)
    {
        for (int i = from; i < s.Length; i++)
        {
            if (s[i] is not ('w' or 'W' or 'h' or 'H' or 'f' or 'F')) continue;
            // A URL starts the text or follows whitespace or one of * _ ~ (.
            if (i > 0 && !(char.IsWhiteSpace(s[i - 1]) || s[i - 1] is '*' or '_' or '~' or '(')) continue;
            var rest = s.AsSpan(i);
            www = rest.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
            if (www || rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        www = false;
        return -1;
    }

    /// <summary>A URL runs to whitespace or "&lt;", less the trailing characters that are more likely the sentence's.</summary>
    private static int FindUrlEnd(string s, int start)
    {
        int end = start;
        while (end < s.Length && !char.IsWhiteSpace(s[end]) && s[end] != '<') end++;
        while (end > start)
        {
            char c = s[end - 1];
            if (IsTrailingUrlPunctuation(c) || (c == ')' && HasUnmatchedCloseParen(s, start, end))) end--;
            else if (c == ';' && TryFindTrailingEntity(s, start, end, out int ampersand)) end = ampersand;
            else break;
        }
        return end;
    }

    private static bool IsTrailingUrlPunctuation(char c) => c is '?' or '!' or '.' or ',' or ':' or '*' or '_' or '~' or '\'' or '"';

    /// <summary>A trailing ")" belongs to the URL only while the URL has no more of them than "(".</summary>
    private static bool HasUnmatchedCloseParen(string s, int start, int end)
    {
        var url = s.AsSpan(start, end - start);
        return url.Count(')') > url.Count('(');
    }

    /// <summary>A trailing entity-like "&amp;name;" is not part of the URL; finds its "&amp;".</summary>
    private static bool TryFindTrailingEntity(string s, int start, int end, out int ampersand)
    {
        ampersand = end - 2;
        while (ampersand > start && char.IsAsciiLetterOrDigit(s[ampersand])) ampersand--;
        return ampersand >= start && s[ampersand] == '&' && ampersand < end - 2;
    }

    /// <summary>A domain is dot-separated segments of letters, digits, "-" and "_", at least two, none empty, up to the path.</summary>
    private static bool ValidDomain(string s, int start, int end)
    {
        int segments = 0, segmentLength = 0;
        for (int i = start; i < end; i++)
        {
            char c = s[i];
            if (c is '/' or '?' or '#' or ':') break;
            if (c == '.')
            {
                if (segmentLength == 0) return false;
                segments++;
                segmentLength = 0;
            }
            else if (char.IsLetterOrDigit(c) || c is '-' or '_') segmentLength++;
            else return false;
        }
        return segments >= 1 && segmentLength > 0;
    }
}
