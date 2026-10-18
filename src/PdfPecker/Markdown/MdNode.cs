using System.Text;

namespace PdfPecker.Markdown;

internal enum MdNodeType : byte
{
    // Blocks
    Document,
    BlockQuote,
    List,
    Item,
    Paragraph,
    Heading,
    ThematicBreak,
    CodeBlock,
    HtmlBlock,
    Table,
    TableRow,
    TableCell,
    DefinitionList,
    DefinitionTerm,
    DefinitionDetails,
    FootnoteDefinition,

    // Inlines
    Text,
    SoftBreak,
    LineBreak,
    Code,
    HtmlInline,
    Emph,
    Strong,
    Strikethrough,
    Link,
    Image,
    FootnoteReference,
}

internal enum TableAlign : byte { None, Left, Center, Right }

/// <summary>A node of the Markdown syntax tree: blocks and inlines share one doubly linked tree, as in the CommonMark reference parser.</summary>
internal sealed class MdNode
{
    public MdNodeType Type;
    public MdNode? Parent, FirstChild, LastChild, Prev, Next;

    // Block parsing state
    public bool Open = true;
    public int StartLine, EndLine;
    public StringBuilder? Content; // raw text of paragraphs and code blocks while parsing

    /// <summary>Text, code and HTML content; for code blocks the literal body.</summary>
    public string? Literal;

    // Headings
    public int Level;

    // Fenced code blocks
    public bool IsFenced;
    public char FenceChar;
    public int FenceLength, FenceOffset;
    public string? Info;

    // HTML blocks
    public int HtmlBlockType;

    // Lists, and the details of definition lists, which continue like list items
    public ListData? ListData;

    /// <summary>Footnote definitions and references: the normalized label.</summary>
    public string? Label;

    /// <summary>GFM task list item state: null when the item has no checkbox.</summary>
    public bool? Checked;

    // Links and images
    public string? Destination, Title;

    // Tables
    public TableAlign[]? Aligns;
    public TableAlign Align;
    public bool IsHeader;

    public MdNode(MdNodeType type) => Type = type;

    public bool IsContainer => Type is MdNodeType.Document or MdNodeType.BlockQuote or MdNodeType.List or MdNodeType.Item
        or MdNodeType.Paragraph or MdNodeType.Heading or MdNodeType.Emph or MdNodeType.Strong or MdNodeType.Strikethrough
        or MdNodeType.Link or MdNodeType.Image or MdNodeType.Table or MdNodeType.TableRow or MdNodeType.TableCell
        or MdNodeType.DefinitionList or MdNodeType.DefinitionTerm or MdNodeType.DefinitionDetails or MdNodeType.FootnoteDefinition;

    /// <summary>
    /// Visits the subtree in document order without recursion (deeply nested input cannot overflow the stack).
    /// Nodes with children are visited twice: entering and leaving; nodes without children once, entering.
    /// </summary>
    public IEnumerable<(MdNode Node, bool Entering)> Walk()
    {
        var current = this;
        bool entering = true;
        while (true)
        {
            yield return (current, entering);
            if (entering && current.FirstChild != null)
            {
                current = current.FirstChild;
                continue;
            }
            if (entering && current.IsContainer && current.FirstChild == null)
            {
                // A container without children still gets a leaving visit.
                yield return (current, false);
            }
            if (current == this) yield break;
            if (current.Next != null)
            {
                current = current.Next;
                entering = true;
            }
            else
            {
                current = current.Parent!;
                entering = false;
                if (current == this)
                {
                    yield return (current, false);
                    yield break;
                }
            }
        }
    }

    public void AppendChild(MdNode child)
    {
        child.Unlink();
        child.Parent = this;
        if (LastChild != null)
        {
            LastChild.Next = child;
            child.Prev = LastChild;
            LastChild = child;
        }
        else
        {
            FirstChild = child;
            LastChild = child;
        }
    }

    public void InsertAfter(MdNode sibling)
    {
        sibling.Unlink();
        sibling.Next = Next;
        if (sibling.Next != null) sibling.Next.Prev = sibling;
        sibling.Prev = this;
        Next = sibling;
        sibling.Parent = Parent;
        if (sibling.Next == null && Parent != null) Parent.LastChild = sibling;
    }

    public void InsertBefore(MdNode sibling)
    {
        sibling.Unlink();
        sibling.Prev = Prev;
        if (sibling.Prev != null) sibling.Prev.Next = sibling;
        sibling.Next = this;
        Prev = sibling;
        sibling.Parent = Parent;
        if (sibling.Prev == null && Parent != null) Parent.FirstChild = sibling;
    }

    public void Unlink()
    {
        if (Prev != null) Prev.Next = Next;
        else if (Parent != null) Parent.FirstChild = Next;
        if (Next != null) Next.Prev = Prev;
        else if (Parent != null) Parent.LastChild = Prev;
        Parent = null;
        Next = null;
        Prev = null;
    }
}

internal sealed class ListData
{
    public bool Ordered;
    public bool Tight = true;
    public char BulletChar;
    public int Start;
    public char Delimiter;
    public int Padding;
    public int MarkerOffset;

    /// <summary>For a definition list: the last entry whose blank lines were checked when the list was last closed.</summary>
    public MdNode? CheckedUntil;
}
