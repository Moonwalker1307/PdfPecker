using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent;

/// <summary>Adds pages to a document.</summary>
public static class DocumentContainerExtensions
{
    /// <summary>
    /// Adds a set of pages: a size, margins, and a header, content and footer. The content flows over as many pages as
    /// it needs, and the header and footer repeat on each. Each further <c>Page</c> call starts a new page with its own
    /// settings; page numbers then count within each set.
    /// </summary>
    public static IDocumentContainer Page(this IDocumentContainer container, Action<PageDescriptor> handler)
    {
        var document = DocumentContainer.Of(container);
        ArgumentNullException.ThrowIfNull(handler);
        var page = new PageSet();
        handler(new PageDescriptor(page));
        document.Pages.Add(page);
        return container;
    }

    /// <summary>
    /// Adds CSS to every page set's style sheet, written as given, for what the fluent API does not cover. Only pass
    /// CSS you wrote yourself: it is not checked.
    /// </summary>
    /// <exception cref="ArgumentException">The CSS contains <c>&lt;/style</c>, which would end the style sheet.</exception>
    public static IDocumentContainer Css(this IDocumentContainer container, string css)
    {
        var document = DocumentContainer.Of(container);
        ArgumentNullException.ThrowIfNull(css);
        if (css.Contains("</style", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The CSS cannot contain </style.", nameof(css));
        document.Css.Add(css);
        return container;
    }
}

/// <summary>The size, margins, default text style and slots of a set of pages.</summary>
public sealed class PageDescriptor
{
    private readonly PageSet _page;

    internal PageDescriptor(PageSet page) => _page = page;

    /// <summary>The page size, such as <see cref="PageSizes.A4"/> (the default) or <c>PageSizes.A4.Landscape</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A side is not between 1 and 14,400 points.</exception>
    public PageDescriptor Size(PageSize size)
    {
        Check(size.Width, nameof(size));
        Check(size.Height, nameof(size));
        _page.Size = size;
        return this;
    }

    /// <summary>The page size in the given unit.</summary>
    public PageDescriptor Size(float width, float height, Unit unit = Unit.Point)
        => Size(new PageSize(Check(Length.ToPoints(width, unit), nameof(width)), Check(Length.ToPoints(height, unit), nameof(height))));

    private static float Check(float points, string paramName)
    {
        if (!float.IsFinite(points) || points < 1 || points > 14_400)
            throw new ArgumentOutOfRangeException(paramName, points, "A page side must be from 1 to 14,400 points (200 inches).");
        return points;
    }

    /// <summary>All four margins (0 by default). The header and footer sit inside them.</summary>
    public PageDescriptor Margin(float value, Unit unit = Unit.Point) => MarginVertical(value, unit).MarginHorizontal(value, unit);

    /// <summary>The left and right margins.</summary>
    public PageDescriptor MarginHorizontal(float value, Unit unit = Unit.Point) => MarginLeft(value, unit).MarginRight(value, unit);

    /// <summary>The top and bottom margins.</summary>
    public PageDescriptor MarginVertical(float value, Unit unit = Unit.Point) => MarginTop(value, unit).MarginBottom(value, unit);

    /// <summary>The top margin.</summary>
    public PageDescriptor MarginTop(float value, Unit unit = Unit.Point)
    {
        _page.MarginTop = Length.Points(value, unit, nameof(value));
        return this;
    }

    /// <summary>The bottom margin.</summary>
    public PageDescriptor MarginBottom(float value, Unit unit = Unit.Point)
    {
        _page.MarginBottom = Length.Points(value, unit, nameof(value));
        return this;
    }

    /// <summary>The left margin.</summary>
    public PageDescriptor MarginLeft(float value, Unit unit = Unit.Point)
    {
        _page.MarginLeft = Length.Points(value, unit, nameof(value));
        return this;
    }

    /// <summary>The right margin.</summary>
    public PageDescriptor MarginRight(float value, Unit unit = Unit.Point)
    {
        _page.MarginRight = Length.Points(value, unit, nameof(value));
        return this;
    }

    /// <summary>The colour of the whole page, margins included.</summary>
    public PageDescriptor PageColor(Color color)
    {
        _page.Background = color;
        return this;
    }

    /// <summary>The text style every text on these pages starts from (12 pt sans-serif black by default).</summary>
    public PageDescriptor DefaultTextStyle(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        _page.Style = style;
        return this;
    }

    /// <summary>The text style every text on these pages starts from, built from <see cref="TextStyle.Default"/>.</summary>
    public PageDescriptor DefaultTextStyle(Func<TextStyle, TextStyle> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return DefaultTextStyle(handler(TextStyle.Default));
    }

    /// <summary>Lays content out left to right (the default).</summary>
    public PageDescriptor ContentFromLeftToRight()
    {
        _page.RightToLeft = false;
        return this;
    }

    /// <summary>Lays content out right to left, for Arabic, Hebrew and other right-to-left languages.</summary>
    public PageDescriptor ContentFromRightToLeft()
    {
        _page.RightToLeft = true;
        return this;
    }

    /// <summary>The slot repeated at the top of every page.</summary>
    public IContainer Header() => _page.Header;

    /// <summary>The slot for the flowing content.</summary>
    public IContainer Content() => _page.Content;

    /// <summary>The slot repeated at the bottom of every page. Page numbers go here or in the header.</summary>
    public IContainer Footer() => _page.Footer;
}
