using PdfPecker.Css;

namespace PdfPecker.Html;

/// <summary>Base class for nodes in the parsed HTML tree.</summary>
internal abstract class Node
{
    public Element? Parent;
}

internal sealed class TextNode : Node
{
    public string Text;
    public TextNode(string text) => Text = text;
}

internal sealed class Element : Node
{
    public readonly string Name;
    private static readonly List<KeyValuePair<string, string>> NoAttributes = new(0);
    private List<KeyValuePair<string, string>>? _attributes;

    /// <summary>The attributes in source order; add them with <see cref="AddAttribute"/>.</summary>
    public List<KeyValuePair<string, string>> Attributes => _attributes ?? NoAttributes;
    public readonly List<Node> Children = new();

    // Filled in before styling.
    public string? Id;
    public string[] Classes = Array.Empty<string>();
    public int ElementIndex;        // 1-based position among element siblings
    public int ElementSiblingCount; // number of element siblings including itself
    public Element? PreviousElementSibling;
    public int TypeIndex;           // 1-based position among element siblings with the same name
    public int TypeCount;           // number of element siblings with the same name, including itself

    // Filled in by the style resolver.
    public ComputedStyle Style = null!;

    /// <summary>Taken out of the normal flow, e.g. a running page header or footer that is drawn on every page.</summary>
    public bool Detached;

    /// <summary>
    /// A body the parser made because the document has no body tag. It has no default margin, so that a fragment keeps
    /// the layout it had before the parser made the element (browsers would add 8px).
    /// </summary>
    public bool ImpliedBody;

    /// <summary>A ::before or ::after box made by the style resolver, not by the parser.</summary>
    public bool Generated;

    /// <summary>
    /// An anonymous table, row or cell box the style resolver wrapped around table parts that miss one (CSS 2.1 §17.2.1),
    /// such as a lone display: table-cell. Selectors never see it: it is taken out again before the next style pass.
    /// </summary>
    public bool Anonymous;

    /// <summary>
    /// The children of this element as the parser made them, while the style resolver has put the children of its
    /// display: contents children in their place. The next style pass restores them.
    /// </summary>
    public List<Node>? DomChildren;

    /// <summary>The link of the display: contents <c>a</c> element this element was taken out of, which it still links to.</summary>
    public string? ContentsHref;

    /// <summary>The style of this list item's ::marker, when a rule styles it; its content replaces the marker text.</summary>
    public Css.ComputedStyle? MarkerStyle;
    public string? MarkerContent;
    /// <summary>The ::first-line style of the block whose first line this element's inline content starts, if any.</summary>
    public Css.FirstLineStyle? FirstLine;
    /// <summary>An anchor key under which layout records where this element starts (string-set elements).</summary>
    public string? LayoutKey;
    /// <summary>The text of this element's bookmark when bookmark-label sets one; null for its own text.</summary>
    public string? BookmarkLabel;

    /// <summary>A footnote (float: footnote): its number among the document's footnotes, from 0 in document order; else -1.</summary>
    public int FootnoteIndex = -1;

    /// <summary>For a ::footnote-call box: the footnote it calls.</summary>
    public Element? FootnoteBody;

    /// <summary>For input, textarea, select and button elements: the form control they draw.</summary>
    public Layout.FormControl? Control;

    /// <summary>
    /// For an img element whose picture is missing or cannot be decoded and that has alt text: that text, which is also
    /// its only child. Such an img is laid out as an ordinary box around the text, as browsers do, not as a replaced one.
    /// </summary>
    public string? AltText;

    /// <summary>
    /// An embed or object element whose resource is a picture: it is laid out and drawn as an img showing that picture,
    /// as browsers show it, and an object's fallback content is not shown.
    /// </summary>
    public bool EmbedsPicture;

    /// <summary>The image pixels per CSS pixel of a generated img that shows an image-set() option; 1 otherwise.</summary>
    public float GeneratedResolution = 1;

    /// <summary>An img element that shows a picture (not its alt text), or an embed or object element that shows one.</summary>
    public bool IsPicture => AltText == null && Name == "img" || EmbedsPicture;

    /// <summary>An element drawn as a picture or drawing that CSS sizes from its intrinsic size: a picture or an inline svg.</summary>
    public bool IsReplaced => AltText == null && Name is "img" or "svg" || EmbedsPicture;

    /// <summary>
    /// For a query container: the size of its content box at its last layout, in points, or NaN before it was laid
    /// out. Container queries and cq units of the next style pass read it.
    /// </summary>
    public float ContainerWidth = float.NaN, ContainerHeight = float.NaN;

    public Element(string name) => Name = name;

    public string? GetAttribute(string name)
    {
        var attrs = Attributes;
        for (int i = 0; i < attrs.Count; i++)
            if (attrs[i].Key == name) return attrs[i].Value;
        return null;
    }

    public bool HasAttribute(string name) => GetAttribute(name) != null;

    public void AddAttribute(string name, string value) => (_attributes ??= new(2)).Add(new KeyValuePair<string, string>(name, value));

    public void AppendChild(Node node)
    {
        node.Parent = this;
        Children.Add(node);
    }

    /// <summary>Concatenated text of all descendant text nodes.</summary>
    public string InnerText()
    {
        var sb = new System.Text.StringBuilder();
        AppendText(this, sb);
        return sb.ToString();

        static void AppendText(Element e, System.Text.StringBuilder sb)
        {
            foreach (var c in e.Children)
            {
                if (c is TextNode t) sb.Append(t.Text);
                else if (c is Element ce) AppendText(ce, sb);
            }
        }
    }

    public Element? FindFirst(string name)
    {
        foreach (var c in Children)
        {
            if (c is Element e)
            {
                if (e.Name == name) return e;
                var found = e.FindFirst(name);
                if (found != null) return found;
            }
        }
        return null;
    }

    public override string ToString() => "<" + Name + ">";
}
