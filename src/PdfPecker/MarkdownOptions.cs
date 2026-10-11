namespace PdfPecker;

/// <summary>Options for <see cref="MarkdownToHtml"/> and <see cref="MarkdownToPdf"/>.</summary>
public sealed class MarkdownOptions
{
    /// <summary>
    /// Enable the GitHub Flavored Markdown extensions: tables, strikethrough (<c>~~text~~</c>), task list items
    /// (<c>- [x] done</c>) and bare URL autolinks. Default: true. When false, the parser follows CommonMark strictly.
    /// </summary>
    public bool GitHubExtensions { get; set; } = true;

    /// <summary>Give headings <c>id</c> attributes (GitHub-style slugs) so <c>[link](#heading-text)</c> works. Default: true.</summary>
    public bool HeadingIds { get; set; } = true;

    /// <summary>
    /// Pass HTML written inside the Markdown through to the output. Default: true, as CommonMark specifies.
    /// When false, raw HTML is shown as text.
    /// </summary>
    public bool AllowRawHtml { get; set; } = true;

    /// <summary>
    /// CSS used by <see cref="MarkdownToHtml.ConvertDocument"/> and <see cref="MarkdownToPdf"/>. Null (the default) uses
    /// <see cref="MarkdownToHtml.DefaultStyleSheet"/>, a clean GitHub-like style; an empty string uses no styles at all.
    /// </summary>
    public string? StyleSheet { get; set; }

    /// <summary>Document title. Defaults to the text of the first heading.</summary>
    public string? Title { get; set; }
}
