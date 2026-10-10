using System.Text.RegularExpressions;

namespace PdfPecker.Markdown;

/// <summary>The few patterns that are clearer as regular expressions; source-generated so they cost nothing at startup.</summary>
internal static partial class MdRegex
{
    private const string TagName = "[A-Za-z][A-Za-z0-9-]*";
    private const string AttributeName = "[a-zA-Z_:][a-zA-Z0-9_.:-]*";
    private const string AttributeValue = "(?:[^\"'=<>`\\x00-\\x20]+|'[^']*'|\"[^\"]*\")";
    private const string Attribute = "(?:\\s+" + AttributeName + "(?:\\s*=\\s*" + AttributeValue + ")?)";
    private const string OpenTag = "<" + TagName + Attribute + "*\\s*/?>";
    private const string CloseTag = "</" + TagName + "\\s*[>]";
    private const string HtmlTag = "(?:" + OpenTag + "|" + CloseTag + "|<!-->|<!--->|<!--[\\s\\S]*?-->|[<][?][\\s\\S]*?[?][>]"
        + "|<![A-Za-z]+[^>]*>|<!\\[CDATA\\[[\\s\\S]*?\\]\\]>)";

    [GeneratedRegex("\\G" + HtmlTag, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex InlineHtml();

    [GeneratedRegex("^(?:" + OpenTag + "|" + CloseTag + ")\\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex HtmlBlockOpen7();

    [GeneratedRegex("^<(?:script|pre|textarea|style)(?:\\s|>|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex HtmlBlockOpen1();

    [GeneratedRegex("^</?(?:address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt"
        + "|fieldset|figcaption|figure|footer|form|frame|frameset|h[123456]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem"
        + "|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)(?:\\s|/?>|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex HtmlBlockOpen6();

    [GeneratedRegex("</(?:script|pre|textarea|style)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex HtmlBlockClose1();

    [GeneratedRegex("\\G<([a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*)>",
        RegexOptions.CultureInvariant)]
    public static partial Regex EmailAutolink();

    [GeneratedRegex("\\G<([A-Za-z][A-Za-z0-9.+-]{1,31}:[^<>\\x00-\\x20]*)>", RegexOptions.CultureInvariant)]
    public static partial Regex UriAutolink();
}
