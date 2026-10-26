using PdfPecker.Fluent.Html;
using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent;

/// <summary>
/// Styling for a slot. Each call returns the slot inside it, so the order matters as in QuestPDF:
/// <c>Padding(5).Background(c)</c> paints inside the padding, <c>Background(c).Padding(5)</c> under it.
/// </summary>
public static partial class ContainerExtensions
{
    private static Container Decorate(IContainer container, Decorator decorator)
        => Container.Of(container).Set(decorator).Inner;

    private static Container Style(IContainer container, int rank, params (string, string)[] declarations)
        => Decorate(container, new Decorator(DecoratorKind.Style) { Rank = rank, Declarations = declarations });

    // Padding

    /// <summary>Space inside the slot on all four sides.</summary>
    public static IContainer Padding(this IContainer container, float value, Unit unit = Unit.Point)
    {
        string v = CssValue.Pt(Length.Points(value, unit, nameof(value)));
        return Style(container, Rank.Padding, ("padding-top", v), ("padding-right", v), ("padding-bottom", v), ("padding-left", v));
    }

    /// <summary>Space inside the slot on the left and right.</summary>
    public static IContainer PaddingHorizontal(this IContainer container, float value, Unit unit = Unit.Point)
    {
        string v = CssValue.Pt(Length.Points(value, unit, nameof(value)));
        return Style(container, Rank.Padding, ("padding-right", v), ("padding-left", v));
    }

    /// <summary>Space inside the slot at the top and bottom.</summary>
    public static IContainer PaddingVertical(this IContainer container, float value, Unit unit = Unit.Point)
    {
        string v = CssValue.Pt(Length.Points(value, unit, nameof(value)));
        return Style(container, Rank.Padding, ("padding-top", v), ("padding-bottom", v));
    }

    /// <summary>Space inside the slot at the top.</summary>
    public static IContainer PaddingTop(this IContainer container, float value, Unit unit = Unit.Point)
        => Style(container, Rank.Padding, ("padding-top", CssValue.Pt(Length.Points(value, unit, nameof(value)))));

    /// <summary>Space inside the slot at the bottom.</summary>
    public static IContainer PaddingBottom(this IContainer container, float value, Unit unit = Unit.Point)
        => Style(container, Rank.Padding, ("padding-bottom", CssValue.Pt(Length.Points(value, unit, nameof(value)))));

    /// <summary>Space inside the slot on the left.</summary>
    public static IContainer PaddingLeft(this IContainer container, float value, Unit unit = Unit.Point)
        => Style(container, Rank.Padding, ("padding-left", CssValue.Pt(Length.Points(value, unit, nameof(value)))));

    /// <summary>Space inside the slot on the right.</summary>
    public static IContainer PaddingRight(this IContainer container, float value, Unit unit = Unit.Point)
        => Style(container, Rank.Padding, ("padding-right", CssValue.Pt(Length.Points(value, unit, nameof(value)))));

    // Paint

    /// <summary>Fills the slot with a colour.</summary>
    public static IContainer Background(this IContainer container, Color color)
        => Style(container, Rank.Paint, ("background-color", CssValue.Color(color)));

    /// <summary>Rounds the slot's corners; its background and content are clipped to them.</summary>
    public static IContainer CornerRadius(this IContainer container, float value, Unit unit = Unit.Point)
        => Style(container, Rank.Paint, ("border-radius", CssValue.Pt(Length.Points(value, unit, nameof(value)))), ("overflow", "hidden"));

    /// <summary>A border on all four sides, black unless <see cref="BorderColor"/> follows.</summary>
    public static IContainer Border(this IContainer container, float value, Unit unit = Unit.Point)
    {
        float w = Length.Points(value, unit, nameof(value));
        return Decorate(container, new Decorator(DecoratorKind.Border) { Rank = Rank.Paint, Top = w, Right = w, Bottom = w, Left = w });
    }

    /// <summary>Borders on the left and right.</summary>
    public static IContainer BorderHorizontal(this IContainer container, float value, Unit unit = Unit.Point)
    {
        float w = Length.Points(value, unit, nameof(value));
        return Decorate(container, new Decorator(DecoratorKind.Border) { Rank = Rank.Paint, Right = w, Left = w });
    }

    /// <summary>Borders at the top and bottom.</summary>
    public static IContainer BorderVertical(this IContainer container, float value, Unit unit = Unit.Point)
    {
        float w = Length.Points(value, unit, nameof(value));
        return Decorate(container, new Decorator(DecoratorKind.Border) { Rank = Rank.Paint, Top = w, Bottom = w });
    }

    /// <summary>A border at the top.</summary>
    public static IContainer BorderTop(this IContainer container, float value, Unit unit = Unit.Point)
        => Decorate(container, new Decorator(DecoratorKind.Border) { Rank = Rank.Paint, Top = Length.Points(value, unit, nameof(value)) });

