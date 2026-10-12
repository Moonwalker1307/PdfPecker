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
    public readonly List<KeyValuePair<string, string>> Attributes = new(2);
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

    /// <summary>A ::before or ::after box made by the style resolver, not by the parser.</summary>
    public bool Generated;

    public Element(string name) => Name = name;

    public string? GetAttribute(string name)
    {
        var attrs = Attributes;
        for (int i = 0; i < attrs.Count; i++)
            if (attrs[i].Key == name) return attrs[i].Value;
        return null;
    }

    public bool HasAttribute(string name) => GetAttribute(name) != null;

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
