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
internal sealed partial class StyleResolver
{
    // The whitespace that separates the words of an attribute or a list value, kept in one array so a split allocates no
    // separator array of its own.
    private static readonly char[] WhitespaceSeparators = { ' ', '\t', '\n', '\r' };

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
    private readonly Dictionary<string, Declaration[]> _inlineCache = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), Declaration> _hintCache = new();
    private readonly Dictionary<StyleKey, ComputedStyle> _styleCache = new();
    private readonly StyleKey _probe = new();
    private readonly List<Declaration> _registeredEach = new(), _registeredRoot = new();
    private bool _decorationLineSet;

    // Query containers (container-type or container-name) around the element being resolved, outermost first, and
    // every container met in this pass with the size its content box had then.
    private readonly List<Element> _containers = new();
    private readonly List<(Element Element, float Width, float Height)> _containersSeen = new();
    private readonly bool _containerRules;
    private bool _containerUnits; // a declaration uses cq units, so styles that use them cannot be shared

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
        _containerRules = authorSheet.HasContainerRules;
        _containerUnits = authorSheet.HasContainerUnits;
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
        _rootStyle = rootStyle;
        _initialStyle = null;
        Strings.Clear();
        Footnotes.Clear();
        _footnoteDepth = 0;
        _footnotePage = null;
        _byId = null;
        _containers.Clear();
        _containersSeen.Clear();
        Prepare(document);
        // cq units outside every container fall back to the viewport units; a style pass of another document (an SVG
        // image) in the middle of this one gets its own.
        var savedUnits = ContainerQuery.SetUnits(float.NaN, float.NaN);
        try
        {
            // Fragments without an <html> element: custom properties declared on :root or html still apply.
            if (_hasCustomProperties && !document.Children.Any(c => c is Element { Name: "html" }))
            {
                var html = new Element("html") { Parent = document, ElementIndex = 1, ElementSiblingCount = 1, TypeIndex = 1, TypeCount = 1 };
                Resolve(html, rootStyle);
                rootStyle.CustomProperties = html.Style.CustomProperties;
            }
            var saved = Compound.BeginMatching();
            try { ResolveChildren(document); }
            finally { Compound.EndMatching(saved); }
        }
        finally
        {
            ContainerQuery.RestoreUnits(savedUnits);
        }
    }

    /// <summary>
    /// Whether the layout that followed this pass gave a query container another size than the one its container
    /// queries and cq units were evaluated with, so that the styles have to be resolved again. Always false for
    /// documents that use neither.
    /// </summary>
    public bool ContainerSizesChanged()
    {
        if (!_containerRules && !_containerUnits) return false;
        foreach (var (e, w, h) in _containersSeen)
            if (!SameSize(e.ContainerWidth, w) || !SameSize(e.ContainerHeight, h)) return true;
        return false;

        static bool SameSize(float a, float b) => float.IsNaN(a) ? float.IsNaN(b) : !float.IsNaN(b) && Math.Abs(a - b) < 0.01f;
    }

    private static bool IsContainer(ComputedStyle s) => s.ContainerType != ContainerType.Normal || s.ContainerNames != null;

    private void PushContainer(Element e)
    {
        _containers.Add(e);
        _containersSeen.Add((e, e.ContainerWidth, e.ContainerHeight));
        SetContainerUnits();
    }

    private void PopContainer()
    {
        _containers.RemoveAt(_containers.Count - 1);
        SetContainerUnits();
    }

    /// <summary>cq units refer to the nearest container that can be queried on their axis: any size container for the
    /// inline axis, a container-type: size one for the block axis.</summary>
    private void SetContainerUnits()
    {
        float w = float.NaN, h = float.NaN;
        bool haveW = false, haveH = false;
        for (int i = _containers.Count - 1; i >= 0 && !(haveW && haveH); i--)
        {
            var c = _containers[i];
            if (!haveW && c.Style.ContainerType != ContainerType.Normal) { w = c.ContainerWidth / 100f; haveW = true; }
            if (!haveH && c.Style.ContainerType == ContainerType.Size) { h = c.ContainerHeight / 100f; haveH = true; }
        }
        ContainerQuery.SetUnits(w, h);
    }

    /// <summary>
    /// Whether the conditions of the @container blocks around a rule hold for <paramref name="e"/> (or its
    /// pseudo-element). Each selects its own query container: the nearest ancestor container with the name it asks
    /// for and a type that covers the axes it tests; for a style() query without a name, the parent, as every element
    /// is a style container. Without one, or before the container's size is known, the condition is false.
    /// </summary>
    private bool ContainersMatch(ContainerQuery[] queries, Element e, PseudoElement which)
    {
        foreach (var q in queries)
        {
            Element? container = null;
            if (!q.NeedsSize && q.Name == null) container = which != PseudoElement.None ? e : e.Parent;
            else
                for (int i = _containers.Count - 1; i >= 0 && container == null; i--)
                    if (q.Accepts(_containers[i].Style)) container = _containers[i];
            if (container?.Style == null || !q.Matches(container, _rootFontSize)) return false;
        }
        return true;
    }

    private static void Prepare(Element parent)
    {
        // Only an earlier pass leaves boxes to undo, and one look at the children spares the first pass the passes below.
        bool earlier = parent.DomChildren != null;
        for (int i = 0; i < parent.Children.Count && !earlier; i++)
            earlier = parent.Children[i] is Element { Anonymous: true } or Element { Generated: true };
        if (earlier)
        {
            // Anonymous table boxes of an earlier pass give their children back; they are made again after the
            // children's styles are known.
            UnwrapAnonymous(parent);
            // ::before, ::after and ::first-letter boxes from an earlier pass are generated again; a first letter goes
            // back into the text it was taken from.
            for (int i = parent.Children.Count - 1; i >= 0; i--)
            {
                if (parent.Children[i] is not Element { Generated: true, Name: FirstLetterName } letter) continue;
                var text = letter.Children.Count == 1 && letter.Children[0] is TextNode t ? t.Text : "";
                if (i + 1 < parent.Children.Count && parent.Children[i + 1] is TextNode rest) rest.Text = text + rest.Text;
                else parent.Children.Insert(i + 1, new TextNode(text) { Parent = parent });
            }
            // The children of display: contents elements go back into them.
            RestoreContents(parent);
            parent.Children.RemoveAll(c => c is Element { Generated: true });
        }
        int count = 0;
        foreach (var c in parent.Children) if (c is Element) count++;
        int index = 0;
        Element? prev = null;
        // The last sibling seen of each element name, for :nth-of-type(). Siblings use only a few names.
        List<Element>? lastOfName = count > 1 ? new List<Element>(4) : null;
        foreach (var c in parent.Children)
        {
            c.Parent = parent; // display: contents gave it another parent in the last pass
            if (c is not Element e) continue;
            e.ContentsHref = null;
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
                e.Classes = cls!.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
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
        HashSet<string>? scope = null;
        bool pseudos = HasPseudoRules && CanHaveGenerated(parent);
        if (pseudos) Generate(parent, PseudoElement.Before, ref scope);
        var children = parent.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not Element e || e.Generated) continue;
            Resolve(e, parent.Style);
            if (e.Style.Display == Display.None) continue; // descendants are never rendered
            bool container = IsContainer(e.Style);
            if (container) PushContainer(e);
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
            if (container) PopContainer();
        }
        if (pseudos) Generate(parent, PseudoElement.After, ref scope);
        if (_contentsUsed && !parent.Style.Contents) SpliceContents(parent);
        if (parent.Name == "details" && !parent.HasAttribute("open")) HideClosedDetails(parent);
        WrapTableParts(parent);
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
}
