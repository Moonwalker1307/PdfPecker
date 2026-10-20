using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Pdf;

/// <summary>
/// An element of a tagged PDF's logical structure: a paragraph, heading, list, table cell, figure, link and so on.
/// Its kids are, in reading order, child elements, <see cref="ContentSlot"/>s (marked page content) and link annotations.
/// </summary>
internal sealed class StructElem
{
    public readonly string Type;
    public readonly StructElem? Parent;
    public readonly List<object> Kids = new(2); // StructElem, ContentSlot or AnnotRef
    public string? Alt, Lang, Scope;
    public string? ListNumbering; // an L element's numbering, once its items have Lbl elements
    public string? Text;       // a link's text, the description of its annotation
    public string? ElementId;  // the element identifier (/ID) a Note needs
    public int RowSpan = 1, ColSpan = 1;
    public int Id;             // object number, once written
    public bool HasContent;    // marked content or an annotation somewhere below it

    public StructElem(string type, StructElem? parent)
    {
        Type = type;
        Parent = parent;
        parent?.Kids.Add(this);
    }

    /// <summary>The nearest element of the given type, starting with this one.</summary>
    public StructElem? Closest(string type)
    {
        for (var e = this; e != null; e = e.Parent)
            if (e.Type == type) return e;
        return null;
    }
}

/// <summary>
/// A place in a structure element's kids for the page content of one piece of the document (a run of text nodes, an
/// image, a list marker). The writer fills it with the marked-content sequences it writes for that content.
/// </summary>
internal sealed class ContentSlot
{
    public readonly StructElem Parent;
    public readonly List<(McStream Stream, int Mcid)> Refs = new(1);
    public ContentSlot(StructElem parent) { Parent = parent; parent.Kids.Add(this); }
}

/// <summary>A content stream with marked content: a page, or a transparency group drawn on one.</summary>
internal sealed class McStream
{
    public readonly int Page;
    public readonly List<StructElem> Elements = new(); // by marked-content id
    public readonly List<(int Id, StructElem Link)> Annotations = new();
    public int Key = -1;  // the stream's key in the parent tree (a page's is its index)
    public int FormId;    // a transparency group's object number
    public McStream(int page) => Page = page;
}

/// <summary>A link annotation as a kid of its Link element.</summary>
internal sealed class AnnotRef
{
    public readonly int Id, Page;
    public AnnotRef(int id, int page) { Id = id; Page = page; }
}

/// <summary>
/// The logical structure of a document, built from the HTML elements' meaning before layout. Layout tags each text run,
/// image and list marker with the slot of the text node or element it came from; anything without a slot (backgrounds,
/// borders, running headers and footers, decorative images) becomes an artifact.
/// </summary>
internal sealed class StructureTree
{
    public readonly StructElem Root = new("Document", null);
    public string? Language { get; private set; }
    public string? FirstHeading { get; private set; }

    private readonly Dictionary<Node, ContentSlot> _content = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Element, StructElem> _elements = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Element, ContentSlot> _markers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, StructElem> _targets = new(StringComparer.Ordinal);
    private int _lastHeading;

    /// <summary>The slot for a text node, or for an img or svg element.</summary>
    public ContentSlot? Of(Node node) => _content.TryGetValue(node, out var slot) ? slot : null;

    /// <summary>The structure element of an element that has one of its own, such as a link.</summary>
    public StructElem? ElementOf(Element e) => _elements.TryGetValue(e, out var se) ? se : null;

    /// <summary>
    /// The structure element a link to <c>#<paramref name="id"/></c> goes to: the element with that id (or name, for an
    /// <c>a</c>) when it has one of its own, else the element that holds it.
    /// </summary>
    public StructElem? TargetOf(string id) => _targets.TryGetValue(id, out var se) ? se : null;

    /// <summary>The Lbl slot of a list item's marker.</summary>
    public ContentSlot? MarkerOf(Element li) => _markers.TryGetValue(li, out var slot) ? slot : null;

    public static StructureTree Build(Element document)
    {
        var tree = new StructureTree();
        var html = document.FindFirst("html");
        var lang = html?.GetAttribute("lang") ?? html?.GetAttribute("xml:lang");
        if (!string.IsNullOrWhiteSpace(lang)) tree.Language = lang!.Trim();
        tree.VisitChildren(document, tree.Root);
        return tree;
    }

    /// <summary>Structure types that can hold text directly; text in any other gets an anonymous paragraph.</summary>
    private static bool HoldsText(string type) => type is "P" or "H1" or "H2" or "H3" or "H4" or "H5" or "H6" or "Lbl" or "LBody"
        or "TD" or "TH" or "Caption" or "Link" or "Code" or "Quote" or "Span" or "Note";

    private static bool IsInline(ComputedStyle s)
        => !s.Floated && !s.OutOfFlow && (s.Display == Display.Inline || s.IsAtomicInline);

