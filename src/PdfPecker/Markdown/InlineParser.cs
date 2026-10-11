using System.Text;

namespace PdfPecker.Markdown;

/// <summary>
/// Inline phase of the Markdown parser: code spans, emphasis (delimiter-run algorithm), links and images (bracket stack),
/// autolinks, raw HTML, entities, escapes and line breaks, plus GFM strikethrough and extended autolinks.
/// </summary>
internal sealed class InlineParser
{
    private sealed class Delimiter
    {
        public char Char;
        public int NumDelims, OrigDelims;
        public MdNode Node = null!;
        public Delimiter? Previous, Next;
        public bool CanOpen, CanClose, Dirty;
    }

    private sealed class Bracket
    {
        public MdNode Node = null!;
        public Bracket? Previous;
        public Delimiter? PreviousDelimiter;
        public int Index;
        public bool Image, Active = true, BracketAfter;
    }

    private const int MaxLinkParenDepth = 32;

    private readonly Dictionary<string, LinkReference> _refmap;
    private readonly bool _gfm;
    private string _subject = "";
    private int _pos;
    private Delimiter? _delimiters;
    private Bracket? _brackets;
    // Delimiter runs partly used by emphasis; their text is fixed up once at the end instead of after every match.
    private readonly List<Delimiter> _dirty = new();

    // Code span closer cache: start of the last backtick run of each length seen, valid once the whole subject was scanned.
    private readonly Dictionary<int, int> _backtickRuns = new();
    private bool _backticksScanned;

    public InlineParser(Dictionary<string, LinkReference> refmap, bool gfm)
    {
        _refmap = refmap;
        _gfm = gfm;
    }

    public void Parse(MdNode block)
    {
        _subject = (block.Content?.ToString() ?? "").Trim(' ', '\t', '\n', '\r');
        block.Content = null;
        _pos = 0;
        _delimiters = null;
        _brackets = null;
        _backtickRuns.Clear();
        _backticksScanned = false;
        while (ParseInline(block)) { }
        ProcessEmphasis(null);
        foreach (var d in _dirty) d.Node.Literal = new string(d.Char, d.NumDelims);
        _dirty.Clear();
        MergeText(block);
        if (_gfm) Linkify(block);
    }

    private char Peek() => _pos < _subject.Length ? _subject[_pos] : '\0';
    private bool AtEnd => _pos >= _subject.Length;

    private static MdNode Text(string s) => new(MdNodeType.Text) { Literal = s };

    private bool ParseInline(MdNode block)
    {
        if (AtEnd) return false;
        char c = _subject[_pos];
        bool res = c switch
        {
            '\n' => ParseNewline(block),
            '\\' => ParseBackslash(block),
            '`' => ParseBackticks(block),
            '*' or '_' => HandleDelim(c, block),
            '~' => _gfm && HandleDelim(c, block),
            '[' => ParseOpenBracket(block),
            '!' => ParseBang(block),
            ']' => ParseCloseBracket(block),
            '<' => ParseAutolink(block) || ParseHtmlTag(block),
            '&' => ParseEntity(block),
            _ => ParseString(block),
        };
        if (!res)
        {
            _pos++;
            block.AppendChild(Text(c.ToString()));
        }
        return true;
    }

    private static bool IsSpecial(char c) => c is '\n' or '\\' or '`' or '*' or '_' or '~' or '[' or '!' or ']' or '<' or '&';

    private bool ParseString(MdNode block)
    {
        int start = _pos;
        while (_pos < _subject.Length && !IsSpecial(_subject[_pos])) _pos++;
        if (_pos == start) return false;
        block.AppendChild(Text(_subject.Substring(start, _pos - start)));
        return true;
    }

