using PdfPecker.Html;

namespace PdfPecker.Css;

internal enum Combinator : byte { None, Descendant, Child, Adjacent, Sibling }

internal enum AttrOp : byte { Exists, Equals, Includes, DashMatch, Prefix, Suffix, Substring }

internal enum PseudoKind : byte { FirstChild, LastChild, OnlyChild, NthChild, NthLastChild, Root, Link, Empty, Never, Not }

internal sealed class AttrCondition
{
    public string Name = "";
    public AttrOp Op;
    public string Value = "";
}

internal sealed class PseudoCondition
{
    public PseudoKind Kind;
    public int A, B; // an+b for nth-child
    public Compound? Inner; // for :not()
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

    private static bool MatchPseudo(Element e, PseudoCondition p)
    {
        switch (p.Kind)
        {
            case PseudoKind.FirstChild: return e.ElementIndex == 1;
            case PseudoKind.LastChild: return e.ElementIndex == e.ElementSiblingCount;
            case PseudoKind.OnlyChild: return e.ElementSiblingCount == 1;
            case PseudoKind.NthChild: return MatchNth(p.A, p.B, e.ElementIndex);
            case PseudoKind.NthLastChild: return MatchNth(p.A, p.B, e.ElementSiblingCount - e.ElementIndex + 1);
            case PseudoKind.Root: return e.Name == "html" || e.Parent?.Name == "#document" && e.Parent.Children.OfType<Element>().Count() == 1;
            case PseudoKind.Link: return e.Name == "a" && e.HasAttribute("href");
            case PseudoKind.Empty: return e.Children.Count == 0;
            case PseudoKind.Not: return p.Inner != null && !p.Inner.Matches(e);
            default: return false;
        }
    }

    private static bool MatchNth(int a, int b, int index)
    {
        if (a == 0) return index == b;
        int n = index - b;
        return (a > 0 ? n >= 0 : n <= 0) && n % a == 0;
    }
}

/// <summary>A complex selector, stored right-to-left: Parts[0] is the subject (rightmost compound).</summary>
internal sealed class Selector
{
    public readonly Compound[] Parts;
    public readonly int Specificity;

    public Selector(Compound[] partsRightToLeft)
    {
        Parts = partsRightToLeft;
        int a = 0, b = 0, c = 0;
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
                if (ps.Kind == PseudoKind.Not && ps.Inner != null) Count(ps.Inner, ref a, ref b, ref c);
                else b++;
            }
        if (p.Tag != null) c++;
    }

    public Compound Subject => Parts[0];

    public bool Matches(Element e)
    {
        if (!Parts[0].Matches(e)) return false;
        return Parts.Length == 1 || MatchFrom(1, e);
    }

    // Parts[i-1].Combinator describes how Parts[i-1] relates to Parts[i] (its left neighbour).
    private bool MatchFrom(int i, Element e)
    {
        if (i == Parts.Length) return true;
        var part = Parts[i];
        switch (Parts[i - 1].Combinator)
        {
            case Combinator.Child:
            {
                var parent = e.Parent;
                return parent != null && parent.Name != "#document" && part.Matches(parent) && MatchFrom(i + 1, parent);
            }
            case Combinator.Descendant:
            {
                for (var anc = e.Parent; anc != null && anc.Name != "#document"; anc = anc.Parent)
                    if (part.Matches(anc) && MatchFrom(i + 1, anc)) return true;
                return false;
            }
            case Combinator.Adjacent:
            {
                var prev = e.PreviousElementSibling;
                return prev != null && part.Matches(prev) && MatchFrom(i + 1, prev);
            }
            case Combinator.Sibling:
            {
                for (var prev = e.PreviousElementSibling; prev != null; prev = prev.PreviousElementSibling)
                    if (part.Matches(prev) && MatchFrom(i + 1, prev)) return true;
                return false;
            }
        }
        return false;
    }

    /// <summary>Parses one complex selector. Returns null for unsupported syntax, which drops the rule.</summary>
    public static Selector? Parse(string text)
    {
        var parts = new List<Compound>();
        int i = 0;
        text = text.Trim();
        Combinator pending = Combinator.None;
        while (i < text.Length)
        {
            char ch = text[i];
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
            var compound = ParseCompound(text, ref i);
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
        return new Selector(rtl);
    }

    private static Compound? ParseCompound(string s, ref int i)
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
                if (i < s.Length && s[i] == ':') return null; // pseudo-elements are not supported
                var name = ReadIdent(s, ref i).ToLowerInvariant();
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
                var p = ParsePseudo(name, arg);
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

    private static PseudoCondition? ParsePseudo(string name, string? arg)
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
            case "nth-child":
            case "nth-last-child":
            {
                if (arg == null || !TryParseNth(arg, out int a, out int b)) return null;
                return new PseudoCondition { Kind = name == "nth-child" ? PseudoKind.NthChild : PseudoKind.NthLastChild, A = a, B = b };
            }
            case "not":
            {
                if (arg == null) return null;
                int j = 0;
                var inner = ParseCompound(arg, ref j);
                if (inner == null || j != arg.Length) return null;
                return new PseudoCondition { Kind = PseudoKind.Not, Inner = inner };
            }
            case "hover":
            case "active":
            case "focus":
            case "visited":
            case "focus-within":
            case "focus-visible":
            case "checked":
            case "disabled":
                return new PseudoCondition { Kind = PseudoKind.Never };
            default:
                return null;
        }
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
        if (v.EndsWith(" i", StringComparison.OrdinalIgnoreCase)) v = v.Substring(0, v.Length - 2).TrimEnd();
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