    /// <summary>A border at the bottom.</summary>
    public static IContainer BorderBottom(this IContainer container, float value, Unit unit = Unit.Point)
        => Decorate(container, new Decorator(DecoratorKind.Border) { Rank = Rank.Paint, Bottom = Length.Points(value, unit, nameof(value)) });

    /// <summary>A border on the left.</summary>
    public static IContainer BorderLeft(this IContainer container, float value, Unit unit = Unit.Point)
        => Decorate(container, new Decorator(DecoratorKind.Border) { Rank = Rank.Paint, Left = Length.Points(value, unit, nameof(value)) });

    /// <summary>A border on the right.</summary>
    public static IContainer BorderRight(this IContainer container, float value, Unit unit = Unit.Point)
        => Decorate(container, new Decorator(DecoratorKind.Border) { Rank = Rank.Paint, Right = Length.Points(value, unit, nameof(value)) });

    /// <summary>The colour of the border set just before, as in <c>.Border(1).BorderColor(Colors.Grey.Lighten1)</c>.</summary>
    /// <exception cref="InvalidOperationException">The slot does not come straight from a border call.</exception>
    public static IContainer BorderColor(this IContainer container, Color color)
    {
        var slot = Container.Of(container);
        if (slot.Owner is not { Kind: DecoratorKind.Border } border)
            throw new InvalidOperationException("BorderColor must follow a Border call, as in .Border(1).BorderColor(color).");
        border.BorderColor = color;
        return container;
    }

    // Size

    /// <summary>A fixed width.</summary>
    public static IContainer Width(this IContainer container, float value, Unit unit = Unit.Point)
        => Style(container, Rank.Size, ("width", CssValue.Pt(Length.Points(value, unit, nameof(value)))));

    /// <summary>A minimum width.</summary>
    public static IContainer MinWidth(this IContainer container, float value, Unit unit = Unit.Point)
        => Style(container, Rank.Size, ("min-width", CssValue.Pt(Length.Points(value, unit, nameof(value)))));

    /// <summary>A maximum width.</summary>
    public static IContainer MaxWidth(this IContainer container, float value, Unit unit = Unit.Point)
        => Style(container, Rank.Size, ("max-width", CssValue.Pt(Length.Points(value, unit, nameof(value)))));

    /// <summary>A fixed height. Content taller than this overflows it.</summary>
    public static IContainer Height(this IContainer container, float value, Unit unit = Unit.Point)
        => Style(container, Rank.Size, ("height", CssValue.Pt(Length.Points(value, unit, nameof(value)))));

    /// <summary>A minimum height.</summary>
    public static IContainer MinHeight(this IContainer container, float value, Unit unit = Unit.Point)
        => Style(container, Rank.Size, ("min-height", CssValue.Pt(Length.Points(value, unit, nameof(value)))));

    /// <summary>A maximum height.</summary>
    public static IContainer MaxHeight(this IContainer container, float value, Unit unit = Unit.Point)
        => Style(container, Rank.Size, ("max-height", CssValue.Pt(Length.Points(value, unit, nameof(value)))));

    /// <summary>Takes the full width available, for example inside an aligned slot.</summary>
    public static IContainer ExtendHorizontal(this IContainer container) => Style(container, Rank.Size, ("width", "100%"));

    /// <summary>Takes the full height available, for example in a row whose items stretch to the tallest.</summary>
    public static IContainer ExtendVertical(this IContainer container) => Style(container, Rank.Size, ("height", "100%"));

    /// <summary>Takes the full width and height available.</summary>
    public static IContainer Extend(this IContainer container) => Style(container, Rank.Size, ("width", "100%"), ("height", "100%"));

    // Alignment

    /// <summary>Places the content at the left, as narrow as it can be.</summary>
    public static IContainer AlignLeft(this IContainer container) => Align(container, HorizontalAlignment.Left, null);

    /// <summary>Centres the content horizontally, as narrow as it can be.</summary>
    public static IContainer AlignCenter(this IContainer container) => Align(container, HorizontalAlignment.Center, null);

    /// <summary>Places the content at the right, as narrow as it can be.</summary>
    public static IContainer AlignRight(this IContainer container) => Align(container, HorizontalAlignment.Right, null);

    /// <summary>Places the content at the top of the slot's height.</summary>
    public static IContainer AlignTop(this IContainer container) => Align(container, null, VerticalAlignment.Top);

    /// <summary>Centres the content vertically in the slot's height (which a row or a <c>Height</c> gives it).</summary>
    public static IContainer AlignMiddle(this IContainer container) => Align(container, null, VerticalAlignment.Middle);

    /// <summary>Places the content at the bottom of the slot's height.</summary>
    public static IContainer AlignBottom(this IContainer container) => Align(container, null, VerticalAlignment.Bottom);

    private static Container Align(IContainer container, HorizontalAlignment? horizontal, VerticalAlignment? vertical)
        => Decorate(container, new Decorator(DecoratorKind.Align) { Horizontal = horizontal, Vertical = vertical });

