namespace PdfPecker.Rendering.Parsing;

/// <summary>
/// Reads a PDF's page tree for every part of the library that needs its pages: the renderer, the merger, the stamper,
/// the linearizer, the form editor and the signature writers.
/// </summary>
/// <remarks>
/// Page trees in damaged and hostile files loop back on themselves, list a node twice or nest without end, and each copy
/// of this walk used to need its own guard against that. Here a node is read at most once and no deeper than
/// <see cref="MaxDepth"/>, so a walk takes time in proportion to the nodes the file has. A node with /Kids is an
/// intermediate node unless it says it is a /Page; every other dictionary is a page.
/// </remarks>
internal static class PageTree
{
    /// <summary>The deepest node read, counting the root as 0; real files nest a handful of levels.</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// Handles a page: <paramref name="node"/> is the object the tree names it by (a reference, or the dictionary itself
    /// when it is direct), <paramref name="page"/> its dictionary and <paramref name="inherited"/> what it inherits.
    /// Returns false to end the walk.
    /// </summary>
    public delegate bool PageVisitor<T>(PdfObject node, PdfDict page, T inherited);

    /// <summary>
    /// The inheritable page attributes (ISO 32000-2, 7.7.3.4) as written, unresolved: each from the page or its nearest
    /// ancestor that has the entry.
    /// </summary>
    public readonly record struct Inherited(PdfObject? Resources, PdfObject? MediaBox, PdfObject? CropBox, PdfObject? Rotate)
    {
        /// <summary>The attributes of <paramref name="node"/>, a child of the node that has these.</summary>
        public Inherited Below(PdfDict node)
            => new(node["Resources"] ?? Resources, node["MediaBox"] ?? MediaBox, node["CropBox"] ?? CropBox, node["Rotate"] ?? Rotate);
    }

    /// <summary>
    /// Walks the tree from <paramref name="root"/>, handing each page to <paramref name="page"/> with its
    /// <see cref="Inherited"/> attributes and each intermediate node, before its kids, to <paramref name="node"/>.
    /// </summary>
    public static void Walk(PdfFile file, PdfObject? root, PageVisitor<Inherited> page, Action<PdfObject, PdfDict>? node = null)
        => Walk(file, root, default, static (d, parent) => parent.Below(d), page, node);

    /// <summary>
    /// Walks the tree from <paramref name="root"/>, handing each page to <paramref name="page"/>. With
    /// <paramref name="referencesOnly"/>, nodes that are not indirect references are skipped.
    /// </summary>
    public static void Walk(PdfFile file, PdfObject? root, Func<PdfObject, PdfDict, bool> page, bool referencesOnly = false,
        int maxNodes = int.MaxValue)
        => Walk(file, root, false, static (_, s) => s, (n, d, _) => page(n, d), null, referencesOnly, maxNodes);

    /// <summary>
    /// Walks the tree from <paramref name="root"/>, working out what each node inherits with <paramref name="inherit"/>
    /// from its dictionary and what its parent has, starting from <paramref name="inherited"/>, and handing each page to
    /// <paramref name="page"/> and each intermediate node, before its kids, to <paramref name="node"/>.
    /// </summary>
    /// <remarks>
    /// With <paramref name="referencesOnly"/> the walk follows only indirect references, as the specification requires
    /// of /Pages and /Kids, and tells nodes apart by object number; otherwise it tells them apart by dictionary. It ends
    /// once <paramref name="maxNodes"/> distinct nodes have been entered.
    /// </remarks>
    public static void Walk<T>(PdfFile file, PdfObject? root, T inherited, Func<PdfDict, T, T> inherit, PageVisitor<T> page,
        Action<PdfObject, PdfDict>? node = null, bool referencesOnly = false, int maxNodes = int.MaxValue)
        => new Walker<T>(file, inherit, page, node, referencesOnly, maxNodes).Visit(root, inherited, 0);

    private sealed class Walker<T>
    {
        private readonly PdfFile _file;
        private readonly Func<PdfDict, T, T> _inherit;
        private readonly PageVisitor<T> _page;
        private readonly Action<PdfObject, PdfDict>? _node;
        private readonly int _maxNodes;
        private readonly HashSet<PdfDict>? _seenDicts;
        private readonly HashSet<int>? _seenNums;

        public Walker(PdfFile file, Func<PdfDict, T, T> inherit, PageVisitor<T> page, Action<PdfObject, PdfDict>? node,
            bool referencesOnly, int maxNodes)
        {
            _file = file;
            _inherit = inherit;
            _page = page;
            _node = node;
            _maxNodes = maxNodes;
            if (referencesOnly) _seenNums = new HashSet<int>();
            else _seenDicts = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        }

        private int Seen => _seenNums?.Count ?? _seenDicts!.Count;

        /// <summary>Reads one node and what is below it; false once the walk is to end.</summary>
        /// <remarks>Recursion is safe here: <see cref="MaxDepth"/> bounds it.</remarks>
        public bool Visit(PdfObject? item, T parent, int depth)
        {
            if (Seen >= _maxNodes) return false;
            if (depth > MaxDepth || item == null) return true;
            PdfDict dict;
            if (_seenNums != null)
            {
                // A reference is marked before it is resolved, so one that leads nowhere is not resolved twice.
                if (item is not PdfRef r || !_seenNums.Add(r.Num) || _file.Resolve(r) is not PdfDict d) return true;
                dict = d;
            }
            else
            {
                if (_file.Resolve(item) is not PdfDict d || !_seenDicts!.Add(d)) return true;
                dict = d;
            }
            PdfFile.CheckCancel();
            var here = _inherit(dict, parent);
            if (_file.GetArray(dict, "Kids") is { } kids && _file.GetName(dict, "Type") != "Page")
            {
                _node?.Invoke(item, dict);
                foreach (var kid in kids.Items)
                    if (!Visit(kid, here, depth + 1)) return false;
                return true;
            }
            return _page(item, dict, here);
        }
    }
}
