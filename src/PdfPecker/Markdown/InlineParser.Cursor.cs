namespace PdfPecker.Markdown;

internal sealed partial class InlineParser
{
    /// <summary>
    /// The read position in the text whose inlines are being parsed. Unlike the block parser's <see cref="LineCursor"/>
    /// it spans several lines and needs no columns, because tabs mean nothing to inline syntax. Looking past the end gives
    /// NUL, which starts no inline construct, so look-ahead needs no bounds checks. It is a mutable struct that lives in a
    /// field of the parser, so that parsing allocates nothing for it.
    /// </summary>
    private struct SubjectCursor
    {
        /// <summary>The text being parsed; <see cref="Start"/> must have been called.</summary>
        public string Text { get; private set; }

        /// <summary>The index of the first character that has not been consumed; parsers set it back when they backtrack.</summary>
        public int Position { get; set; }

        public void Start(string text, int position = 0)
        {
            Text = text;
            Position = position;
        }

        public readonly bool AtEnd => Position >= Text.Length;

        /// <summary>The character at the cursor, or NUL at the end.</summary>
        public readonly char Current => Position < Text.Length ? Text[Position] : '\0';

        /// <summary>The character just consumed; at least one must have been.</summary>
        public readonly char Previous => Text[Position - 1];

        /// <summary>The text that has not been consumed.</summary>
        public readonly ReadOnlySpan<char> Rest => Text.AsSpan(Position);

        public void Advance(int count = 1) => Position += count;

        /// <summary>Consumes <paramref name="c"/>, which must not be NUL, when it is the next character.</summary>
        public bool TryConsume(char c)
        {
            if (Current != c) return false;
            Position++;
            return true;
        }

        /// <summary>Consumes the run of <paramref name="c"/> at the cursor and returns its length.</summary>
        public int ConsumeRun(char c)
        {
            // Runs are mostly one to three characters long, too short for a vectorised search to pay off.
            int start = Position;
            while (Position < Text.Length && Text[Position] == c) Position++;
            return Position - start;
        }

        public void SkipSpacesAndTabs()
        {
            while (MdUtil.IsSpaceOrTab(Current)) Position++;
        }
    }
}
