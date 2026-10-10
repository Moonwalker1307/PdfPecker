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

    public RuleIndex(Stylesheet sheet)
    {
        foreach (var r in sheet.Rules)
        {
            var subj = r.Selector.Subject;
            if (subj.Id != null) Add(ById, subj.Id, r);
            else if (subj.Classes != null) Add(ByClass, subj.Classes[0], r);
            else if (subj.Tag != null) Add(ByTag, subj.Tag, r);
            else Universal.Add(r);
        }
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

    private long[] _keys = new long[64];
    private Declaration[] _decls = new Declaration[64];
    private int _count;
    private readonly List<Declaration> _scratch = new();
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

    public StyleResolver(Stylesheet authorSheet) => _author = new RuleIndex(authorSheet);

    public void ResolveTree(Element document, ComputedStyle rootStyle)
    {
        document.Style = rootStyle;
        Prepare(document);
        ResolveChildren(document);
    }

    private static void Prepare(Element parent)
    {
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
        foreach (var c in parent.Children)
        {
            if (c is not Element e) continue;
            Resolve(e, parent.Style);
            if (e.Style.Display == Display.None) continue; // descendants are never rendered
            ResolveChildren(e);
        }
    }

    private void Resolve(Element e, ComputedStyle parent)
    {
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

        _probe.Set(parent, _decls, _count);
        if (_styleCache.TryGetValue(_probe, out var shared))
        {
            e.Style = shared;
            if (e.Name == "html") _rootFontSize = shared.FontSize;
            Array.Clear(_decls, 0, _count);
            return;
        }

        var s = parent.CreateChild();

        // font-size first: em-based lengths in every other property depend on it.
        for (int i = _count - 1; i >= 0; i--)
        {
            if (_decls[i].Property == "font-size")
            {
                ApplyFontSize(s, parent, _decls[i].Value);
                break;
            }
        }
        if (e.Name == "html") _rootFontSize = s.FontSize;

        for (int i = 0; i < _count; i++)
        {
            var d = _decls[i];
            if (d.Property != "font-size") Apply(s, parent, d.Property, d.Value);
        }

        if (s.Floated && s.Display == Display.Inline) s.Display = Display.Block;
        s.InvalidateFont();
        e.Style = s;
        _styleCache[_probe.Freeze()] = s;
        Array.Clear(_decls, 0, _count);
    }

    private void Collect(RuleIndex index, Element e, long tier, long importantTier)
    {
        if (e.Id != null && index.ById.TryGetValue(e.Id, out var byId)) Match(byId, e, tier, importantTier);
        foreach (var c in e.Classes)
            if (index.ByClass.TryGetValue(c, out var byClass)) Match(byClass, e, tier, importantTier);
        if (index.ByTag.TryGetValue(e.Name, out var byTag)) Match(byTag, e, tier, importantTier);
        if (index.Universal.Count > 0) Match(index.Universal, e, tier, importantTier);
    }

    private void Match(List<StyleRule> rules, Element e, long tier, long importantTier)
    {
        foreach (var r in rules)
        {
            if (!r.Selector.Matches(e)) continue;
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
                if (TryParseLength(v, parent.FontSize, _rootFontSize, out var len) && !len.IsAuto)
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
                    "block" or "flow-root" or "flex" or "grid" => Display.Block,
                    "inline" or "contents" => Display.Inline,
                    "inline-block" or "inline-flex" or "inline-grid" => Display.InlineBlock,
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
                    _ => s.Display,
                };
                break;
            case "color":
                if (inherit) s.Color = parent.Color;
                else if (CssColor.TryParse(v, parent.Color, out var col)) s.Color = CssColor.FlattenOnWhite(col);
                break;
            case "background-color":
                if (inherit) s.BackgroundColor = parent.BackgroundColor;
                else if (CssColor.TryParse(v, s.Color, out var bg)) s.BackgroundColor = CssColor.FlattenOnWhite(bg);
                break;
            case "font-family":
                if (inherit) { s.FontFamily = parent.FontFamily; break; }
                s.FontFamily = MapFontFamily(v, s.FontFamily);
                s.FontFamilyName = v;
                break;
            case "font-weight":
                s.Bold = lower switch
                {
                    "bold" => true,
                    "bolder" => true,
                    "normal" => false,
                    "lighter" => false,
                    "inherit" => parent.Bold,
                    _ => int.TryParse(lower, out int w) ? w >= 600 : s.Bold,
                };
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
                else if (TryParseLength(lower, em, _rootFontSize, out var lh) && !lh.IsAuto)
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
                if (TryParseLength(lower, em, _rootFontSize, out var ti)) s.TextIndent = ti.IsPercent ? 0 : ti.Resolve(0);
                break;
            case "letter-spacing":
                if (lower == "normal") s.LetterSpacing = 0;
                else if (TryParseLength(lower, em, _rootFontSize, out var ls)) s.LetterSpacing = ls.Resolve(0);
                break;
            case "word-spacing":
                if (lower == "normal") s.WordSpacing = 0;
                else if (TryParseLength(lower, em, _rootFontSize, out var ws)) s.WordSpacing = ws.Resolve(0);
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
            case "border-collapse":
                s.BorderCollapse = lower == "collapse";
                break;
            case "border-spacing-h":
                if (TryParseLength(lower, em, _rootFontSize, out var bsh)) s.BorderSpacingH = bsh.Resolve(0);
                break;
            case "border-spacing-v":
                if (TryParseLength(lower, em, _rootFontSize, out var bsv)) s.BorderSpacingV = bsv.Resolve(0);
                break;
            case "box-sizing":
                s.BorderBox = lower == "border-box";
                break;
            case "float":
                s.Floated = lower is "left" or "right";
                break;
            case "clear":
                s.ClearBoth = lower != "none";
                break;
            case "break-before":
                s.PageBreakBefore = lower is "always" or "page" or "left" or "right" or "recto" or "verso";
                break;
            case "break-after":
                s.PageBreakAfter = lower is "always" or "page" or "left" or "right" or "recto" or "verso";
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

            case "border-top-width": s.BorderTopWidth = BorderWidth(lower, em, s.BorderTopWidth); break;
            case "border-right-width": s.BorderRightWidth = BorderWidth(lower, em, s.BorderRightWidth); break;
            case "border-bottom-width": s.BorderBottomWidth = BorderWidth(lower, em, s.BorderBottomWidth); break;
            case "border-left-width": s.BorderLeftWidth = BorderWidth(lower, em, s.BorderLeftWidth); break;
            case "border-top-style": s.BorderTopStyle = ParseBorderStyle(lower, s.BorderTopStyle); break;
            case "border-right-style": s.BorderRightStyle = ParseBorderStyle(lower, s.BorderRightStyle); break;
            case "border-bottom-style": s.BorderBottomStyle = ParseBorderStyle(lower, s.BorderBottomStyle); break;
            case "border-left-style": s.BorderLeftStyle = ParseBorderStyle(lower, s.BorderLeftStyle); break;
            case "border-top-color": s.BorderTopColor = BorderColor(v, s); break;
            case "border-right-color": s.BorderRightColor = BorderColor(v, s); break;
            case "border-bottom-color": s.BorderBottomColor = BorderColor(v, s); break;
            case "border-left-color": s.BorderLeftColor = BorderColor(v, s); break;
        }
    }

    private void SetLength(ref Length target, string lower, float em, Length? inherited, bool noAuto = false)
    {
        if (inherited.HasValue) { target = inherited.Value; return; }
        if (TryParseLength(lower, em, _rootFontSize, out var len) && !(noAuto && len.IsAuto)) target = len;
    }

    private float BorderWidth(string lower, float em, float current)
    {
        switch (lower)
        {
            case "thin": return 0.75f;
            case "medium": return 1.5f;
            case "thick": return 3.75f;
        }
        return TryParseLength(lower, em, _rootFontSize, out var len) && !len.IsAuto && !len.IsPercent ? Math.Max(0, len.Value) : current;
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

    private static uint? BorderColor(string v, ComputedStyle s)
    {
        if (v.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)) return null;
        return CssColor.TryParse(v, s.Color, out var c) ? CssColor.FlattenOnWhite(c) : null;
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
    public static bool TryParseLength(string v, float em, float rem, out Length len)
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
            case "vw": pt = n * 5.95f; break; // relative to an A4 page width
            case "vh": pt = n * 8.42f; break;
            default: return false;
        }
        len = Length.Pt(pt);
        return true;
    }
}