    private bool ParseNewline(MdNode block)
    {
        _pos++;
        var last = block.LastChild;
        if (last != null && last.Type == MdNodeType.Text && last.Literal!.EndsWith(' '))
        {
            var lit = last.Literal;
            bool hard = lit.Length >= 2 && lit[^2] == ' ';
            last.Literal = lit.TrimEnd(' ');
            block.AppendChild(new MdNode(hard ? MdNodeType.LineBreak : MdNodeType.SoftBreak));
        }
        else block.AppendChild(new MdNode(MdNodeType.SoftBreak));
        while (Peek() == ' ') _pos++; // leading spaces of the next line
        return true;
    }

    private bool ParseBackslash(MdNode block)
    {
        _pos++;
        char c = Peek();
        if (c == '\n')
        {
            _pos++;
            block.AppendChild(new MdNode(MdNodeType.LineBreak));
        }
        else if (!AtEnd && MdUtil.IsAsciiPunctuation(c))
        {
            _pos++;
            block.AppendChild(Text(c.ToString()));
        }
        else block.AppendChild(Text("\\"));
        return true;
    }

    private bool ParseBackticks(MdNode block)
    {
        int start = _pos;
        while (Peek() == '`') _pos++;
        int ticks = _pos - start;
        int afterOpen = _pos;
        if (_backticksScanned && (!_backtickRuns.TryGetValue(ticks, out int lastRun) || lastRun < afterOpen))
        {
            // A previous scan reached the end: there is no closing run of this length.
            block.AppendChild(Text(new string('`', ticks)));
            return true;
        }
        // Find a closing run of exactly the same length.
        int p = afterOpen;
        while (p < _subject.Length)
        {
            int q = _subject.IndexOf('`', p);
            if (q < 0) break;
            int r = q;
            while (r < _subject.Length && _subject[r] == '`') r++;
            _backtickRuns[r - q] = q;
            if (r - q == ticks)
            {
                var contents = _subject.Substring(afterOpen, q - afterOpen).Replace('\n', ' ');
                if (contents.Length > 1 && contents[0] == ' ' && contents[^1] == ' ' && contents.AsSpan().ContainsAnyExcept(' '))
                    contents = contents.Substring(1, contents.Length - 2);
                block.AppendChild(new MdNode(MdNodeType.Code) { Literal = contents });
                _pos = r;
                return true;
            }
            p = r;
        }
        _backticksScanned = true;
        _pos = afterOpen;
        block.AppendChild(Text(new string('`', ticks)));
        return true;
    }

    // ---- Emphasis ------------------------------------------------------------------------------------------------------

    private bool HandleDelim(char c, MdNode block)
    {
        int start = _pos;
        while (Peek() == c) _pos++;
        int numDelims = _pos - start;

        char before = start == 0 ? '\n' : _subject[start - 1];
        char after = AtEnd ? '\n' : _subject[_pos];
        bool afterWs = MdUtil.IsUnicodeWhitespace(after), afterPunct = MdUtil.IsPunctuation(after);
        bool beforeWs = MdUtil.IsUnicodeWhitespace(before), beforePunct = MdUtil.IsPunctuation(before);
        bool leftFlanking = !afterWs && (!afterPunct || beforeWs || beforePunct);
        bool rightFlanking = !beforeWs && (!beforePunct || afterWs || afterPunct);
        bool canOpen, canClose;
        if (c == '_')
        {
            canOpen = leftFlanking && (!rightFlanking || beforePunct);
            canClose = rightFlanking && (!leftFlanking || afterPunct);
        }
        else if (c == '~')
        {
            // GFM strikethrough: one or two tildes; longer runs are literal text.
            canOpen = numDelims <= 2 && leftFlanking;
            canClose = numDelims <= 2 && rightFlanking;
        }
        else
        {
            canOpen = leftFlanking;
            canClose = rightFlanking;
        }

        var node = Text(_subject.Substring(start, numDelims));
        block.AppendChild(node);
        if (canOpen || canClose)
        {
            _delimiters = new Delimiter
            {
                Char = c, NumDelims = numDelims, OrigDelims = numDelims, Node = node, Previous = _delimiters,
                CanOpen = canOpen, CanClose = canClose,
            };
            if (_delimiters.Previous != null) _delimiters.Previous.Next = _delimiters;
        }
        return true;
    }

