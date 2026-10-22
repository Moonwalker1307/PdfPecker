using PdfPecker.Fluent.Html;
using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent;

public static partial class ContainerExtensions
{
    // Shrinking. A slot is as tall as its content already, except where a row stretches it or a Height sets it.

    /// <summary>Makes the slot as narrow as its content, instead of as wide as the space it is given.</summary>
    public static IContainer ShrinkHorizontal(this IContainer container)
        => Style(container, Rank.Size, ("width", "fit-content"), HtmlWriter.ContentSized);

    /// <summary>Makes the slot as short as its content, instead of as tall as a row or a <c>Height</c> makes it.</summary>
    public static IContainer ShrinkVertical(this IContainer container) => Style(container, Rank.Size, ("height", "auto"));

    /// <summary>Makes the slot as small as its content in both directions.</summary>
    public static IContainer Shrink(this IContainer container)
        => Style(container, Rank.Size, ("width", "fit-content"), ("height", "auto"), HtmlWriter.ContentSized);

    // Semantics. PdfPecker tags a PDF/UA document from its HTML elements, so each call writes the matching element.

    /// <summary>Tags the content as a section (Sect) in PDF/UA.</summary>
    public static IContainer SemanticSection(this IContainer container) => Semantic(container, "section");

    /// <summary>Tags the content as an article (Art) in PDF/UA.</summary>
    public static IContainer SemanticArticle(this IContainer container) => Semantic(container, "article");

    /// <summary>Tags the content as a table of contents; PdfPecker tags it as a section (Sect).</summary>
    public static IContainer SemanticTableOfContents(this IContainer container) => Semantic(container, "nav");

    /// <summary>Tags the content as a block quotation (BlockQuote) in PDF/UA.</summary>
    public static IContainer SemanticBlockQuotation(this IContainer container) => Semantic(container, "blockquote");

    /// <summary>Tags the content as the caption (Caption) of a table or figure in PDF/UA.</summary>
    public static IContainer SemanticCaption(this IContainer container) => Semantic(container, "figcaption");

    /// <summary>Tags the content as a list (L) in PDF/UA, with <see cref="SemanticListItem"/> items. <c>List</c> does this itself.</summary>
    public static IContainer SemanticList(this IContainer container) => Semantic(container, "ul");

    /// <summary>Tags the content as an item (LI) of a <see cref="SemanticList"/> in PDF/UA.</summary>
    public static IContainer SemanticListItem(this IContainer container) => Semantic(container, "li");

    /// <summary>Tags the text inside as a level 1 heading; the same as <c>Heading(1)</c>.</summary>
    public static IContainer SemanticHeader1(this IContainer container) => Heading(container, 1);

    /// <summary>Tags the text inside as a level 2 heading; the same as <c>Heading(2)</c>.</summary>
    public static IContainer SemanticHeader2(this IContainer container) => Heading(container, 2);

    /// <summary>Tags the text inside as a level 3 heading; the same as <c>Heading(3)</c>.</summary>
    public static IContainer SemanticHeader3(this IContainer container) => Heading(container, 3);

    /// <summary>Tags the text inside as a level 4 heading; the same as <c>Heading(4)</c>.</summary>
    public static IContainer SemanticHeader4(this IContainer container) => Heading(container, 4);

    /// <summary>Tags the text inside as a level 5 heading; the same as <c>Heading(5)</c>.</summary>
    public static IContainer SemanticHeader5(this IContainer container) => Heading(container, 5);

    /// <summary>Tags the text inside as a level 6 heading; the same as <c>Heading(6)</c>.</summary>
    public static IContainer SemanticHeader6(this IContainer container) => Heading(container, 6);

    /// <summary>
    /// Tags the text inside as a paragraph (P). Text is a paragraph already, so this changes nothing; it is here so
    /// QuestPDF code ports unchanged.
    /// </summary>
    public static IContainer SemanticParagraph(this IContainer container)
    {
        Container.Of(container);
        return container;
    }

    /// <summary>
    /// Tags a link. Links are tagged (Link) with their text already, so this changes nothing; it is here so QuestPDF
    /// code ports unchanged.
    /// </summary>
    public static IContainer SemanticLink(this IContainer container, string alternativeText)
    {
        ArgumentNullException.ThrowIfNull(alternativeText);
        Container.Of(container);
        return container;
    }

    /// <summary>
    /// The text alternative of the images inside, which PDF/UA tags as figures and screen readers read aloud. An
    /// image's own <c>AlternativeText</c> wins.
    /// </summary>
    public static IContainer SemanticImage(this IContainer container, string alternativeText)
    {
        ArgumentNullException.ThrowIfNull(alternativeText);
        return Decorate(container, new Decorator(DecoratorKind.AlternativeText) { Text = alternativeText });
    }

    /// <summary>The same as <see cref="SemanticImage"/>: the text alternative of the images inside.</summary>
    public static IContainer SemanticFigure(this IContainer container, string alternativeText) => SemanticImage(container, alternativeText);

    /// <summary>Leaves the content out of the tagged structure, as decoration (an artifact) that screen readers skip.</summary>
    public static IContainer SemanticIgnore(this IContainer container) => Decorate(container, new Decorator(DecoratorKind.Artifact));

    private static Container Semantic(IContainer container, string tag)
        => Decorate(container, new Decorator(DecoratorKind.Semantic) { Text = tag });
}
