namespace PdfPecker.Fluent.Model;

/// <summary>A node of the document tree that the fluent calls build and the HTML writer turns into markup.</summary>
internal abstract class Element;

/// <summary>A slot that holds at most one element. Every public <see cref="IContainer"/> is one of these.</summary>
internal class Container : IContainer
{
    public Element? Child { get; private set; }

    /// <summary>The decorator this slot is the inside of, which <c>BorderColor</c> changes.</summary>
    public Decorator? Owner { get; init; }

    public T Set<T>(T element) where T : Element
    {
        if (Child != null)
            throw new InvalidOperationException("This container already has content. Put several elements in a Column, Row or Table.");
        Child = element;
        return element;
    }

    public static Container Of(IContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container as Container
            ?? throw new ArgumentException("Only containers that PdfPecker.Fluent creates can be used.", nameof(container));
    }
}

/// <summary>A table cell's slot, with its place and spans.</summary>
internal sealed class TableCell : Container, ITableCellContainer
{
    public int? Row { get; set; }
    public int? Column { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColumnSpan { get; set; } = 1;
}

internal enum DecoratorKind
{
    /// <summary>CSS declarations on the box, with a rank that says where in the box model they sit.</summary>
    Style,
    Border,
    Align,
    Hyperlink,
    Section,
    Direction,
    TextStyle,
    Heading,
    Hidden,

    /// <summary>Moves the content to the next page unless at least <see cref="Decorator.Value"/> points are left.</summary>
    EnsureSpace,

    /// <summary>Writes the box as the HTML element named by <see cref="Decorator.Text"/>, which PdfPecker tags.</summary>
    Semantic,

    /// <summary>Leaves the content out of the tagged structure: it becomes an artifact.</summary>
    Artifact,

    /// <summary>The text alternative of the images inside.</summary>
    AlternativeText,

    /// <summary>Shows the content only on the first page of a header or footer; elsewhere it shows as usual.</summary>
    ShowOnce,

    /// <summary>Shows the content on every page of a header or footer but the first; elsewhere it is left out.</summary>
    SkipOnce,

    /// <summary>Draws the content smaller, keeping its proportions, when it does not fit the slot.</summary>
    ScaleToFit,
}

/// <summary>
/// Where a declaration sits in the CSS box model, outermost first. Declarations merge into one element while their
/// rank does not go back outwards: a size, then a border or background, then padding. QuestPDF-style chains such as
/// <c>Padding(5).Background(c)</c> put the background inside the padding, so they need a nested element.
/// </summary>
internal static class Rank
{
    /// <summary>Applies anywhere: inherited text properties, page-break control.</summary>
    public const int Any = -1;
    public const int Size = 0;
    public const int Paint = 1;
    public const int Padding = 2;
}

/// <summary>A decoration of the content inside it: a style, alignment, link, or one of the other kinds.</summary>
internal sealed class Decorator : Element
{
    public Decorator(DecoratorKind kind)
    {
        Kind = kind;
        Inner = new Container { Owner = this };
    }

    public DecoratorKind Kind { get; }
    public Container Inner { get; }

    public int Rank { get; init; } = Fluent.Model.Rank.Any;
    public (string Property, string Value)[] Declarations { get; init; } = [];

    // Border: widths per side (NaN where the side has none) and a colour that BorderColor may change later.
    public float Top { get; init; } = float.NaN;
    public float Right { get; init; } = float.NaN;
    public float Bottom { get; init; } = float.NaN;
    public float Left { get; init; } = float.NaN;
    public Color BorderColor { get; set; } = Colors.Black;

    // Align
    public HorizontalAlignment? Horizontal { get; init; }
    public VerticalAlignment? Vertical { get; init; }