    private void RemoveDelimiter(Delimiter d)
    {
        if (d.Previous != null) d.Previous.Next = d.Next;
        if (d.Next == null) _delimiters = d.Previous;
        else d.Next.Previous = d.Previous;
    }

    private static void RemoveDelimitersBetween(Delimiter bottom, Delimiter top)
    {
        if (bottom.Next != top)
        {
            bottom.Next = top;
            top.Previous = bottom;
        }
    }

    private static int OpenersBottomIndex(Delimiter closer) => closer.Char switch
    {
        '_' => (closer.CanOpen ? 3 : 0) + closer.OrigDelims % 3,
        '*' => 6 + (closer.CanOpen ? 3 : 0) + closer.OrigDelims % 3,
        _ => 12 + (closer.OrigDelims == 2 ? 1 : 0),
    };

    private void ProcessEmphasis(Delimiter? stackBottom)
    {
        var openersBottom = new Delimiter?[14];
        for (int i = 0; i < openersBottom.Length; i++) openersBottom[i] = stackBottom;

        // Find the first closer above the stack bottom.
        var closer = _delimiters;
        while (closer != null && closer.Previous != stackBottom) closer = closer.Previous;

        while (closer != null)
        {
            if (!closer.CanClose)
            {
                closer = closer.Next;
                continue;
            }
            int bottomIndex = OpenersBottomIndex(closer);
            var opener = closer.Previous;
            bool found = false;
            while (opener != null && opener != stackBottom && opener != openersBottom[bottomIndex])
            {
                if (opener.Char == closer.Char && opener.CanOpen)
                {
                    if (closer.Char == '~')
                    {
                        if (opener.NumDelims == closer.NumDelims) { found = true; break; }
                    }
                    else
                    {
                        bool oddMatch = (closer.CanOpen || opener.CanClose) && closer.OrigDelims % 3 != 0
                                        && (opener.OrigDelims + closer.OrigDelims) % 3 == 0;
                        if (!oddMatch) { found = true; break; }
                    }
                }
                opener = opener.Previous;
            }

            var oldCloser = closer;
            if (found)
            {
                int use = closer.Char == '~' ? closer.NumDelims : closer.NumDelims >= 2 && opener!.NumDelims >= 2 ? 2 : 1;
                var openerNode = opener!.Node;
                var closerNode = closer.Node;
                opener.NumDelims -= use;
                closer.NumDelims -= use;
                if (!opener.Dirty) { opener.Dirty = true; _dirty.Add(opener); }
                if (!closer.Dirty) { closer.Dirty = true; _dirty.Add(closer); }

                var wrapper = new MdNode(closer.Char == '~' ? MdNodeType.Strikethrough : use == 1 ? MdNodeType.Emph : MdNodeType.Strong);
                var tmp = openerNode.Next;
                while (tmp != null && tmp != closerNode)
                {
                    var next = tmp.Next;
                    wrapper.AppendChild(tmp);
                    tmp = next;
                }
                openerNode.InsertAfter(wrapper);

                RemoveDelimitersBetween(opener, closer);
                if (opener.NumDelims == 0)
                {
                    openerNode.Unlink();
                    RemoveDelimiter(opener);
                }
                if (closer.NumDelims == 0)
                {
                    closerNode.Unlink();
                    var nextCloser = closer.Next;
                    RemoveDelimiter(closer);
                    closer = nextCloser;
                }
            }
            else
            {
                closer = closer.Next;
                // Later searches for this kind of closer need not look further back than here.
                openersBottom[bottomIndex] = oldCloser.Previous;
                if (!oldCloser.CanOpen) RemoveDelimiter(oldCloser);
            }
        }

        while (_delimiters != null && _delimiters != stackBottom) RemoveDelimiter(_delimiters);
    }

