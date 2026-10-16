using PdfPecker.Html;

namespace PdfPecker.Css;

internal enum Combinator : byte { None, Descendant, Child, Adjacent, Sibling }

internal enum AttrOp : byte { Exists, Equals, Includes, DashMatch, Prefix, Suffix, Substring }

internal enum PseudoKind : byte
{
    FirstChild, LastChild, OnlyChild, NthChild, NthLastChild, FirstOfType, LastOfType, OnlyOfType, NthOfType, NthLastOfType,
    Root, Link, Empty, Never, Not, Is, Where, Has, HasAnchor, Lang, Checked, Disabled, Enabled, Required, Optional, ReadOnly,
    ReadWrite, PlaceholderShown, Default, Always,
}

/// <summary>The pseudo-element a selector's subject names, if any.</summary>
internal enum PseudoElement : byte { None, Before, After, Marker, FirstLetter, FirstLine, FootnoteCall, FootnoteMarker }

internal sealed class AttrCondition
{
    public string Name = "";
    public AttrOp Op;
    public string Value = "";
    public bool IgnoreCase; // the "i" flag: [type="a" i]
}

internal sealed class PseudoCondition
{
    public PseudoKind Kind;
    public int A, B; // an+b for nth-child and nth-of-type
    public Selector[]? List; // the selector list of :not(), :is(), :where(), :has() (relative) and :nth-child(... of S)
    public string[]? Langs; // the ranges of :lang()

    // Set when the list holds a selector with combinators or another list. Matching such a list walks the tree, and the
    // selector around it asks again for each element it tries, so the result is remembered per element.
    public bool Memoize;
}

/// <summary>A compound selector such as <c>div.note#main[title]</c>.</summary>
internal sealed class Compound
{
    public string? Tag;           // null = any
    public string? Id;
    public string[]? Classes;
    public AttrCondition[]? Attrs;
    public PseudoCondition[]? Pseudos;
    public Combinator Combinator; // how this compound relates to the one on its left

    public bool Matches(Element e)
    {
        if (Tag != null && Tag != e.Name) return false;
        if (Id != null && Id != e.Id) return false;
        if (Classes != null)
        {
            var have = e.Classes;
            foreach (var c in Classes)
                if (Array.IndexOf(have, c) < 0) return false;
        }
        if (Attrs != null)
            foreach (var a in Attrs)
                if (!MatchAttr(e, a)) return false;
        if (Pseudos != null)
            foreach (var p in Pseudos)
                if (!MatchPseudo(e, p)) return false;
        return true;
    }

