using System.Text;

namespace PdfPecker.Markdown;

// Closing blocks, link reference definitions, and the inline pass over the finished tree.
internal sealed partial class BlockParser
{
    /// <summary>Closes a block: its collected text becomes its final content, and the parser's tip moves up to its parent.</summary>
    private void Finalize(MdNode block, int lineNumber)
    {
        var parent = block.Parent;
        block.Open = false;
        block.EndLine = lineNumber;
        switch (block.Type)
        {
            case MdNodeType.Paragraph:
                FinalizeParagraph(block);
                break;
            case MdNodeType.CodeBlock:
                if (block.IsFenced) FinalizeFencedCode(block);
                else FinalizeIndentedCode(block, lineNumber);
                block.Content = null;
                break;
            case MdNodeType.HtmlBlock:
                FinalizeHtmlBlock(block);
                break;
            case MdNodeType.Item:
                block.EndLine = block.LastChild?.EndLine ?? block.StartLine;
                break;
            case MdNodeType.List:
                FinalizeList(block);
                break;
        }
        _tip = parent!;
    }

    /// <summary>Takes the link reference definitions off the start of a paragraph; a paragraph of nothing else disappears.</summary>
    private void FinalizeParagraph(MdNode paragraph)
    {
        var content = paragraph.Content?.ToString() ?? "";
        var rest = StripReferenceDefinitions(content);
        if (rest.Length == content.Length) return;
        if (MdUtil.IsBlank(rest)) paragraph.Unlink();
        else paragraph.Content = new StringBuilder(rest);
    }

    /// <summary>The first line of a fenced code block is its info string.</summary>
    private static void FinalizeFencedCode(MdNode block)
    {
        var content = block.Content?.ToString() ?? "";
        int newline = content.IndexOf('\n');
        var firstLine = newline < 0 ? content : content.Substring(0, newline);
        block.Info = MdUtil.Unescape(firstLine.Trim(' ', '\t'));
        block.Literal = newline < 0 ? "" : content.Substring(newline + 1);
    }

    private static void FinalizeIndentedCode(MdNode block, int lineNumber)
    {
        // Trailing blank lines are not part of an indented code block.
        var lines = (block.Content?.ToString() ?? "").Split('\n');
        int kept = lines.Length;
        while (kept > 0 && MdUtil.IsBlank(lines[kept - 1])) kept--;
        int blankLines = Math.Max(0, lines.Length - 1 - kept);
        block.Literal = string.Join("\n", lines, 0, kept) + "\n";
        block.EndLine = Math.Max(block.StartLine, lineNumber - blankLines);
    }

    private static void FinalizeHtmlBlock(MdNode block)
    {
        var content = block.Content?.ToString() ?? "";
        // Drop the trailing newlines; spaces on the last line are kept.
        int end = content.Length;
        while (end > 0 && content[end - 1] == '\n') end--;
        block.Literal = content.Substring(0, end);
        block.Content = null;
    }

    /// <summary>A list is loose when a blank line separates two of its items, or two blocks inside an item.</summary>
    private static void FinalizeList(MdNode list)
    {
        var data = list.ListData!;
        for (var item = list.FirstChild; item != null && data.Tight; item = item.Next)
        {
            if (item.Next != null && EndsWithBlankLine(item)) { data.Tight = false; break; }
            for (var child = item.FirstChild; child != null; child = child.Next)
            {
                if (child.Next != null && EndsWithBlankLine(child)) { data.Tight = false; break; }
            }
        }
        if (list.LastChild != null) list.EndLine = list.LastChild.EndLine;
    }

    private static bool EndsWithBlankLine(MdNode block)
        => block.Next != null && block.EndLine != block.Next.StartLine - 1;

    /// <summary>Registers the link reference definitions at the start of <paramref name="content"/> and returns the rest.</summary>
    private string StripReferenceDefinitions(string content)
    {
        int position = 0;
        while (position < content.Length && content[position] == '[')
        {
            int consumed = _inlines.ParseReference(content, position);
            if (consumed == 0) break;
            position += consumed;
        }
        return position == 0 ? content : content.Substring(position);
    }

    // ---- Inlines ---------------------------------------------------------------------------------------------------

    private void ProcessInlines(MdNode root)
    {
        // Depth-first walk without recursion, so deeply nested input cannot overflow the stack.
        var pending = new Stack<MdNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (HoldsInlines(node.Type))
            {
                if (node.Type == MdNodeType.Paragraph && IsFirstChildOfItem(node)) ExtractTaskMarker(node);
                _inlines.Parse(node);
                continue;
            }
            for (var child = node.LastChild; child != null; child = child.Prev) pending.Push(child);
        }
    }

    private static bool HoldsInlines(MdNodeType type) => type is MdNodeType.Paragraph or MdNodeType.Heading or MdNodeType.TableCell;

    private bool IsFirstChildOfItem(MdNode paragraph)
        => _gfm && paragraph.Parent?.Type == MdNodeType.Item && paragraph.Parent.FirstChild == paragraph;

    /// <summary>Turns a leading "[ ]", "[x]" or "[X]" of a list item into its checked state.</summary>
    private static void ExtractTaskMarker(MdNode paragraph)
    {
        const int MarkerLength = 4; // "[x] ": the box and the whitespace after it
        var text = paragraph.Content;
        if (text == null || text.Length < MarkerLength || text[0] != '[' || text[2] != ']' || !(text[3] is ' ' or '\t' or '\n')) return;
        char mark = text[1];
        if (mark is not (' ' or 'x' or 'X')) return;
        paragraph.Parent!.Checked = mark != ' ';
        text.Remove(0, MarkerLength);
    }
}
