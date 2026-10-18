namespace PdfPecker.Markdown;

// Links and images: "[" and "![" go on a bracket stack, and a "]" closes the nearest one when an inline destination or a
// known reference follows it.
internal sealed partial class InlineParser
{
    /// <summary>An opening "[" or "![" that a later "]" may close.</summary>
    private sealed class Bracket
    {
        /// <summary>The text node that holds the bracket; what follows it becomes the link text.</summary>
        public MdNode Node = null!;

        public Bracket? Previous;

        /// <summary>The top of the delimiter stack when the bracket opened: emphasis in the link text is matched down to it.</summary>
        public Delimiter? PreviousDelimiter;

        /// <summary>The index of the "[", where the link text starts when it is also the reference label.</summary>
        public int Index;

        public bool Image;

        /// <summary>True when another bracket opened after this one; a label may not contain brackets, so the link text is then no label.</summary>
        public bool BracketAfter;
    }

    /// <summary>The top of the bracket stack.</summary>
    private Bracket? _brackets;

    /// <summary>
    /// A "[" that opened before this index can no longer open a link, because a link has closed after it: links may not
    /// contain other links. Brackets open in the order of their index, so one bound marks every bracket that a closing
    /// link deactivates, where marking each of them would walk the whole stack again for every link.
    /// </summary>
    private int _linksInactiveBefore;

    /// <summary>Whether the bracket may still open a link or an image; images may contain links.</summary>
    private bool IsActive(Bracket bracket) => bracket.Image || bracket.Index >= _linksInactiveBefore;

    private void ParseOpenBracket(MdNode block)
    {
        int index = _cursor.Position;
        if (TryParseFootnoteReference(block)) return;
        _cursor.Advance();
        OpenBracket(block, "[", index, image: false);
    }

    private void ParseBang(MdNode block)
    {
        _cursor.Advance();
        if (_cursor.TryConsume('[')) OpenBracket(block, "![", _cursor.Position - 1, image: true);
        else block.AppendChild(Text("!"));
    }

    private void OpenBracket(MdNode block, string text, int index, bool image)
    {
        var node = Text(text);
        block.AppendChild(node);
        if (_brackets != null) _brackets.BracketAfter = true;
        _brackets = new Bracket { Node = node, Previous = _brackets, PreviousDelimiter = _delimiters, Index = index, Image = image };
    }

    private void ParseCloseBracket(MdNode block)
    {
        _cursor.Advance();
        int afterBracket = _cursor.Position;
        var opener = _brackets;
        if (opener == null)
        {
            block.AppendChild(Text("]"));
            return;
        }
        if (!IsActive(opener))
        {
            // The bracket can no longer open a link, so both brackets stay literal.
            block.AppendChild(Text("]"));
            _brackets = opener.Previous;
            return;
        }

        if (!TryParseInlineLinkTail(out var destination, out var title)
            && !TryParseReferenceLinkTail(opener, afterBracket, out destination, out title))
        {
            _brackets = opener.Previous;
            _cursor.Position = afterBracket;
            block.AppendChild(Text("]"));
            return;
        }
        CloseLink(block, opener, destination, title);
    }

    /// <summary>
    /// A footnote reference, "[^label]" for a label that has a definition. As in markdown-it it comes before links, so it
    /// opens no bracket; an undefined label stays text.
    /// </summary>
    private bool TryParseFootnoteReference(MdNode block)
    {
        if (_footnotes == null || _footnotes.Count == 0) return false;
        int length = BlockParser.FootnoteLabelLength(_cursor.Rest, 0);
        if (length == 0) return false;
        var label = MdUtil.NormalizeFootnoteLabel(_cursor.Rest.Slice(2, length));
        if (!_footnotes.ContainsKey(label)) return false;
        _cursor.Advance(length + 3);
        block.AppendChild(new MdNode(MdNodeType.FootnoteReference) { Label = label });
        return true;
    }

    /// <summary>The destination and optional title of an inline link: <c>[text](destination "title")</c>.</summary>
    private bool TryParseInlineLinkTail(out string destination, out string? title)
    {
        destination = "";
        title = null;
        int start = _cursor.Position;
        if (!_cursor.TryConsume('(')) return false;
        SkipLinkWhitespace();
        if (ParseLinkDestination() is { } parsed)
        {
            SkipLinkWhitespace();
            // Whitespace must separate the title from the destination.
            if (MdUtil.IsUnicodeWhitespace(_cursor.Previous)) title = ParseLinkTitle();
            SkipLinkWhitespace();
            if (_cursor.TryConsume(')'))
            {
                destination = parsed;
                return true;
            }
        }
        _cursor.Position = start;
        return false;
    }

    /// <summary>
    /// The definition a reference link names: <c>[text][label]</c>, or with an empty label or none, the link text itself.
    /// The cursor ends after the label when there is a definition.
    /// </summary>
    private bool TryParseReferenceLinkTail(Bracket opener, int afterBracket, out string destination, out string? title)
    {
        int labelStart = _cursor.Position;
        int labelLength = ParseLinkLabel();
        string? label = labelLength > EmptyLabelLength ? _cursor.Text.Substring(labelStart, labelLength)
            : !opener.BracketAfter ? _cursor.Text.Substring(opener.Index, afterBracket - opener.Index)
            : null;
        if (label != null && _refmap.TryGetValue(MdUtil.NormalizeReference(label), out var reference))
        {
            destination = reference.Destination;
            title = reference.Title;
            return true;
        }
        destination = "";
        title = null;
        return false;
    }

    /// <summary>Makes everything after the opening bracket the text of a new link or image.</summary>
    private void CloseLink(MdNode block, Bracket opener, string destination, string? title)
    {
        var link = new MdNode(opener.Image ? MdNodeType.Image : MdNodeType.Link) { Destination = destination, Title = title ?? "" };
        MoveSiblingsInto(link, opener.Node.Next, null);
        block.AppendChild(link);
        ProcessEmphasis(opener.PreviousDelimiter);
        _brackets = opener.Previous;
        opener.Node.Unlink();

        // Links may not contain other links, so the brackets before a link can no longer open one; images may.
        if (!opener.Image) _linksInactiveBefore = _cursor.Position;
    }
}
