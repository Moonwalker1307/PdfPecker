namespace PdfPecker.Markdown;

// Continuation: does an open block carry on over the current line?
internal sealed partial class BlockParser
{
    /// <summary>A closing fence may be indented by at most three columns.</summary>
    private const int MaxFenceIndent = 3;

    private Continuation Continue(MdNode container)
    {
        switch (container.Type)
        {
            case MdNodeType.BlockQuote:
                return ContinueBlockQuote();
            case MdNodeType.Item:
            case MdNodeType.DefinitionDetails:
                return ContinueItem(container);
            case MdNodeType.FootnoteDefinition:
                return ContinueFootnoteDefinition();
            case MdNodeType.Heading:
            case MdNodeType.ThematicBreak:
                return Continuation.NotMatched;
            case MdNodeType.CodeBlock:
                return container.IsFenced ? ContinueFencedCode(container) : ContinueIndentedCode();
            case MdNodeType.HtmlBlock:
                // Block-level and other HTML blocks end at a blank line; the raw text kinds run to their closing marker.
                return _cursor.IsBlank && container.HtmlBlockType is HtmlBlockKind.BlockTag or HtmlBlockKind.AnyTag
                    ? Continuation.NotMatched
                    : Continuation.Matched;
            case MdNodeType.Paragraph:
            case MdNodeType.Table:
                return _cursor.IsBlank ? Continuation.NotMatched : Continuation.Matched;
            default:
                return Continuation.Matched;
        }
    }

    private Continuation ContinueBlockQuote()
    {
        if (_cursor.IsIndented || _cursor.NextNonspaceChar != '>') return Continuation.NotMatched;
        ConsumeBlockQuoteMarker();
        return Continuation.Matched;
    }

    /// <summary>Consumes a ">" and the single space or tab that may follow it.</summary>
    private void ConsumeBlockQuoteMarker()
    {
        _cursor.AdvanceToNextNonspace();
        _cursor.Advance(1, columns: false);
        if (_cursor.PeekIsSpaceOrTab(_cursor.Offset)) _cursor.Advance(1, columns: true);
    }

    private Continuation ContinueItem(MdNode item)
    {
        var data = item.ListData!;
        if (_cursor.IsBlank)
        {
            if (item.FirstChild == null) return Continuation.NotMatched; // a blank line after an empty item ends it
            _cursor.AdvanceToNextNonspace();
        }
        else if (_cursor.Indent >= data.MarkerOffset + data.Padding)
        {
            _cursor.Advance(data.MarkerOffset + data.Padding, columns: true);
        }
        else return Continuation.NotMatched;
        return Continuation.Matched;
    }

    private Continuation ContinueFencedCode(MdNode fence)
    {
        if (_cursor.Indent <= MaxFenceIndent && _cursor.NextNonspaceChar == fence.FenceChar)
        {
            int length = _cursor.CountRun(_cursor.NextNonspace, fence.FenceChar);
            if (length >= fence.FenceLength && MdUtil.IsBlank(_cursor.Line, _cursor.NextNonspace + length))
            {
                Finalize(fence, _lineNumber);
                return Continuation.LineConsumed;
            }
        }
        // Skip the optional spaces of the fence's own indentation.
        for (int i = fence.FenceOffset; i > 0 && _cursor.PeekIsSpaceOrTab(_cursor.Offset); i--) _cursor.Advance(1, columns: true);
        return Continuation.Matched;
    }

    private Continuation ContinueIndentedCode()
    {
        if (_cursor.Indent >= CodeIndent) _cursor.Advance(CodeIndent, columns: true);
        else if (_cursor.IsBlank) _cursor.AdvanceToNextNonspace();
        else return Continuation.NotMatched;
        return Continuation.Matched;
    }
}
