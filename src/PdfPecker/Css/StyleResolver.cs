using System.Globalization;
using PdfPecker.Fonts;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>Rules bucketed by the most selective part of their subject compound, so each element only tests likely matches.</summary>
internal sealed class RuleIndex
{
    public readonly Dictionary<string, List<StyleRule>> ById = new(StringComparer.Ordinal);
    public readonly Dictionary<string, List<StyleRule>> ByClass = new(StringComparer.Ordinal);
    public readonly Dictionary<string, List<StyleRule>> ByTag = new(StringComparer.Ordinal);
    public readonly List<StyleRule> Universal = new();
    public readonly RuleIndex? PseudoElements; // rules for ::before and ::after, matched against their host

    public RuleIndex(Stylesheet sheet) : this(sheet.Rules, pseudo: false) { }

    private RuleIndex(IEnumerable<StyleRule> rules, bool pseudo)
    {
        List<StyleRule>? pseudoRules = null;
        foreach (var r in rules)
        {
            if (!pseudo && r.Selector.PseudoElement != PseudoElement.None) { (pseudoRules ??= new()).Add(r); continue; }
            var subj = r.Selector.Subject;
            if (subj.Id != null) Add(ById, subj.Id, r);
            else if (subj.Classes != null) Add(ByClass, subj.Classes[0], r);
            else if (subj.Tag != null) Add(ByTag, subj.Tag, r);
            else Universal.Add(r);
        }
        if (pseudoRules != null) PseudoElements = new RuleIndex(pseudoRules, pseudo: true);
    }

    private static void Add(Dictionary<string, List<StyleRule>> d, string key, StyleRule r)
    {
        if (!d.TryGetValue(key, out var list)) d[key] = list = new List<StyleRule>();
        list.Add(r);
    }
}

/// <summary>Computes the style of every element: user-agent rules, presentational attributes, author rules and inline styles.</summary>
internal sealed class StyleResolver
{
    private static readonly Lazy<RuleIndex> UaIndex = new(() =>
    {
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet(UserAgentStyles.Css, sheet);
        return new RuleIndex(sheet);
    });

    // Cascade tiers, lowest first.
    private const long TierUa = 0, TierHint = 1, TierAuthor = 2, TierInline = 3, TierAuthorImportant = 4, TierInlineImportant = 5, TierUaImportant = 6;

    private readonly RuleIndex _author;
    private float _rootFontSize = 12f;
    private readonly float _vw, _vh; // 1vw and 1vh in points
    private readonly List<Declaration> _expanded = new();
    private readonly List<KeyValuePair<string, string>> _custom = new();

    /// <summary>True once a declaration with vh, vmin or vmax units has been applied.</summary>
    public bool UsedViewportHeight { get; private set; }

    /// <summary>Pagination results that generated content uses (target-counter(), string()); shared between passes.</summary>
    public PagedContent Paged { get; init; } = new();

    /// <summary>The string-set assignments of the last resolution, in document order.</summary>
    public List<StringAssignment> Strings { get; } = new();

    /// <summary>The counter styles of list-style-type and counter(): the predefined ones and the sheet's @counter-style rules.</summary>
    public CounterStyles CounterStyles { get; init; }

    /// <summary>The footnotes (float: footnote) of the last resolution, in document order.</summary>
    public List<Element> Footnotes { get; } = new();

    private int _footnoteDepth; // inside a footnote, where float: footnote does nothing
    private int? _footnotePage; // the page of the last footnote's call, for per-page numbering

    /// <summary>Some element has a page property other than auto (named pages).</summary>
    public bool UsedPageNames { get; private set; }

    private Dictionary<string, Element>? _byId;