    public string? Text { get; init; }
    public TextStyle? Style { get; init; }
    public int Level { get; init; }
    public float Value { get; init; }
}

internal enum HorizontalAlignment { Left, Center, Right }

internal enum VerticalAlignment { Top, Middle, Bottom }

internal sealed class ColumnElement : Element
{
    public float Spacing { get; set; }
    public List<Container> Items { get; } = [];
}

internal enum RowItemKind { Relative, Constant, Auto }

internal sealed class RowElement : Element
{
    public float Spacing { get; set; }
    public List<(RowItemKind Kind, float Size, Container Slot)> Items { get; } = [];
}

internal sealed class TableElement : Element
{
    public List<(bool Relative, float Size)> Columns { get; } = [];
    public List<TableCell> Header { get; } = [];
    public List<TableCell> Body { get; } = [];
    public List<TableCell> Footer { get; } = [];
}

internal enum RunKind { Text, PageNumber, TotalPages, Hyperlink, SectionLink, SectionPageNumber, Element }

internal sealed class TextRun
{
    public RunKind Kind { get; init; }
    public string Text { get; init; } = "";
    public string? Target { get; init; }
    public TextStyle Style { get; set; } = TextStyle.Default;

    /// <summary>The content of an <see cref="RunKind.Element"/> run, and its CSS vertical-align.</summary>
    public Container? Slot { get; init; }
    public string VerticalAlign { get; init; } = "baseline";
}

internal enum TextAlignment { Left, Center, Right, Start, End, Justify }

internal sealed class TextElement : Element
{
    public List<TextRun> Runs { get; } = [];
    public TextStyle Style { get; set; } = TextStyle.Default;
    public TextAlignment? Alignment { get; set; }
    public int? MaxLines { get; set; }
    public float? FirstLineIndent { get; set; }
    public float? ParagraphSpacing { get; set; }
}

internal enum ImageScaling { FitWidth, FitHeight, FitArea, FitUnproportionally }

internal sealed class ImageElement : Element
{
    public ImageElement(byte[] data) => Data = data;

    public byte[] Data { get; }
    public ImageScaling Scaling { get; set; }
    public string AlternativeText { get; set; } = "";
}

internal sealed class LineElement : Element
{
    public bool Vertical { get; init; }
    public float Thickness { get; init; }
    public Color Color { get; set; } = Colors.Black;
    public string Style { get; set; } = "solid";
}

/// <summary>Content drawn on top of each other: one primary layer sets the size, the others cover it.</summary>
internal sealed class LayersElement : Element
{
    public List<(bool Primary, Container Slot)> Layers { get; } = [];
}

/// <summary>Items laid out like words: side by side, wrapping onto new lines.</summary>
internal sealed class InlinedElement : Element
{
    public float HorizontalSpacing { get; set; }
    public float VerticalSpacing { get; set; }
    public string Justify { get; set; } = "flex-start";
    public string Align { get; set; } = "flex-start";
    public List<Container> Items { get; } = [];
}

/// <summary>Content flowing through several columns, like a newspaper.</summary>
internal sealed class MultiColumnElement : Element
{
    public int Columns { get; set; } = 2;
    public float Spacing { get; set; }
    public bool Balance { get; set; } = true;
    public Container Content { get; } = new();
}

/// <summary>Content with a part before and after it that repeat on every page the content spans.</summary>
internal sealed class DecorationElement : Element
{
    public Container Before { get; } = new();
    public Container Content { get; } = new();
    public Container After { get; } = new();
}

/// <summary>A bulleted or numbered list.</summary>
internal sealed class ListElement : Element
{
    public List<Container> Items { get; } = [];
    public float Spacing { get; set; }
    public float Indent { get; set; } = 18;

    /// <summary>The CSS list-style-type: a keyword such as disc or decimal, or a quoted marker string.</summary>
    public string MarkerStyle { get; set; } = "disc";
    public bool Ordered { get; set; }
    public int Start { get; set; } = 1;
}

internal sealed class PageBreakElement : Element;

internal sealed class HtmlElement : Element
{
    public HtmlElement(string html) => Html = html;

    public string Html { get; }
}

/// <summary>One <c>Page</c> call: a page size, margins and the header, content and footer slots.</summary>
internal sealed class PageSet
{
    public PageSize Size { get; set; } = PageSize.A4;
    public float MarginTop { get; set; }
    public float MarginRight { get; set; }
    public float MarginBottom { get; set; }
    public float MarginLeft { get; set; }
    public Color? Background { get; set; }
    public TextStyle Style { get; set; } = TextStyle.Default;
    public bool RightToLeft { get; set; }
    public Container Header { get; } = new();
    public Container Content { get; } = new();
    public Container Footer { get; } = new();
}
