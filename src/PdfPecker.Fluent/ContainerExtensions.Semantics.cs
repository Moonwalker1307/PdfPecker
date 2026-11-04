using PdfPecker.Fluent.Html;
using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent;

public static partial class ContainerExtensions
{
    // Shrinking. A slot is as tall as its content already, except where a row stretches it or a Height sets it.

    /// <summary>Makes the slot as narrow as its content, instead of as wide as the space it is given.</summary>
    public static IContainer ShrinkHorizontal(this IContainer container)
        => Style(container, Rank.Size, ("width", "fit-content"));

    /// <summary>Makes the slot as short as its content, instead of as tall as a row or a <c>Height</c> makes it.</summary>
    public static IContainer ShrinkVertical(this IContainer container) => Style(container, Rank.Size, ("height", "auto"));

    /// <summary>Makes the slot as small as its content in both directions.</summary>
    public static IContainer Shrink(this IContainer container)
        => Style(container, Rank.Size, ("width", "fit-content"), ("height", "auto"));

    /// <summary>
    /// Draws the content smaller, keeping its proportions, when it is taller or wider than the slot, as for a name on a
    /// badge or a label of fixed size. Text first wraps into fewer, longer lines before it gets smaller. The slot's
    /// height comes from a <c>Height</c> or <c>MaxHeight</c> before this call, or from the slot around it.
    /// </summary>
    public static IContainer ScaleToFit(this IContainer container) => Decorate(container, new Decorator(DecoratorKind.ScaleToFit));

    // Semantics. PdfPecker tags a PDF/UA document from its HTML elements, so each call writes the matching element.

    /// <summary>Tags the content as a section (Sect) in PDF/UA.</summary>
    public static IContainer SemanticSection(this IContainer container) => Semantic(container, "section");

    /// <summary>Tags the content as an article (Art) in PDF/UA.</summary>
    public static IContainer SemanticArticle(this IContainer container) => Semantic(container, "article");

    /// <summary>
    /// Tags the content as a table of contents (TOC) in PDF/UA, whose entries are <see cref="SemanticTableOfContentsItem"/>
    /// slots; a table of contents inside an entry nests.
    /// </summary>
    public static IContainer SemanticTableOfContents(this IContainer container)
        => Tagging(Semantic(container, "nav"), ("role", "doc-toc"));

    /// <summary>Tags the content as an entry (TOCI) of a <see cref="SemanticTableOfContents"/> in PDF/UA.</summary>
    public static IContainer SemanticTableOfContentsItem(this IContainer container) => Semantic(container, "li");

    /// <summary>
    /// Tags the content as a formula (Formula) in PDF/UA, which screen readers read out by its text alternative, as
    /// they do a figure.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="alternativeText"/> is empty.</exception>
    public static IContainer SemanticFormula(this IContainer container, string alternativeText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alternativeText);
        return Tagging(container, ("role", "math"), ("aria-label", alternativeText));
    }

    /// <summary>Tags the content as an index (Index) in PDF/UA, such as the list of terms at the back of a book.</summary>
    public static IContainer SemanticIndex(this IContainer container) => Tagging(container, ("role", "doc-index"));

    /// <summary>
    /// Tags the content as the label (Lbl) of the <see cref="SemanticListItem"/> it is in, such as the number in a row
    /// beside the item's text. The rest of the item is its body. <c>List</c> tags its markers this way itself.
    /// </summary>
    public static IContainer SemanticListLabel(this IContainer container) => Tagging(container, (HtmlWriter.ListLabelAttribute, ""));

    /// <summary>
    /// Tags the content as the body (LBody) of the <see cref="SemanticListItem"/> it is in. Everything in an item but
    /// its <see cref="SemanticListLabel"/> is the body already, so this changes nothing; it is here so QuestPDF code
    /// ports unchanged.
    /// </summary>
    public static IContainer SemanticListItemBody(this IContainer container)
    {
        Container.Of(container);
        return container;
    }

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

    /// <summary>Tags the text inside as a level 1 heading; the same as <c>Heading(1)</c>.</summary>
    public static IContainer SemanticHeading1(this IContainer container) => Heading(container, 1);

    /// <summary>Tags the text inside as a level 2 heading; the same as <c>Heading(2)</c>.</summary>
    public static IContainer SemanticHeading2(this IContainer container) => Heading(container, 2);

    /// <summary>Tags the text inside as a level 3 heading; the same as <c>Heading(3)</c>.</summary>
    public static IContainer SemanticHeading3(this IContainer container) => Heading(container, 3);

    /// <summary>Tags the text inside as a level 4 heading; the same as <c>Heading(4)</c>.</summary>
    public static IContainer SemanticHeading4(this IContainer container) => Heading(container, 4);

    /// <summary>Tags the text inside as a level 5 heading; the same as <c>Heading(5)</c>.</summary>
    public static IContainer SemanticHeading5(this IContainer container) => Heading(container, 5);

    /// <summary>Tags the text inside as a level 6 heading; the same as <c>Heading(6)</c>.</summary>
    public static IContainer SemanticHeading6(this IContainer container) => Heading(container, 6);

    /// <summary>
    /// Tags the content as computer code (Code) in PDF/UA. It keeps the font around it; give it a monospaced
    /// <c>FontFamily</c> to look like code.
    /// </summary>
    public static IContainer SemanticCode(this IContainer container) => Semantic(container, "code");

    /// <summary>
    /// Tags the content as a short quotation (Quote) in PDF/UA, inside other text; <see cref="SemanticBlockQuotation"/>
    /// is for whole paragraphs. No quotation marks are added.
    /// </summary>
    public static IContainer SemanticQuote(this IContainer container) => Semantic(container, "q");

    /// <summary>The language of the content, such as "de"; the same as <c>Language(language)</c>.</summary>
    /// <exception cref="ArgumentException">The language is not a valid language tag.</exception>
    public static IContainer SemanticLanguage(this IContainer container, string language) => Language(container, language);

    /// <summary>
    /// Groups content (Div). PdfPecker tags content by what it is and leaves plain groups out of the tag tree, which
    /// PDF/UA allows, so this changes nothing; it is here so QuestPDF code ports unchanged.
    /// </summary>
    public static IContainer SemanticDivision(this IContainer container)
    {
        Container.Of(container);
        return container;
    }

    /// <summary>
    /// Groups inline text (Span). As with <see cref="SemanticDivision"/>, PdfPecker leaves plain groups out of the tag
    /// tree, so this changes nothing and the alternative text is not written; it is here so QuestPDF code ports
    /// unchanged.
    /// </summary>
    public static IContainer SemanticSpan(this IContainer container, string? alternativeText = null)
    {
        Container.Of(container);
        return container;
    }

    /// <summary>
    /// Tags a table. A <c>Table</c> is tagged as a table (Table) with its header, body and footer rows already, so
    /// this changes nothing; it is here so QuestPDF code ports unchanged.
    /// </summary>
    public static IContainer SemanticTable(this IContainer container)
    {
        Container.Of(container);
        return container;
    }

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

    private static Container Tagging(IContainer container, params (string Name, string Value)[] attributes)
        => Decorate(container, new Decorator(DecoratorKind.Tagging) { Declarations = attributes });

    private static Container Semantic(IContainer container, string tag)
        => Decorate(container, new Decorator(DecoratorKind.Semantic) { Text = tag });
}
