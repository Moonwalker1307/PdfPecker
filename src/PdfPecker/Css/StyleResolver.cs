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
        _author = new RuleIndex(authorSheet);
        _hasCustomProperties = authorSheet.Rules.Any(r => r.Declarations.Any(d => d.Property.StartsWith("--", StringComparison.Ordinal)));
        // Chrome sizes the print viewport in whole CSS pixels, rounded down: 100vh on A4 is 1122px, not 1122.52px.
        _vw = WholePx(viewportWidth) / 100f;
        _vh = WholePx(viewportHeight) / 100f;
    }

    private static float WholePx(float pt) => MathF.Floor(pt / 0.75f + 0.001f) * 0.75f;

    public void ResolveTree(Element document, ComputedStyle rootStyle)
    {
        document.Style = rootStyle;
        Prepare(document);
        // Fragments without an <html> element: custom properties declared on :root or html still apply.
        if (_hasCustomProperties && !document.Children.Any(c => c is Element { Name: "html" }))
        {
            var html = new Element("html") { Parent = document, ElementIndex = 1, ElementSiblingCount = 1 };
            Resolve(html, rootStyle);
            rootStyle.CustomProperties = html.Style.CustomProperties;
        }
        ResolveChildren(document);
    }

    private static void Prepare(Element parent)
    {
        // ::before and ::after boxes from an earlier pass are generated again.
        parent.Children.RemoveAll(c => c is Element { Generated: true });
        int count = 0;
        foreach (var c in parent.Children) if (c is Element) count++;
        int index = 0;
        Element? prev = null;
        foreach (var c in parent.Children)
        {
            if (c is not Element e) continue;
            e.ElementIndex = ++index;
            e.ElementSiblingCount = count;
            e.PreviousElementSibling = prev;
            prev = e;
            e.Id = e.GetAttribute("id");
            var cls = e.GetAttribute("class");
            if (!string.IsNullOrWhiteSpace(cls))
                e.Classes = cls!.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            Prepare(e);
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
            if (_countersUsed) ApplyCounters(e.Style, ref scope);
            ResolveChildren(e);
        }
        if (pseudos) Generate(parent, PseudoElement.After, ref scope);
        if (scope != null)
            foreach (var name in scope)
            {
                var stack = _counters[name];
                stack.RemoveAt(stack.Count - 1);
            }
    }

    private bool HasPseudoRules => _author.PseudoElements != null || UaIndex.Value.PseudoElements != null;

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

    // ---------------------------------------------------------------- counters

    private readonly Dictionary<string, List<int>> _counters = new(StringComparer.Ordinal);
    private bool _countersUsed;
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

    /// <summary>The values of a counter's instances, outermost first; empty when it has none.</summary>
    internal IReadOnlyList<int> CounterValues(string name) => _counters.TryGetValue(name, out var stack) ? stack : Array.Empty<int>();

    /// <summary>Stops resolving when cancelled.</summary>
    public CancellationToken Cancellation { get; init; }

    private void Resolve(Element e, ComputedStyle parent)
    {
        if ((++_resolved & 255) == 0) Cancellation.ThrowIfCancellationRequested();
        _count = 0;
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

        // Floats and absolutely positioned boxes are block-level (CSS 2.1 §9.7).
        if (s.Floated || s.OutOfFlow)
        {
            if (s.Display is Display.Inline or Display.InlineBlock) s.Display = Display.Block;
            else if (s.Display == Display.InlineFlex) s.Display = Display.Flex;
            else if (s.Display == Display.InlineGrid) s.Display = Display.Grid;
            if (s.OutOfFlow) { s.Floated = false; s.Float = FloatSide.None; }
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
            for (int i = 0; i < decls.Length; i++)
                Push(decls[i].Important ? importantTier : tier, r.Selector.Specificity, r.Order, i, decls[i]);
        }
    }

    private void Push(long tier, int specificity, int order, int declIndex, Declaration d)
    {
        if (_count == _keys.Length)
        {
            Array.Resize(ref _keys, _count * 2);
            Array.Resize(ref _decls, _count * 2);
        }
        long key = tier << 56
                   | (long)Math.Min(specificity, 0xFFFFF) << 36
                   | (long)Math.Min(order, 0xFFFFF) << 16
                   | (long)Math.Min(declIndex, 0xFFFF);
        _keys[_count] = key;
        _decls[_count++] = d;
    }

    // ---------------------------------------------------------------- presentational attributes

    private void AddHints(Element e)
    {
        if (e.Attributes.Count == 0 && e.Name != "td" && e.Name != "th") return;
        _scratch.Clear();
        string? v;
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
    };

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

    private void AlignHint(string? align, bool block)
    {
        if (align == null) return;
        var a = align.ToLowerInvariant();
        if (a == "center" && block) { Hint("margin-left", "auto"); Hint("margin-right", "auto"); }
        else if (a is "middle" or "top" or "bottom") Hint("vertical-align", a);
    }

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
                    "left" or "start" or "-webkit-left" => TextAlign.Left,
                    "right" or "end" or "-webkit-right" => TextAlign.Right,
                    "center" or "middle" or "-webkit-center" => TextAlign.Center,
                    "justify" => TextAlign.Justify,
                    "inherit" => parent.TextAlign,
                    _ => s.TextAlign,
                };
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
                break;
            }
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
                s.ListStyleType = lower switch
                {
                    "disc" => ListStyleType.Disc,
                    "circle" => ListStyleType.Circle,
                    "square" => ListStyleType.Square,
                    "decimal" => ListStyleType.Decimal,
                    "decimal-leading-zero" => ListStyleType.DecimalLeadingZero,
                    "lower-alpha" or "lower-latin" => ListStyleType.LowerAlpha,
                    "upper-alpha" or "upper-latin" => ListStyleType.UpperAlpha,
                    "lower-roman" => ListStyleType.LowerRoman,
                    "upper-roman" => ListStyleType.UpperRoman,
                    "none" => ListStyleType.None,
                    "inherit" => parent.ListStyleType,
                    _ => s.ListStyleType,
                };
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
                    "none" => FloatSide.None,
                    "inherit" => parent.Float,
                    _ => s.Float,
                };
                s.Floated = s.Float != FloatSide.None;
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
                // Any value but visible and clip makes the box a block formatting context root, which contains its floats.
                foreach (var t in lower.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (t is "hidden" or "auto" or "scroll" or "overlay") s.FormattingRoot = true;
                break;
            case "transform":
                if (lower == "none") s.Transform = null;
                else if (inherit) s.Transform = parent.Transform;
                else if (CssTransform.TryParse(lower, em, _rootFontSize, _vw, _vh, out var tf)) s.Transform = tf;
                break;
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
                s.SvgClipPath = lower.StartsWith("url(", StringComparison.Ordinal) && v.IndexOf(')') is int cp and > 4
                    ? v.Substring(4, cp - 4).Trim().Trim('"', '\'') : null;
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
        }
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
            // "span name" spans to the next line of that name; approximated as the given count of tracks.
            return new GridLine(0, number > 0 ? Math.Min(number, 1000) : 1);
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
        size = len.IsPercent ? new TrackSize(TrackSizeKind.Percent, len.Value) : new TrackSize(TrackSizeKind.Fixed, Math.Max(0, ToLayoutUnits(len.Value)));
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
            "solid" or "groove" or "ridge" or "inset" or "outset" => BorderStyle.Solid,
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
