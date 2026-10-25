using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Html;

/// <summary>
/// The structure tree of a tagged PDF: its elements, such as headings, paragraphs, lists and tables, in reading order,
/// with the marked content of each page they hold. Custom types are mapped to the standard ones through the role map.
/// </summary>
internal sealed class StructureTree
{
    /// <summary>Elements read per document, and how deep they may nest; a tree past these is read only so far.</summary>
    private const int MaxNodes = 200_000, MaxDepth = 64;

    /// <summary>An element of the tree: its standard type, its children in order, and its marked content on each page.</summary>
    internal sealed class Node
    {
        public required string Type;
        /// <summary>Child elements and marked content, in order: a <see cref="Node"/> or a <see cref="Content"/>.</summary>
        public readonly List<object> Kids = new();
        public int RowSpan = 1, ColumnSpan = 1;
        /// <summary>The first and last page the element or its children have content on, or -1 when none.</summary>
        public int FirstPage = -1, LastPage = -1;

        public bool OnPage(int page) => FirstPage >= 0 && page >= FirstPage && page <= LastPage;
    }

    /// <summary>Marked content on a page, by the id its content stream gives it.</summary>
    internal readonly record struct Content(int Page, int Mcid);

    public readonly Node Root = new() { Type = "Document" };

    private readonly PdfFile _file;
    private readonly Dictionary<PdfDict, int> _pages;
    private readonly PdfDict? _roleMap, _classMap;
    private readonly HashSet<PdfDict> _seen = new(ReferenceEqualityComparer.Instance);
    private int _nodes;

    private StructureTree(PdfFile file, PdfDict root, List<RenderDocument.PageInfo> pages)
    {
        _file = file;
        _pages = new Dictionary<PdfDict, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < pages.Count; i++) _pages.TryAdd(pages[i].Dict, i);
        _roleMap = file.GetDict(root, "RoleMap");
        _classMap = file.GetDict(root, "ClassMap");
        AddKids(Root, file.Resolve(root["K"]), -1, 0);
        Span(Root);
    }

    /// <summary>
    /// The structure tree of a document marked as tagged, or null when it has none or its tree holds no marked content.
    /// </summary>
    public static StructureTree? Read(PdfFile file, List<RenderDocument.PageInfo> pages)
    {
        var catalog = file.Catalog;
        if (file.GetDict(catalog, "StructTreeRoot") is not { } root) return null;
        var tree = new StructureTree(file, root, pages);
        return tree.Root.FirstPage >= 0 ? tree : null;
    }

    private void AddKids(Node parent, PdfObject? kids, int page, int depth)
    {
        if (kids is PdfArray array)
        {
            foreach (var kid in array.Items) AddKid(parent, _file.Resolve(kid), page, depth);
        }
        else AddKid(parent, kids, page, depth);
    }

    private void AddKid(Node parent, PdfObject? kid, int page, int depth)
    {
        switch (kid)
        {
            case PdfNumber { IsInteger: true } mcid when page >= 0 && mcid.Value >= 0:
                parent.Kids.Add(new Content(page, mcid.IntValue));
                break;
            case PdfDict dict when _file.GetName(dict, "Type") == "MCR":
                // Marked content in a form XObject (/Stm) has ids of the form's own, which glyphs do not carry.
                if (dict["Stm"] != null) break;
                int on = Page(dict, page);
                if (on >= 0 && _file.Resolve(dict["MCID"]) is PdfNumber { IsInteger: true, Value: >= 0 } id) parent.Kids.Add(new Content(on, id.IntValue));
                break;
            case PdfDict dict when _file.GetName(dict, "Type") == "OBJR":
                break; // an annotation or XObject, whose text is not the page's content
            case PdfDict dict when dict["S"] != null:
                if (depth >= MaxDepth || _nodes >= MaxNodes || !_seen.Add(dict)) break;
                _nodes++;
                var node = new Node { Type = Standard(_file.GetName(dict, "S")) };
                Attributes(node, dict);
                parent.Kids.Add(node);
                AddKids(node, _file.Resolve(dict["K"]), Page(dict, page), depth + 1);
                break;
        }
    }

    /// <summary>The page an element or reference names with /Pg, or the one it inherits.</summary>
    private int Page(PdfDict dict, int inherited)
        => _file.GetDict(dict, "Pg") is { } pg && _pages.TryGetValue(pg, out int index) ? index : inherited;

    /// <summary>A type through the role map to a standard type; at most a few steps, as a map can loop.</summary>
    private string Standard(string? type)
    {
        type ??= "NonStruct";
        for (int i = 0; i < 8 && _file.GetName(_roleMap, type) is { } mapped && mapped != type; i++) type = mapped;
        return type;
    }

    /// <summary>A table cell's row and column spans, from its own attributes or the classes it names.</summary>
    private void Attributes(Node node, PdfDict dict)
    {
        if (node.Type is not ("TH" or "TD")) return;
        var owners = new List<PdfDict>();
        Collect(_file.Resolve(dict["A"]));
        if (_file.Resolve(dict["C"]) is { } classes)
            foreach (var name in classes is PdfArray a ? a.Items : new List<PdfObject> { classes })
                if (_file.Resolve(name) is PdfName n) Collect(_file.Resolve(_classMap?[n.Value]));
        foreach (var a in owners)
        {
            node.RowSpan = Math.Clamp(_file.GetInt(a, "RowSpan", node.RowSpan), 1, 1000);
            node.ColumnSpan = Math.Clamp(_file.GetInt(a, "ColSpan", node.ColumnSpan), 1, 1000);
        }

        void Collect(PdfObject? attributes)
        {
            if (attributes is PdfDict d) owners.Add(d);
            else if (attributes is PdfArray array)
                foreach (var item in array.Items)
                    if (_file.Resolve(item) is PdfDict ad) owners.Add(ad);
        }
    }

    /// <summary>Sets the pages each element has content on, from its children's.</summary>
    private static void Span(Node node)
    {
        // An explicit stack, as a tree may be deep enough to overflow the call stack.
        var stack = new Stack<(Node Node, int Next)>();
        stack.Push((node, 0));
        while (stack.Count > 0)
        {
            var (n, next) = stack.Pop();
            if (next < n.Kids.Count)
            {
                stack.Push((n, next + 1));
                if (n.Kids[next] is Node child) stack.Push((child, 0));
                else if (n.Kids[next] is Content c) Extend(n, c.Page, c.Page);
                continue;
            }
            if (stack.Count > 0 && n.FirstPage >= 0) Extend(stack.Peek().Node, n.FirstPage, n.LastPage);
        }

        static void Extend(Node n, int first, int last)
        {
            n.FirstPage = n.FirstPage < 0 ? first : Math.Min(n.FirstPage, first);
            n.LastPage = Math.Max(n.LastPage, last);
        }
    }
}
