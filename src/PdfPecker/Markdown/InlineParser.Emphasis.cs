using System.Runtime.CompilerServices;

namespace PdfPecker.Markdown;

// Emphasis, strong emphasis and GFM strikethrough: runs of *, _ and ~ go on a delimiter stack, and closers are matched
// with openers by the CommonMark algorithm once the inlines they may enclose are known.
internal sealed partial class InlineParser
{
    /// <summary>A run of delimiter characters that may open or close emphasis; the runs form a doubly linked stack.</summary>
    private sealed class Delimiter
    {
        public char Char;

        /// <summary>The delimiters of the run that emphasis has not used yet.</summary>
        public int Remaining;

        /// <summary>The length of the run as written, which the rule of three looks at.</summary>
        public int OriginalLength;

        /// <summary>The text node that holds the run.</summary>
        public MdNode Node = null!;

        public Delimiter? Previous, Next;
        public bool CanOpen, CanClose;

        /// <summary>True once emphasis has used some of the run, so that its text must be shortened at the end.</summary>
        public bool PartlyUsed;
    }

    // GFM strikethrough takes runs of one or two tildes; longer runs are literal text.
    private const int MaxStrikethroughRun = 2;

    // Strong emphasis takes two delimiters from each side, emphasis one.
    private const int StrongDelimiters = 2;

    // How far back a closer needs to look for an opener depends on its character, whether it can also open and its
    // length modulo 3 (the rule of three); for strikethrough only on whether it is one tilde or two. Each combination has
    // a slot of its own.
    private const int RuleOfThree = 3;
    private const int SlotsPerEmphasisChar = 2 * RuleOfThree;
    private const int StrikethroughSlot = 2 * SlotsPerEmphasisChar;
    private const int OpenersBottomSlots = StrikethroughSlot + MaxStrikethroughRun;

    /// <summary>
    /// For each slot (see <see cref="OpenersBottomSlot"/>), the delimiter below which no opener for such a closer can be
    /// found. It is an inline array, so that matching emphasis, which runs for every block and every link, keeps it on the
    /// stack.
    /// </summary>
    [InlineArray(OpenersBottomSlots)]
    private struct OpenersBottom
    {
        private Delimiter? _element;
    }

    /// <summary>The top of the delimiter stack.</summary>
    private Delimiter? _delimiters;

    // Runs that emphasis used in part; their text is shortened once at the end instead of after every match.
    private readonly List<Delimiter> _partlyUsed = new();

    private void ParseDelimiterRun(char c, MdNode block)
    {
        int start = _cursor.Position;
        int length = _cursor.ConsumeRun(c);
        // The start and the end of the text count as line endings.
        char before = start == 0 ? '\n' : _cursor.Text[start - 1];
        char after = _cursor.AtEnd ? '\n' : _cursor.Current;
        var (canOpen, canClose) = OpensAndCloses(c, length, before, after);

        var node = Text(_cursor.Text.Substring(start, length));
        block.AppendChild(node);
        if (canOpen || canClose) PushDelimiter(c, length, node, canOpen, canClose);
    }

    /// <summary>Whether a run can open and close emphasis, by whether it is left- and right-flanking.</summary>
    private static (bool CanOpen, bool CanClose) OpensAndCloses(char c, int length, char before, char after)
    {
        bool afterSpace = MdUtil.IsUnicodeWhitespace(after), afterPunctuation = MdUtil.IsPunctuation(after);
        bool beforeSpace = MdUtil.IsUnicodeWhitespace(before), beforePunctuation = MdUtil.IsPunctuation(before);
        bool leftFlanking = !afterSpace && (!afterPunctuation || beforeSpace || beforePunctuation);
        bool rightFlanking = !beforeSpace && (!beforePunctuation || afterSpace || afterPunctuation);
        return c switch
        {
            // An underscore run inside a word neither opens nor closes.
            '_' => (leftFlanking && (!rightFlanking || beforePunctuation), rightFlanking && (!leftFlanking || afterPunctuation)),
            '~' => (length <= MaxStrikethroughRun && leftFlanking, length <= MaxStrikethroughRun && rightFlanking),
            _ => (leftFlanking, rightFlanking),
        };
    }

    private void PushDelimiter(char c, int length, MdNode node, bool canOpen, bool canClose)
    {
        var delimiter = new Delimiter
        {
            Char = c, Remaining = length, OriginalLength = length, Node = node, Previous = _delimiters,
            CanOpen = canOpen, CanClose = canClose,
        };
        if (_delimiters != null) _delimiters.Next = delimiter;
        _delimiters = delimiter;
    }

    private void RemoveDelimiter(Delimiter d)
    {
        if (d.Previous != null) d.Previous.Next = d.Next;
        if (d.Next == null) _delimiters = d.Previous;
        else d.Next.Previous = d.Previous;
    }