    private void VisitChildren(Element e, StructElem container)
    {
        bool holdsText = HoldsText(container.Type);
        StructElem? anonymous = null; // the paragraph of the current run of inline content
        foreach (var child in e.Children)
        {
            if (child is TextNode t)
            {
                bool blank = string.IsNullOrWhiteSpace(t.Text);
                StructElem? target = holdsText ? container : anonymous;
                if (target == null && !blank) target = anonymous = new StructElem("P", container);
                // Whitespace between blocks stays an artifact; inside a paragraph it belongs to the text.
                if (target != null) _content[t] = SlotIn(target);
                continue;
            }
            if (child is Element { FootnoteIndex: >= 0 } footnote && footnote.Style is { Display: not Display.None })
            {
                // A footnote is out of the flow but read where its call is: a Note in the text, which the call links to.
                var holder = holdsText ? container : anonymous ??= new StructElem("P", container);
                var note = new StructElem("Note", holder)
                {
                    Lang = LangOf(footnote),
                    ElementId = "fn" + (footnote.FootnoteIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                _elements[footnote] = note;
                _targets[Layout.FootnoteSetup.BodyKey(footnote.FootnoteIndex)] = note;
                if (footnote.Id != null) _targets[footnote.Id] = note;
                VisitChildren(footnote, note);
                continue;
            }
            if (child is not Element c || Skipped(c)) continue;
            if (IsInline(c.Style))
            {
                var target = holdsText ? container : anonymous ??= new StructElem("P", container);
                VisitElement(c, target);
            }
            else
            {
                anonymous = null;
                VisitElement(c, container);
            }
        }
    }

    /// <summary>The slot at the end of <paramref name="se"/>'s kids, so adjacent text nodes share one.</summary>
    private static ContentSlot SlotIn(StructElem se) => se.Kids.Count > 0 && se.Kids[^1] is ContentSlot last ? last : new ContentSlot(se);

    private static bool Skipped(Element c)
        => c.Style == null || c.Style.Display == Display.None || c.Detached
           // Fixed boxes repeat on every page, like running headers: they are artifacts.
           || c.Style.Position == Position.Fixed
           || string.Equals(c.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase)
           || c.Name is "head" or "script" or "style" or "template" or "title";

    private void VisitElement(Element e, StructElem parent)
    {
        // Link targets: the element itself when it gets a structure element (set below), else the one that holds it.
        if (e.Id != null) _targets[e.Id] = parent;
        if (e.Name == "a" && e.GetAttribute("name") is { Length: > 0 } name) _targets[name] = parent;
        switch (e.Name)
        {
            // An image that shows its alt text as text is no figure: the text is content of its parent.
            case "img" when e.AltText != null:
                break;
            case "img":
            case "svg":
            {
                // Images without a text alternative are decorative (alt="" says so explicitly) and become artifacts.
                var alt = AltText(e);
                if (string.IsNullOrWhiteSpace(alt)) return;
                var figure = new StructElem("Figure", parent) { Alt = alt!.Trim(), Lang = LangOf(e) };
                _content[e] = new ContentSlot(figure);
                return;
            }
            case "br":
            case "hr":
                return;
            case "input":
            case "select":
            case "textarea":
                // A fillable field is a Form element holding its widget; its value is drawn by the widget, not the page.
                if (e.Control?.Field is { } field)
                {
                    _elements[e] = new StructElem("Form", parent) { Alt = field.Tooltip ?? field.Name, Lang = LangOf(e) };
                    return;
                }
                break;
        }

        var type = TypeOf(e);
        var lang = LangOf(e);
        if (type == null && lang != null) type = IsInline(e.Style) ? "Span" : "Div"; // to carry the language
        if (type == null)
        {
            VisitChildren(e, parent);
            return;
        }
        var se = new StructElem(type, parent) { Lang = lang };
        _elements[e] = se;
        if (e.Id != null) _targets[e.Id] = se;
        if (e.Name == "a" && e.GetAttribute("name") is { Length: > 0 } anchorName) _targets[anchorName] = se;
        if (type == "Link") se.Text = System.Text.RegularExpressions.Regex.Replace(e.InnerText(), @"\s+", " ").Trim();
        if (e.FootnoteBody != null) se.Text = ("Footnote " + se.Text).Trim();
        if (type[0] == 'H' && type.Length == 2 && FirstHeading == null)
        {
            var text = System.Text.RegularExpressions.Regex.Replace(e.InnerText(), @"\s+", " ").Trim();
            if (text.Length > 0) FirstHeading = text;
        }
        switch (type)
        {
            case "LI":
                if (e.Style.Display == Display.ListItem && (e.Style.ListStyleType != ListStyleType.None || e.Style.ListStyleImage != null))
                {
                    _markers[e] = new ContentSlot(new StructElem("Lbl", se));
                    // A list with labels says how they number its items (PDF/UA-2), as its first marked item draws them.
                    if (parent.Type == "L") parent.ListNumbering ??= Numbering(e.Style.ListStyleType);
                }
                VisitChildren(e, new StructElem("LBody", se));
                return;
            case "TH":
            case "TD":
                if (Span(e, "rowspan") is int rs) se.RowSpan = rs;
                if (Span(e, "colspan") is int cs) se.ColSpan = cs;
                if (type == "TH") se.Scope = HeaderScope(e);
                break;
        }
        VisitChildren(e, se);
    }

    /// <summary>The ListNumbering attribute value for a list marker style.</summary>
    private static string Numbering(ListStyleType type) => type switch
    {
        ListStyleType.Circle => "Circle",
        ListStyleType.Square => "Square",
        ListStyleType.Decimal or ListStyleType.DecimalLeadingZero => "Decimal",
        ListStyleType.LowerAlpha => "LowerAlpha",
        ListStyleType.UpperAlpha => "UpperAlpha",
        ListStyleType.LowerRoman => "LowerRoman",
        ListStyleType.UpperRoman => "UpperRoman",
        // Other counter styles have no PDF name: the labels are in the Lbl elements.
        ListStyleType.Other => "None",
        _ => "Disc",
    };

    private string? TypeOf(Element e)
    {
        switch (e.Name)
        {
            case "p": return "P";
            case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
            {
                // Heading levels may not skip one on the way down (PDF/UA), so h1 then h3 is tagged H1, H2.
                int level = Math.Min(e.Name[1] - '0', _lastHeading + 1);
                _lastHeading = level;
                return "H" + level;
            }
            case "ul": case "ol": return "L";
            case "li": return "LI";
            case "table": return "Table";
            case "thead": return "THead";
            case "tbody": return "TBody";
            case "tfoot": return "TFoot";
            case "tr": return "TR";
            case "th": return "TH";
            case "td": return "TD";
            case "caption": case "figcaption": return "Caption";
            // A figure with a caption groups the two, so the caption is its group's first or last child.
            case "figure": return e.Children.Any(c => c is Element { Name: "figcaption" }) ? "Sect" : null;
            case "a": return e.GetAttribute("href") is { Length: > 0 } ? "Link" : null;
            case "::footnote-call": return e.FootnoteBody != null ? "Link" : null;
            case "blockquote": return "BlockQuote";
            case "q": return "Quote";
            case "code": case "kbd": case "samp": return "Code";
            case "pre": return "P";
            case "article": return "Art";
            case "section": case "main": case "nav": case "aside": case "header": case "footer": return "Sect";
            default: return null;
        }
    }

    private string? LangOf(Element e)
    {
        var lang = e.GetAttribute("lang");
        if (string.IsNullOrWhiteSpace(lang) || e.Name == "html") return null;
        lang = lang!.Trim();
        // Only a change of language needs saying.
        for (var p = e.Parent; p != null; p = p.Parent)
            if (p.GetAttribute("lang") is { } outer && !string.IsNullOrWhiteSpace(outer))
                return string.Equals(outer.Trim(), lang, StringComparison.OrdinalIgnoreCase) ? null : lang;
        return string.Equals(Language, lang, StringComparison.OrdinalIgnoreCase) ? null : lang;
    }

    private static string? AltText(Element e)
    {
        if (e.GetAttribute("alt") is { } alt) return alt; // alt="" marks a decorative image
        if (e.GetAttribute("aria-label") is { } label) return label;
        if (e.Name == "svg")
            foreach (var c in e.Children)
                if (c is Element { Name: "title" } title) return title.InnerText();
        return e.GetAttribute("title");
    }

    private static int? Span(Element e, string attribute)
        => int.TryParse(e.GetAttribute(attribute), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n)
           && n > 1 ? Math.Min(n, 1000) : null;

    /// <summary>A header cell heads its column in the table head or in a row of header cells, else its row.</summary>
    private static string HeaderScope(Element th)
    {
        var scope = th.GetAttribute("scope")?.Trim().ToLowerInvariant();
        if (scope is "col" or "colgroup") return "Column";
        if (scope is "row" or "rowgroup") return "Row";
        var row = th.Parent;
        if (row?.Parent?.Name == "thead") return "Column";
        if (row != null && row.Children.OfType<Element>().All(c => c.Name is "th" or "::before" or "::after")) return "Column";
        return "Row";
    }

    /// <summary>
    /// Marks the elements that ended up with content, so empty ones (a paragraph of whitespace, a figure whose image did
    /// not load) are left out. Table cells and rows stay, so rows keep their columns.
    /// </summary>
    public void Prune()
    {
        Mark(Root);
        Remove(Root);

        static bool Mark(StructElem e)
        {
            bool any = false;
            foreach (var k in e.Kids)
                any |= k switch
                {
                    StructElem c => Mark(c),
                    ContentSlot s => s.Refs.Count > 0,
                    _ => true,
                };
            return e.HasContent = any;
        }

        static void Remove(StructElem e)
        {
            bool table = e.Type == "Table";
            e.Kids.RemoveAll(k => k switch
            {
                StructElem c => !c.HasContent && !(table || InTable(c)),
                ContentSlot s => s.Refs.Count == 0,
                _ => false,
            });
            foreach (var k in e.Kids)
                if (k is StructElem c) Remove(c);
        }

        static bool InTable(StructElem e) => e.Type is "THead" or "TBody" or "TFoot" or "TR" or "TH" or "TD";
    }
}