    // ---- Links and images ----------------------------------------------------------------------------------------------

    private void AddBracket(MdNode node, int index, bool image)
    {
        if (_brackets != null) _brackets.BracketAfter = true;
        _brackets = new Bracket { Node = node, Previous = _brackets, PreviousDelimiter = _delimiters, Index = index, Image = image };
    }

    private bool ParseOpenBracket(MdNode block)
    {
        int start = _pos;
        _pos++;
        var node = Text("[");
        block.AppendChild(node);
        AddBracket(node, start, false);
        return true;
    }

    private bool ParseBang(MdNode block)
    {
        int start = _pos;
        _pos++;
        if (Peek() == '[')
        {
            _pos++;
            var node = Text("![");
            block.AppendChild(node);
            AddBracket(node, start + 1, true);
        }
        else block.AppendChild(Text("!"));
        return true;
    }

    private bool ParseCloseBracket(MdNode block)
    {
        _pos++;
        int startPos = _pos;
        var opener = _brackets;
        if (opener == null)
        {
            block.AppendChild(Text("]"));
            return true;
        }
        if (!opener.Active)
        {
            block.AppendChild(Text("]"));
            _brackets = opener.Previous;
            return true;
        }

        bool isImage = opener.Image;
        int savePos = _pos;
        bool matched = false;
        string? dest = null, title = null;

        // Inline link: [text](destination "title")
        if (Peek() == '(')
        {
            _pos++;
            Spnl();
            if ((dest = ParseLinkDestination()) != null)
            {
                Spnl();
                if (_pos > 0 && MdUtil.IsUnicodeWhitespace(_subject[_pos - 1])) title = ParseLinkTitle();
                Spnl();
                if (Peek() == ')')
                {
                    _pos++;
                    matched = true;
                }
            }
            if (!matched) _pos = savePos;
        }

        if (!matched)
        {
            // Reference link: [text][label], [text][] or [text]
            int beforeLabel = _pos;
            int n = ParseLinkLabel();
            string? refLabel = null;
            if (n > 2) refLabel = _subject.Substring(beforeLabel, n);
            else if (!opener.BracketAfter) refLabel = _subject.Substring(opener.Index, startPos - opener.Index);
            if (n == 0) _pos = savePos;
            if (refLabel != null && _refmap.TryGetValue(MdUtil.NormalizeReference(refLabel), out var link))
            {
                dest = link.Destination;
                title = link.Title;
                matched = true;
            }
        }

        if (!matched)
        {
            _brackets = opener.Previous;
            _pos = startPos;
            block.AppendChild(Text("]"));
            return true;
        }

        var node = new MdNode(isImage ? MdNodeType.Image : MdNodeType.Link) { Destination = dest, Title = title ?? "" };
        var tmp = opener.Node.Next;
        while (tmp != null)
        {
            var next = tmp.Next;
            node.AppendChild(tmp);
            tmp = next;
        }
        block.AppendChild(node);
        ProcessEmphasis(opener.PreviousDelimiter);
        _brackets = opener.Previous;
        opener.Node.Unlink();

        // No links inside links: deactivate earlier link openers.
        if (!isImage)
        {
            for (var b = _brackets; b != null; b = b.Previous)
                if (!b.Image) b.Active = false;
        }
        return true;
    }

    private void Spnl()
    {
        while (Peek() is ' ' or '\t') _pos++;
        if (Peek() == '\n')
        {
            _pos++;
            while (Peek() is ' ' or '\t') _pos++;
        }
    }

