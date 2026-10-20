namespace PdfPecker.Html;

/// <summary>
/// A forgiving, single-pass HTML parser. It handles the tag soup that real documents contain
/// (unclosed paragraphs and list items, implicit table cell closing, void elements, raw text
/// elements) without implementing the full HTML5 tree construction algorithm.
/// </summary>
internal sealed class HtmlParser
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextElements = new(StringComparer.Ordinal)
    {
        "script", "style", "textarea", "title", "xmp", "noscript",
    };

    // Elements whose start tag closes an open <p>.
    private static readonly HashSet<string> ClosesParagraph = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "fieldset",
        "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "main",
        "menu", "nav", "ol", "p", "pre", "section", "summary", "table", "ul",
    };

    private readonly string _src;
    private int _pos;
    private readonly List<Element> _stack = new();

    // Tag and attribute names as written, mapped to their lower-case strings: a document repeats the same few names, so
    // each gets one string instead of one per tag.
#if NET9_0_OR_GREATER
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _nameLookup;

    private HtmlParser(string src)
    {
        _src = src;
        _nameLookup = _names.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    private string Name(ReadOnlySpan<char> written)
    {
        if (_nameLookup.TryGetValue(written, out var name)) return name;
        var key = written.ToString();
        name = key.ToLowerInvariant();
        _names[key] = name;
        return name;
    }
#else
    // .NET 8 has no span lookup on Dictionary, so names are keyed by their span hash and the stored key is compared;
    // a hash collision just replaces the entry.
    private readonly Dictionary<int, (string Key, string Name)> _names = new();

    private HtmlParser(string src) => _src = src;

    private string Name(ReadOnlySpan<char> written)
    {
        var hash = string.GetHashCode(written);
        if (_names.TryGetValue(hash, out var entry) && written.SequenceEqual(entry.Key)) return entry.Name;
        var key = written.ToString();
        var name = key.ToLowerInvariant();
        _names[hash] = (key, name);
        return name;
    }
#endif

    public static Element Parse(string html)
    {
        var p = new HtmlParser(html);
        var doc = new Element("#document");
        p._stack.Add(doc);
        p.Run();
        p.FlushMerge();
        return doc;
    }

    private Element Current => _stack[_stack.Count - 1];

    private void Run()
    {
        string s = _src;
        int textStart = 0;
        while (_pos < s.Length)
        {
            int lt = s.IndexOf('<', _pos);
            if (lt < 0) { _pos = s.Length; break; }
            if (lt + 1 >= s.Length) { _pos = s.Length; break; }
            char n = s[lt + 1];
            if (n == '!' || n == '?' || n == '/' || IsAsciiLetter(n))
            {
                if (lt > textStart) AddText(s.Substring(textStart, lt - textStart));
                _pos = lt;
                ParseMarkup();
                textStart = _pos;
            }
            else
            {
                _pos = lt + 1; // a lone '<' is text
            }
        }
        if (s.Length > textStart) AddText(s.Substring(textStart));
    }

    private static bool IsAsciiLetter(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';

    private void AddText(string raw)
    {
        var cur = Current;
        string text = HtmlEntities.Decode(raw);
        // Merge with a preceding text node (happens around comments and ignored end tags). The pieces are collected in
        // a builder: appending to the node's string each time would be quadratic for many pieces.
        if (IsTableContext(cur.Name) && !string.IsNullOrWhiteSpace(text))
            Foster(new TextNode(text));
        else if (cur.Children.Count > 0 && cur.Children[cur.Children.Count - 1] is TextNode prev)
        {
            if (_merging != prev)
            {
                FlushMerge();
                _merging = prev;
                _mergeBuffer.Append(prev.Text);
            }
            _mergeBuffer.Append(text);
        }
        else
            cur.AppendChild(new TextNode(text));
    }

    private TextNode? _merging;
    private readonly System.Text.StringBuilder _mergeBuffer = new();

    private void FlushMerge()
    {
        if (_merging == null) return;
        _merging.Text = _mergeBuffer.ToString();
        _mergeBuffer.Clear();
        _merging = null;
    }

    private void ParseMarkup()
    {
        string s = _src;
        int p = _pos + 1;
        char c = s[p];
        if (c == '!')
        {
            if (string.CompareOrdinal(s, p + 1, "--", 0, 2) == 0)
            {
                int end = s.IndexOf("-->", p + 3, StringComparison.Ordinal);
                _pos = end < 0 ? s.Length : end + 3;
                return;
            }
            if (string.CompareOrdinal(s, p + 1, "[CDATA[", 0, 7) == 0)
            {
                int end = s.IndexOf("]]>", p + 8, StringComparison.Ordinal);
                int stop = end < 0 ? s.Length : end;
                Current.AppendChild(new TextNode(s.Substring(p + 8, stop - (p + 8))));
                _pos = end < 0 ? s.Length : end + 3;
                return;
            }
            SkipPast('>', p);
            return;
        }
        if (c == '?') { SkipPast('>', p); return; }
        if (c == '/')
        {
            p++;
            int nameStart = p;
            while (p < s.Length && !IsNameEnd(s[p])) p++;
            string name = Name(s.AsSpan(nameStart, p - nameStart));
            SkipPast('>', p);
            if (name.Length > 0) CloseElement(name);
            return;
        }

        // Start tag.
        {
            int nameStart = p;
            while (p < s.Length && !IsNameEnd(s[p])) p++;
            string name = Name(s.AsSpan(nameStart, p - nameStart));
            var el = new Element(name);
            bool selfClosing = ParseAttributes(el, ref p);
            _pos = p;

            if (IsTableStructure(name) && name != "table") CloseFostered();
            else if (name == "form" && HasOpen("form")) return; // browsers ignore a form inside a form
            else if (name is "input" or "textarea") CloseSelect();
            ImplicitlyClose(name);
            if (name == "tr" && Current.Name == "table")
            {
                // As in browsers, rows directly inside a table get an implied <tbody>.
                var tbody = new Element("tbody");
                Current.AppendChild(tbody);
                _stack.Add(tbody);
            }
            bool inTable = IsTableContext(Current.Name) && !IsTableStructure(name);
            if (inTable && name == "form")
            {
                // A form directly in a table or row stays there without holding the cells that follow, as in browsers.
                Current.AppendChild(el);
                return;
            }
            if (inTable && !StaysInTable(el)) Foster(el);
            else Current.AppendChild(el);

            if (VoidElements.Contains(name) || selfClosing && name != "script")
                return;

            if (RawTextElements.Contains(name))
            {
                string closeTag = "</" + name;
                int end = IndexOfIgnoreCase(s, closeTag, _pos);
                int stop = end < 0 ? s.Length : end;
                if (stop > _pos)
                {
                    string raw = s.Substring(_pos, stop - _pos);
                    el.AppendChild(new TextNode(name == "title" || name == "textarea" ? HtmlEntities.Decode(raw) : raw));
                }
                _pos = stop;
                if (end >= 0) SkipPast('>', end);
                return;
            }
            // Deeply nested documents are flattened instead of risking a stack overflow in the recursive stages.
            if (_stack.Count < MaxDepth) _stack.Add(el);
        }
    }

    private const int MaxDepth = 256;

    private static bool IsNameEnd(char c) => c == '>' || c == '/' || c <= ' ';

    private void SkipPast(char ch, int from)
    {
        int i = _src.IndexOf(ch, from);
        _pos = i < 0 ? _src.Length : i + 1;
    }

    private static int IndexOfIgnoreCase(string s, string value, int start)
        => s.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses attributes up to and including the closing '&gt;'. Returns true for "/&gt;".</summary>
    private bool ParseAttributes(Element el, ref int p)
    {
        string s = _src;
        bool selfClosing = false;
        while (p < s.Length)
        {
            char c = s[p];
            if (c <= ' ') { p++; continue; }
            if (c == '>') { p++; return selfClosing; }
            if (c == '/') { selfClosing = true; p++; continue; }
            selfClosing = false;

            int nameStart = p;
            while (p < s.Length && s[p] > ' ' && s[p] != '=' && s[p] != '>' && !(s[p] == '/' && p + 1 < s.Length && s[p + 1] == '>')) p++;
            string attrName = Name(s.AsSpan(nameStart, p - nameStart));
            while (p < s.Length && s[p] <= ' ') p++;
            string value = "";
            if (p < s.Length && s[p] == '=')
            {
                p++;
                while (p < s.Length && s[p] <= ' ') p++;
                if (p < s.Length && (s[p] == '"' || s[p] == '\''))
                {
                    char q = s[p++];
                    int end = s.IndexOf(q, p);
                    if (end < 0) end = s.Length;
                    value = HtmlEntities.Decode(s.Substring(p, end - p));
                    p = Math.Min(end + 1, s.Length);
                }
                else
                {
                    int vs = p;
                    while (p < s.Length && s[p] > ' ' && s[p] != '>') p++;
                    value = HtmlEntities.Decode(s.Substring(vs, p - vs));
                }
            }
            if (attrName.Length > 0 && el.GetAttribute(attrName) == null)
                el.AddAttribute(attrName, value);
        }
        return selfClosing;
    }

    private void ImplicitlyClose(string name)
    {
        if (ClosesParagraph.Contains(name)) CloseIfOpenInScope("p", "#document");

        switch (name)
        {
            case "li":
                CloseIfOpenInScope("li", "ul", "ol", "menu");
                break;
            case "dt":
            case "dd":
                CloseIfOpenInScope("dt", "dl");
                CloseIfOpenInScope("dd", "dl");
                break;
            case "tr":
                CloseIfOpenInScope("td", "table");
                CloseIfOpenInScope("th", "table");
                CloseIfOpenInScope("tr", "table");
                break;
            case "td":
            case "th":
                CloseIfOpenInScope("td", "tr", "table");
                CloseIfOpenInScope("th", "tr", "table");
                break;
            case "thead":
            case "tbody":
            case "tfoot":
                CloseIfOpenInScope("td", "table");
                CloseIfOpenInScope("th", "table");
                CloseIfOpenInScope("tr", "table");
                CloseIfOpenInScope("thead", "table");
                CloseIfOpenInScope("tbody", "table");
                CloseIfOpenInScope("tfoot", "table");
                break;
            case "option":
                CloseIfOpenInScope("option", "select");
                break;
        }
    }

    /// <summary>Pops the stack down to and including <paramref name="name"/> if it is open below any boundary element.</summary>
    private void CloseIfOpenInScope(string name, string boundary1, string? boundary2 = null, string? boundary3 = null)
    {
        for (int i = _stack.Count - 1; i > 0; i--)
        {
            string n = _stack[i].Name;
            if (n == name)
            {
                _stack.RemoveRange(i, _stack.Count - i);
                return;
            }
            if (n == boundary1 || n == boundary2 || n == boundary3) return;
            // A <p> is closed by block starts even through inline formatting elements, but not through blocks.
            if (name == "p" && n != "span" && n != "b" && n != "i" && n != "em" && n != "strong" && n != "a" && n != "font" && n != "u")
                return;
        }
    }

    private static bool IsTableStructure(string name)
        => name is "table" or "tr" or "td" or "th" or "tbody" or "thead" or "tfoot" or "caption";

    // Where only rows, cells and sections belong: anything else written there is moved in front of the table.
    private static bool IsTableContext(string name) => name is "table" or "tbody" or "thead" or "tfoot" or "tr";

    private static bool StaysInTable(Element el)
        => el.Name is "col" or "colgroup" or "script" or "style" or "template"
           || el.Name == "input" && string.Equals(el.GetAttribute("type")?.Trim(), "hidden", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts a node that a table cannot hold in front of the table, as browsers' foster parenting does; otherwise the
    /// table layout, which only looks at rows and cells, would drop it.
    /// </summary>
    private void Foster(Node node)
    {
        for (int i = _stack.Count - 1; i > 0; i--)
        {
            var table = _stack[i];
            if (table.Name != "table") continue;
            if (table.Parent is not { } parent) break;
            // The open table is its parent's last child, so the search from the end stops at once.
            parent.Children.Insert(parent.Children.LastIndexOf(table), node);
            node.Parent = parent;
            return;
        }
        Current.AppendChild(node);
    }

    /// <summary>
    /// Closes the elements that were moved in front of a table when the table's own structure continues, so that the
    /// next row or cell goes back into the table.
    /// </summary>
    private void CloseFostered()
    {
        for (int i = _stack.Count - 1; i > 0; i--)
        {
            string n = _stack[i].Name;
            if (n is "td" or "th" or "caption") return;
            if (!IsTableContext(n)) continue;
            if (i + 1 < _stack.Count && _stack[i + 1].Parent != _stack[i]) _stack.RemoveRange(i + 1, _stack.Count - i - 1);
            return;
        }
    }

    private bool HasOpen(string name)
    {
        for (int i = _stack.Count - 1; i > 0; i--)
            if (_stack[i].Name == name) return true;
        return false;
    }

    /// <summary>A field cannot sit inside a drop-down: browsers close the select (and its open options) first.</summary>
    private void CloseSelect()
    {
        for (int i = _stack.Count - 1; i > 0; i--)
        {
            string n = _stack[i].Name;
            if (n == "select") { _stack.RemoveRange(i, _stack.Count - i); return; }
            if (n is not ("option" or "optgroup")) return;
        }
    }

    private void CloseElement(string name)
    {
        if (name == "br")
        {
            // </br> is treated as <br> by browsers.
            Current.AppendChild(new Element("br"));
            return;
        }
        for (int i = _stack.Count - 1; i > 0; i--)
        {
            if (_stack[i].Name == name)
            {
                _stack.RemoveRange(i, _stack.Count - i);
                return;
            }
            // Don't let a stray end tag escape a table cell or table.
            string n = _stack[i].Name;
            if ((n == "td" || n == "th" || n == "table") && !IsTableStructure(name))
                return;
        }
    }
}