    private static void RemoveDelimitersBetween(Delimiter bottom, Delimiter top)
    {
        if (bottom.Next == top) return;
        bottom.Next = top;
        top.Previous = bottom;
    }

    /// <summary>
    /// Matches each closer above <paramref name="stackBottom"/> with the nearest opener that can pair with it, wraps what
    /// lies between them, and then empties the stack down to <paramref name="stackBottom"/>.
    /// </summary>
    private void ProcessEmphasis(Delimiter? stackBottom)
    {
        var openersBottom = new OpenersBottom();
        ((Span<Delimiter?>)openersBottom).Fill(stackBottom);

        // Start at the first delimiter above the stack bottom. When the bottom is the top there is none, which is the
        // usual case for a link without emphasis in its text; the walk down would find none either, but would cost time
        // in proportion to the whole stack for every such link.
        var closer = _delimiters == stackBottom ? null : _delimiters;
        while (closer != null && closer.Previous != stackBottom) closer = closer.Previous;

        while (closer != null)
        {
            if (!closer.CanClose)
            {
                closer = closer.Next;
                continue;
            }
            int slot = OpenersBottomSlot(closer);
            var opener = FindOpener(closer, stackBottom, openersBottom[slot]);
            if (opener != null)
            {
                closer = MatchEmphasis(opener, closer);
                continue;
            }
            // Later searches for this kind of closer need not look further back than here.
            openersBottom[slot] = closer.Previous;
            var next = closer.Next;
            if (!closer.CanOpen) RemoveDelimiter(closer);
            closer = next;
        }

        while (_delimiters != null && _delimiters != stackBottom) RemoveDelimiter(_delimiters);
    }

    private static int OpenersBottomSlot(Delimiter closer) => closer.Char switch
    {
        '_' => EmphasisSlot(closer),
        '*' => SlotsPerEmphasisChar + EmphasisSlot(closer),
        _ => StrikethroughSlot + (closer.OriginalLength == 2 ? 1 : 0),
    };

    private static int EmphasisSlot(Delimiter closer) => (closer.CanOpen ? RuleOfThree : 0) + closer.OriginalLength % RuleOfThree;

    /// <summary>The nearest opener below <paramref name="closer"/> that can pair with it, searching down to the first of the two bottoms.</summary>
    private static Delimiter? FindOpener(Delimiter closer, Delimiter? stackBottom, Delimiter? searchBottom)
    {
        for (var opener = closer.Previous; opener != null && opener != stackBottom && opener != searchBottom; opener = opener.Previous)
        {
            if (opener.Char == closer.Char && opener.CanOpen && CanPair(opener, closer)) return opener;
        }
        return null;
    }

    private static bool CanPair(Delimiter opener, Delimiter closer)
    {
        // Strikethrough pairs runs of the same length.
        if (closer.Char == '~') return opener.Remaining == closer.Remaining;
        // The rule of three: when one of the runs can both open and close, the lengths may not add up to a multiple of
        // three unless both are multiples of three.
        bool eitherCanBoth = closer.CanOpen || opener.CanClose;
        return !(eitherCanBoth && closer.OriginalLength % RuleOfThree != 0 && (opener.OriginalLength + closer.OriginalLength) % RuleOfThree == 0);
    }

    /// <summary>
    /// Wraps the inlines between an opener and a closer in emphasis, strong emphasis or strikethrough and uses up the
    /// delimiters that took. Returns the closer to look at next: the same one while it has delimiters left.
    /// </summary>
    private Delimiter? MatchEmphasis(Delimiter opener, Delimiter closer)
    {
        bool strikethrough = closer.Char == '~';
        int used = strikethrough ? closer.Remaining
            : closer.Remaining >= StrongDelimiters && opener.Remaining >= StrongDelimiters ? StrongDelimiters : 1;
        opener.Remaining -= used;
        closer.Remaining -= used;
        MarkPartlyUsed(opener);
        MarkPartlyUsed(closer);

        var wrapper = new MdNode(strikethrough ? MdNodeType.Strikethrough : used == 1 ? MdNodeType.Emph : MdNodeType.Strong);
        MoveSiblingsInto(wrapper, opener.Node.Next, closer.Node);
        opener.Node.InsertAfter(wrapper);

        RemoveDelimitersBetween(opener, closer);
        if (opener.Remaining == 0)
        {
            opener.Node.Unlink();
            RemoveDelimiter(opener);
        }
        if (closer.Remaining > 0) return closer;
        closer.Node.Unlink();
        var next = closer.Next;
        RemoveDelimiter(closer);
        return next;
    }

    private void MarkPartlyUsed(Delimiter delimiter)
    {
        if (delimiter.PartlyUsed) return;
        delimiter.PartlyUsed = true;
        _partlyUsed.Add(delimiter);
    }

    private void ShortenPartlyUsedDelimiters()
    {
        foreach (var d in _partlyUsed) d.Node.Literal = new string(d.Char, d.Remaining);
        _partlyUsed.Clear();
    }
}
