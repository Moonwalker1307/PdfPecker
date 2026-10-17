namespace PdfPecker.Css;

/// <summary>One declaration of a rule, after shorthand expansion: a longhand property, its value and its importance.</summary>
internal sealed class Declaration
{
    public readonly string Property;
    public readonly string Value;
    public readonly bool Important;

    public Declaration(string property, string value, bool important)
    {
        Property = property;
        Value = value;
        Important = important;
    }

    public override string ToString() => Property + ": " + Value + (Important ? " !important" : "");
}

/// <summary>A style rule: one selector with its declarations, its place in source order and the blocks around it.</summary>
internal sealed class StyleRule
{
    public readonly Selector Selector;
    public readonly Declaration[] Declarations;
    public readonly int Order;
    public readonly CascadeLayer? Layer; // null for rules outside any @layer
    /// <summary>The conditions of the @container blocks around the rule, outermost first; null outside any.</summary>
    public readonly ContainerQuery[]? Containers;

    public StyleRule(Selector selector, Declaration[] declarations, int order, CascadeLayer? layer = null, ContainerQuery[]? containers = null)
    {
        Selector = selector;
        Declarations = declarations;
        Order = order;
        Layer = layer;
        Containers = containers;
    }
}

/// <summary>
/// A cascade layer (<c>@layer</c>). Layers are ordered by their first appearance; a layer's sub-layers come before the
/// layer's own rules, and rules outside every layer come last, so they win over layered ones.
/// </summary>
internal sealed class CascadeLayer
{
    /// <summary>
    /// A layer takes no more sub-layers than this, so a sheet cannot grow the tree without bound; a sub-layer beyond
    /// the limit still gets its rules, but has no place in the order.
    /// </summary>
    private const int MaxChildren = 1000;

    public readonly string Name;
    public readonly List<CascadeLayer> Children = new();
    public int Rank; // 0 = the earliest layer; set by Stylesheet.RankLayers

    public CascadeLayer(string name) => Name = name;

    /// <summary>The sub-layer of this name, created on first use.</summary>
    public CascadeLayer Child(string name)
    {
        foreach (var child in Children)
            if (child.Name == name) return child;
        var layer = new CascadeLayer(name);
        if (Children.Count < MaxChildren) Children.Add(layer);
        return layer;
    }
}

/// <summary>An <c>@page</c> rule: its page selector, declarations and margin boxes (<c>@top-center</c> and so on).</summary>
internal sealed class PageRule
{
    public string Name = "";     // the page name of a named page rule (@page wide), or ""
    public string Selector = ""; // "", ":first", ":left", ":right" or ":blank"
    public readonly List<Declaration> Declarations = new();
    public readonly Dictionary<string, List<Declaration>> MarginBoxes = new(StringComparer.Ordinal);
    /// <summary>The declarations of the rule's <c>@footnote</c> area, or null when it has none.</summary>
    public List<Declaration>? Footnote;
    public int Specificity => (Name.Length > 0 ? 4 : 0) + Selector switch { ":first" or ":blank" => 2, ":left" or ":right" => 1, _ => 0 };
}

/// <summary>A custom property registered with <c>@property</c>.</summary>
internal sealed record RegisteredProperty(string Name, string? InitialValue, bool Inherits);

/// <summary>The rules of a document's stylesheets, in the order they were parsed.</summary>
internal sealed class Stylesheet
{
    public readonly List<StyleRule> Rules = new();
    public readonly List<Declaration> PageDeclarations = new(); // from @page rules without a page selector
    public readonly List<PageRule> PageRules = new();
    public readonly List<List<Declaration>> FontFaces = new(); // @font-face descriptors, one list per rule
    public readonly List<RegisteredProperty> RegisteredProperties = new();
    public readonly List<CounterStyleRule> CounterStyles = new(); // @counter-style rules, in source order
    public readonly CascadeLayer LayerRoot = new("");
    public MediaContext Media = new();
    public int NextOrder;

    /// <summary>Some rule is inside an @container block.</summary>
    public bool HasContainerRules;

    private int _unitsCheckedAt = -1;
    private bool _containerUnits;

    /// <summary>Some declaration uses cq units (checked again only when rules were added since).</summary>
    public bool HasContainerUnits
    {
        get
        {
            if (_unitsCheckedAt != Rules.Count)
            {
                _containerUnits = Rules.Exists(r => Array.Exists(r.Declarations, d => ContainerQuery.HasContainerUnit(d.Value)));
                _unitsCheckedAt = Rules.Count;
            }
            return _containerUnits;
        }
    }

    /// <summary>
    /// Loads the stylesheet an <c>@import</c> names: the URL as written, and for a URL given as a plain string the
    /// importing sheet's own href (null for a style block), as url() references of a linked sheet are already made
    /// relative to the document. Returns the sheet's text with its url() references rebased, and its href; null when
    /// it cannot be loaded. Without a loader, @import is ignored.
    /// </summary>
    public Func<string, string?, (string Css, string Href)?>? ImportLoader;

    /// <summary>The href of the sheet being parsed, for the @import rules in it.</summary>
    internal string? CurrentHref;
    internal readonly List<string> ImportChain = new(); // hrefs of the sheets being imported, against import cycles
    internal int ImportCount;
    private int _anonymousLayers;
    private bool _ranked;

    internal string AnonymousLayerName() => "\0anon" + _anonymousLayers++;

    /// <summary>Numbers the layers in cascade order (sub-layers before their parent) and returns how many there are.</summary>
    public int RankLayers()
    {
        int next = 0;
        void Visit(CascadeLayer layer)
        {
            foreach (var c in layer.Children) Visit(c);
            if (layer != LayerRoot) layer.Rank = Math.Min(next++, MaxLayerRank);
        }
        Visit(LayerRoot);
        _ranked = true;
        return next;
    }

    /// <summary>Ranks above this share the last rank: far more layers than any stylesheet uses.</summary>
    public const int MaxLayerRank = 250;

    public bool LayersRanked => _ranked;
}