    // Flow

    /// <summary>Keeps the content on one page: it moves to the next page whole unless it is taller than a page.</summary>
    public static IContainer PreventPageBreak(this IContainer container) => Style(container, Rank.Any, ("break-inside", "avoid"));

    /// <summary>The same as <see cref="PreventPageBreak"/>, under QuestPDF's older name.</summary>
    public static IContainer ShowEntire(this IContainer container) => PreventPageBreak(container);

    /// <summary>Renders the content only when <paramref name="condition"/> is true.</summary>
    public static IContainer ShowIf(this IContainer container, bool condition)
        => condition ? container : Decorate(container, new Decorator(DecoratorKind.Hidden));

    /// <summary>
    /// In a page header or footer, shows the content on the first page only, as for a letterhead. Elsewhere content
    /// appears once anyway, so this changes nothing there.
    /// </summary>
    public static IContainer ShowOnce(this IContainer container) => Decorate(container, new Decorator(DecoratorKind.ShowOnce));

    /// <summary>
    /// In a page header or footer, shows the content on every page but the first, as for a slim header after a
    /// letterhead. Elsewhere content appears once, on its first page, so it is left out there.
    /// </summary>
    public static IContainer SkipOnce(this IContainer container) => Decorate(container, new Decorator(DecoratorKind.SkipOnce));

    // Text and semantics

    /// <summary>The text style for every text inside the slot.</summary>
    public static IContainer DefaultTextStyle(this IContainer container, TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return Decorate(container, new Decorator(DecoratorKind.TextStyle) { Style = style });
    }

    /// <summary>The text style for every text inside the slot, built from <see cref="TextStyle.Default"/>.</summary>
    public static IContainer DefaultTextStyle(this IContainer container, Func<TextStyle, TextStyle> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return DefaultTextStyle(container, handler(TextStyle.Default));
    }

    /// <summary>Lays the slot's content out right to left.</summary>
    public static IContainer ContentFromRightToLeft(this IContainer container)
        => Decorate(container, new Decorator(DecoratorKind.Direction) { Text = "rtl" });

    /// <summary>Lays the slot's content out left to right.</summary>
    public static IContainer ContentFromLeftToRight(this IContainer container)
        => Decorate(container, new Decorator(DecoratorKind.Direction) { Text = "ltr" });

    /// <summary>
    /// Marks the text inside as a heading of the given level (1 to 6). Headings become PDF bookmarks and tagged
    /// headings in PDF/UA; the look stays the text's own style.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not 1 to 6.</exception>
    public static IContainer Heading(this IContainer container, int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 6);
        return Decorate(container, new Decorator(DecoratorKind.Heading) { Level = level });
    }

    /// <summary>Makes the whole slot a link to a web address (http, https or mailto).</summary>
    /// <exception cref="ArgumentException">The address is not an absolute http, https or mailto URL.</exception>
    public static IContainer Hyperlink(this IContainer container, string url)
        => Decorate(container, new Decorator(DecoratorKind.Hyperlink) { Text = Links.Check(url, nameof(url)) });

    /// <summary>Names the slot as a link target for <see cref="SectionLink"/>.</summary>
    public static IContainer Section(this IContainer container, string sectionName)
        => Decorate(container, new Decorator(DecoratorKind.Section) { Text = Links.Section(sectionName, nameof(sectionName)) });

    /// <summary>Makes the whole slot a link to a <see cref="Section"/> of the document.</summary>
    public static IContainer SectionLink(this IContainer container, string sectionName)
        => Decorate(container, new Decorator(DecoratorKind.Hyperlink) { Text = "#" + Links.Section(sectionName, nameof(sectionName)) });

    // Composition

    /// <summary>Applies a styling method to the slot, as in <c>.Element(CellStyle).Text(...)</c>.</summary>
    public static IContainer Element(this IContainer container, Func<IContainer, IContainer> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return handler(container);
    }

    /// <summary>Fills the slot with a method, as in <c>page.Header().Element(ComposeHeader)</c>.</summary>
    public static void Element(this IContainer container, Action<IContainer> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler(container);
    }

    /// <summary>Fills the slot with a reusable component.</summary>
    public static void Component(this IContainer container, IComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        component.Compose(container);
    }
}

/// <summary>Checks link targets: only web and mail addresses, never script or file URLs.</summary>
internal static class Links
{
    public static string Check(string url, string paramName)
    {
        ArgumentNullException.ThrowIfNull(url, paramName);
        url = url.Trim();
        // The scheme is checked before System.Uri parses the address: .NET throws IndexOutOfRangeException for some
        // file and UNC paths, such as two backslashes and a right-to-left override.
        if (!(url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
              || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeMailto))
            throw new ArgumentException("A link must be an absolute http, https or mailto address.", paramName);
        return uri.OriginalString;
    }

    public static string Section(string name, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, paramName);
        return name;
    }
}
