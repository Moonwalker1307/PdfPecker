namespace PdfPecker.Markdown;

// List items: parsing the marker and starting the list and item blocks.
internal sealed partial class BlockParser
{
    /// <summary>The start number of an ordered list has at most nine digits.</summary>
    private const int MaxOrderedListDigits = 9;

    /// <summary>
    /// With this many columns of space after the marker the content is an indented code block, so the marker is
    /// followed by one space and the rest belongs to the content.
    /// </summary>
    private const int MinSpacesForCodeAfterMarker = CodeIndent + 1;

    private BlockStart ListItemStart(MdNode container)
    {
        if (_cursor.IsIndented && container.Type != MdNodeType.List) return BlockStart.None;
        var data = ParseListMarker(container);
        if (data == null) return BlockStart.None;

        CloseUnmatchedBlocks();
        if (_tip.Type != MdNodeType.List || !ListsMatch(container.ListData!, data))
        {
            var list = AddChild(MdNodeType.List);
            list.ListData = data;
        }
        var item = AddChild(MdNodeType.Item);
        item.ListData = data;
        return BlockStart.Container;
    }

    /// <summary>Items belong to the same list when they use the same kind of marker.</summary>
    private static bool ListsMatch(ListData a, ListData b)
        => a.Ordered == b.Ordered && a.Delimiter == b.Delimiter && a.BulletChar == b.BulletChar;

    /// <summary>
    /// Reads a list marker at the next non-space character and consumes it with the spaces after it. Returns null, with
    /// nothing consumed, when there is no marker that may start an item here.
    /// </summary>
    private ListData? ParseListMarker(MdNode container)
    {
        if (_cursor.Indent >= CodeIndent) return null;
        var data = new ListData { MarkerOffset = _cursor.Indent };
        int markerStart = _cursor.NextNonspace;
        if (!TryReadMarker(container, markerStart, data, out int markerLength)) return null;

        int afterMarker = markerStart + markerLength;
        char next = _cursor.Peek(afterMarker);
        if (!(next == '\0' || next == '\t' || next == ' ')) return null;
        // A list item interrupting a paragraph cannot start with a blank line.
        if (container.Type == MdNodeType.Paragraph && MdUtil.IsBlank(_cursor.Line, afterMarker)) return null;

        ConsumeMarker(data, markerLength);
        return data;
    }

    /// <summary>Reads a bullet ("*", "+" or "-") or an ordered marker ("1." or "1)") into <paramref name="data"/>.</summary>
    private bool TryReadMarker(MdNode container, int start, ListData data, out int markerLength)
    {
        markerLength = 0;
        char c = _cursor.Peek(start);
        if (c is '*' or '+' or '-')
        {
            data.BulletChar = c;
            markerLength = 1;
            return true;
        }

        int digits = 0, number = 0;
        while (digits <= MaxOrderedListDigits && char.IsAsciiDigit(_cursor.Peek(start + digits)))
        {
            number = number * 10 + (_cursor.Peek(start + digits) - '0');
            digits++;
        }
        if (digits is 0 or > MaxOrderedListDigits) return false;
        char delimiter = _cursor.Peek(start + digits);
        if (delimiter != '.' && delimiter != ')') return false;
        // An ordered list can only interrupt a paragraph when it starts with 1.
        if (container.Type == MdNodeType.Paragraph && number != 1) return false;

        data.Ordered = true;
        data.Start = number;
        data.Delimiter = delimiter;
        markerLength = digits + 1;
        return true;
    }

    /// <summary>Consumes the marker and the spaces after it, and records how far the item's content is indented.</summary>
    private void ConsumeMarker(ListData data, int markerLength)
    {
        _cursor.AdvanceToNextNonspace();
        _cursor.Advance(markerLength, columns: true);
        int spacesStartColumn = _cursor.Column, spacesStartOffset = _cursor.Offset;
        do
        {
            _cursor.Advance(1, columns: true);
        } while (_cursor.Column - spacesStartColumn < MinSpacesForCodeAfterMarker && _cursor.PeekIsSpaceOrTab(_cursor.Offset));

        bool blankItem = _cursor.AtEnd;
        int spacesAfterMarker = _cursor.Column - spacesStartColumn;
        if (spacesAfterMarker >= MinSpacesForCodeAfterMarker || spacesAfterMarker < 1 || blankItem)
        {
            data.Padding = markerLength + 1;
            _cursor.Rewind(spacesStartOffset, spacesStartColumn);
            if (_cursor.PeekIsSpaceOrTab(_cursor.Offset)) _cursor.Advance(1, columns: true);
        }
        else data.Padding = markerLength + spacesAfterMarker;
    }
}