    private string? ParseLinkDestination()
    {
        if (Peek() == '<')
        {
            int p = _pos + 1;
            while (p < _subject.Length)
            {
                char c = _subject[p];
                if (c == '\\' && p + 1 < _subject.Length && MdUtil.IsAsciiPunctuation(_subject[p + 1])) { p += 2; continue; }
                if (c is '<' or '\n') return null;
                if (c == '>')
                {
                    var raw = _subject.Substring(_pos + 1, p - _pos - 1);
                    _pos = p + 1;
                    return MdUtil.NormalizeUri(MdUtil.Unescape(raw));
                }
                p++;
            }
            return null;
        }

        int save = _pos, parens = 0;
        char ch = '\0';
        while (!AtEnd)
        {
            ch = _subject[_pos];
            if (ch == '\\' && _pos + 1 < _subject.Length && MdUtil.IsAsciiPunctuation(_subject[_pos + 1])) _pos += 2;
            else if (ch == '(')
            {
                // Like cmark, limit nesting so a run of "(" cannot make every link attempt rescan the rest of the text.
                if (parens >= MaxLinkParenDepth) return null;
                _pos++;
                parens++;
            }
            else if (ch == ')')
            {
                if (parens < 1) break;
                _pos++;
                parens--;
            }
            else if (ch <= ' ') break; // whitespace and control characters end the destination
            else _pos++;
        }
        if (_pos == save && ch != ')') return null;
        if (parens != 0) return null;
        return MdUtil.NormalizeUri(MdUtil.Unescape(_subject.Substring(save, _pos - save)));
    }

    private string? ParseLinkTitle()
    {
        char open = Peek();
        char close = open switch { '"' => '"', '\'' => '\'', '(' => ')', _ => '\0' };
        if (close == '\0') return null;
        int p = _pos + 1;
        while (p < _subject.Length)
        {
            char c = _subject[p];
            if (c == '\\' && p + 1 < _subject.Length) { p += 2; continue; }
            if (c == close)
            {
                var raw = _subject.Substring(_pos + 1, p - _pos - 1);
                _pos = p + 1;
                return MdUtil.Unescape(raw);
            }
            if (open == '(' && c == '(') return null;
            p++;
        }
        return null;
    }

    /// <summary>Returns the length of a link label at the current position (including brackets), or 0.</summary>
    private int ParseLinkLabel()
    {
        if (Peek() != '[') return 0;
        int p = _pos + 1;
        while (p < _subject.Length && p - _pos <= 1000)
        {
            char c = _subject[p];
            if (c == '\\' && p + 1 < _subject.Length) { p += 2; continue; }
            if (c == '[') return 0;
            if (c == ']')
            {
                int len = p + 1 - _pos;
                if (len > 1001) return 0;
                _pos = p + 1;
                return len;
            }
            p++;
        }
        return 0;
    }

    /// <summary>Parses a link reference definition at <paramref name="start"/> in <paramref name="s"/>; returns the characters consumed, or 0.</summary>
    public int ParseReference(string s, int start)
    {
        _subject = s;
        _pos = start;
        int n = ParseLinkLabel();
        if (n == 0) return 0;
        var rawLabel = s.Substring(start, n);
        if (Peek() != ':') return 0;
        _pos++;
        Spnl();
        var dest = ParseLinkDestination();
        if (dest == null) return 0;

        int beforeTitle = _pos;
        Spnl();
        string? title = null;
        if (_pos != beforeTitle) title = ParseLinkTitle();
        if (title == null)
        {
            title = "";
            _pos = beforeTitle;
        }

        bool atLineEnd = SpaceAtEndOfLine();
        if (!atLineEnd)
        {
            if (title.Length == 0) return 0;
            // The title is not at the end of its line: the definition may still be valid without it.
            title = "";
            _pos = beforeTitle;
            atLineEnd = SpaceAtEndOfLine();
        }
        if (!atLineEnd) return 0;

        var label = MdUtil.NormalizeReference(rawLabel);
        if (label.Length == 0) return 0;
        _refmap.TryAdd(label, new LinkReference(dest, title));
        return _pos - start;
    }

