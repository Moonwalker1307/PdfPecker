using System.Buffers;

namespace PdfPecker.Markdown;

// Plain text, line endings, backslash escapes and entity references.
internal sealed partial class InlineParser
{
    // The characters that may start an inline construct; plain text runs up to the next of them.
    private static readonly SearchValues<char> SpecialCharacters = SearchValues.Create("\n\\`*_~[!]<&");

    // Two spaces or more before a line ending make it a hard line break.
    private const int HardBreakSpaces = 2;

    /// <summary>Appends the run of characters at the cursor that start nothing; the first of them is known to be one.</summary>
    private void ParseText(MdNode block)
    {
        int start = _cursor.Position;
        int length = _cursor.Rest.IndexOfAny(SpecialCharacters);
        if (length < 0) length = _cursor.Text.Length - start;
        _cursor.Advance(length);
        block.AppendChild(Text(_cursor.Text.Substring(start, length)));
    }

    private void ParseLineEnding(MdNode block)
    {
        _cursor.Advance();
        block.AppendChild(new MdNode(TrimSpacesBeforeLineEnding(block) ? MdNodeType.LineBreak : MdNodeType.SoftBreak));
        // The leading spaces of the next line are not part of its text.
        _cursor.ConsumeRun(' ');
    }

    /// <summary>Removes the spaces that end the text before a line ending and returns whether they make a hard break.</summary>
    private static bool TrimSpacesBeforeLineEnding(MdNode block)
    {
        if (block.LastChild is not { Type: MdNodeType.Text } last || !last.Literal!.EndsWith(' ')) return false;
        var text = last.Literal;
        last.Literal = text.TrimEnd(' ');
        return text.Length >= HardBreakSpaces && text[^HardBreakSpaces] == ' ';
    }

    /// <summary>A backslash escapes ASCII punctuation, and before a line ending makes a hard break; otherwise it is literal.</summary>
    private void ParseBackslash(MdNode block)
    {
        _cursor.Advance();
        char next = _cursor.Current;
        if (next == '\n')
        {
            _cursor.Advance();
            block.AppendChild(new MdNode(MdNodeType.LineBreak));
        }
        else if (MdUtil.IsAsciiPunctuation(next))
        {
            _cursor.Advance();
            block.AppendChild(Text(next.ToString()));
        }
        else block.AppendChild(Text("\\"));
    }

    private bool TryParseEntity(MdNode block)
    {
        if (!MdUtil.TryDecodeEntity(_cursor.Text, _cursor.Position, out var value, out int length)) return false;
        block.AppendChild(Text(value));
        _cursor.Advance(length);
        return true;
    }
}
