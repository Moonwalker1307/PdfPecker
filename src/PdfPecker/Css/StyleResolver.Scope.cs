using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>
/// The rules of @scope blocks. A rule applies to an element when one of the element's ancestors (or the element itself)
/// is a scope root, no scope limit lies between them, and the selector matches with :scope standing for that root. The
/// cascade prefers the rule whose root is closest (CSS Cascade 6, scope proximity), as Chrome does.
/// </summary>
internal sealed partial class StyleResolver
{
    /// <summary>The proximities beyond this many generations rank the same.</summary>
    private const int MaxScopeCloseness = 15;

    /// <summary>
    /// Scope roots and limits are looked for at most this many generations up, so a hostile document of deeply nested
    /// elements and scopes cannot make matching quadratic in its depth.
    /// </summary>
    internal const int MaxScopeWalk = 128;

    // Whether an element is a root of a scope, remembered for one style pass: every rule of a block asks the same.
    private readonly Dictionary<(ScopeRule, Element), bool> _scopeRoots = new();

    /// <summary>How close a scope root of <paramref name="hops"/> generations up ranks: 1 to 15, more for closer.</summary>
    private static int ScopeCloseness(int hops) => MaxScopeCloseness - Math.Min(hops, MaxScopeCloseness - 1);

    /// <summary>
    /// The generations from the element up to the closest scope root for which the selector matches it, or null when
    /// it matches for none.
    /// </summary>
    private int? ScopeHops(ScopeRule scope, Selector selector, Element e)
    {
        var saved = Compound._scopeRoot;
        try
        {
            int hops = 0;
            for (var root = e; root != null && hops < MaxScopeWalk; root = ParentElement(root), hops++)
            {
                if (!IsScopeRoot(scope, root) || Limited(scope, e, root)) continue;
                Compound._scopeRoot = root;
                if (selector.Matches(e)) return hops;
            }
            return null;
        }
        finally { Compound._scopeRoot = saved; }
    }

    /// <summary>Whether an element is a root of the scope: it matches the start, and lies in the scope of an outer block.</summary>
    private bool IsScopeRoot(ScopeRule scope, Element root)
    {
        if (_scopeRoots.TryGetValue((scope, root), out bool known)) return known;
        bool result = scope.Start != null ? StartMatches(scope, root)
            : scope.ImplicitRoot != null ? ReferenceEquals(root, scope.ImplicitRoot)
            : ParentElement(root) == null;
        if (result && scope.Parent != null && scope.Start == null) result = InScope(scope.Parent, root);
        _scopeRoots[(scope, root)] = result;
        return result;
    }

    // The start of a nested block is relative to the outer block's root: its :scope is that root.
    private bool StartMatches(ScopeRule scope, Element root)
    {
        if (scope.Parent == null) return AnyMatches(scope.Start!, root, null);
        int hops = 0;
        for (var outer = root; outer != null && hops < MaxScopeWalk; outer = ParentElement(outer), hops++)
            if (IsScopeRoot(scope.Parent, outer) && !Limited(scope.Parent, root, outer) && AnyMatches(scope.Start!, root, outer)) return true;
        return false;
    }

    /// <summary>Whether an element is in the scope of a block: at or below a root, and not at or below a limit under it.</summary>
    private bool InScope(ScopeRule scope, Element e)
    {
        int hops = 0;
        for (var root = e; root != null && hops < MaxScopeWalk; root = ParentElement(root), hops++)
            if (IsScopeRoot(scope, root) && !Limited(scope, e, root)) return true;
        return false;
    }

    /// <summary>Whether an element at or below <paramref name="e"/> up to (not including) the root matches the scope's end.</summary>
    private static bool Limited(ScopeRule scope, Element e, Element root)
    {
        if (scope.End == null) return false;
        for (var x = e; x != null && !ReferenceEquals(x, root); x = ParentElement(x))
            if (AnyMatches(scope.End, x, root)) return true;
        return false;
    }

    private static bool AnyMatches(Selector[] list, Element e, Element? scopeRoot)
    {
        var saved = Compound._scopeRoot;
        Compound._scopeRoot = scopeRoot;
        try
        {
            foreach (var s in list)
                if (s.Matches(e)) return true;
            return false;
        }
        finally { Compound._scopeRoot = saved; }
    }

    private static Element? ParentElement(Element e) => e.Parent is { } p && p.Name != "#document" ? p : null;
}