    private bool SpaceAtEndOfLine()
    {
        int p = _pos;
        while (p < _subject.Length && _subject[p] is ' ' or '\t') p++;
        if (p < _subject.Length && _subject[p] != '\n') return false;
        _pos = p < _subject.Length ? p + 1 : p;
        return true;
    }

    // ---- Autolinks, HTML, entities ---------------------------------------------------------------------------------

    private bool ParseAutolink(MdNode block)
    {
        var m = MdRegex.EmailAutolink().Match(_subject, _pos);
        if (m.Success)
        {
            var addr = m.Groups[1].Value;
            AppendLink(block, MdUtil.NormalizeUri("mailto:" + addr), addr);
            _pos += m.Length;
            return true;
        }
        m = MdRegex.UriAutolink().Match(_subject, _pos);
        if (m.Success)
        {
            var uri = m.Groups[1].Value;
            AppendLink(block, MdUtil.NormalizeUri(uri), uri);
            _pos += m.Length;
            return true;
        }
        return false;
    }

    private static void AppendLink(MdNode parent, string dest, string text)
    {
        var link = new MdNode(MdNodeType.Link) { Destination = dest, Title = "" };
        link.AppendChild(Text(text));
        parent.AppendChild(link);
    }

    private bool ParseHtmlTag(MdNode block)
    {
        var m = MdRegex.InlineHtml().Match(_subject, _pos);
        if (!m.Success) return false;
        block.AppendChild(new MdNode(MdNodeType.HtmlInline) { Literal = m.Value });
        _pos += m.Length;
        return true;
    }

    private bool ParseEntity(MdNode block)
    {
        if (!MdUtil.TryDecodeEntity(_subject, _pos, out var value, out int len)) return false;
        block.AppendChild(Text(value));
        _pos += len;
        return true;
    }

    // ---- Post-processing -------------------------------------------------------------------------------------------

    private static void MergeText(MdNode root)
    {
        var containers = new List<MdNode>();
        foreach (var (n, entering) in root.Walk())
            if (entering && n.FirstChild != null) containers.Add(n);
        foreach (var parent in containers)
        {
            var node = parent.FirstChild;
            while (node != null)
            {
                var next = node.Next;
                if (node.Type == MdNodeType.Text)
                {
                    if (next is { Type: MdNodeType.Text })
                    {
                        var sb = new StringBuilder(node.Literal);
                        while (next is { Type: MdNodeType.Text })
                        {
                            sb.Append(next.Literal);
                            var after = next.Next;
                            next.Unlink();
                            next = after;
                        }
                        node.Literal = sb.ToString();
                    }
                    if (node.Literal!.Length == 0) node.Unlink();
                }
                node = next;
            }
        }
    }

    /// <summary>GFM extended autolinks: bare www., http(s):// and ftp:// URLs and e-mail addresses in text become links.</summary>
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

    private static void LinkifyText(MdNode node)
    {
        var s = node.Literal!;
        int searchFrom = 0, textStart = 0;
        int emailStart = -2, emailEnd = 0; // -2: not searched yet, -1: there is none
        var insertAfter = node;
        while (searchFrom < s.Length)
        {
            int start = FindUrlStart(s, searchFrom, out bool www);
            if (emailStart == -2 || (emailStart >= 0 && emailStart < searchFrom)) emailStart = FindEmail(s, searchFrom, out emailEnd);
            int end;
            string dest;
            if (emailStart >= 0 && (start < 0 || emailStart < start))
            {
                start = emailStart;
                end = emailEnd;
                dest = "mailto:" + s.Substring(start, end - start);
            }
            else
            {
                if (start < 0) break;
                end = start;
                while (end < s.Length && !char.IsWhiteSpace(s[end]) && s[end] != '<') end++;
                end = TrimUrlEnd(s, start, end);
                int hostStart = www ? start : s.IndexOf("://", start, StringComparison.Ordinal) + 3;
                if (!ValidDomain(s, hostStart, end))
                {
                    searchFrom = start + 1;
                    continue;
                }
                dest = www ? "http://" + s.Substring(start, end - start) : s.Substring(start, end - start);
            }

            if (start > textStart)
            {
                var before = Text(s.Substring(textStart, start - textStart));
                insertAfter.InsertAfter(before);
                insertAfter = before;
            }
            var link = new MdNode(MdNodeType.Link) { Destination = MdUtil.NormalizeUri(dest), Title = "" };
            link.AppendChild(Text(s.Substring(start, end - start)));
            insertAfter.InsertAfter(link);
            insertAfter = link;
            textStart = searchFrom = end;
        }
        if (insertAfter == node) return; // nothing found
        if (textStart < s.Length) insertAfter.InsertAfter(Text(s.Substring(textStart)));
        node.Unlink();
    }