    private static bool MatchAttr(Element e, AttrCondition a)
    {
        var v = e.GetAttribute(a.Name);
        if (v == null) return false;
        if (a.IgnoreCase && a.Op != AttrOp.Exists) return MatchAttrIgnoreCase(v, a);
        switch (a.Op)
        {
            case AttrOp.Exists: return true;
            case AttrOp.Equals: return v == a.Value;
            case AttrOp.Prefix: return a.Value.Length > 0 && v.StartsWith(a.Value, StringComparison.Ordinal);
            case AttrOp.Suffix: return a.Value.Length > 0 && v.EndsWith(a.Value, StringComparison.Ordinal);
            case AttrOp.Substring: return a.Value.Length > 0 && v.Contains(a.Value);
            case AttrOp.DashMatch: return v == a.Value || v.StartsWith(a.Value + "-", StringComparison.Ordinal);
            case AttrOp.Includes:
                foreach (var part in v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (part == a.Value) return true;
                return false;
        }
        return false;
    }

    private static bool MatchAttrIgnoreCase(string v, AttrCondition a)
    {
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        switch (a.Op)
        {
            case AttrOp.Equals: return string.Equals(v, a.Value, ic);
            case AttrOp.Prefix: return a.Value.Length > 0 && v.StartsWith(a.Value, ic);
            case AttrOp.Suffix: return a.Value.Length > 0 && v.EndsWith(a.Value, ic);
            case AttrOp.Substring: return a.Value.Length > 0 && v.Contains(a.Value, ic);
            case AttrOp.DashMatch: return string.Equals(v, a.Value, ic) || v.StartsWith(a.Value + "-", ic);
            case AttrOp.Includes:
                foreach (var part in v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (string.Equals(part, a.Value, ic)) return true;
                return false;
        }
        return false;
    }

    /// <summary>The element :has() is being matched for; its relative selectors start at this element.</summary>
    [ThreadStatic] private static Element? _hasAnchor;

    private static bool MatchPseudo(Element e, PseudoCondition p)
    {
        switch (p.Kind)
        {
            case PseudoKind.FirstChild: return e.ElementIndex == 1;
            case PseudoKind.LastChild: return e.ElementIndex == e.ElementSiblingCount;
            case PseudoKind.OnlyChild: return e.ElementSiblingCount == 1;
            case PseudoKind.NthChild: return p.List == null ? MatchNth(p.A, p.B, e.ElementIndex) : MatchNthOf(e, p, fromEnd: false);
            case PseudoKind.NthLastChild:
                return p.List == null ? MatchNth(p.A, p.B, e.ElementSiblingCount - e.ElementIndex + 1) : MatchNthOf(e, p, fromEnd: true);
            case PseudoKind.FirstOfType: return e.TypeIndex == 1;
            case PseudoKind.LastOfType: return e.TypeIndex == e.TypeCount;
            case PseudoKind.OnlyOfType: return e.TypeCount == 1;
            case PseudoKind.NthOfType: return MatchNth(p.A, p.B, e.TypeIndex);
            case PseudoKind.NthLastOfType: return MatchNth(p.A, p.B, e.TypeCount - e.TypeIndex + 1);
            case PseudoKind.Root: return e.Name == "html" || e.Parent?.Name == "#document" && e.ElementSiblingCount == 1;
            case PseudoKind.Link: return e.Name == "a" && e.HasAttribute("href");
            case PseudoKind.Empty: return e.Children.Count == 0;
            case PseudoKind.Not: return !ListMatches(p, e);
            case PseudoKind.Is:
            case PseudoKind.Where: return ListMatches(p, e);
            case PseudoKind.Has: return Remembered(p, e) ?? Remember(p, e, MatchHas(e, p.List!));
            case PseudoKind.HasAnchor: return ReferenceEquals(e, _hasAnchor);
            case PseudoKind.Lang: return MatchLang(e, p.Langs!);
            case PseudoKind.Always: return true;
            case PseudoKind.Checked:
                return e.Name == "input" && IsCheckable(e) && e.HasAttribute("checked") || e.Name == "option" && e.HasAttribute("selected");
            case PseudoKind.Default:
                return e.Name == "input" && IsCheckable(e) && e.HasAttribute("checked") || e.Name == "option" && e.HasAttribute("selected");
            case PseudoKind.Disabled: return IsFormElement(e) && IsDisabled(e);
            case PseudoKind.Enabled: return IsFormElement(e) && !IsDisabled(e);
            case PseudoKind.Required: return IsRequirable(e) && e.HasAttribute("required");
            case PseudoKind.Optional: return IsRequirable(e) && !e.HasAttribute("required");
            case PseudoKind.ReadWrite: return IsReadWrite(e);
            case PseudoKind.ReadOnly: return !IsReadWrite(e);
            case PseudoKind.PlaceholderShown:
                return (e.Name == "textarea" ? e.InnerText().Length == 0 : e.Name == "input" && string.IsNullOrEmpty(e.GetAttribute("value")))
                       && e.HasAttribute("placeholder") && (e.Name == "textarea" || IsTextInput(e));
            default: return false;
        }
    }

    private static string InputType(Element e) => (e.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();

    private static bool IsCheckable(Element e) => InputType(e) is "checkbox" or "radio";

    private static bool IsTextInput(Element e)
        => InputType(e) is "text" or "search" or "url" or "tel" or "email" or "password" or "number" or "date" or "month"
            or "week" or "time" or "datetime-local" or "";

    private static bool IsFormElement(Element e) => e.Name is "button" or "input" or "select" or "textarea" or "optgroup" or "option" or "fieldset";

    private static bool IsRequirable(Element e) => e.Name is "select" or "textarea" || e.Name == "input" && InputType(e) is not ("hidden" or "button" or "submit" or "reset" or "image" or "range" or "color");

    private static bool IsDisabled(Element e)
    {
        if (e.HasAttribute("disabled")) return true;
        if (e.Name == "option" && e.Parent is { Name: "optgroup" } g && g.HasAttribute("disabled")) return true;
        // A disabled fieldset disables its controls, except those in its first legend.
        for (var child = e; child.Parent is { } parent; child = parent)
        {
            if (parent.Name != "fieldset" || !parent.HasAttribute("disabled")) continue;
            Element? firstLegend = null;
            foreach (var c in parent.Children)
                if (c is Element { Name: "legend" } l) { firstLegend = l; break; }
            if (!ReferenceEquals(child, firstLegend)) return true;
        }
        return false;
    }

    private static bool IsReadWrite(Element e)
    {
        if (e.Name == "textarea") return !e.HasAttribute("readonly") && !IsDisabled(e);
        if (e.Name == "input") return IsTextInput(e) && !e.HasAttribute("readonly") && !IsDisabled(e);
        for (var x = e; x != null; x = x.Parent)
        {
            var ce = x.GetAttribute("contenteditable");
            if (ce == null) continue;
            return ce.Length == 0 || ce.Equals("true", StringComparison.OrdinalIgnoreCase) || ce.Equals("plaintext-only", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool MatchLang(Element e, string[] ranges)
    {
        string? lang = null;
        for (var x = e; x != null && lang == null; x = x.Parent) lang = x.GetAttribute("lang") ?? x.GetAttribute("xml:lang");
        if (lang == null) return false;
        foreach (var r in ranges)
        {
            if (r == "*") return lang.Length > 0;
            if (string.Equals(lang, r, StringComparison.OrdinalIgnoreCase)
                || lang.Length > r.Length && lang[r.Length] == '-' && lang.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>:nth-child(An+B of S): the position counts only the siblings that match S.</summary>
    private static bool MatchNthOf(Element e, PseudoCondition p, bool fromEnd)
    {
        if (!ListMatches(p, e) || e.Parent == null) return false;
        int index = 0;
        var siblings = e.Parent.Children;
        bool passed = false;
        for (int k = fromEnd ? siblings.Count - 1 : 0; k >= 0 && k < siblings.Count; k += fromEnd ? -1 : 1)
        {
            if (siblings[k] is not Element s || s.Generated) continue;
            if (ListMatches(p, s)) index++;
            if (ReferenceEquals(s, e)) { passed = true; break; }
        }
        return passed && MatchNth(p.A, p.B, index);
    }

    /// <summary>
    /// :has(): some element, found from <paramref name="e"/> along each relative selector's leading combinator, matches
    /// it. Each relative selector ends (on its left) in an anchor compound that only matches <paramref name="e"/>.
    /// </summary>
    private static bool MatchHas(Element e, Selector[] relative)
    {
        var saved = _hasAnchor;
        _hasAnchor = e;
        try
        {
            foreach (var sel in relative)
            {
                var lead = sel.Parts[^2].Combinator;
                if (lead is Combinator.Descendant or Combinator.Child)
                {
                    // A lone child compound ("> a") only needs the children.
                    if (AnyDescendantMatches(e, sel, childrenOnly: lead == Combinator.Child && sel.Parts.Length == 2)) return true;
                }
                else if (e.Parent != null)
                {
                    var siblings = e.Parent.Children;
                    int k = siblings.IndexOf(e);
                    bool adjacentOnly = lead == Combinator.Adjacent && sel.Parts.Length == 2;
                    for (k++; k < siblings.Count; k++)
                    {
                        if (siblings[k] is not Element s || s.Generated) continue;
                        if (sel.Matches(s)) return true;
                        if (adjacentOnly) break;
                        // Deeper parts ("+ a b") can match inside a following sibling.
                        if (sel.Parts.Length > 2 && AnyDescendantMatches(s, sel, childrenOnly: false)) return true;
                    }
                }
            }
            return false;
        }
        finally { _hasAnchor = saved; }
    }

    private static bool AnyDescendantMatches(Element root, Selector sel, bool childrenOnly)
    {
        foreach (var c in root.Children)
        {
            if (c is not Element e || e.Generated) continue;
            if (sel.Matches(e)) return true;
            if (!childrenOnly && AnyDescendantMatches(e, sel, false)) return true;
        }
        return false;
    }

    /// <summary>
    /// Results of selector lists per element while a tree is being styled. Without them a list nested in a complex
    /// selector is matched again for every element the outer selector tries, so each level of nesting multiplies the
    /// work; with them every list is matched at most once per element. The results stay valid only while the tree does
    /// not change, so the cache lives for one <see cref="StyleResolver.ResolveTree"/>.
    /// </summary>
    [ThreadStatic] private static Dictionary<(PseudoCondition, Element), bool>? _memo;

    /// <summary>Starts remembering list results; returns the previous cache for <see cref="EndMatching"/>.</summary>
    internal static object? BeginMatching()
    {
        var saved = _memo;
        _memo = new Dictionary<(PseudoCondition, Element), bool>();
        return saved;
    }

    internal static void EndMatching(object? saved) => _memo = (Dictionary<(PseudoCondition, Element), bool>?)saved;

    private static bool? Remembered(PseudoCondition p, Element e)
        => p.Memoize && _memo != null && _memo.TryGetValue((p, e), out bool hit) ? hit : null;

    private static bool Remember(PseudoCondition p, Element e, bool result)
    {
        if (p.Memoize && _memo != null) _memo[(p, e)] = result;
        return result;
    }

    private static bool ListMatches(PseudoCondition p, Element e) => Remembered(p, e) ?? Remember(p, e, AnyMatches(p.List!, e));

    private static bool AnyMatches(Selector[] list, Element e)
    {
        foreach (var sel in list)
            if (sel.Matches(e)) return true;
        return false;
    }

    private static bool MatchNth(int a, int b, int index)
    {
        if (a == 0) return index == b;
        long n = (long)index - b; // in long: extreme a and b from the stylesheet must not overflow
        return (a > 0 ? n >= 0 : n <= 0) && n % a == 0;
    }
}

/// <summary>A complex selector, stored right-to-left: Parts[0] is the subject (rightmost compound).</summary>
internal sealed class Selector
{
    public readonly Compound[] Parts;
    public readonly int Specificity;
    public readonly PseudoElement PseudoElement;

    public Selector(Compound[] partsRightToLeft, PseudoElement pseudoElement = PseudoElement.None)
    {
        Parts = partsRightToLeft;
        PseudoElement = pseudoElement;
        int a = 0, b = 0, c = pseudoElement != PseudoElement.None ? 1 : 0;
        foreach (var p in Parts) Count(p, ref a, ref b, ref c);
        Specificity = a * 10000 + b * 100 + c;
    }

    private static void Count(Compound p, ref int a, ref int b, ref int c)
    {
        if (p.Id != null) a++;
        b += (p.Classes?.Length ?? 0) + (p.Attrs?.Length ?? 0);
        if (p.Pseudos != null)
            foreach (var ps in p.Pseudos)
            {
                switch (ps.Kind)
                {
                    case PseudoKind.Where:
                    case PseudoKind.HasAnchor:
                        break; // always zero
                    case PseudoKind.NthChild when ps.List != null:
                    case PseudoKind.NthLastChild when ps.List != null:
                    {
                        // A pseudo-class plus the most specific selector of the "of" list.
                        int max = 0;
                        foreach (var sel in ps.List!) max = Math.Max(max, sel.Specificity);
                        a += max / 10000;
                        b += max / 100 % 100 + 1;
                        c += max % 100;
                        break;
                    }
                    case PseudoKind.Not:
                    case PseudoKind.Is:
                    case PseudoKind.Has:
                    {
                        // The specificity of the most specific selector in the list.
                        int max = 0;
                        foreach (var sel in ps.List!) max = Math.Max(max, sel.Specificity);
                        a += max / 10000;
                        b += max / 100 % 100;
                        c += max % 100;
                        break;
                    }
                    default: b++; break;
                }
            }
        if (p.Tag != null) c++;
    }

    public Compound Subject => Parts[0];

    public bool Matches(Element e)
    {
        if (!Parts[0].Matches(e)) return false;
        return Parts.Length == 1 || MatchFrom(1, e) == MatchResult.Matched;
    }

    // How a failed match tells the combinators to its right whether trying other elements can still help. Without it,
    // descendant and sibling combinators backtrack exponentially ("div div div ... p" over deep markup).
    // This is the scheme browsers use (Servo's selectors crate, WebKit's SelectorChecker).
    private enum MatchResult : byte { Matched, RestartFromLaterSibling, RestartFromDescendant, FailedGlobally }

    // Parts[i-1].Combinator describes how Parts[i-1] relates to Parts[i] (its left neighbour).
    private MatchResult MatchFrom(int i, Element e)
    {
        if (i == Parts.Length) return MatchResult.Matched;
        var part = Parts[i];
        var combinator = Parts[i - 1].Combinator;
        bool sibling = combinator is Combinator.Adjacent or Combinator.Sibling;
        var candidate = sibling ? e.PreviousElementSibling : ParentElement(e);
        while (true)
        {
            // Running out of siblings may still be fixed higher up; running out of ancestors cannot.
            if (candidate == null) return sibling ? MatchResult.RestartFromDescendant : MatchResult.FailedGlobally;
            var result = part.Matches(candidate) ? MatchFrom(i + 1, candidate) : MatchResult.RestartFromLaterSibling;
            if (result is MatchResult.Matched or MatchResult.FailedGlobally || combinator == Combinator.Adjacent) return result;
            if (combinator == Combinator.Child) return MatchResult.RestartFromDescendant;
            if (result == MatchResult.RestartFromDescendant && combinator == Combinator.Sibling) return result;
            candidate = sibling ? candidate.PreviousElementSibling : ParentElement(candidate);
        }
    }

    private static Element? ParentElement(Element e) => e.Parent is { } p && p.Name != "#document" ? p : null;

    // Real selectors stay far below these; the limits keep hostile stylesheets from costing deep recursion. Matching
    // cost is bounded separately, by remembering the results of nested lists (see Compound.BeginMatching).
    private const int MaxParts = 64, MaxNesting = 16;

    /// <summary>Parses one complex selector. Returns null for unsupported syntax, which drops the rule.</summary>
    public static Selector? Parse(string text) => Parse(text, 0);

    private static Selector? Parse(string text, int nesting)
    {
        var parts = new List<Compound>();
        int i = 0;
        text = text.Trim();
        Combinator pending = Combinator.None;
        var pseudoElement = PseudoElement.None;
        while (i < text.Length)
        {
            char ch = text[i];
            if (pseudoElement != PseudoElement.None) return null; // a pseudo-element ends the selector
            if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r')
            {
                i++;
                if (pending == Combinator.None && parts.Count > 0) pending = Combinator.Descendant;
                continue;
            }
            if (ch == '>' || ch == '+' || ch == '~')
            {
                if (parts.Count == 0) return null;
                pending = ch == '>' ? Combinator.Child : ch == '+' ? Combinator.Adjacent : Combinator.Sibling;
                i++;
                continue;
            }
            if (parts.Count > 0 && pending == Combinator.None) return null;
            if (parts.Count == MaxParts) return null;
            var compound = ParseCompound(text, ref i, nesting, ref pseudoElement);
            if (compound == null) return null;
            if (parts.Count > 0) compound.Combinator = pending; // relation to the left neighbour, fixed up below
            parts.Add(compound);
            pending = Combinator.None;
        }
        if (parts.Count == 0 || pending != Combinator.None) return null;

        // Convert to right-to-left with each part's Combinator describing its relation to the next (left) part.
        var rtl = new Compound[parts.Count];
        for (int k = 0; k < parts.Count; k++)
        {
            var c = parts[parts.Count - 1 - k];
            rtl[k] = c;
        }
        // In left-to-right order parts[k].Combinator is the relation between parts[k-1] and parts[k].
        // Right-to-left, rtl[k] needs the relation to rtl[k+1], which is the same value.
        for (int k = 0; k < rtl.Length; k++)
            if (k == rtl.Length - 1) rtl[k].Combinator = Combinator.None;
        return new Selector(rtl, pseudoElement);
    }

    private static Compound? ParseCompound(string s, ref int i, int nesting, ref PseudoElement pseudoElement)
    {
        var c = new Compound();
        List<string>? classes = null;
        List<AttrCondition>? attrs = null;
        List<PseudoCondition>? pseudos = null;
        bool any = false;
        while (i < s.Length)
        {
            char ch = s[i];
            if (ch == '*') { i++; any = true; continue; }
            if (IsIdentStart(ch) && !any && c.Tag == null && c.Id == null && classes == null && attrs == null && pseudos == null)
            {
                c.Tag = ReadIdent(s, ref i).ToLowerInvariant();
                any = true;
                continue;
            }
            if (ch == '#')
            {
                i++;
                c.Id = ReadIdent(s, ref i);
                if (c.Id.Length == 0) return null;
                any = true;
                continue;
            }
            if (ch == '.')
            {
                i++;
                var name = ReadIdent(s, ref i);
                if (name.Length == 0) return null;
                (classes ??= new()).Add(name);
                any = true;
                continue;
            }
            if (ch == '[')
            {
                int end = s.IndexOf(']', i);
                if (end < 0) return null;
                var a = ParseAttr(s.Substring(i + 1, end - i - 1));
                if (a == null) return null;
                (attrs ??= new()).Add(a);
                i = end + 1;
                any = true;
                continue;
            }
            if (ch == ':')
            {
                i++;
                bool doubleColon = i < s.Length && s[i] == ':';
                if (doubleColon) i++;
                var name = ReadIdent(s, ref i).ToLowerInvariant();
                if (doubleColon || name is "before" or "after" or "first-letter" or "first-line")
                {
                    // ::before, ::after, ::first-letter, ::first-line (also with the old single colon) and ::marker;
                    // other pseudo-elements drop the rule.
                    if (nesting > 0 || name is not ("before" or "after" or "marker" or "first-letter" or "first-line" or "footnote-call"
                            or "footnote-marker")) return null;
                    pseudoElement = name switch
                    {
                        "before" => PseudoElement.Before,
                        "after" => PseudoElement.After,
                        "first-letter" => PseudoElement.FirstLetter,
                        "first-line" => PseudoElement.FirstLine,
                        "footnote-call" => PseudoElement.FootnoteCall,
                        "footnote-marker" => PseudoElement.FootnoteMarker,
                        _ => PseudoElement.Marker,
                    };
                    any = true;
                    break;
                }
                string? arg = null;
                if (i < s.Length && s[i] == '(')
                {
                    int depth = 0, start = i + 1;
                    for (; i < s.Length; i++)
                    {
                        if (s[i] == '(') depth++;
                        else if (s[i] == ')' && --depth == 0) break;
                    }
                    if (i >= s.Length) return null;
                    arg = s.Substring(start, i - start).Trim();
                    i++;
                }
                var p = ParsePseudo(name, arg, nesting);
                if (p == null) return null;
                (pseudos ??= new()).Add(p);
                any = true;
                continue;
            }
            break;
        }
        if (!any) return null;
        c.Classes = classes?.ToArray();
        c.Attrs = attrs?.ToArray();
        c.Pseudos = pseudos?.ToArray();
        return c;
    }

    private static PseudoCondition? ParsePseudo(string name, string? arg, int depth)
    {
        switch (name)
        {
            case "first-child": return new PseudoCondition { Kind = PseudoKind.FirstChild };
            case "last-child": return new PseudoCondition { Kind = PseudoKind.LastChild };
            case "only-child": return new PseudoCondition { Kind = PseudoKind.OnlyChild };
            case "root": return new PseudoCondition { Kind = PseudoKind.Root };
            case "link":
            case "any-link": return new PseudoCondition { Kind = PseudoKind.Link };
            case "empty": return new PseudoCondition { Kind = PseudoKind.Empty };
            case "first-of-type": return new PseudoCondition { Kind = PseudoKind.FirstOfType };
            case "last-of-type": return new PseudoCondition { Kind = PseudoKind.LastOfType };
            case "only-of-type": return new PseudoCondition { Kind = PseudoKind.OnlyOfType };
            case "nth-child":
            case "nth-last-child":
            case "nth-of-type":
            case "nth-last-of-type":
            {
                if (arg == null) return null;
                Selector[]? of = null;
                if (name is "nth-child" or "nth-last-child" && OfIndex(arg) is int ofAt and > 0)
                {
                    // :nth-child(An+B of S): the position among the siblings matching S.
                    if (depth >= MaxNesting) return null;
                    var list = new List<Selector>();
                    foreach (var part in CssParser.SplitTopLevel(arg.Substring(ofAt + 4), ','))
                    {
                        var sel = Parse(part, depth + 1);
                        if (sel == null || sel.PseudoElement != PseudoElement.None) return null;
                        list.Add(sel);
                    }
                    if (list.Count == 0) return null;
                    of = list.ToArray();
                    arg = arg.Substring(0, ofAt);
                }
                if (!TryParseNth(arg, out int a, out int b)) return null;
                var kind = name switch
                {
                    "nth-child" => PseudoKind.NthChild,
                    "nth-last-child" => PseudoKind.NthLastChild,
                    "nth-of-type" => PseudoKind.NthOfType,
                    _ => PseudoKind.NthLastOfType,
                };
                return new PseudoCondition { Kind = kind, A = a, B = b, List = of, Memoize = of != null && IsComplex(of) };
            }
            case "has":
            {
                // Relative selectors; :has() may not nest inside another :has().
                if (arg == null || depth >= MaxNesting || _parsingHas) return null;
                var list = new List<Selector>();
                _parsingHas = true;
                try
                {
                    foreach (var part in CssParser.SplitTopLevel(arg, ','))
                    {
                        var rel = ParseRelative(part.Trim(), depth + 1);
                        if (rel == null) return null;
                        list.Add(rel);
                    }
                }
                finally { _parsingHas = false; }
                return list.Count == 0 ? null : new PseudoCondition { Kind = PseudoKind.Has, List = list.ToArray(), Memoize = true };
            }
            case "lang":
            {
                if (string.IsNullOrWhiteSpace(arg)) return null;
                var langs = CssParser.SplitTopLevel(arg, ',').Select(l => l.Trim().Trim('"', '\'')).Where(l => l.Length > 0).ToArray();
                return langs.Length == 0 ? null : new PseudoCondition { Kind = PseudoKind.Lang, Langs = langs };
            }
            case "checked": return new PseudoCondition { Kind = PseudoKind.Checked };
            case "default": return new PseudoCondition { Kind = PseudoKind.Default };
            case "disabled": return new PseudoCondition { Kind = PseudoKind.Disabled };
            case "enabled": return new PseudoCondition { Kind = PseudoKind.Enabled };
            case "required": return new PseudoCondition { Kind = PseudoKind.Required };
            case "optional": return new PseudoCondition { Kind = PseudoKind.Optional };
            case "read-only": return new PseudoCondition { Kind = PseudoKind.ReadOnly };
            case "read-write": return new PseudoCondition { Kind = PseudoKind.ReadWrite };
            case "placeholder-shown": return new PseudoCondition { Kind = PseudoKind.PlaceholderShown };
            case "scope": return new PseudoCondition { Kind = PseudoKind.Root };
            case "defined": return new PseudoCondition { Kind = PseudoKind.Always };
            case "not":
            case "is":
            case "matches":
            case "any":
            case "-webkit-any":
            case "where":
            {
                if (arg == null || depth >= MaxNesting) return null;
                // :is() and :where() take a forgiving list: selectors that do not parse are left out. :not() does not.
                bool forgiving = name != "not";
                var list = new List<Selector>();
                foreach (var part in CssParser.SplitTopLevel(arg, ','))
                {
                    var sel = Parse(part, depth + 1);
                    if (sel == null || sel.PseudoElement != PseudoElement.None)
                    {
                        if (forgiving) continue;
                        return null;
                    }
                    list.Add(sel);
                }
                if (list.Count == 0 && !forgiving) return null;
                var kind = name == "not" ? PseudoKind.Not : name == "where" ? PseudoKind.Where : PseudoKind.Is;
                var selectors = list.ToArray();
                return new PseudoCondition { Kind = kind, List = selectors, Memoize = IsComplex(selectors) };
            }
            case "hover":
            case "active":
            case "focus":
            case "visited":
            case "focus-within":
            case "focus-visible":
            case "target":
            case "target-within":
            case "indeterminate":
            case "fullscreen":
            case "modal":
            case "popover-open":
            case "playing":
            case "paused":
            case "autofill":
            case "user-invalid":
            case "user-valid":
                return new PseudoCondition { Kind = PseudoKind.Never };
            default:
                return null;
        }
    }

    [ThreadStatic] private static bool _parsingHas;

    /// <summary>Whether matching the list can walk the tree: some selector has a combinator or a list of its own.</summary>
    private static bool IsComplex(Selector[] list)
    {
        foreach (var sel in list)
        {
            if (sel.Parts.Length > 1) return true;
            if (sel.Parts[0].Pseudos is { } pseudos)
                foreach (var p in pseudos)
                    if (p.List != null) return true;
        }
        return false;
    }

    /// <summary>The index of " of " in an :nth-child() argument, or -1.</summary>
    private static int OfIndex(string arg)
    {
        for (int i = 0; i + 4 <= arg.Length; i++)
            if (char.IsWhiteSpace(arg[i]) && string.Compare(arg, i + 1, "of", 0, 2, StringComparison.OrdinalIgnoreCase) == 0
                && i + 3 < arg.Length && char.IsWhiteSpace(arg[i + 3])) return i;
        return -1;
    }

    /// <summary>
    /// Parses a relative selector of :has() ("&gt; img", "+ p", "a b") and adds the anchor compound on its left, joined by
    /// the leading combinator (a descendant one when there is none).
    /// </summary>
    private static Selector? ParseRelative(string text, int depth)
    {
        if (text.Length == 0) return null;
        var lead = Combinator.Descendant;
        if (text[0] is '>' or '+' or '~')
        {
            lead = text[0] == '>' ? Combinator.Child : text[0] == '+' ? Combinator.Adjacent : Combinator.Sibling;
            text = text.Substring(1).TrimStart();
        }
        var sel = Parse(text, depth);
        if (sel == null || sel.PseudoElement != PseudoElement.None) return null;
        var parts = new Compound[sel.Parts.Length + 1];
        Array.Copy(sel.Parts, parts, sel.Parts.Length);
        // Parts are shared with nothing else, so the leftmost one can take the leading combinator.
        parts[^2].Combinator = lead;
        parts[^1] = new Compound { Pseudos = new[] { new PseudoCondition { Kind = PseudoKind.HasAnchor } } };
        return new Selector(parts);
    }

    private static bool TryParseNth(string arg, out int a, out int b)
    {
        a = 0; b = 0;
        var s = arg.Replace(" ", "").ToLowerInvariant();
        if (s == "odd") { a = 2; b = 1; return true; }
        if (s == "even") { a = 2; b = 0; return true; }
        int n = s.IndexOf('n');
        if (n < 0) return int.TryParse(s, out b);
        var aPart = s.Substring(0, n);
        a = aPart switch { "" or "+" => 1, "-" => -1, _ => int.TryParse(aPart, out int v) ? v : int.MinValue };
        if (a == int.MinValue) return false;
        var bPart = s.Substring(n + 1);
        if (bPart.Length == 0) return true;
        return int.TryParse(bPart, out b);
    }

    private static AttrCondition? ParseAttr(string body)
    {
        var a = new AttrCondition();
        int opIdx = body.IndexOf('=');
        if (opIdx < 0)
        {
            a.Name = body.Trim().ToLowerInvariant();
            a.Op = AttrOp.Exists;
            return a.Name.Length > 0 ? a : null;
        }
        int nameEnd = opIdx;
        a.Op = AttrOp.Equals;
        if (opIdx > 0)
        {
            switch (body[opIdx - 1])
            {
                case '~': a.Op = AttrOp.Includes; nameEnd--; break;
                case '|': a.Op = AttrOp.DashMatch; nameEnd--; break;
                case '^': a.Op = AttrOp.Prefix; nameEnd--; break;
                case '$': a.Op = AttrOp.Suffix; nameEnd--; break;
                case '*': a.Op = AttrOp.Substring; nameEnd--; break;
            }
        }
        a.Name = body.Substring(0, nameEnd).Trim().ToLowerInvariant();
        var v = body.Substring(opIdx + 1).Trim();
        if (v.EndsWith(" i", StringComparison.OrdinalIgnoreCase) || v.Length > 2 && v[^2] is '"' or '\'' && v[^1] is 'i' or 'I')
        {
            a.IgnoreCase = true;
            v = v.Substring(0, v.Length - 1).TrimEnd();
        }
        else if (v.EndsWith(" s", StringComparison.OrdinalIgnoreCase) || v.Length > 2 && v[^2] is '"' or '\'' && v[^1] is 's' or 'S')
            v = v.Substring(0, v.Length - 1).TrimEnd();
        if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0]) v = v.Substring(1, v.Length - 2);
        a.Value = v;
        return a.Name.Length > 0 ? a : null;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '-' || c > 127;

    private static string ReadIdent(string s, ref int i)
    {
        int start = i;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c > 127) i++;
            else if (c == '\\' && i + 1 < s.Length) i += 2;
            else break;
        }
        return s.Substring(start, i - start).Replace("\\", "");
    }
}
