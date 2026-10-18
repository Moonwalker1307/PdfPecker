namespace PdfPecker.Markdown;

// Autolinks in angle brackets and raw inline HTML, which both start with "<".
internal sealed partial class InlineParser
{
    private bool TryParseAutolink(MdNode block)
    {
        var match = MdRegex.EmailAutolink().Match(_cursor.Text, _cursor.Position);
        if (match.Success)
        {
            var address = match.Groups[1].Value;
            block.AppendChild(AutolinkNode(MdUtil.NormalizeUri("mailto:" + address), address));
            _cursor.Advance(match.Length);
            return true;
        }
        match = MdRegex.UriAutolink().Match(_cursor.Text, _cursor.Position);
        if (match.Success)
        {
            var uri = match.Groups[1].Value;
            block.AppendChild(AutolinkNode(MdUtil.NormalizeUri(uri), uri));
            _cursor.Advance(match.Length);
            return true;
        }
        return false;
    }

    /// <summary>A link without a title whose text is the address as written.</summary>
    private static MdNode AutolinkNode(string destination, string text)
    {
        var link = new MdNode(MdNodeType.Link) { Destination = destination, Title = "" };
        link.AppendChild(Text(text));
        return link;
    }

    private bool TryParseHtmlTag(MdNode block)
    {
        var match = MdRegex.InlineHtml().Match(_cursor.Text, _cursor.Position);
        if (!match.Success) return false;
        block.AppendChild(new MdNode(MdNodeType.HtmlInline) { Literal = match.Value });
        _cursor.Advance(match.Length);
        return true;
    }
}