    private static bool IsEmailLocalChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+';

    /// <summary>Finds a bare e-mail address (GFM extended email autolink) at or after <paramref name="from"/>.</summary>
    private static int FindEmail(string s, int from, out int end)
    {
        end = -1;
        for (int at = s.IndexOf('@', from); at >= 0; at = s.IndexOf('@', at + 1))
        {
            int start = at;
            while (start > from && IsEmailLocalChar(s[start - 1])) start--;
            if (start == at) continue;
            int p = at + 1, dots = 0;
            while (p < s.Length && (char.IsAsciiLetterOrDigit(s[p]) || s[p] is '-' or '_' || (s[p] == '.' && p + 1 < s.Length && char.IsAsciiLetterOrDigit(s[p + 1]))))
            {
                if (s[p] == '.') dots++;
                p++;
            }
            if (dots == 0 || p == at + 1 || s[p - 1] is '-' or '_') continue;
            end = p;
            return start;
        }
        return -1;
    }

    private static int FindUrlStart(string s, int from, out bool www)
    {
        for (int i = from; i < s.Length; i++)
        {
            char c = s[i];
            if (c is not ('w' or 'W' or 'h' or 'H' or 'f' or 'F')) continue;
            // Must start the text or follow whitespace or one of * _ ~ (.
            if (i > 0 && !(char.IsWhiteSpace(s[i - 1]) || s[i - 1] is '*' or '_' or '~' or '(')) continue;
            var rest = s.AsSpan(i);
            if (rest.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) { www = true; return i; }
            if (rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            {
                www = false;
                return i;
            }
        }
        www = false;
        return -1;
    }

    private static int TrimUrlEnd(string s, int start, int end)
    {
        while (end > start)
        {
            char c = s[end - 1];
            if (c is '?' or '!' or '.' or ',' or ':' or '*' or '_' or '~' or '\'' or '"') { end--; continue; }
            if (c == ')')
            {
                int open = 0, close = 0;
                for (int i = start; i < end; i++)
                {
                    if (s[i] == '(') open++;
                    else if (s[i] == ')') close++;
                }
                if (close > open) { end--; continue; }
            }
            if (c == ';')
            {
                // A trailing entity-like "&name;" is not part of the URL.
                int amp = end - 2;
                while (amp > start && char.IsAsciiLetterOrDigit(s[amp])) amp--;
                if (amp >= start && s[amp] == '&' && amp < end - 2) { end = amp; continue; }
            }
            break;
        }
        return end;
    }

    private static bool ValidDomain(string s, int start, int end)
    {
        int segments = 0, segLen = 0;
        for (int i = start; i < end; i++)
        {
            char c = s[i];
            if (c is '/' or '?' or '#' or ':') break;
            if (c == '.')
            {
                if (segLen == 0) return false;
                segments++;
                segLen = 0;
            }
            else if (char.IsLetterOrDigit(c) || c is '-' or '_') segLen++;
            else return false;
        }
        return segments >= 1 && segLen > 0;
    }
}