    /// <summary>The element with this id in the document <paramref name="any"/> belongs to (the first one).</summary>
    internal Element? ElementById(Element any, string id)
    {
        if (_byId == null)
        {
            _byId = new Dictionary<string, Element>(StringComparer.Ordinal);
            var root = any;
            while (root.Parent != null) root = root.Parent;
            var stack = new Stack<Element>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                if (e.GetAttribute("id") is { } eid) _byId.TryAdd(eid, e);
                for (int i = e.Children.Count - 1; i >= 0; i--)
                    if (e.Children[i] is Element c && !c.Generated) stack.Push(c);
            }
        }
        return _byId.TryGetValue(id, out var found) ? found : null;
    }

    private long[] _keys = new long[64];
    private Declaration[] _decls = new Declaration[64];
    private int _count;
    private readonly List<Declaration> _scratch = new();
    private readonly bool _hasCustomProperties;
    private int _resolved;
    private readonly Dictionary<string, List<Declaration>> _inlineCache = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), Declaration> _hintCache = new();
    private readonly Dictionary<StyleKey, ComputedStyle> _styleCache = new();
    private readonly StyleKey _probe = new();
    private readonly List<Declaration> _registeredEach = new(), _registeredRoot = new();
    private bool _decorationLineSet;

    /// <summary>
    /// Elements with the same parent style and the same winning declarations get the same computed style. This makes
    /// repeated structures (table rows, list items, paragraphs) nearly free to style.
    /// </summary>
    private sealed class StyleKey : IEquatable<StyleKey>
    {
        private ComputedStyle _parent = null!;
        private Declaration[] _decls = Array.Empty<Declaration>();
        private int _count;
        private int _hash;

        /// <summary>Points the key at the resolver's working buffer, for lookups without copying.</summary>
        public void Set(ComputedStyle parent, Declaration[] decls, int count)
        {
            _parent = parent;
            _decls = decls;
            _count = count;
            var h = new HashCode();
            h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(parent));
            for (int i = 0; i < count; i++) h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(decls[i]));
            _hash = h.ToHashCode();
        }

        /// <summary>An immutable copy suitable for storing in the cache.</summary>
        public StyleKey Freeze()
            => new() { _parent = _parent, _decls = _decls.AsSpan(0, _count).ToArray(), _count = _count, _hash = _hash };

        public bool Equals(StyleKey? other)
        {
            if (other == null || other._hash != _hash || !ReferenceEquals(other._parent, _parent) || other._count != _count) return false;
            for (int i = 0; i < _count; i++)
                if (!ReferenceEquals(_decls[i], other._decls[i])) return false;
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as StyleKey);
        public override int GetHashCode() => _hash;
    }

    /// <param name="authorSheet">The document's style rules.</param>
    /// <param name="viewportWidth">Width in points that 100vw refers to: the page's content width.</param>
    /// <param name="viewportHeight">Height in points that 100vh refers to: the page's content height.</param>
    public StyleResolver(Stylesheet authorSheet, float viewportWidth = 595.28f, float viewportHeight = 841.89f)
    {
        authorSheet.RankLayers();
        _author = new RuleIndex(authorSheet);
        _firstLetterRules = authorSheet.Rules.Any(r => r.Selector.PseudoElement == PseudoElement.FirstLetter);
        _firstLineRules = authorSheet.Rules.Any(r => r.Selector.PseudoElement == PseudoElement.FirstLine);
        CounterStyles = CounterStyles.From(authorSheet.CounterStyles);
        if (authorSheet.RegisteredProperties.Count > 0)
        {
            // Registered custom properties start at their initial value: on every element when they do not inherit,
            // on the root when they do. These act as the lowest-priority declarations.
            foreach (var p in authorSheet.RegisteredProperties)
                (p.Inherits ? _registeredRoot : _registeredEach).Add(new Declaration(p.Name, p.InitialValue ?? "initial", false));
        }
        _hasCustomProperties = authorSheet.RegisteredProperties.Count > 0
                               || authorSheet.Rules.Any(r => r.Declarations.Any(d => d.Property.StartsWith("--", StringComparison.Ordinal)));
        // Chrome sizes the print viewport in whole CSS pixels, rounded down: 100vh on A4 is 1122px, not 1122.52px.
        _vw = WholePx(viewportWidth) / 100f;
        _vh = WholePx(viewportHeight) / 100f;
    }

    private static float WholePx(float pt) => MathF.Floor(pt / 0.75f + 0.001f) * 0.75f;

    public void ResolveTree(Element document, ComputedStyle rootStyle)
    {
        document.Style = rootStyle;
        Strings.Clear();
        Footnotes.Clear();
        _footnoteDepth = 0;
        _footnotePage = null;
        _byId = null;
        Prepare(document);
        // Fragments without an <html> element: custom properties declared on :root or html still apply.
        if (_hasCustomProperties && !document.Children.Any(c => c is Element { Name: "html" }))
        {
            var html = new Element("html") { Parent = document, ElementIndex = 1, ElementSiblingCount = 1, TypeIndex = 1, TypeCount = 1 };
            Resolve(html, rootStyle);
            rootStyle.CustomProperties = html.Style.CustomProperties;
        }
        ResolveChildren(document);
    }

    private static void Prepare(Element parent)
    {
        // ::before, ::after and ::first-letter boxes from an earlier pass are generated again; a first letter goes back
        // into the text it was taken from.
        for (int i = parent.Children.Count - 1; i >= 0; i--)
        {
            if (parent.Children[i] is not Element { Generated: true, Name: FirstLetterName } letter) continue;
            var text = letter.Children.Count == 1 && letter.Children[0] is TextNode t ? t.Text : "";
            if (i + 1 < parent.Children.Count && parent.Children[i + 1] is TextNode rest) rest.Text = text + rest.Text;
            else parent.Children.Insert(i + 1, new TextNode(text) { Parent = parent });
        }
        parent.Children.RemoveAll(c => c is Element { Generated: true });
        int count = 0;
        foreach (var c in parent.Children) if (c is Element) count++;
        int index = 0;
        Element? prev = null;
        // The last sibling seen of each element name, for :nth-of-type(). Siblings use only a few names.
        List<Element>? lastOfName = count > 1 ? new List<Element>(4) : null;
        foreach (var c in parent.Children)
        {
            if (c is not Element e) continue;
            e.ElementIndex = ++index;
            e.ElementSiblingCount = count;
            e.PreviousElementSibling = prev;
            prev = e;
            e.TypeIndex = 1;
            if (lastOfName != null)
            {
                int k = 0;
                while (k < lastOfName.Count && lastOfName[k].Name != e.Name) k++;
                if (k == lastOfName.Count) lastOfName.Add(e);
                else
                {
                    e.TypeIndex = lastOfName[k].TypeIndex + 1;
                    lastOfName[k] = e;
                }
            }
            e.Id = e.GetAttribute("id");
            e.LayoutKey = null;
            if (e.FootnoteIndex >= 0)
            {
                // A footnote of the last pass goes back into the flow until its style says again where it goes.
                e.FootnoteIndex = -1;
                e.Detached = false;
            }
            e.FirstLine = null;
            var cls = e.GetAttribute("class");
            if (!string.IsNullOrWhiteSpace(cls))
                e.Classes = cls!.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            Prepare(e);
        }
        foreach (var c in parent.Children)
        {
            if (c is not Element e) continue;
            if (lastOfName == null) { e.TypeCount = 1; continue; }
            foreach (var last in lastOfName)
                if (last.Name == e.Name) { e.TypeCount = last.TypeIndex; break; }
        }
    }

    private void ResolveChildren(Element parent)
    {
        // Counters created by these children (and the parent's ::before) are in scope until the parent ends.
        List<string>? scope = null;
        bool pseudos = HasPseudoRules && CanHaveGenerated(parent);
        if (pseudos) Generate(parent, PseudoElement.Before, ref scope);
        var children = parent.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not Element e || e.Generated) continue;
            Resolve(e, parent.Style);
            if (e.Style.Display == Display.None) continue; // descendants are never rendered
            bool footnote = e.Style.Footnote && _footnoteDepth == 0;
            if (footnote) StartFootnote();
            if (_countersUsed) ApplyCounters(e.Style, ref scope);
            Element? footnoteMarker = null;
            if (footnote)
            {
                footnoteMarker = MakeFootnote(parent, i, e);
                i++; // the call went in before the footnote
            }
            if (e.Id != null && Paged.WantedCounters.Count > 0) SnapshotCounters(e.Id);
            if (e.Style.Display == Display.ListItem && HasPseudoRules) ResolveMarker(e);
            if (footnote) _footnoteDepth++;
            ResolveChildren(e);
            if (footnote)
            {
                _footnoteDepth--;
                // The marker goes before the footnote's ::before box, which its children's resolution generated, and
                // into its first paragraph when it starts with one, so the marker begins the footnote's first line.
                if (footnoteMarker != null)
                {
                    var holder = FirstLineHolder(e);
                    footnoteMarker.Parent = holder;
                    holder.Children.Insert(0, footnoteMarker);
                }
            }
            if ((_firstLetterRules || _firstLineRules) && e.Style.Display is Display.Block or Display.ListItem or Display.InlineBlock
                    or Display.TableCell or Display.TableCaption)
            {
                if (_firstLineRules) ResolveFirstLine(e);
                if (_firstLetterRules) GenerateFirstLetter(e);
            }
            if (e.Style.StringSet != null) SetStrings(e);
        }
        if (pseudos) Generate(parent, PseudoElement.After, ref scope);
        if (scope != null)
            foreach (var name in scope)
            {
                var stack = _counters[name];
                stack.RemoveAt(stack.Count - 1);
            }
    }

    /// <summary>Keeps the values of the counters target-counter() asks for at an element with an id.</summary>
    private void SnapshotCounters(string id)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in Paged.WantedCounters)
            if (_counters.TryGetValue(name, out var stack) && stack.Count > 0) values[name] = stack[^1];
        Paged.CounterSnapshots[id] = values;
    }

    /// <summary>Evaluates an element's string-set: "name content-list" pairs separated by commas.</summary>
    private void SetStrings(Element e)
    {
        foreach (var part in CssParser.SplitTopLevel(e.Style.StringSet!, ','))
        {
            var p = part.Trim();
            int sp = 0;
            while (sp < p.Length && !char.IsWhiteSpace(p[sp])) sp++;
            if (sp == 0 || sp >= p.Length) continue;
            var name = p.Substring(0, sp);
            var value = GeneratedContent.Evaluate(p.Substring(sp).Trim(), e, this);
            if (value == null) continue;
            e.LayoutKey ??= "\u0001s" + Strings.Count.ToString(CultureInfo.InvariantCulture);
            Strings.Add(new StringAssignment(e, name, value, e.LayoutKey));
        }
    }

    private bool HasPseudoRules => _author.PseudoElements != null || UaIndex.Value.PseudoElements != null;

    /// <summary>
    /// Before a footnote's own counter properties apply: the footnote counter gets a document-wide instance, so
    /// footnotes number through the document, and starts again on each page when an @page rule resets it.
    /// </summary>
    private void StartFootnote()
    {
        _countersUsed = true;
        if (!_counters.TryGetValue("footnote", out var stack)) _counters["footnote"] = stack = new List<int>();
        if (stack.Count == 0) stack.Add(0);
        if (Paged.FootnotePageReset is int start && Paged.FootnotePageOf?.Invoke(Footnotes.Count) is int page)
        {
            if (page != _footnotePage) stack[^1] = start;
            _footnotePage = page;
        }
    }

    /// <summary>
    /// Takes a footnote out of the flow: increments the footnote counter (unless its own counter-increment names it),
    /// puts its ::footnote-call box in its place in <paramref name="parent"/> and returns its ::footnote-marker box,
    /// which goes first in the footnote once its children are resolved.
    /// </summary>
    private Element? MakeFootnote(Element parent, int index, Element footnote)
    {
        var s = footnote.Style;
        if (s.CounterIncrement == null || !Array.Exists(s.CounterIncrement, c => c.Name == "footnote"))
        {
            var stack = _counters["footnote"];
            if (stack.Count == 0) stack.Add(0);
            stack[^1] = unchecked(stack[^1] + 1);
        }
        footnote.FootnoteIndex = Footnotes.Count;
        footnote.Detached = true;
        Footnotes.Add(footnote);

        var call = new Element("::footnote-call") { Generated = true, Parent = parent, FootnoteBody = footnote };
        call.Style = PseudoStyle(footnote, PseudoElement.FootnoteCall, "::footnote-call") ?? s.CreateChild();
        // A call without content still links its footnote, and still marks its place in the text for the layout.
        var callText = call.Style.Display == Display.None ? null : GeneratedContent.Evaluate(call.Style.Content ?? "counter(footnote)", footnote, this);
        if (!string.IsNullOrEmpty(callText)) call.AppendChild(new TextNode(callText));
        if (call.Style.Display != Display.Inline) call.Style = Inlined(call.Style);
        parent.Children.Insert(index, call);

        var ms = PseudoStyle(footnote, PseudoElement.FootnoteMarker, "::footnote-marker");
        if (ms == null || ms.Display == Display.None) return null;
        var markerText = GeneratedContent.Evaluate(ms.Content ?? "counter(footnote) \". \"", footnote, this);
        if (string.IsNullOrEmpty(markerText)) return null;
        var marker = new Element("::footnote-marker") { Generated = true, Style = ms.Display == Display.Inline ? ms : Inlined(ms), Parent = footnote };
        marker.AppendChild(new TextNode(markerText));
        return marker;
    }

    /// <summary>The block whose content starts <paramref name="e"/>: its first child, while that is a block before any text.</summary>
    private static Element FirstLineHolder(Element e)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            Element? next = null;
            foreach (var c in e.Children)
            {
                if (c is TextNode t && string.IsNullOrWhiteSpace(t.Text)) continue;
                if (c is Element { Generated: false, Detached: false, Style: { Display: Display.Block or Display.ListItem, Floated: false } } ce
                    && !ce.Style.OutOfFlow) next = ce;
                break;
            }
            if (next == null) return e;
            e = next;
        }
        return e;
    }

    /// <summary>The style rules give a pseudo-element of <paramref name="host"/>, or null when none matches.</summary>
    private ComputedStyle? PseudoStyle(Element host, PseudoElement which, string name)
    {
        _count = 0;
        if (UaIndex.Value.PseudoElements is { } ua) Collect(ua, host, TierUa, TierUaImportant, which);
        if (_author.PseudoElements is { } author) Collect(author, host, TierAuthor, TierAuthorImportant, which);
        if (_count == 0) return null;
        for (int i = 0; i < _registeredEach.Count; i++) Push(TierUa, 0, 0, i, _registeredEach[i]);
        if (_count > 1) Array.Sort(_keys, _decls, 0, _count);
        return Compute(name, host.Style);
    }

    /// <summary>The footnote call and marker are inline boxes in the text, whatever display they were given.</summary>
    private static ComputedStyle Inlined(ComputedStyle s)
    {
        var c = s.Clone();
        c.Display = Display.Inline;
        return c;
    }

    /// <summary>Elements that can hold ::before and ::after boxes: not replaced or void elements, SVG or table internals.</summary>
    private static bool CanHaveGenerated(Element e)
    {
        if (e.Name is "#document" or "img" or "br" or "wbr" or "svg" or "input" or "select" or "textarea" or "video" or "audio"
            or "iframe" or "canvas" or "object" or "embed" or "meta" or "link" or "style" or "script" or "head" or "title" or "html") return false;
        var d = e.Style.Display;
        if (d is Display.None or Display.Table or Display.TableRow or Display.TableRowGroup or Display.TableHeaderGroup
            or Display.TableFooterGroup or Display.TableColumn or Display.TableColumnGroup) return false;
        return !IsSvgElement(e);
    }

    /// <summary>Adds the ::before or ::after box of <paramref name="host"/> when a rule gives it content.</summary>
    private void Generate(Element host, PseudoElement which, ref List<string>? scope)
    {
        _count = 0;
        if (UaIndex.Value.PseudoElements is { } ua) Collect(ua, host, TierUa, TierUaImportant, which);
        if (_author.PseudoElements is { } author) Collect(author, host, TierAuthor, TierAuthorImportant, which);
        if (_count == 0) return;
        for (int i = 0; i < _registeredEach.Count; i++) Push(TierUa, 0, 0, i, _registeredEach[i]);
        if (_count > 1) Array.Sort(_keys, _decls, 0, _count);
        var s = Compute(host.Name, host.Style);
        if (s.Content == null || s.Display == Display.None) return;
        if (s.CounterReset != null || s.CounterSet != null || s.CounterIncrement != null) { _countersUsed = true; ApplyCounters(s, ref scope); }
        var text = GeneratedContent.Evaluate(s.Content, host, this);
        if (text == null) return;
        var box = new Element(which == PseudoElement.Before ? "::before" : "::after") { Generated = true, Style = s, Parent = host };
        if (text.Length > 0) box.AppendChild(new TextNode(text));
        if (which == PseudoElement.Before) host.Children.Insert(0, box);
        else host.Children.Add(box);
    }

    private const string FirstLetterName = "::first-letter";

    /// <summary>
    /// Wraps the first letter of a block's first line in a ::first-letter box when rules style it, together with the
    /// punctuation around it (CSS Pseudo-Elements 4 §2.2). The letter may sit in an inline descendant, a ::before box
    /// or the first block inside this one; an image or other atomic box before any text means there is no first letter.
    /// </summary>
    private void GenerateFirstLetter(Element host)
    {
        _count = 0;
        if (UaIndex.Value.PseudoElements is { } ua) Collect(ua, host, TierUa, TierUaImportant, PseudoElement.FirstLetter);
        if (_author.PseudoElements is { } author) Collect(author, host, TierAuthor, TierAuthorImportant, PseudoElement.FirstLetter);
        if (_count == 0) return;
        if (FindFirstText(host) is not { } found)
        {
            Array.Clear(_decls, 0, _count);
            return;
        }
        var (owner, index, start, length) = found;
        var node = (TextNode)owner.Children[index];
        if (_count > 1) Array.Sort(_keys, _decls, 0, _count);
        // The first letter is inside the first line, so it inherits the ::first-line style.
        var parentStyle = owner.Style;
        for (var a = owner; a != null; a = a == host ? null : a.Parent)
            if (a.FirstLine is { } fl) { parentStyle = fl.Map(owner.Style); break; }
        var s = Compute(FirstLetterName, parentStyle);
        // A first letter that floats or is absolutely positioned becomes a block box; otherwise it stays inline.
        if (s.Float != FloatSide.None || s.Position is Position.Absolute or Position.Fixed) s.Display = Display.Block;
        else if (s.Display != Display.Inline) s.Display = Display.Inline;
        var letter = new Element(FirstLetterName) { Generated = true, Style = s, Parent = owner };
        letter.AppendChild(new TextNode(node.Text.Substring(start, length)));
        var rest = node.Text.Substring(start + length);
        node.Text = node.Text.Substring(0, start);
        owner.Children.Insert(index + 1, letter);
        if (rest.Length > 0) owner.Children.Insert(index + 2, new TextNode(rest) { Parent = owner });
    }

    /// <summary>
    /// The text node holding the first letter of <paramref name="e"/>'s first line, and where in it the letter and the
    /// punctuation around it start and how long they are; null when the first line starts with something else.
    /// </summary>
    private static (Element Owner, int Index, int Start, int Length)? FindFirstText(Element e)
    {
        for (int i = 0; i < e.Children.Count; i++)
        {
            switch (e.Children[i])
            {
                case TextNode t:
                {
                    var text = t.Text;
                    int k = 0;
                    while (k < text.Length && char.IsWhiteSpace(text[k])) k++;
                    if (k == text.Length) continue;
                    int start = k;
                    while (k < text.Length && IsFirstLetterPunctuation(text, k)) k++;
                    if (k == text.Length || char.IsWhiteSpace(text[k])) return null; // only punctuation: no letter
                    k += char.IsSurrogatePair(text, k) ? 2 : 1;
                    while (k < text.Length && CharUnicodeInfo.GetUnicodeCategory(text[k]) is UnicodeCategory.NonSpacingMark
                               or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark) k++;
                    while (k < text.Length && IsFirstLetterPunctuation(text, k)) k++;
                    return (e, i, start, k - start);
                }
                case Element c:
                {
                    var s = c.Style;
                    if (s == null || s.Display == Display.None) continue;
                    // Out-of-flow boxes are not on the first line.
                    if (s.Float != FloatSide.None || s.Position is Position.Absolute or Position.Fixed) continue;
                    if (c.Name is "br" or "img" or "svg" or "input" or "select" or "textarea" or "button" or "video" or "audio" or "iframe"
                        or "canvas" or "object" or "embed") return null;
                    if (c.Name == FirstLetterName) return null; // a nested block already took it
                    if (s.Display is Display.Inline or Display.Block or Display.ListItem) return FindFirstText(c);
                    return null; // an inline-block, table, flex or grid box starts the line
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Works out a block's ::first-line style, when rules give it one, and hands it to the element whose inline content
    /// makes the first line: the block itself, or the first block nested at its start. Layout applies it to the text
    /// of that line.
    /// </summary>
    private void ResolveFirstLine(Element host)
    {
        _count = 0;
        if (UaIndex.Value.PseudoElements is { } ua) Collect(ua, host, TierUa, TierUaImportant, PseudoElement.FirstLine);
        if (_author.PseudoElements is { } author) Collect(author, host, TierAuthor, TierAuthorImportant, PseudoElement.FirstLine);
        if (_count == 0) return;
        if (_count > 1) Array.Sort(_keys, _decls, 0, _count);
        var line = Compute("::first-line", host.Style);
        var target = host;
        while (FirstBlockChild(target) is { } inner) target = inner;
        // A ::first-line of a nested block was resolved first and stays: it is the more specific of the two.
        target.FirstLine ??= new FirstLineStyle(host.Style, line);
    }

    /// <summary>The block that starts <paramref name="e"/>'s content, when no text or inline box comes before it.</summary>
    private static Element? FirstBlockChild(Element e)
    {
        foreach (var c in e.Children)
        {
            if (c is TextNode t)
            {
                if (string.IsNullOrWhiteSpace(t.Text)) continue;
                return null;
            }
            if (c is not Element child || child.Style is not { } s || s.Display == Display.None) continue;
            if (s.Float != FloatSide.None || s.Position is Position.Absolute or Position.Fixed) continue;
            return s.Display is Display.Block or Display.ListItem ? child : null;
        }
        return null;
    }

    // Punctuation that goes with the first letter: the Ps, Pe, Pi, Pf and Po classes, as CSS Pseudo-Elements says.
    private static bool IsFirstLetterPunctuation(string text, int k)
        => CharUnicodeInfo.GetUnicodeCategory(text[k]) is UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;

    /// <summary>The style and content of a list item's ::marker, when rules give it one.</summary>
    private void ResolveMarker(Element e)
    {
        _count = 0;
        if (UaIndex.Value.PseudoElements is { } ua) Collect(ua, e, TierUa, TierUaImportant, PseudoElement.Marker);
        if (_author.PseudoElements is { } author) Collect(author, e, TierAuthor, TierAuthorImportant, PseudoElement.Marker);
        if (_count == 0) { e.MarkerStyle = null; e.MarkerContent = null; return; }
        if (_count > 1) Array.Sort(_keys, _decls, 0, _count);
        var s = Compute("::marker", e.Style);
        e.MarkerStyle = s;
        e.MarkerContent = s.Content != null ? GeneratedContent.Evaluate(s.Content, e, this) : null;
    }

    // ---------------------------------------------------------------- counters

    private readonly Dictionary<string, List<int>> _counters = new(StringComparer.Ordinal);
    private bool _countersUsed;
    private readonly bool _firstLetterRules, _firstLineRules;
    internal int QuoteDepth;

    /// <summary>Applies counter-reset, counter-increment and counter-set, in that order (CSS Lists 3).</summary>
    private void ApplyCounters(ComputedStyle s, ref List<string>? scope)
    {
        if (s.CounterReset != null)
            foreach (var c in s.CounterReset)
            {
                if (scope != null && scope.Contains(c.Name)) _counters[c.Name][^1] = c.Value; // a sibling's counter: reset it
                else
                {
                    if (!_counters.TryGetValue(c.Name, out var stack)) _counters[c.Name] = stack = new List<int>();
                    if (stack.Count >= 64) continue; // runaway nesting
                    stack.Add(c.Value);
                    (scope ??= new List<string>()).Add(c.Name);
                }
            }
        if (s.CounterIncrement != null)
            foreach (var c in s.CounterIncrement)
            {
                var stack = Instance(c.Name, ref scope);
                stack[^1] = unchecked(stack[^1] + c.Value);
            }
        if (s.CounterSet != null)
            foreach (var c in s.CounterSet)
                Instance(c.Name, ref scope)[^1] = c.Value;
    }

    /// <summary>The counter's instances, creating one (at 0, scoped to the current parent) when none is in scope.</summary>
    private List<int> Instance(string name, ref List<string>? scope)
    {
        if (!_counters.TryGetValue(name, out var stack)) _counters[name] = stack = new List<int>();
        if (stack.Count == 0)
        {
            stack.Add(0);
            (scope ??= new List<string>()).Add(name);
        }
        return stack;
    }

    /// <summary>Gives a counter a single instance with this value (the page and pages counters of margin boxes).</summary>
    internal void SetCounter(string name, int value) => _counters[name] = new List<int> { value };

    /// <summary>The values of a counter's instances, outermost first; empty when it has none.</summary>
    internal IReadOnlyList<int> CounterValues(string name) => _counters.TryGetValue(name, out var stack) ? stack : Array.Empty<int>();

    /// <summary>Stops resolving when cancelled.</summary>
    public CancellationToken Cancellation { get; init; }

    private void Resolve(Element e, ComputedStyle parent)
    {
        if ((++_resolved & 255) == 0) Cancellation.ThrowIfCancellationRequested();
        _count = 0;
        if (_registeredEach.Count > 0 || _registeredRoot.Count > 0) PushRegistered(e);
        Collect(UaIndex.Value, e, TierUa, TierUaImportant);
        AddHints(e);
        Collect(_author, e, TierAuthor, TierAuthorImportant);
        var inline = e.GetAttribute("style");
        if (!string.IsNullOrEmpty(inline))
        {
            if (!_inlineCache.TryGetValue(inline!, out var parsed))
            {
                parsed = new List<Declaration>();
                CssParser.ParseDeclarations(inline!, parsed);
                _inlineCache[inline!] = parsed;
            }
            for (int i = 0; i < parsed.Count; i++)
                Push(parsed[i].Important ? TierInlineImportant : TierInline, 0, 0, i, parsed[i]);
        }

        if (_count > 1) Array.Sort(_keys, _decls, 0, _count);
        e.Style = Compute(e.Name, parent);
        if (e.Style.CounterReset != null || e.Style.CounterSet != null || e.Style.CounterIncrement != null) _countersUsed = true;
    }

    private void PushRegistered(Element e)
    {
        for (int i = 0; i < _registeredEach.Count; i++) Push(TierUa, 0, 0, i, _registeredEach[i]);
        if (e.Name == "html" || e.Parent is { Name: "#document" })
            for (int i = 0; i < _registeredRoot.Count; i++) Push(TierUa, 0, 0, _registeredEach.Count + i, _registeredRoot[i]);
    }

    /// <summary>Computes a style from the sorted declarations in the working buffer, sharing it when it was computed before.</summary>
    private ComputedStyle Compute(string name, ComputedStyle parent)
    {
        _probe.Set(parent, _decls, _count);
        if (_styleCache.TryGetValue(_probe, out var shared))
        {
            if (name == "html") _rootFontSize = shared.FontSize;
            Array.Clear(_decls, 0, _count);
            return shared;
        }

        var s = parent.CreateChild();

        // Custom properties first: any other declaration may reference them.
        _custom.Clear();
        for (int i = 0; i < _count; i++)
            if (_decls[i].Property.StartsWith("--", StringComparison.Ordinal)) _custom.Add(new(_decls[i].Property, _decls[i].Value));
        if (_custom.Count > 0) s.CustomProperties = CssVariables.Compute(parent.CustomProperties, _custom);

        // direction before the logical properties (padding-inline-start, ...) that depend on it.
        for (int i = _count - 1; i >= 0; i--)
        {
            if (_decls[i].Property == "direction")
            {
                if (Substituted(_decls[i].Value, s) is { } dir) Apply(s, parent, "direction", dir);
                break;
            }
        }

        // font-size next: em-based lengths in every other property depend on it.
        for (int i = _count - 1; i >= 0; i--)
        {
            if (_decls[i].Property == "font-size")
            {
                var fs = Substituted(_decls[i].Value, s);
                if (fs != null) ApplyFontSize(s, parent, fs);
                break;
            }
        }
        if (name == "html") _rootFontSize = s.FontSize;

        for (int i = 0; i < _count; i++)
        {
            var d = _decls[i];
            if (!UsedViewportHeight && HasViewportHeightUnit(d.Value)) UsedViewportHeight = true;
            if (d.Property == "font-size" || d.Property.StartsWith("--", StringComparison.Ordinal)) continue;
            if (!CssParser.HasVar(d.Value))
            {
                Apply(s, parent, d.Property, d.Value);
                continue;
            }
            // var(): substitute, then expand shorthands now that the value is known.
            var value = CssVariables.Substitute(d.Value, s.CustomProperties);
            if (value == null) continue; // invalid at computed-value time
            _expanded.Clear();
            CssParser.Expand(d.Property, value, d.Important, _expanded);
            foreach (var x in _expanded)
            {
                if (x.Property == "font-size") ApplyFontSize(s, parent, x.Value);
                else Apply(s, parent, x.Property, x.Value);
            }
        }

        // A box that sets its text decoration fixes the decoration's colour to its own colour (currentColor here), so
        // descendants with another colour keep it.
        if (_decorationLineSet)
        {
            _decorationLineSet = false;
            if (s.DecorationColor == null && s.TextDecoration != TextDecoration.None) s.DecorationColor = s.Color;
        }

        // translate, rotate and scale apply before the transform property, in that order.
        if (s.Translate != null || s.Rotate != null || s.Scale != null)
        {
            var all = new List<TransformFn>();
            if (s.Translate != null) all.AddRange(s.Translate);
            if (s.Rotate != null) all.AddRange(s.Rotate);
            if (s.Scale != null) all.AddRange(s.Scale);
            if (s.Transform != null) all.AddRange(s.Transform);
            s.Transform = all.ToArray();
        }

        // Floats and absolutely positioned boxes are block-level (CSS 2.1 §9.7).
        if (s.Floated || s.OutOfFlow)
        {
            if (s.Display is Display.Inline or Display.InlineBlock) s.Display = Display.Block;
            else if (s.Display == Display.InlineFlex) s.Display = Display.Flex;
            else if (s.Display == Display.InlineGrid) s.Display = Display.Grid;
            if (s.OutOfFlow) { s.Floated = false; s.Float = FloatSide.None; }
        }
        if (s.Footnote)
        {
            // A footnote is block-level unless footnote-display makes it inline; an absolutely positioned box is no footnote.
            if (s.OutOfFlow || s.Display == Display.None) s.Footnote = false;
            else if (s.FootnoteDisplay != FootnoteDisplay.Block) s.Display = Display.Inline;
            else if (s.Display is Display.Inline or Display.InlineBlock) s.Display = Display.Block;
            else if (s.Display == Display.InlineFlex) s.Display = Display.Flex;
            else if (s.Display == Display.InlineGrid) s.Display = Display.Grid;
        }
        // Flex and grid items are laid out as blocks, which carry their own opacity layer.
        bool inlineBox = s.Display == Display.Inline && !parent.IsFlex && !parent.IsGrid;
        s.InlineFade = inlineBox ? s.Opacity * (parent.Display == Display.Inline ? parent.InlineFade : 1) : 1;
        s.InvalidateFont();
        _styleCache[_probe.Freeze()] = s;
        Array.Clear(_decls, 0, _count);
        return s;
    }

    private static bool HasViewportHeightUnit(string v)
        => v.IndexOf("vh", StringComparison.OrdinalIgnoreCase) >= 0 || v.IndexOf("vmin", StringComparison.OrdinalIgnoreCase) >= 0
           || v.IndexOf("vmax", StringComparison.OrdinalIgnoreCase) >= 0 || v.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string? Substituted(string value, ComputedStyle s)
        => CssParser.HasVar(value) ? CssVariables.Substitute(value, s.CustomProperties) : value;

    private void Collect(RuleIndex index, Element e, long tier, long importantTier, PseudoElement which = PseudoElement.None)
    {
        if (e.Id != null && index.ById.TryGetValue(e.Id, out var byId)) Match(byId, e, tier, importantTier, which);
        foreach (var c in e.Classes)
            if (index.ByClass.TryGetValue(c, out var byClass)) Match(byClass, e, tier, importantTier, which);
        if (index.ByTag.TryGetValue(e.Name, out var byTag)) Match(byTag, e, tier, importantTier, which);
        if (index.Universal.Count > 0) Match(index.Universal, e, tier, importantTier, which);
    }

    private void Match(List<StyleRule> rules, Element e, long tier, long importantTier, PseudoElement which)
    {
        foreach (var r in rules)
        {
            if (r.Selector.PseudoElement != which || !r.Selector.Matches(e)) continue;
            var decls = r.Declarations;
            // Cascade layers: later layers win over earlier ones and unlayered rules over all of them; for !important
            // declarations the order is reversed.
            int layer = r.Layer is { } l ? l.Rank : UnlayeredRank;
            int importantLayer = r.Layer is { } il ? UnlayeredRank - il.Rank : 0;
            for (int i = 0; i < decls.Length; i++)
            {
                bool important = decls[i].Important;
                Push(important ? importantTier : tier, r.Selector.Specificity, r.Order, i, decls[i], important ? importantLayer : layer);
            }
        }
    }

    private const int UnlayeredRank = Stylesheet.MaxLayerRank + 1;

    private void Push(long tier, int specificity, int order, int declIndex, Declaration d, int layer = 0)
    {
        if (_count == _keys.Length)
        {
            Array.Resize(ref _keys, _count * 2);
            Array.Resize(ref _decls, _count * 2);
        }
        long key = tier << 60
                   | (long)layer << 52
                   | (long)Math.Min(specificity, 0xFFFFF) << 32
                   | (long)Math.Min(order, 0xFFFFF) << 12
                   | (long)Math.Min(declIndex, 0xFFF);
        _keys[_count] = key;
        _decls[_count++] = d;
    }

    // ---------------------------------------------------------------- presentational attributes

    private void AddHints(Element e)
    {
        if (e.Attributes.Count == 0 && e.Name != "td" && e.Name != "th" && e.Name != "bdi") return;
        _scratch.Clear();
        string? v;
        if (e.HasAttribute("dir") || e.Name == "bdi") DirHints(e);
        // The content language, inherited, for hyphens: auto. An empty lang means an unknown language.
        if ((e.GetAttribute("lang") ?? e.GetAttribute("xml:lang")) is { } lang) Hint(LangProperty, lang.Trim() is { Length: > 0 } tag ? tag : "und");
        if (IsSvgElement(e))
        {
            SvgHints(e);
            for (int i = 0; i < _scratch.Count; i++) Push(TierHint, 0, 0, i, _scratch[i]);
            return;
        }
        switch (e.Name)
        {
            case "img":
                if ((v = e.GetAttribute("width")) != null) Hint("width", PxOrPercent(v));
                if ((v = e.GetAttribute("height")) != null) Hint("height", PxOrPercent(v));
                AlignHint(e.GetAttribute("align"), block: true);
                break;
            case "table":
                if ((v = e.GetAttribute("width")) != null) Hint("width", PxOrPercent(v));
                if ((v = e.GetAttribute("height")) != null) Hint("height", PxOrPercent(v));
                if ((v = e.GetAttribute("bgcolor")) != null) Hint("background-color", v);
                if ((v = e.GetAttribute("cellspacing")) != null) { Hint("border-spacing-h", PxOrPercent(v)); Hint("border-spacing-v", PxOrPercent(v)); }
                if ((v = e.GetAttribute("border")) != null)
                {
                    var bw = v.Length == 0 ? "1px" : PxOrPercent(v);
                    if (bw != "0px")
                    {
                        foreach (var side in new[] { "top", "right", "bottom", "left" })
                        {
                            Hint("border-" + side + "-width", bw);
                            Hint("border-" + side + "-style", "outset");
                            Hint("border-" + side + "-color", "gray");
                        }
                    }
                }
                AlignHint(e.GetAttribute("align"), block: true);
                break;
            case "td":
            case "th":
            {
                if ((v = e.GetAttribute("width")) != null) Hint("width", PxOrPercent(v));
                if ((v = e.GetAttribute("height")) != null) Hint("height", PxOrPercent(v));
                if ((v = e.GetAttribute("bgcolor")) != null) Hint("background-color", v);
                if ((v = e.GetAttribute("align")) != null) Hint("text-align", v.ToLowerInvariant());
                if ((v = e.GetAttribute("valign")) != null) Hint("vertical-align", v.ToLowerInvariant());
                if (e.HasAttribute("nowrap")) Hint("white-space", "nowrap");
                var table = FindTable(e);
                if (table != null)
                {
                    if ((v = table.GetAttribute("cellpadding")) != null)
                        foreach (var side in new[] { "top", "right", "bottom", "left" }) Hint("padding-" + side, PxOrPercent(v));
                    if ((v = table.GetAttribute("border")) != null && (v.Length == 0 || PxOrPercent(v) != "0px"))
                        foreach (var side in new[] { "top", "right", "bottom", "left" })
                        {
                            Hint("border-" + side + "-width", "1px");
                            Hint("border-" + side + "-style", "inset");
                            Hint("border-" + side + "-color", "gray");
                        }
                }
                break;
            }
            case "tr":
            case "thead":
            case "tbody":
            case "tfoot":
                if ((v = e.GetAttribute("bgcolor")) != null) Hint("background-color", v);
                if ((v = e.GetAttribute("align")) != null) Hint("text-align", v.ToLowerInvariant());
                if ((v = e.GetAttribute("valign")) != null) Hint("vertical-align", v.ToLowerInvariant());
                break;
            case "input" when e.Control is { Kind: Layout.ControlKind.Text }:
                // Chrome sizes a text field from its size attribute: 0.6em a character, and a little more.
                if (PositiveInt(e.GetAttribute("size")) is int chars) Hint("width", Em(chars * 0.6f + 1.275f));
                break;
            case "textarea":
                if (PositiveInt(e.GetAttribute("cols")) is int cols) Hint("width", Em(cols * 0.6f + 1.275f));
                if (PositiveInt(e.GetAttribute("rows")) is int rows) Hint("height", Em(rows * 1.13f));
                break;
            case "select" when e.Control is { Kind: Layout.ControlKind.List }:
            {
                // A list box shows size rows (four when only multiple is given) of 17px, and room for a scroll bar.
                Hint("height", "calc(" + Em(e.Control.Rows * 1.275f) + " + 2px)"); // select is border-box
                Hint("padding-top", "0px");
                Hint("padding-bottom", "0px");
                Hint("padding-left", "0px");
                Hint("padding-right", "16px");
                break;
            }
            case "font":
                if ((v = e.GetAttribute("color")) != null) Hint("color", v);
                if ((v = e.GetAttribute("face")) != null) Hint("font-family", v);
                if ((v = e.GetAttribute("size")) != null) Hint("font-size", FontSizeAttr(v));
                break;
            case "hr":
                if ((v = e.GetAttribute("width")) != null) Hint("width", PxOrPercent(v));
                if ((v = e.GetAttribute("size")) != null) { Hint("border-top-width", PxOrPercent(v)); Hint("border-bottom-width", "0"); }
                if ((v = e.GetAttribute("color")) != null) { Hint("border-top-color", v); Hint("border-bottom-color", v); Hint("background-color", v); }
                if (e.HasAttribute("noshade")) { Hint("border-top-style", "solid"); Hint("border-bottom-style", "solid"); }
                AlignHint(e.GetAttribute("align"), block: true);
                break;
            case "body":
                if ((v = e.GetAttribute("bgcolor")) != null) Hint("background-color", v);
                if ((v = e.GetAttribute("text")) != null) Hint("color", v);
                break;
            default:
                if ((v = e.GetAttribute("align")) != null) Hint("text-align", v.ToLowerInvariant());
                if ((v = e.GetAttribute("bgcolor")) != null) Hint("background-color", v);
                break;
        }
        for (int i = 0; i < _scratch.Count; i++) Push(TierHint, 0, 0, i, _scratch[i]);
    }

    private static readonly HashSet<string> SvgPresentationAttributes = new(StringComparer.Ordinal)
    {
        "fill", "stroke", "stroke-width", "fill-opacity", "stroke-opacity", "fill-rule", "stroke-linecap", "stroke-linejoin",
        "stroke-miterlimit", "opacity", "display", "visibility", "color", "clip-path", "clip-rule", "text-anchor",
        "dominant-baseline", "alignment-baseline", "font-size", "font-family", "font-weight", "font-style", "letter-spacing",
        "mask", "mask-type", "filter", "marker", "marker-start", "marker-mid", "marker-end", "flood-color", "flood-opacity",
        "lighting-color", "color-interpolation-filters", "direction", "unicode-bidi",
    };

    /// <summary>The fragment of a url(#id) reference, as "#id"; null for none or anything else.</summary>
    private static string? SvgUrl(string v, string lower)
        => lower.StartsWith("url(", StringComparison.Ordinal) && v.IndexOf(')') is int end and > 4
            ? v.Substring(4, end - 4).Trim().Trim('"', '\'') : null;

    /// <summary>A font-stretch keyword or percentage as a percentage of the normal width; null when invalid.</summary>
    internal static float? FontStretchPercent(string v) => v switch
    {
        "ultra-condensed" => 50, "extra-condensed" => 62.5f, "condensed" => 75, "semi-condensed" => 87.5f, "normal" => 100,
        "semi-expanded" => 112.5f, "expanded" => 125, "extra-expanded" => 150, "ultra-expanded" => 200,
        _ => v.EndsWith('%') && float.TryParse(v.AsSpan(0, v.Length - 1), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float p) && p >= 0 ? p : null,
    };

    /// <summary>Whether font-feature-settings turns standard ligatures on or off ("liga" 0), or null when it leaves them.</summary>
    private static bool? LigaSetting(string value)
    {
        bool? result = null;
        foreach (var part in value.Split(','))
        {
            var p = part.Trim();
            if (p.Length < 6 || (p[0] != '"' && p[0] != '\'') || p[5] != p[0] || p.Substring(1, 4) != "liga") continue;
            var arg = p.Substring(6).Trim();
            result = arg is "" or "on" || (int.TryParse(arg, out int n) && n != 0);
        }
        return result;
    }

    /// <summary>True for &lt;svg&gt; and the elements inside it.</summary>
    internal static bool IsSvgElement(Element e)
    {
        if (e.Name == "svg") return true;
        for (var p = e.Parent; p != null; p = p.Parent)
            if (p.Name == "svg") return true;
        return false;
    }

    private void SvgHints(Element e)
    {
        foreach (var (name, value) in e.Attributes)
        {
            if (SvgPresentationAttributes.Contains(name)) Hint(name, value);
            else if (e.Name == "svg" && name is "width" or "height" && e.Parent?.Name != "svg") Hint(name, SvgLength(value));
        }
    }

    private static string SvgLength(string v)
    {
        v = v.Trim();
        // Plain numbers are pixels; values with units or percentages are CSS lengths.
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? v + "px" : v.ToLowerInvariant();
    }

    // The dir attribute (and bdi, which defaults to dir=auto): the direction, and an isolated run of text. dir=auto
    // takes the direction of the first strong character of the content, as HTML specifies.
    private void DirHints(Element e)
    {
        var dir = e.GetAttribute("dir")?.Trim().ToLowerInvariant();
        if (dir is not ("ltr" or "rtl"))
        {
            if (dir != "auto" && e.Name != "bdi") return;
            dir = FirstStrongDirection(e) ? "rtl" : "ltr";
        }
        Hint("direction", dir);
        if (e.Name != "bdo") Hint("unicode-bidi", "isolate");
    }

    // True when the first strong character of the element's text is right to left; descendants with their own dir
    // attribute, and script and style elements, are skipped.
    private static bool FirstStrongDirection(Element e)
    {
        foreach (var c in e.Children)
        {
            if (c is TextNode t)
            {
                foreach (char ch in t.Text)
                {
                    var bc = Text.UnicodeTables.Bidi(ch);
                    if (bc == Text.BidiClass.L) return false;
                    if (bc is Text.BidiClass.R or Text.BidiClass.AL) return true;
                }
            }
            else if (c is Element ce && ce.Name is not ("script" or "style" or "bdi") && !ce.HasAttribute("dir"))
            {
                if (HasStrong(ce, out bool rtl)) return rtl;
            }
        }
        return false;

        static bool HasStrong(Element e, out bool rtl)
        {
            foreach (var c in e.Children)
            {
                if (c is TextNode t)
                    foreach (char ch in t.Text)
                    {
                        var bc = Text.UnicodeTables.Bidi(ch);
                        if (bc == Text.BidiClass.L) { rtl = false; return true; }
                        if (bc is Text.BidiClass.R or Text.BidiClass.AL) { rtl = true; return true; }
                    }
                else if (c is Element ce && ce.Name is not ("script" or "style" or "bdi") && !ce.HasAttribute("dir") && HasStrong(ce, out rtl)) return true;
            }
            rtl = false;
            return false;
        }
    }

    private void AlignHint(string? align, bool block)
    {
        if (align == null) return;
        var a = align.ToLowerInvariant();
        if (a == "center" && block) { Hint("margin-left", "auto"); Hint("margin-right", "auto"); }
        else if (a is "middle" or "top" or "bottom") Hint("vertical-align", a);
    }

    // The internal property that carries the lang attribute into the computed style.
    private const string LangProperty = "-pdfpecker-lang";

    private void Hint(string property, string value)
    {
        // Interned so that elements with identical attributes produce identical declaration lists (see the style cache).
        if (!_hintCache.TryGetValue((property, value), out var d)) _hintCache[(property, value)] = d = new Declaration(property, value, false);
        _scratch.Add(d);
    }

    private static Element? FindTable(Element e)
    {
        for (var p = e.Parent; p != null; p = p.Parent)
            if (p.Name == "table") return p;
        return null;
    }

    private static int? PositiveInt(string? v)
        => int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0 ? Math.Min(n, 10000) : null;

    private static string Em(float n) => n.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "em";

    private static string PxOrPercent(string v)
    {
        v = v.Trim();
        if (v.EndsWith("%", StringComparison.Ordinal)) return v;
        int n = 0;
        while (n < v.Length && (char.IsDigit(v[n]) || v[n] == '.')) n++;
        return n == 0 ? "0px" : v.Substring(0, n) + "px";
    }

    private static string FontSizeAttr(string v)
    {
        v = v.Trim();
        int size;
        if (v.StartsWith("+", StringComparison.Ordinal) && int.TryParse(v.Substring(1), out int plus)) size = 3 + plus;
        else if (v.StartsWith("-", StringComparison.Ordinal) && int.TryParse(v.Substring(1), out int minus)) size = 3 - minus;
        else if (!int.TryParse(v, out size)) size = 3;
        return Math.Clamp(size, 1, 7) switch { 1 => "x-small", 2 => "small", 3 => "medium", 4 => "large", 5 => "x-large", 6 => "xx-large", _ => "48px" };
    }

    // ---------------------------------------------------------------- property application

    private void ApplyFontSize(ComputedStyle s, ComputedStyle parent, string value)
    {
        var v = value.Trim().ToLowerInvariant();
        float px;
        switch (v)
        {
            case "xx-small": px = 9; break;
            case "x-small": px = 10; break;
            case "small": px = 13; break;
            case "medium": px = 16; break;
            case "large": px = 18; break;
            case "x-large": px = 24; break;
            case "xx-large": px = 32; break;
            case "xxx-large": px = 48; break;
            case "smaller": s.FontSize = parent.FontSize / 1.2f; return;
            case "larger": s.FontSize = parent.FontSize * 1.2f; return;
            case "inherit": s.FontSize = parent.FontSize; return;
            case "initial": s.FontSize = 12; return;
            default:
                if (TryParseLength(v, parent.FontSize, _rootFontSize, out var len, _vw, _vh) && !len.IsAuto)
                    s.FontSize = Math.Max(0, len.Resolve(parent.FontSize));
                return;
        }
        s.FontSize = px * 0.75f;
    }

    private void Apply(ComputedStyle s, ComputedStyle parent, string prop, string raw)
    {
        string v = raw.Trim();
        // Math functions that produce plain numbers (opacity: calc(1 / 2)) become the number; lengths are parsed later.
        if (CssMath.HasMath(v)) v = CssMath.FoldNumbers(v);
        string lower = v.ToLowerInvariant();
        float em = s.FontSize;
        bool inherit = lower == "inherit";

        switch (prop)
        {
            case "display":
                s.Display = lower switch
                {
                    "block" => Display.Block,
                    "flow-root" => Display.Block,
                    "flex" or "block flex" => Display.Flex,
                    "grid" or "block grid" => Display.Grid,
                    "inline-flex" or "inline flex" => Display.InlineFlex,
                    "inline-grid" or "inline grid" => Display.InlineGrid,
                    "inline" or "contents" => Display.Inline,
                    "inline-block" => Display.InlineBlock,
                    // The legacy box of line clamping (display: -webkit-box; -webkit-box-orient: vertical) lays out as a block.
                    "-webkit-box" => Display.Block,
                    "-webkit-inline-box" => Display.InlineBlock,
                    "list-item" => Display.ListItem,
                    "table" or "inline-table" => Display.Table,
                    "table-row" => Display.TableRow,
                    "table-cell" => Display.TableCell,
                    "table-row-group" => Display.TableRowGroup,
                    "table-header-group" => Display.TableHeaderGroup,
                    "table-footer-group" => Display.TableFooterGroup,
                    "table-caption" => Display.TableCaption,
                    "table-column" => Display.TableColumn,
                    "table-column-group" => Display.TableColumnGroup,
                    "none" => Display.None,
                    // display is not inherited, so unset is its initial value, inline.
                    "initial" or "unset" => Display.Inline,
                    "inherit" => parent.Display,
                    _ => s.Display,
                };
                if (lower == "flow-root") s.FormattingRoot = true;
                break;
            case "color":
                if (inherit) s.Color = parent.Color;
                else if (CssColor.TryParse(v, parent.Color, out var col)) s.Color = col;
                break;
            case "background-color":
                if (inherit) s.BackgroundColor = parent.BackgroundColor;
                else if (CssColor.TryParse(v, s.Color, out var bg)) s.BackgroundColor = bg;
                break;
            case "background-image":
                s.BackgroundImages = inherit ? parent.BackgroundImages : ParseBackgroundImage(v, s);
                break;
            case "background-repeat":
                s.BackgroundRepeats = inherit ? parent.BackgroundRepeats : BackgroundList(v, CssBackground.Repeat) ?? s.BackgroundRepeats;
                break;
            case "background-size":
                s.BackgroundSizes = inherit ? parent.BackgroundSizes
                    : BackgroundList(v, l => CssBackground.Size(l, em, _rootFontSize, _vw, _vh)) ?? s.BackgroundSizes;
                break;
            case "background-position":
                if (inherit) { s.BackgroundPositionsX = parent.BackgroundPositionsX; s.BackgroundPositionsY = parent.BackgroundPositionsY; break; }
                if (BackgroundList(v, l => CssBackground.Position(l, em, _rootFontSize, _vw, _vh)) is { } positions)
                {
                    s.BackgroundPositionsX = positions.Select(p => p.X).ToArray();
                    s.BackgroundPositionsY = positions.Select(p => p.Y).ToArray();
                }
                break;
            case "background-position-x":
                s.BackgroundPositionsX = inherit ? parent.BackgroundPositionsX
                    : BackgroundList(v, l => CssBackground.Axis(l, true, em, _rootFontSize, _vw, _vh)) ?? s.BackgroundPositionsX;
                break;
            case "background-position-y":
                s.BackgroundPositionsY = inherit ? parent.BackgroundPositionsY
                    : BackgroundList(v, l => CssBackground.Axis(l, false, em, _rootFontSize, _vw, _vh)) ?? s.BackgroundPositionsY;
                break;
            case "background-origin":
                s.BackgroundOrigins = inherit ? parent.BackgroundOrigins : BackgroundList(v, l => CssBackground.Box(l.Trim().ToLowerInvariant())) ?? s.BackgroundOrigins;
                break;
            case "background-clip":
                s.BackgroundClips = inherit ? parent.BackgroundClips : BackgroundList(v, l => CssBackground.Box(l.Trim().ToLowerInvariant())) ?? s.BackgroundClips;
                break;
            case "font-family":
                if (inherit) { s.FontFamily = parent.FontFamily; s.FontFamilyName = parent.FontFamilyName; break; }
                s.FontFamily = MapFontFamily(v, s.FontFamily);
                s.FontFamilyName = v;
                break;
            case "font-weight":
                s.FontWeight = lower switch
                {
                    "bold" => 700,
                    "normal" => 400,
                    "bolder" => parent.FontWeight < 350 ? 400 : parent.FontWeight < 550 ? 700 : Math.Max(900, parent.FontWeight),
                    "lighter" => parent.FontWeight < 100 ? parent.FontWeight : parent.FontWeight < 550 ? 100 : parent.FontWeight < 750 ? 400 : 700,
                    "inherit" => parent.FontWeight,
                    _ => int.TryParse(lower, out int w) && w >= 1 && w <= 1000 ? w : s.FontWeight,
                };
                s.Bold = s.FontWeight >= 600;
                break;
            case "font-stretch":
                if (inherit) s.FontStretch = parent.FontStretch;
                else if (FontStretchPercent(lower) is float stretch) s.FontStretch = stretch;
                break;
            case "font-variation-settings":
                s.FontVariationSettings = inherit ? parent.FontVariationSettings : lower is "normal" or "initial" or "unset" ? null : v;
                break;
            case "font-optical-sizing":
                s.FontOpticalSizingNone = inherit ? parent.FontOpticalSizingNone : lower == "none";
                break;
            case "font-style":
                s.Italic = lower switch { "italic" or "oblique" => true, "normal" => false, "inherit" => parent.Italic, _ => s.Italic };
                break;
            case "line-height":
                if (inherit) { s.LineHeightKind = parent.LineHeightKind; s.LineHeightValue = parent.LineHeightValue; break; }
                if (lower == "normal") { s.LineHeightKind = LineHeightKind.Normal; break; }
                if (float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float factor))
                {
                    s.LineHeightKind = LineHeightKind.Factor;
                    s.LineHeightValue = factor;
                }
                else if (TryParseLength(lower, em, _rootFontSize, out var lh, _vw, _vh) && !lh.IsAuto)
                {
                    s.LineHeightKind = LineHeightKind.Absolute;
                    s.LineHeightValue = lh.Resolve(em);
                }
                break;
            case "text-align":
                s.TextAlign = lower switch
                {
                    "start" => TextAlign.Start,
                    "end" => TextAlign.End,
                    "left" or "-webkit-left" => TextAlign.Left,
                    "right" or "-webkit-right" => TextAlign.Right,
                    "center" or "middle" or "-webkit-center" => TextAlign.Center,
                    "justify" or "justify-all" => TextAlign.Justify,
                    "inherit" => parent.TextAlign,
                    _ => s.TextAlign,
                };
                if (lower == "justify-all") s.TextAlignLast = TextAlign.Justify;
                break;
            case "text-align-last":
                s.TextAlignLast = lower switch
                {
                    "auto" or "initial" or "unset" => null,
                    "start" => TextAlign.Start,
                    "end" => TextAlign.End,
                    "left" => TextAlign.Left,
                    "right" => TextAlign.Right,
                    "center" => TextAlign.Center,
                    "justify" => TextAlign.Justify,
                    "inherit" => parent.TextAlignLast,
                    _ => s.TextAlignLast,
                };
                break;
            case "tab-size":
            case "-moz-tab-size":
                if (inherit) s.TabSize = parent.TabSize;
                else if (int.TryParse(lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tabs) && tabs >= 0) s.TabSize = Math.Min(tabs, 64);
                else if (TryParseLength(v, em, _rootFontSize, out var tabLen, _vw, _vh) && !tabLen.IsAuto && !tabLen.IsPercent)
                {
                    // A length is turned into whole spaces of the current font.
                    float space = Math.Max(0.01f, s.Font.CharWidth(' ', s.FontSize) + s.LetterSpacing + s.WordSpacing);
                    s.TabSize = Math.Clamp((int)MathF.Round(tabLen.Value / space), 0, 64);
                }
                break;
            case "hyphens":
            case "-webkit-hyphens":
                s.NoHyphens = lower == "inherit" ? parent.NoHyphens : lower == "none";
                s.HyphensAuto = lower == "inherit" ? parent.HyphensAuto : lower == "auto";
                break;
            case "hyphenate-character":
            case "-webkit-hyphenate-character":
                if (inherit) s.HyphenateCharacter = parent.HyphenateCharacter;
                else if (lower == "auto" || lower == "initial" || lower == "unset") s.HyphenateCharacter = null;
                else if (GeneratedContent.UnquoteString(v) is { } hyphen) s.HyphenateCharacter = hyphen;
                break;
            case LangProperty:
                s.Lang = v;
                break;
            case "font-variant-caps":
                s.SmallCaps = lower switch
                {
                    "small-caps" or "petite-caps" => 1,
                    "all-small-caps" or "all-petite-caps" => 2,
                    "inherit" => parent.SmallCaps,
                    _ => 0,
                };
                break;
            case "string-set":
                s.StringSet = lower is "none" ? null : inherit ? parent.StringSet : v;
                break;
            case "page":
                if (inherit || lower == "auto") s.PageName = parent.PageName;
                else if (CssParser.Tokens(v) is { Count: 1 } name && CssParser.IsPageName(name[0]))
                {
                    s.PageName = name[0];
                    UsedPageNames = true;
                }
                break;
            case "-webkit-line-clamp":
            case "line-clamp":
                s.LineClamp = int.TryParse(lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out int clamp) && clamp > 0 ? Math.Min(clamp, 10000) : 0;
                break;
            case "text-decoration-line":
            {
                if (inherit) { s.TextDecoration = parent.TextDecoration; break; }
                var td = TextDecoration.None;
                foreach (var t in CssParser.Tokens(lower))
                {
                    if (t == "underline") td |= TextDecoration.Underline;
                    else if (t == "line-through") td |= TextDecoration.LineThrough;
                    else if (t == "overline") td |= TextDecoration.Overline;
                }
                s.TextDecoration = td;
                _decorationLineSet = true;
                break;
            }
            case "text-decoration-color":
                if (inherit) s.DecorationColor = parent.DecorationColor;
                else if (lower == "currentcolor") { s.DecorationColor = null; _decorationLineSet = true; }
                else if (CssColor.TryParse(v, s.Color, out var dc)) s.DecorationColor = dc;
                break;
            case "text-decoration-style":
                s.DecorationStyle = lower switch
                {
                    "solid" => DecorationStyle.Solid,
                    "double" => DecorationStyle.Double,
                    "dotted" => DecorationStyle.Dotted,
                    "dashed" => DecorationStyle.Dashed,
                    "wavy" => DecorationStyle.Wavy,
                    "inherit" => parent.DecorationStyle,
                    _ => s.DecorationStyle,
                };
                break;
            case "text-decoration-thickness":
                if (inherit) s.DecorationThickness = parent.DecorationThickness;
                else if (lower is "auto" or "from-font" or "initial" or "unset") s.DecorationThickness = null;
                else if (TryParseLength(v, em, _rootFontSize, out var dt, _vw, _vh) && !dt.IsAuto) s.DecorationThickness = dt;
                break;
            case "text-underline-offset":
                if (inherit) s.UnderlineOffset = parent.UnderlineOffset;
                else if (lower is "auto" or "initial" or "unset") s.UnderlineOffset = null;
                else if (TryParseLength(v, em, _rootFontSize, out var uo, _vw, _vh) && !uo.IsAuto) s.UnderlineOffset = uo;
                break;
            case "text-transform":
                s.TextTransform = lower switch
                {
                    "uppercase" => TextTransform.Uppercase,
                    "lowercase" => TextTransform.Lowercase,
                    "capitalize" => TextTransform.Capitalize,
                    "none" => TextTransform.None,
                    "inherit" => parent.TextTransform,
                    _ => s.TextTransform,
                };
                break;
            case "text-indent":
                if (TryParseLength(lower, em, _rootFontSize, out var ti, _vw, _vh)) s.TextIndent = ti.IsPercent ? 0 : ti.Resolve(0);
                break;
            case "letter-spacing":
                if (lower == "normal") s.LetterSpacing = 0;
                else if (TryParseLength(lower, em, _rootFontSize, out var ls, _vw, _vh)) s.LetterSpacing = ls.Resolve(0);
                break;
            case "font-variant-ligatures":
                if (lower == "inherit") s.NoLigatures = parent.NoLigatures;
                else if (lower is "normal" or "initial" or "unset") s.NoLigatures = false;
                else if (lower == "none") s.NoLigatures = true;
                else if (lower.Contains("no-common-ligatures")) s.NoLigatures = true;
                else if (lower.Contains("common-ligatures")) s.NoLigatures = false;
                break;
            case "font-feature-settings":
                if (lower == "inherit")
                {
                    s.NoLigatures = parent.NoLigatures;
                    s.FontFeatureSettings = parent.FontFeatureSettings;
                }
                else
                {
                    if (LigaSetting(lower) is bool liga) s.NoLigatures = !liga;
                    // Tags are case-sensitive, so the value is kept as written.
                    s.FontFeatureSettings = lower is "normal" or "initial" or "unset" ? null : v;
                }
                break;
            case "font-variant-numeric":
                s.FontVariantNumeric = lower switch
                {
                    "inherit" => parent.FontVariantNumeric,
                    "normal" or "initial" or "unset" => null,
                    _ => lower,
                };
                break;
            case "word-spacing":
                if (lower == "normal") s.WordSpacing = 0;
                else if (TryParseLength(lower, em, _rootFontSize, out var ws, _vw, _vh)) s.WordSpacing = ws.Resolve(0);
                break;
            case "white-space":
                s.WhiteSpace = lower switch
                {
                    "normal" => WhiteSpace.Normal,
                    "nowrap" => WhiteSpace.NoWrap,
                    "pre" => WhiteSpace.Pre,
                    "pre-wrap" or "break-spaces" => WhiteSpace.PreWrap,
                    "pre-line" => WhiteSpace.PreLine,
                    "inherit" => parent.WhiteSpace,
                    _ => s.WhiteSpace,
                };
                break;
            case "overflow-wrap":
                s.OverflowWrap = lower switch
                {
                    "normal" => OverflowWrap.Normal,
                    "break-word" => OverflowWrap.BreakWord,
                    "anywhere" => OverflowWrap.Anywhere,
                    "inherit" => parent.OverflowWrap,
                    _ => s.OverflowWrap,
                };
                break;
            case "word-break":
                switch (lower)
                {
                    case "normal": s.WordBreak = WordBreak.Normal; break;
                    case "break-all": s.WordBreak = WordBreak.BreakAll; break;
                    case "keep-all": s.WordBreak = WordBreak.KeepAll; break;
                    // Deprecated: the same as word-break: normal with overflow-wrap: anywhere.
                    case "break-word": s.WordBreak = WordBreak.Normal; s.OverflowWrap = OverflowWrap.Anywhere; break;
                    case "inherit": s.WordBreak = parent.WordBreak; break;
                }
                break;
            case "vertical-align":
                s.VerticalAlign = lower switch
                {
                    "baseline" => VerticalAlign.Baseline,
                    "sub" => VerticalAlign.Sub,
                    "super" => VerticalAlign.Super,
                    "top" => VerticalAlign.Top,
                    "middle" => VerticalAlign.Middle,
                    "bottom" => VerticalAlign.Bottom,
                    "text-top" => VerticalAlign.TextTop,
                    "text-bottom" => VerticalAlign.TextBottom,
                    "inherit" => parent.VerticalAlign,
                    _ => s.VerticalAlign,
                };
                break;
            case "list-style-type":
                if (GeneratedContent.UnquoteString(v) is { } markerText)
                {
                    // A string is the marker text itself.
                    s.ListStyleString = markerText;
                    s.ListStyleType = ListStyleType.Disc;
                    s.ListCounterStyle = null;
                    break;
                }
                if (lower is "inherit" or "unset")
                {
                    s.ListStyleString = parent.ListStyleString;
                    s.ListStyleType = parent.ListStyleType;
                    s.ListCounterStyle = parent.ListCounterStyle;
                    break;
                }
                if (lower is "initial") lower = "disc";
                var listBullet = lower switch
                {
                    "disc" => ListStyleType.Disc,
                    "circle" => ListStyleType.Circle,
                    "square" => ListStyleType.Square,
                    "disclosure-open" => ListStyleType.DisclosureOpen,
                    "disclosure-closed" => ListStyleType.DisclosureClosed,
                    "none" => ListStyleType.None,
                    _ => (ListStyleType?)null,
                };
                if (listBullet is { } listBulletType)
                {
                    // The bullets are drawn as shapes, as Chrome draws them, and cannot be redefined by @counter-style.
                    s.ListStyleType = listBulletType;
                    s.ListStyleString = null;
                    s.ListCounterStyle = null;
                }
                else if (CounterStyles.TryGet(v, out var listCounter))
                {
                    s.ListStyleString = null;
                    s.ListCounterStyle = listCounter;
                    s.ListStyleType = listCounter.Name switch
                    {
                        "decimal" => ListStyleType.Decimal,
                        "decimal-leading-zero" => ListStyleType.DecimalLeadingZero,
                        "lower-alpha" or "lower-latin" => ListStyleType.LowerAlpha,
                        "upper-alpha" or "upper-latin" => ListStyleType.UpperAlpha,
                        "lower-roman" => ListStyleType.LowerRoman,
                        "upper-roman" => ListStyleType.UpperRoman,
                        _ => ListStyleType.Other,
                    };
                }
                break;
            case "list-style-image":
                if (lower is "inherit" or "unset") s.ListStyleImage = parent.ListStyleImage;
                else if (lower is "none" or "initial") s.ListStyleImage = null;
                else if (CssBackground.Url(v) is { } listImage) s.ListStyleImage = listImage;
                // Gradients and image-set() are not drawn as markers: like an image that fails to load, the type shows.
                else if (lower.Contains("gradient(", StringComparison.Ordinal) || lower.StartsWith("image-set(", StringComparison.Ordinal)) s.ListStyleImage = null;
                break;
            case "list-style-position":
                s.ListStylePositionInside = lower == "inside";
                break;
            case "visibility":
                s.Visible = lower is not ("hidden" or "collapse");
                break;
            case "box-shadow":
                if (inherit) s.BoxShadows = parent.BoxShadows;
                else if (BoxShadow.Parse(v, em, _rootFontSize, _vw, _vh) is { } shadows) s.BoxShadows = shadows.Length > 0 ? shadows : null;
                break;
            case "table-layout":
                s.TableLayoutFixed = lower == "fixed";
                break;
            case "border-collapse":
                s.BorderCollapse = lower == "collapse";
                break;
            case "border-spacing-h":
                if (TryParseLength(lower, em, _rootFontSize, out var bsh, _vw, _vh)) s.BorderSpacingH = ToLayoutUnits(bsh.Resolve(0));
                break;
            case "border-spacing-v":
                if (TryParseLength(lower, em, _rootFontSize, out var bsv, _vw, _vh)) s.BorderSpacingV = ToLayoutUnits(bsv.Resolve(0));
                break;
            case "box-sizing":
                s.BorderBox = lower == "border-box";
                break;
            case "float":
                s.Float = lower switch
                {
                    "left" or "inline-start" => FloatSide.Left,
                    "right" or "inline-end" => FloatSide.Right,
                    "none" or "footnote" => FloatSide.None,
                    "inherit" => parent.Float,
                    _ => s.Float,
                };
                if (lower is "left" or "inline-start" or "right" or "inline-end" or "none" or "footnote" or "inherit")
                    s.Footnote = lower == "footnote" || inherit && parent.Footnote;
                s.Floated = s.Float != FloatSide.None;
                break;
            case "footnote-display":
                s.FootnoteDisplay = inherit ? parent.FootnoteDisplay : lower switch
                {
                    "block" => FootnoteDisplay.Block,
                    "inline" => FootnoteDisplay.Inline,
                    "compact" => FootnoteDisplay.Compact,
                    _ => s.FootnoteDisplay,
                };
                break;
            case "footnote-policy":
                s.FootnotePolicy = inherit ? parent.FootnotePolicy : lower switch
                {
                    "auto" => FootnotePolicy.Auto,
                    "line" => FootnotePolicy.Line,
                    "block" => FootnotePolicy.Block,
                    _ => s.FootnotePolicy,
                };
                break;
            case "clear":
                if (inherit) { s.ClearLeft = parent.ClearLeft; s.ClearRight = parent.ClearRight; break; }
                s.ClearLeft = lower is "left" or "both" or "inline-start";
                s.ClearRight = lower is "right" or "both" or "inline-end";
                break;
            case "position":
                s.Position = lower switch
                {
                    "relative" or "sticky" => Position.Relative,
                    "absolute" => Position.Absolute,
                    "fixed" => Position.Fixed,
                    "static" => Position.Static,
                    "inherit" => parent.Position,
                    _ => s.Position,
                };
                break;
            case "top": SetLength(ref s.Top, lower, em, inherit ? parent.Top : (Length?)null); break;
            case "right": SetLength(ref s.Right, lower, em, inherit ? parent.Right : (Length?)null); break;
            case "bottom": SetLength(ref s.Bottom, lower, em, inherit ? parent.Bottom : (Length?)null); break;
            case "left": SetLength(ref s.Left, lower, em, inherit ? parent.Left : (Length?)null); break;
            case "z-index":
                if (lower == "auto") s.ZIndex = null;
                else if (inherit) s.ZIndex = parent.ZIndex;
                else if (int.TryParse(lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z)) s.ZIndex = z;
                break;
            case "overflow":
            case "overflow-x":
            case "overflow-y":
            {
                // Any value but visible and clip makes the box a block formatting context root, which contains its floats.
                var parts = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var t in parts)
                    if (t is "hidden" or "auto" or "scroll" or "overlay") s.FormattingRoot = true;
                if (parts.Length == 0) break;
                Overflow Parse(string t, Overflow inherited) => t switch
                {
                    "visible" => Overflow.Visible,
                    "clip" => Overflow.Clip,
                    "hidden" or "auto" or "scroll" or "overlay" => Overflow.Hidden,
                    "inherit" => inherited,
                    _ => Overflow.Visible,
                };
                if (prop != "overflow-y") s.OverflowX = Parse(parts[0], parent.OverflowX);
                if (prop != "overflow-x") s.OverflowY = Parse(parts[prop == "overflow" && parts.Length > 1 ? 1 : 0], parent.OverflowY);
                break;
            }
            case "transform":
                if (lower == "none") s.Transform = null;
                else if (inherit) s.Transform = parent.Transform;
                else if (CssTransform.TryParse(lower, em, _rootFontSize, _vw, _vh, out var tf)) s.Transform = tf;
                break;
            case "translate":
            {
                // translate: x [y [z]]; a z offset is ignored (no 3D).
                var t = CssParser.Tokens(lower);
                if (lower is "none" or "initial" or "unset") s.Translate = null;
                else if (inherit) s.Translate = parent.Translate;
                else if (t.Count is >= 1 and <= 3 && CssTransform.TryParse("translate(" + t[0] + ", " + (t.Count > 1 ? t[1] : "0") + ")",
                             em, _rootFontSize, _vw, _vh, out var tr)) s.Translate = tr;
                break;
            }
            case "rotate":
            {
                // rotate: angle, or "z angle"; rotations about x, y or a vector are 3D and ignored.
                var t = CssParser.Tokens(lower);
                if (lower is "none" or "initial" or "unset") s.Rotate = null;
                else if (inherit) s.Rotate = parent.Rotate;
                else if (t.Count == 2 && t[0] == "z" || t.Count == 1)
                {
                    if (CssTransform.TryParse("rotate(" + t[^1] + ")", em, _rootFontSize, _vw, _vh, out var ro)) s.Rotate = ro;
                }
                else if (t.Count == 4 && t[0] == "0" && t[1] == "0" && float.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) && z > 0
                         && CssTransform.TryParse("rotate(" + t[3] + ")", em, _rootFontSize, _vw, _vh, out var rz)) s.Rotate = rz;
                break;
            }
            case "scale":
            {
                var t = CssParser.Tokens(lower);
                if (lower is "none" or "initial" or "unset") s.Scale = null;
                else if (inherit) s.Scale = parent.Scale;
                else if (t.Count is >= 1 and <= 3 && CssTransform.TryParse("scale(" + t[0] + ", " + (t.Count > 1 ? t[1] : t[0]) + ")",
                             em, _rootFontSize, _vw, _vh, out var sc)) s.Scale = sc;
                break;
            }
            case "transform-origin":
                if (inherit) { s.TransformOriginX = parent.TransformOriginX; s.TransformOriginY = parent.TransformOriginY; }
                else if (CssTransform.TryParseOrigin(lower, em, _rootFontSize, _vw, _vh, out var ox, out var oy)) { s.TransformOriginX = ox; s.TransformOriginY = oy; }
                break;
            case "content":
                s.Content = lower is "normal" or "none" ? null : inherit ? parent.Content : v;
                break;
            case "counter-reset":
                s.CounterReset = inherit ? parent.CounterReset : ParseCounters(v, 0);
                break;
            case "counter-set":
                s.CounterSet = inherit ? parent.CounterSet : ParseCounters(v, 0);
                break;
            case "counter-increment":
                s.CounterIncrement = inherit ? parent.CounterIncrement : ParseCounters(v, 1);
                break;
            case "break-before":
                s.PageBreakBefore = lower is "always" or "page" or "left" or "right" or "recto" or "verso";
                s.AvoidBreakBefore = lower is "avoid" or "avoid-page";
                break;
            case "break-after":
                s.PageBreakAfter = lower is "always" or "page" or "left" or "right" or "recto" or "verso";
                s.AvoidBreakAfter = lower is "avoid" or "avoid-page";
                break;
            case "orphans":
                if (inherit) s.Orphans = parent.Orphans;
                else if (int.TryParse(lower, out int orphans) && orphans > 0) s.Orphans = orphans;
                break;
            case "widows":
                if (inherit) s.Widows = parent.Widows;
                else if (int.TryParse(lower, out int widows) && widows > 0) s.Widows = widows;
                break;
            case "break-inside":
                s.AvoidBreakInside = lower is "avoid" or "avoid-page";
                break;

            case "margin-top": SetLength(ref s.MarginTop, lower, em, inherit ? parent.MarginTop : (Length?)null); break;
            case "margin-right": SetLength(ref s.MarginRight, lower, em, inherit ? parent.MarginRight : (Length?)null); break;
            case "margin-bottom": SetLength(ref s.MarginBottom, lower, em, inherit ? parent.MarginBottom : (Length?)null); break;
            case "margin-left": SetLength(ref s.MarginLeft, lower, em, inherit ? parent.MarginLeft : (Length?)null); break;
            case "padding-top": SetLength(ref s.PaddingTop, lower, em, inherit ? parent.PaddingTop : (Length?)null, noAuto: true); break;
            case "padding-right": SetLength(ref s.PaddingRight, lower, em, inherit ? parent.PaddingRight : (Length?)null, noAuto: true); break;
            case "padding-bottom": SetLength(ref s.PaddingBottom, lower, em, inherit ? parent.PaddingBottom : (Length?)null, noAuto: true); break;
            case "direction":
                s.Rtl = lower switch { "rtl" => true, "ltr" => false, "inherit" => parent.Rtl, "initial" => false, _ => s.Rtl };
                break;
            case "unicode-bidi":
                s.UnicodeBidi = lower switch
                {
                    "normal" => UnicodeBidi.Normal,
                    "embed" => UnicodeBidi.Embed,
                    "isolate" or "-webkit-isolate" => UnicodeBidi.Isolate,
                    "bidi-override" => UnicodeBidi.BidiOverride,
                    "isolate-override" or "-webkit-isolate-override" => UnicodeBidi.IsolateOverride,
                    "plaintext" or "-webkit-plaintext" => UnicodeBidi.Plaintext,
                    "inherit" => parent.UnicodeBidi,
                    _ => s.UnicodeBidi,
                };
                break;
            // Logical properties map to the physical side the direction gives.
            case "margin-inline-start": Apply(s, parent, s.Rtl ? "margin-right" : "margin-left", raw); break;
            case "margin-inline-end": Apply(s, parent, s.Rtl ? "margin-left" : "margin-right", raw); break;
            case "padding-inline-start": Apply(s, parent, s.Rtl ? "padding-right" : "padding-left", raw); break;
            case "padding-inline-end": Apply(s, parent, s.Rtl ? "padding-left" : "padding-right", raw); break;
            case "margin-block-start": Apply(s, parent, "margin-top", raw); break;
            case "margin-block-end": Apply(s, parent, "margin-bottom", raw); break;
            case "padding-block-start": Apply(s, parent, "padding-top", raw); break;
            case "padding-block-end": Apply(s, parent, "padding-bottom", raw); break;
            case "padding-left": SetLength(ref s.PaddingLeft, lower, em, inherit ? parent.PaddingLeft : (Length?)null, noAuto: true); break;
            case "width": SetLength(ref s.Width, lower, em, inherit ? parent.Width : (Length?)null); break;
            case "height": SetLength(ref s.Height, lower, em, inherit ? parent.Height : (Length?)null); break;
            case "min-width": SetLength(ref s.MinWidth, lower, em, null); break;
            case "min-height": SetLength(ref s.MinHeight, lower, em, null); break;
            case "max-width": SetLength(ref s.MaxWidth, lower == "none" ? "auto" : lower, em, null); break;
            case "max-height": SetLength(ref s.MaxHeight, lower == "none" ? "auto" : lower, em, null); break;

            case "opacity":
                if (TryParseNumberOrPercent(lower, out float op)) s.Opacity = Math.Clamp(op, 0, 1);
                break;

            case "outline-style": s.OutlineStyle = lower == "auto" ? BorderStyle.Solid : ParseBorderStyle(lower, s.OutlineStyle); break;
            case "outline-width": s.OutlineWidth = BorderWidth(lower, em, s.OutlineWidth); break;
            case "outline-color":
                // invert is drawn as the text colour, as browsers without inversion do.
                s.OutlineColor = lower == "invert" ? null : BorderColor(v, s);
                break;
            case "outline-offset":
                if (TryParseLength(lower, em, _rootFontSize, out var oo, _vw, _vh) && !oo.IsAuto && !oo.IsPercent) s.OutlineOffset = oo.Value;
                break;
            case "text-shadow":
                if (inherit) s.TextShadows = parent.TextShadows;
                else if (lower == "initial") s.TextShadows = null;
                else if (BoxShadow.Parse(v, em, _rootFontSize, _vw, _vh) is { } ts && !ts.Any(t => t.Inset || t.Spread != 0))
                    s.TextShadows = ts.Length > 0 ? ts : null;
                break;
            case "text-overflow":
            {
                // One value for both ends, or start and end: only the end of the line is cut, as in Chrome.
                var tov = CssParser.Tokens(v.Trim());
                if (inherit || tov.Count is not (1 or 2)) break;
                var end = tov[^1];
                if (GeneratedContent.UnquoteString(end) is { } str) s.TextOverflow = str;
                else if (end.Equals("ellipsis", StringComparison.OrdinalIgnoreCase)) s.TextOverflow = "\u2026";
                else if (end.Equals("clip", StringComparison.OrdinalIgnoreCase) || end.Equals("initial", StringComparison.OrdinalIgnoreCase)) s.TextOverflow = null;
                break;
            }
            case "object-fit":
                s.ObjectFit = lower switch
                {
                    "fill" => ObjectFit.Fill,
                    "contain" => ObjectFit.Contain,
                    "cover" => ObjectFit.Cover,
                    "none" => ObjectFit.None,
                    "scale-down" => ObjectFit.ScaleDown,
                    "inherit" => parent.ObjectFit,
                    _ => s.ObjectFit,
                };
                break;
            case "object-position":
                if (inherit) { s.ObjectPositionX = parent.ObjectPositionX; s.ObjectPositionY = parent.ObjectPositionY; }
                else if (CssBackground.Position(lower, em, _rootFontSize, _vw, _vh) is { } opos) { s.ObjectPositionX = opos.X; s.ObjectPositionY = opos.Y; }
                break;
            case "aspect-ratio":
                if (inherit) { s.AspectRatio = parent.AspectRatio; s.AspectRatioAuto = parent.AspectRatioAuto; }
                else if (ParseAspectRatio(lower, out float ratio, out bool ratioAuto)) { s.AspectRatio = ratio; s.AspectRatioAuto = ratioAuto; }
                break;

            // ---- flexbox, grid, multi-column
            case "flex-direction":
                s.FlexDirection = lower switch
                {
                    "row" => FlexDirection.Row,
                    "row-reverse" => FlexDirection.RowReverse,
                    "column" => FlexDirection.Column,
                    "column-reverse" => FlexDirection.ColumnReverse,
                    _ => s.FlexDirection,
                };
                break;
            case "flex-wrap":
                s.FlexWrap = lower switch { "nowrap" => FlexWrap.NoWrap, "wrap" => FlexWrap.Wrap, "wrap-reverse" => FlexWrap.WrapReverse, _ => s.FlexWrap };
                break;
            case "flex-grow":
                if (float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float fg) && fg >= 0) s.FlexGrow = fg;
                break;
            case "flex-shrink":
                if (float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float fsh) && fsh >= 0) s.FlexShrink = fsh;
                break;
            case "flex-basis":
                if (lower is "content" or "auto" or "max-content" or "min-content" or "fit-content") s.FlexBasis = Length.Auto;
                else SetLength(ref s.FlexBasis, lower, em, null);
                break;
            case "order":
                if (int.TryParse(lower, out int order)) s.Order = order;
                break;
            case "justify-content": s.JustifyContent = ParseAlign(lower, s.JustifyContent); break;
            case "align-items": s.AlignItems = ParseAlign(lower, s.AlignItems); break;
            case "align-content": s.AlignContent = ParseAlign(lower, s.AlignContent); break;
            case "justify-items": s.JustifyItems = ParseAlign(lower, s.JustifyItems); break;
            case "align-self": s.AlignSelf = ParseAlign(lower, s.AlignSelf); break;
            case "justify-self": s.JustifySelf = ParseAlign(lower, s.JustifySelf); break;
            case "row-gap":
            case "column-gap":
            {
                Length? gap = null;
                if (lower != "normal")
                {
                    if (!TryParseLength(lower, em, _rootFontSize, out var gl, _vw, _vh) || gl.IsAuto) break;
                    gap = ToLayoutUnits(gl);
                }
                if (prop == "row-gap") s.RowGap = gap;
                else s.ColumnGap = gap;
                break;
            }
            case "grid-template-columns":
                s.GridTemplateColumns = lower == "none" ? null : ParseTrackList(lower, em);
                break;
            case "grid-template-rows":
                s.GridTemplateRows = lower == "none" ? null : ParseTrackList(lower, em);
                break;
            case "grid-auto-columns":
                s.GridAutoColumns = ParseTrackList(lower, em)?.Before is { Length: > 0 } ac ? ac : null;
                break;
            case "grid-auto-rows":
                s.GridAutoRows = ParseTrackList(lower, em)?.Before is { Length: > 0 } ar ? ar : null;
                break;
            case "grid-auto-flow":
                s.GridAutoFlowColumn = lower.Contains("column");
                s.GridAutoFlowDense = lower.Contains("dense");
                break;
            case "grid-template-areas":
                s.GridTemplateAreas = ParseGridAreas(v);
                break;
            case "grid-column-start": s.GridColumnStart = ParseGridLine(lower); break;
            case "grid-column-end": s.GridColumnEnd = ParseGridLine(lower); break;
            case "grid-row-start": s.GridRowStart = ParseGridLine(lower); break;
            case "grid-row-end": s.GridRowEnd = ParseGridLine(lower); break;
            case "column-count":
                s.ColumnCount = int.TryParse(lower, out int cc) && cc > 0 ? Math.Min(cc, 100) : 0;
                break;
            case "column-width":
                SetLength(ref s.ColumnWidth, lower, em, null);
                if (s.ColumnWidth.IsPercent) s.ColumnWidth = Length.Auto;
                break;
            case "column-rule-style": s.ColumnRuleStyle = ParseBorderStyle(lower, s.ColumnRuleStyle); break;
            case "column-rule-width": s.ColumnRuleWidth = BorderWidth(lower, em, s.ColumnRuleWidth); break;
            case "column-rule-color": s.ColumnRuleColor = BorderColor(v, s); break;
            case "column-span": s.ColumnSpanAll = lower == "all"; break;

            // ---- SVG presentation properties
            case "fill":
                ParsePaint(v, s, inherit ? parent.SvgFill : null, inherit ? parent.SvgFillUrl : null, inherit, out s.SvgFill, out s.SvgFillUrl, s.SvgFill, s.SvgFillUrl);
                break;
            case "stroke":
                ParsePaint(v, s, inherit ? parent.SvgStroke : null, inherit ? parent.SvgStrokeUrl : null, inherit, out s.SvgStroke, out s.SvgStrokeUrl, s.SvgStroke, s.SvgStrokeUrl);
                break;
            case "stroke-width":
                if (float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float sw)) s.SvgStrokeWidth = Math.Max(0, sw);
                else if (TryParseLength(lower, em, _rootFontSize, out var swl, _vw, _vh) && !swl.IsAuto && !swl.IsPercent) s.SvgStrokeWidth = Math.Max(0, swl.Value / 0.75f);
                break;
            case "fill-opacity":
                if (TryParseNumberOrPercent(lower, out float fo)) s.SvgFillOpacity = Math.Clamp(fo, 0, 1);
                break;
            case "stroke-opacity":
                if (TryParseNumberOrPercent(lower, out float so)) s.SvgStrokeOpacity = Math.Clamp(so, 0, 1);
                break;
            case "fill-rule":
                s.SvgEvenOdd = lower == "evenodd";
                break;
            case "stroke-linecap":
                s.SvgLineCap = lower switch { "round" => SvgLineCap.Round, "square" => SvgLineCap.Square, "butt" => SvgLineCap.Butt, _ => s.SvgLineCap };
                break;
            case "stroke-linejoin":
                s.SvgLineJoin = lower switch { "round" => SvgLineJoin.Round, "bevel" => SvgLineJoin.Bevel, "miter" or "miter-clip" or "arcs" => SvgLineJoin.Miter, _ => s.SvgLineJoin };
                break;
            case "clip-path":
                if (inherit) { s.SvgClipPath = parent.SvgClipPath; s.ClipShape = parent.ClipShape; }
                else if (lower is "none" or "initial" or "unset") { s.SvgClipPath = null; s.ClipShape = null; }
                else if (SvgUrl(v, lower) is { } clipUrl) { s.SvgClipPath = clipUrl; s.ClipShape = null; }
                else if (ClipShape.Parse(v, em, _rootFontSize, _vw, _vh) is { } shape) { s.ClipShape = shape; s.SvgClipPath = null; }
                break;
            case "mix-blend-mode":
                if (inherit) s.BlendMode = parent.BlendMode;
                else if (lower is "initial" or "unset") s.BlendMode = null;
                else if (BlendModes.PdfName(lower, out bool blendValid) is var blend && blendValid) s.BlendMode = blend;
                break;
            case "background-blend-mode":
            {
                if (inherit) { s.BackgroundBlendModes = parent.BackgroundBlendModes; break; }
                if (lower is "initial" or "unset") { s.BackgroundBlendModes = null; break; }
                var parts = CssParser.SplitTopLevel(lower, ',');
                var modes = new string?[parts.Count];
                bool ok = parts.Count > 0, any = false;
                for (int i = 0; i < parts.Count && ok; i++)
                {
                    modes[i] = BlendModes.PdfName(parts[i].Trim(), out ok);
                    any |= modes[i] != null;
                }
                if (ok) s.BackgroundBlendModes = any ? modes : null;
                break;
            }
            case "isolation":
                s.Isolate = lower == "isolate" || inherit && parent.Isolate;
                break;
            case "mask":
                s.SvgMask = SvgUrl(v, lower);
                break;
            case "mask-type":
                s.SvgMaskAlpha = lower == "alpha";
                break;
            case "filter":
                if (inherit) { s.SvgFilter = parent.SvgFilter; s.Filters = parent.Filters; }
                else if (lower is "none" or "initial" or "unset") { s.SvgFilter = null; s.Filters = null; }
                else if (SvgUrl(v, lower) is { } filterUrl && CssParser.Tokens(lower).Count == 1) { s.SvgFilter = filterUrl; s.Filters = null; }
                else if (FilterFunction.Parse(v, em, _rootFontSize, _vw, _vh) is { } filters) { s.Filters = filters.Length > 0 ? filters : null; s.SvgFilter = null; }
                break;
            case "marker":
                s.SvgMarkerStart = s.SvgMarkerMid = s.SvgMarkerEnd = lower == "inherit" ? parent.SvgMarkerStart : SvgUrl(v, lower);
                break;
            case "marker-start":
                s.SvgMarkerStart = lower == "inherit" ? parent.SvgMarkerStart : SvgUrl(v, lower);
                break;
            case "marker-mid":
                s.SvgMarkerMid = lower == "inherit" ? parent.SvgMarkerMid : SvgUrl(v, lower);
                break;
            case "marker-end":
                s.SvgMarkerEnd = lower == "inherit" ? parent.SvgMarkerEnd : SvgUrl(v, lower);
                break;
            case "flood-color":
                if (CssColor.TryParse(v, s.Color, out uint flood)) s.SvgFloodColor = flood;
                break;
            case "lighting-color":
                if (CssColor.TryParse(v, s.Color, out uint light)) s.SvgLightingColor = light;
                break;
            case "flood-opacity":
                if (TryParseNumberOrPercent(lower, out float fop)) s.SvgFloodOpacity = Math.Clamp(fop, 0, 1);
                break;
            case "color-interpolation-filters":
                s.SvgFilterSrgb = lower switch { "srgb" => true, "linearrgb" or "auto" => false, "inherit" => parent.SvgFilterSrgb, _ => s.SvgFilterSrgb };
                break;
            case "clip-rule":
                s.SvgClipEvenOdd = lower == "evenodd";
                break;
            case "text-anchor":
                s.SvgTextAnchor = lower switch { "middle" => 1, "end" => 2, "start" => 0, "inherit" => parent.SvgTextAnchor, _ => s.SvgTextAnchor };
                break;
            case "dominant-baseline":
            case "alignment-baseline":
                s.SvgBaseline = lower switch
                {
                    "middle" or "central" => 1,
                    "hanging" or "text-before-edge" or "before-edge" => 2,
                    "text-after-edge" or "after-edge" or "ideographic" => 3,
                    "auto" or "alphabetic" or "baseline" => 0,
                    "inherit" => parent.SvgBaseline,
                    _ => s.SvgBaseline,
                };
                break;
            case "stroke-miterlimit":
                if (float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float ml) && ml >= 1) s.SvgMiterLimit = ml;
                break;

            case "border-top-width": s.BorderTopWidth = BorderWidth(lower, em, s.BorderTopWidth); break;
            case "border-right-width": s.BorderRightWidth = BorderWidth(lower, em, s.BorderRightWidth); break;
            case "border-bottom-width": s.BorderBottomWidth = BorderWidth(lower, em, s.BorderBottomWidth); break;
            case "border-left-width": s.BorderLeftWidth = BorderWidth(lower, em, s.BorderLeftWidth); break;
            case "border-top-style": s.BorderTopStyle = ParseBorderStyle(lower, s.BorderTopStyle); break;
            case "border-right-style": s.BorderRightStyle = ParseBorderStyle(lower, s.BorderRightStyle); break;
            case "border-bottom-style": s.BorderBottomStyle = ParseBorderStyle(lower, s.BorderBottomStyle); break;
            case "border-left-style": s.BorderLeftStyle = ParseBorderStyle(lower, s.BorderLeftStyle); break;
            case "border-top-left-radius": SetRadius(s, 0, lower, em, inherit ? parent : null); break;
            case "border-top-right-radius": SetRadius(s, 1, lower, em, inherit ? parent : null); break;
            case "border-bottom-right-radius": SetRadius(s, 2, lower, em, inherit ? parent : null); break;
            case "border-bottom-left-radius": SetRadius(s, 3, lower, em, inherit ? parent : null); break;
            case "border-top-color": s.BorderTopColor = BorderColor(v, s); break;
            case "border-right-color": s.BorderRightColor = BorderColor(v, s); break;
            case "border-bottom-color": s.BorderBottomColor = BorderColor(v, s); break;
            case "border-left-color": s.BorderLeftColor = BorderColor(v, s); break;
            case "border-image-source":
                if (inherit) s.BorderImageSource = parent.BorderImageSource;
                else if (lower is "none" or "initial" or "unset") s.BorderImageSource = null;
                else if (ParseBackgroundImage(v, s) is [{ } source]) s.BorderImageSource = source;
                break;
            case "border-image-slice":
                if (inherit) { s.BorderImageSlice = parent.BorderImageSlice; s.BorderImageFill = parent.BorderImageFill; }
                else if (lower is "initial" or "unset") { s.BorderImageSlice = null; s.BorderImageFill = false; }
                else if (CssBorderImage.Slice(lower) is { } slice) { s.BorderImageSlice = slice.Slice; s.BorderImageFill = slice.Fill; }
                break;
            case "border-image-width":
                if (inherit) s.BorderImageWidths = parent.BorderImageWidths;
                else if (lower is "initial" or "unset") s.BorderImageWidths = null;
                else s.BorderImageWidths = CssBorderImage.Widths(lower, false, em, _rootFontSize, _vw, _vh) ?? s.BorderImageWidths;
                break;
            case "border-image-outset":
                if (inherit) s.BorderImageOutsets = parent.BorderImageOutsets;
                else if (lower is "initial" or "unset") s.BorderImageOutsets = null;
                else s.BorderImageOutsets = CssBorderImage.Widths(lower, true, em, _rootFontSize, _vw, _vh) ?? s.BorderImageOutsets;
                break;
            case "border-image-repeat":
                if (inherit) (s.BorderImageRepeatX, s.BorderImageRepeatY) = (parent.BorderImageRepeatX, parent.BorderImageRepeatY);
                else if (lower is "initial" or "unset") s.BorderImageRepeatX = s.BorderImageRepeatY = BorderImageRepeat.Stretch;
                else if (CssBorderImage.Repeat(lower) is { } rep) (s.BorderImageRepeatX, s.BorderImageRepeatY) = rep;
                break;
        }
    }

    /// <summary>Parses aspect-ratio: auto, a ratio ("16 / 9" or "1.5"), or both. A ratio of zero is treated as auto.</summary>
    private static bool ParseAspectRatio(string lower, out float ratio, out bool auto)
    {
        ratio = 0;
        auto = false;
        string rest = lower;
        if (rest.StartsWith("auto", StringComparison.Ordinal)) { auto = true; rest = rest.Substring(4); }
        else if (rest.EndsWith("auto", StringComparison.Ordinal)) { auto = true; rest = rest.Substring(0, rest.Length - 4); }
        rest = rest.Trim();
        if (rest.Length == 0) return auto;
        int slash = rest.IndexOf('/');
        string a = slash < 0 ? rest : rest.Substring(0, slash), b = slash < 0 ? "1" : rest.Substring(slash + 1);
        if (!float.TryParse(a.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float w) || w < 0
            || !float.TryParse(b.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float h) || h < 0) return false;
        if (w > 0 && h > 0 && float.IsFinite(w / h)) ratio = w / h;
        else auto = true; // a degenerate ratio such as 0 / 1 behaves as auto
        return true;
    }

    private static bool TryParseNumberOrPercent(string lower, out float value)
    {
        if (lower.EndsWith("%", StringComparison.Ordinal))
        {
            bool ok = float.TryParse(lower.AsSpan(0, lower.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            value /= 100f;
            return ok;
        }
        return float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static BoxAlign ParseAlign(string lower, BoxAlign current)
    {
        // Drop overflow and baseline-position modifiers: "safe center", "first baseline".
        var t = lower;
        foreach (var prefix in new[] { "safe ", "unsafe ", "first ", "last ", "legacy " })
            if (t.StartsWith(prefix, StringComparison.Ordinal)) t = t.Substring(prefix.Length).Trim();
        return t switch
        {
            "normal" => BoxAlign.Normal,
            "auto" => BoxAlign.Auto,
            "start" or "flex-start" or "self-start" or "left" => BoxAlign.Start,
            "end" or "flex-end" or "self-end" or "right" => BoxAlign.End,
            "center" => BoxAlign.Center,
            "stretch" => BoxAlign.Stretch,
            "baseline" => BoxAlign.Baseline,
            "space-between" => BoxAlign.SpaceBetween,
            "space-around" => BoxAlign.SpaceAround,
            "space-evenly" => BoxAlign.SpaceEvenly,
            _ => current,
        };
    }

    private BackgroundImage?[]? ParseBackgroundImage(string v, ComputedStyle s)
    {
        var layers = CssParser.SplitTopLevel(v, ',');
        var result = new BackgroundImage?[layers.Count];
        bool any = false;
        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i].Trim();
            if (CssBackground.Url(layer) is { } url) result[i] = new BackgroundImage { Url = url };
            else if (layer.IndexOf("gradient(", StringComparison.OrdinalIgnoreCase) >= 0
                     && CssGradient.Parse(layer, s.Color, s.FontSize, _rootFontSize, _vw, _vh) is { } g) result[i] = new BackgroundImage { Gradient = g };
            any |= result[i] != null;
        }
        return any ? result : null;
    }

    /// <summary>A comma-separated background longhand, one entry per layer; null when any layer is invalid.</summary>
    private static T[]? BackgroundList<T>(string v, Func<string, T?> parse) where T : struct
    {
        var layers = CssParser.SplitTopLevel(v, ',');
        if (layers.Count == 0) return null;
        var result = new T[layers.Count];
        for (int i = 0; i < layers.Count; i++)
        {
            if (parse(layers[i]) is not { } item) return null;
            result[i] = item;
        }
        return result;
    }

    /// <summary>Parses grid-template-areas: one string per row, "." for unnamed cells. Invalid when an area is not a rectangle.</summary>
    internal static GridAreas? ParseGridAreas(string v)
    {
        var rows = new List<string[]>();
        int i = 0;
        while (i < v.Length)
        {
            char q = v[i];
            if (q != '"' && q != '\'') { i++; continue; }
            int close = v.IndexOf(q, i + 1);
            if (close < 0) return null;
            rows.Add(SplitAreaRow(v.Substring(i + 1, close - i - 1)));
            i = close + 1;
        }
        if (rows.Count == 0 || rows[0].Length == 0 || rows.Any(r => r.Length != rows[0].Length)) return null;
        var areas = new GridAreas { Rows = rows.Count, Columns = rows[0].Length };
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < rows[r].Length; c++)
            {
                var name = rows[r][c];
                if (name == ".") continue;
                if (areas.Areas.TryGetValue(name, out var a))
                    areas.Areas[name] = (a.RowStart, a.ColStart, Math.Max(a.RowEnd, r + 1), Math.Max(a.ColEnd, c + 1));
                else areas.Areas[name] = (r, c, r + 1, c + 1);
            }
        foreach (var (name, a) in areas.Areas)
            for (int r = a.RowStart; r < a.RowEnd; r++)
                for (int c = a.ColStart; c < a.ColEnd; c++)
                    if (rows[r][c] != name) return null;
        return areas;
    }

    /// <summary>Cell tokens of one grid-template-areas row; a run of dots is one null cell token.</summary>
    private static string[] SplitAreaRow(string row)
    {
        var cells = new List<string>();
        int i = 0;
        while (i < row.Length)
        {
            if (char.IsWhiteSpace(row[i])) { i++; continue; }
            int start = i;
            if (row[i] == '.') { while (i < row.Length && row[i] == '.') i++; cells.Add("."); continue; }
            while (i < row.Length && !char.IsWhiteSpace(row[i]) && row[i] != '.') i++;
            cells.Add(row.Substring(start, i - start).ToLowerInvariant());
        }
        return cells.ToArray();
    }

    private static GridLine ParseGridLine(string lower)
    {
        var t = CssParser.Tokens(lower);
        if (t.Count == 0 || t[0] == "auto") return default;
        bool span = false;
        int number = 0;
        string? name = null;
        foreach (var tok in t)
        {
            if (tok == "span") span = true;
            else if (int.TryParse(tok, out int n)) number = n;
            else if (IsIdent(tok)) name = tok;
            else return default;
        }
        if (span)
        {
            // "span 2" spans two tracks; "span name" (or "span 2 name") spans to the next (second) line of that name.
            if (number < 0) return default;
            return new GridLine(0, number > 0 ? Math.Min(number, 1000) : 1, null, name);
        }
        if (name != null) return new GridLine(number == 0 ? 1 : Math.Clamp(number, -1000, 1000), 0, name);
        return number != 0 ? new GridLine(Math.Clamp(number, -1000, 1000), 0) : default;
    }

    private static bool IsIdent(string tok)
        => tok.Length > 0 && (char.IsLetter(tok[0]) || tok[0] is '_' or '-') && tok.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Parses a grid track list: lengths, percentages, fr, auto, min-content, max-content, minmax(), fit-content(),
    /// repeat() and [line names].
    /// </summary>
    private TrackList? ParseTrackList(string lower, float em)
    {
        var list = new TrackList();
        var before = new List<Track>();
        var after = new List<Track>();
        var current = before;
        List<(string, int, bool)>? names = null;
        void Name(string tok, int line)
        {
            foreach (var n in tok.Trim('[', ']').Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                (names ??= new()).Add((n, line, current == after));
        }
        foreach (var tok in CssParser.Tokens(lower))
        {
            if (tok.StartsWith("[", StringComparison.Ordinal)) { Name(tok, current.Count); continue; }
            if (tok.StartsWith("repeat(", StringComparison.Ordinal) && tok.EndsWith(")", StringComparison.Ordinal))
            {
                var inner = tok.Substring(7, tok.Length - 8);
                int comma = inner.IndexOf(',');
                if (comma < 0) return null;
                var countText = inner.Substring(0, comma).Trim();
                var tracks = new List<Track>();
                var repeatNames = new List<(string Tok, int Index)>();
                foreach (var rt in CssParser.Tokens(inner.Substring(comma + 1).Trim()))
                {
                    if (rt.StartsWith("[", StringComparison.Ordinal)) { repeatNames.Add((rt, tracks.Count)); continue; }
                    if (!TryParseTrack(rt, em, out var tr)) return null;
                    tracks.Add(tr);
                }
                if (tracks.Count == 0) return null;
                if (countText is "auto-fill" or "auto-fit")
                {
                    // Names inside an automatic repetition are not tracked.
                    if (list.AutoRepeat != null) return null;
                    list.AutoRepeat = tracks.ToArray();
                    list.AutoFit = countText == "auto-fit";
                    current = after;
                    continue;
                }
                if (!int.TryParse(countText, out int count) || count <= 0) return null;
                for (int k = 0; k < Math.Min(count, 1000); k++)
                {
                    foreach (var (nameTok, index) in repeatNames) Name(nameTok, current.Count + index);
                    current.AddRange(tracks);
                }
                continue;
            }
            if (!TryParseTrack(tok, em, out var t)) return null;
            current.Add(t);
        }
        list.Before = before.ToArray();
        list.After = after.ToArray();
        list.LineNames = names;
        return list.Before.Length + list.After.Length > 0 || list.AutoRepeat != null ? list : null;
    }

    private bool TryParseTrack(string tok, float em, out Track track)
    {
        track = default;
        if (tok.StartsWith("minmax(", StringComparison.Ordinal) && tok.EndsWith(")", StringComparison.Ordinal))
        {
            var parts = CssParser.SplitTopLevel(tok.Substring(7, tok.Length - 8), ',');
            if (parts.Count != 2 || !TryParseTrackSize(parts[0].Trim(), em, out var min) || !TryParseTrackSize(parts[1].Trim(), em, out var max)) return false;
            if (min.Kind == TrackSizeKind.Fr) min = new TrackSize(TrackSizeKind.Auto);
            track = new Track(min, max);
            return true;
        }
        if (tok.StartsWith("fit-content(", StringComparison.Ordinal) && tok.EndsWith(")", StringComparison.Ordinal))
        {
            // Approximated as minmax(auto, max-content) capped later by the track's available space.
            track = new Track(new TrackSize(TrackSizeKind.Auto), new TrackSize(TrackSizeKind.MaxContent));
            return true;
        }
        if (!TryParseTrackSize(tok, em, out var size)) return false;
        track = size.Kind == TrackSizeKind.Fr ? new Track(new TrackSize(TrackSizeKind.Auto), size) : new Track(size, size);
        return true;
    }

    private bool TryParseTrackSize(string tok, float em, out TrackSize size)
    {
        size = default;
        switch (tok)
        {
            case "auto": size = new TrackSize(TrackSizeKind.Auto); return true;
            case "min-content": size = new TrackSize(TrackSizeKind.MinContent); return true;
            case "max-content": size = new TrackSize(TrackSizeKind.MaxContent); return true;
        }
        if (tok.EndsWith("fr", StringComparison.Ordinal))
        {
            if (!float.TryParse(tok.AsSpan(0, tok.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out float fr) || fr < 0) return false;
            size = new TrackSize(TrackSizeKind.Fr, fr);
            return true;
        }
        if (!TryParseLength(tok, em, _rootFontSize, out var len, _vw, _vh) || len.IsAuto) return false;
        size = len.Unit == LengthUnit.Calc ? new TrackSize(len) : len.IsPercent ? new TrackSize(TrackSizeKind.Percent, len.Value) : new TrackSize(TrackSizeKind.Fixed, Math.Max(0, ToLayoutUnits(len.Value)));
        return true;
    }

    private static void ParsePaint(string v, ComputedStyle s, uint? inheritedColor, string? inheritedUrl, bool inherit,
        out uint? color, out string? url, uint? currentColor, string? currentUrl)
    {
        if (inherit) { color = inheritedColor; url = inheritedUrl; return; }
        var t = v.Trim();
        if (t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            color = null;
            url = null;
            return;
        }
        if (t.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            int close = t.IndexOf(')');
            url = close > 4 ? t.Substring(4, close - 4).Trim().Trim('"', '\'') : null;
            var fallback = close >= 0 ? t.Substring(close + 1).Trim() : "";
            color = fallback.Length > 0 && CssColor.TryParse(fallback, s.Color, out var fc) ? fc : null;
            return;
        }
        if (CssColor.TryParse(t, s.Color, out var c))
        {
            color = CssColor.IsVisible(c) ? c : null;
            url = null;
            return;
        }
        color = currentColor;
        url = currentUrl;
    }

    /// <summary>Parses a counter-reset, counter-set or counter-increment list: names, each optionally followed by an integer.</summary>
    private static CounterOp[]? ParseCounters(string v, int defaultValue)
    {
        var t = v.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (t.Length == 0 || t.Length == 1 && t[0].Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        var list = new List<CounterOp>();
        for (int i = 0; i < t.Length; i++)
        {
            if (int.TryParse(t[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return null; // a number without a name
            string name = t[i];
            int value = defaultValue;
            if (i + 1 < t.Length && int.TryParse(t[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) { value = n; i++; }
            list.Add(new CounterOp(name, value));
        }
        return list.ToArray();
    }

    private void SetLength(ref Length target, string lower, float em, Length? inherited, bool noAuto = false)
    {
        if (inherited.HasValue) { target = inherited.Value; return; }
        if (TryParseLength(lower, em, _rootFontSize, out var len, _vw, _vh) && !(noAuto && len.IsAuto)) target = ToLayoutUnits(len);
    }

    /// <summary>
    /// Chrome stores box lengths in units of 1/64 CSS pixel, cutting off the rest (toward zero). Doing the same keeps
    /// long stacks of boxes sized in mm or pt from drifting away from Chrome's positions.
    /// </summary>
    internal static float ToLayoutUnits(float pt) => MathF.Truncate(pt / 0.75f * 64f) / 64f * 0.75f;

    private static Length ToLayoutUnits(Length len) => len.Unit == LengthUnit.Pt ? Length.Pt(ToLayoutUnits(len.Value)) : len;

    private float BorderWidth(string lower, float em, float current)
    {
        switch (lower)
        {
            case "thin": return 0.75f;
            case "medium": return 1.5f;
            case "thick": return 3.75f;
        }
        return TryParseLength(lower, em, _rootFontSize, out var len, _vw, _vh) && !len.IsAuto && !len.IsPercent ? SnapBorderWidth(len.Value) : current;
    }

    /// <summary>
    /// Border widths are rounded down to whole CSS pixels, and a width under one pixel up to one pixel, as browsers do
    /// (CSS Values 4 "snap as a border width"): a 1pt border is 1px wide, not 1.33px.
    /// </summary>
    internal static float SnapBorderWidth(float pt)
    {
        if (pt <= 0) return 0;
        float px = pt / 0.75f;
        return (px < 1 ? 1 : MathF.Floor(px + 0.001f)) * 0.75f;
    }

    private static BorderStyle ParseBorderStyle(string lower, BorderStyle current)
        => lower switch
        {
            "none" or "hidden" => BorderStyle.None,
            "solid" => BorderStyle.Solid,
            "groove" => BorderStyle.Groove,
            "ridge" => BorderStyle.Ridge,
            "inset" => BorderStyle.Inset,
            "outset" => BorderStyle.Outset,
            "dashed" => BorderStyle.Dashed,
            "dotted" => BorderStyle.Dotted,
            "double" => BorderStyle.Double,
            _ => current,
        };

    /// <summary>Sets one corner's border radius: one length for both axes, or horizontal then vertical.</summary>
    private void SetRadius(ComputedStyle s, int corner, string lower, float em, ComputedStyle? inherited)
    {
        Length h, v;
        if (inherited != null)
        {
            if (inherited.BorderRadii == null) { h = v = Length.Zero; }
            else { h = inherited.BorderRadii[corner * 2]; v = inherited.BorderRadii[corner * 2 + 1]; }
        }
        else
        {
            var t = CssParser.Tokens(lower);
            if (t.Count is 0 or > 2) return;
            if (!TryParseLength(t[0], em, _rootFontSize, out h, _vw, _vh) || h.IsAuto || h.Value < 0) return;
            v = h;
            if (t.Count == 2 && (!TryParseLength(t[1], em, _rootFontSize, out v, _vw, _vh) || v.IsAuto || v.Value < 0)) return;
        }
        if (s.BorderRadii == null)
        {
            if (h.Value == 0 && v.Value == 0) return;
            s.BorderRadii = new Length[8];
            Array.Fill(s.BorderRadii, Length.Zero);
        }
        else s.BorderRadii = (Length[])s.BorderRadii.Clone(); // the array may be shared with the style it was copied from
        s.BorderRadii[corner * 2] = h;
        s.BorderRadii[corner * 2 + 1] = v;
    }

    private static uint? BorderColor(string v, ComputedStyle s)
    {
        if (v.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)) return null;
        return CssColor.TryParse(v, s.Color, out var c) ? c : null;
    }

    private static FontFamily MapFontFamily(string value, FontFamily current)
    {
        foreach (var part in value.Split(','))
        {
            var name = part.Trim().Trim('"', '\'').Trim().ToLowerInvariant();
            if (name.Length == 0) continue;
            if (name.Contains("mono") || name.Contains("courier") || name.Contains("consolas") || name.Contains("menlo")
                || name.Contains("monaco") || name.Contains("console") || name.Contains("code"))
                return FontFamily.Courier;
            if (name == "serif" || name.Contains("times") || name.Contains("georgia") || name.Contains("garamond")
                || name.Contains("cambria") || name.Contains("palatino") || name.Contains("baskerville")
                || name.Contains("book antiqua") || name.Contains("didot") || name.Contains("bodoni") || name.Contains("merriweather")
                || name.Contains("charter") || name.Contains("constantia") || name.EndsWith(" serif", StringComparison.Ordinal))
                return FontFamily.Times;
            if (name == "sans-serif" || name.Contains("arial") || name.Contains("helvetica") || name.Contains("verdana")
                || name.Contains("tahoma") || name.Contains("segoe") || name.Contains("calibri") || name.Contains("roboto")
                || name.Contains("sans") || name.Contains("system-ui") || name.Contains("apple-system") || name.Contains("inter")
                || name.Contains("lato") || name.Contains("trebuchet") || name.Contains("lucida") || name.Contains("ubuntu")
                || name.Contains("montserrat") || name.Contains("poppins") || name.Contains("nunito") || name.Contains("geneva")
                || name == "cursive" || name == "fantasy")
                return FontFamily.Helvetica;
        }
        return FontFamily.Helvetica;
    }

    /// <summary>Parses a CSS length. Font-relative units are resolved here; percentages are kept for layout.</summary>
    public static bool TryParseLength(string v, float em, float rem, out Length len, float vw = 5.9528f, float vh = 8.4189f)
    {
        len = default;
        if (v.Length == 0) return false;
        if (v == "auto") { len = Length.Auto; return true; }
        if (v == "0") { len = Length.Zero; return true; }
        if (v[^1] == ')') return CssMath.IsMathFunction(v) && CssMath.TryParseLength(v, em, rem, vw, vh, out len);
        int end = v.Length;
        int unitStart = end;
        while (unitStart > 0 && (char.IsLetter(v[unitStart - 1]) || v[unitStart - 1] == '%')) unitStart--;
        if (unitStart == 0) return false;
        if (!float.TryParse(v.AsSpan(0, unitStart), NumberStyles.Float, CultureInfo.InvariantCulture, out float n)) return false;
        var unit = v.AsSpan(unitStart);
        float pt;
        switch (unit.ToString())
        {
            case "px": pt = n * 0.75f; break;
            case "pt": pt = n; break;
            case "": pt = n * 0.75f; break; // unitless (quirks): treat as px
            case "em": pt = n * em; break;
            case "rem": pt = n * rem; break;
            case "ex": pt = n * em * 0.5f; break;
            case "ch": pt = n * em * 0.5f; break;
            case "%": len = new Length(n, LengthUnit.Percent); return true;
            case "in": pt = n * 72f; break;
            case "cm": pt = n * 72f / 2.54f; break;
            case "mm": pt = n * 72f / 25.4f; break;
            case "q": pt = n * 72f / 101.6f; break;
            case "pc": pt = n * 12f; break;
            case "vw": pt = n * vw; break; // 1% of the page's content width
            case "vh": pt = n * vh; break; // 1% of the page's content height
            case "vmin": pt = n * Math.Min(vw, vh); break;
            case "vmax": pt = n * Math.Max(vw, vh); break;
            default: return false;
        }
        len = Length.Pt(pt);
        return true;
    }
}
