using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Text;

namespace PdfPecker.Layout;

/// <summary>
/// text-justify: where a justified line's free space goes. Between words, as always; with auto also between CJK
/// ideographs and symbols, with inter-character between every two characters, and with none nowhere. The opportunities
/// are Chrome's: a character takes space after it, or before it when the one before takes none, and a line's last
/// opportunity is dropped so that its text still ends at the line's end.
/// </summary>
internal sealed partial class LayoutEngine
{
    // Where an item of a justified line gets a share of the free space: before it, after it, and with Letters after each
    // of its characters but the last as well, so that it is drawn with that share as letter spacing.
    private const byte JustifyBefore = 1, JustifyAfter = 2, JustifyLetters = 4;

    // An inline image or inline-block counts as this character, as in Chrome.
    private const int ObjectReplacement = 0xFFFC;

    // A text item's grapheme clusters (their first characters) and their opportunities, reused between items.
    private int[] _clusterStart = new int[16];
    private byte[] _clusterJustify = new byte[16];
    private readonly List<Item> _justifyPieces = new();
    private bool _justifySplit; // whether JustifyText has split a word into pieces since AlignLine cleared it

    /// <summary>
    /// Finds the justification opportunities of a justified line and marks its items with them. Returns how many there
    /// are, or -1 when the line is justified at its spaces alone, as it always was: a right-to-left or bidirectional
    /// line, or one with no opportunity but its spaces under auto or inter-word. Words with opportunities inside that
    /// cannot be drawn with letter spacing are split into pieces at them.
    /// </summary>
    private int JustificationOpportunities(Line line, TextJustify mode, bool rtl)
    {
        if (mode == TextJustify.None) return 0;
        var items = line.Items;
        if (rtl) return -1;
        foreach (var it in items)
            if (it.Level != 0) return -1;

        var context = new JustificationContext(mode);
        int count = 0;
        bool betweenCharacters = false;
        Item? lastAfter = null;
        for (int k = 0; k < items.Count; k++)
        {
            var it = items[k];
            it.Justify = 0;
            int pieces = 1;
            switch (it.Kind)
            {
                case ItemKind.Space:
                    it.Justify = context.Check(' ');
                    break;
                case ItemKind.Image or ItemKind.Box:
                    it.Justify = context.Check(ObjectReplacement);
                    betweenCharacters |= it.Justify != 0;
                    break;
                case ItemKind.Text when it.Length > 0:
                    pieces = JustifyText(items, k, ref context);
                    break;
                default:
                    continue;
            }
            for (int p = k; p < k + pieces; p++)
            {
                var piece = items[p];
                byte j = piece.Justify;
                if (j == 0) continue;
                if (piece.Kind == ItemKind.Text) betweenCharacters = true;
                count += ((j & JustifyBefore) != 0 ? 1 : 0) + ((j & JustifyAfter) != 0 ? 1 : 0) + ((j & JustifyLetters) != 0 ? piece.Length - 1 : 0);
                if ((j & JustifyAfter) != 0) lastAfter = piece;
            }
            k += pieces - 1;
        }
        // Space after the line's last character would only move its end away from the line's end.
        if (context.IsAfter && lastAfter != null)
        {
            lastAfter.Justify &= unchecked((byte)~JustifyAfter);
            count--;
        }
        return betweenCharacters || mode == TextJustify.InterCharacter ? count : -1;
    }

    /// <summary>
    /// Marks the opportunities of the text item at <paramref name="k"/>, cluster by cluster. A word with opportunities
    /// between its characters is drawn with letter spacing when every pair of neighbouring characters has one and its
    /// glyphs are its characters; otherwise it is replaced by pieces split at them. Returns how many items it now is.
    /// </summary>
    private int JustifyText(List<Item> items, int k, ref JustificationContext context)
    {
        var it = items[k];
        string s = it.Source;
        int start = it.Start, n = it.Length;
        if (_clusterStart.Length <= n)
        {
            _clusterStart = new int[n * 2];
            _clusterJustify = new byte[n * 2];
        }
        int m = 0, previous = 0;
        for (int i = 0; i < n;)
        {
            int cp = s[start + i], units = 1;
            if (char.IsHighSurrogate(s[start + i]) && i + 1 < n && char.IsLowSurrogate(s[start + i + 1]))
            {
                cp = char.ConvertToUtf32(s[start + i], s[start + i + 1]);
                units = 2;
            }
            if (m == 0 || !JustificationCharacters.ExtendsCluster(cp, previous))
            {
                _clusterStart[m] = i;
                _clusterJustify[m] = context.Check(cp);
                m++;
            }
            previous = cp;
            i += units;
        }
        _clusterStart[m] = n;

        bool inside = false, everywhere = true;
        for (int c = 1; c < m; c++)
        {
            bool gap = GapBefore(c);
            inside |= gap;
            everywhere &= gap;
        }
        byte lead = (byte)(_clusterJustify[0] & JustifyBefore), trail = (byte)(_clusterJustify[m - 1] & JustifyAfter);
        if (!inside)
        {
            it.Justify = (byte)(lead | trail | (n == 1 && trail != 0 ? JustifyLetters : 0));
            return 1;
        }
        if (everywhere && m == n && DrawsCharactersAsGlyphs(it.Style.Font, s, start, n))
        {
            it.Justify = (byte)(lead | trail | JustifyLetters);
            return 1;
        }

        // Pieces between the opportunities; the last takes what is left of the width, so that the line keeps its width.
        var pieces = _justifyPieces;
        pieces.Clear();
        var style = it.Style;
        float used = 0;
        for (int from = 0, c = 1; c <= m; c++)
        {
            if (c < m && !GapBefore(c)) continue;
            int offset = _clusterStart[from], length = _clusterStart[c] - offset;
            float width = c == m ? 0 : TextWidth(style.Font, s, start + offset, length, style.TextSize, style.LetterSpacing);
            var piece = it.Slice(offset, length, width);
            if (c < m) width = piece.Width; // without the trim of its first character
            byte before = from == 0 ? lead : (byte)(_clusterJustify[from] & JustifyBefore);
            byte after = (byte)(_clusterJustify[c - 1] & JustifyAfter);
            piece.Justify = (byte)(before | after | (length == 1 && after != 0 ? JustifyLetters : 0));
            if (pieces.Count > 0) used += PairKern(pieces[^1], piece);
            used += width;
            pieces.Add(piece);
            from = c;
        }
        pieces[^1].Width = it.Width - used;
        items.RemoveAt(k);
        items.InsertRange(k, pieces);
        _justifySplit = true;
        return pieces.Count;
    }

    /// <summary>Whether the current text item has an opportunity between cluster <paramref name="c"/> and the one before.</summary>
    private bool GapBefore(int c) => (_clusterJustify[c - 1] & JustifyAfter) != 0 || (_clusterJustify[c] & JustifyBefore) != 0;

    /// <summary>
    /// Whether a font draws each character of a text as a glyph of its own, so that letter spacing goes after each
    /// character: no ligatures and no complex shaping.
    /// </summary>
    private static bool DrawsCharactersAsGlyphs(TextFont font, string s, int start, int length)
    {
        if (ComplexShaper.Handles(s, start, length)) return false;
        switch (font)
        {
            case EmbeddedFont embedded:
                return !embedded.UsesShaper(s, start, length) && !embedded.MayLigate(s, start, length);
            case FontChain:
                // A chain picks a font per character; CJK ideographs and symbols are not ligated.
                for (int i = start; i < start + length; i++)
                    if (!JustificationCharacters.IsCjkIdeographOrSymbol(s[i])) return false;
                return true;
            default:
                return true;
        }
    }

    /// <summary>
    /// Whether the item after <paramref name="k"/> continues a run of text drawn with the run's letter spacing: the
    /// letter spacing suits it, and the run puts it where the line does.
    /// </summary>
    private bool JoinsRun(List<Item> items, int k, float[] xs, float spaceExtra, float letterExtra)
    {
        var it = items[k];
        var next = items[k + 1];
        if (next.Kind != ItemKind.Space && ((next.Justify & JustifyLetters) != 0) != (letterExtra != 0)) return false;
        // The run moves each character by the letter spacing, and each space also by the word spacing.
        float advance = it.Width + _kern[k] + letterExtra * Glyphs(it) + (it.Kind == ItemKind.Space ? (spaceExtra - letterExtra) * it.Length : 0);
        return MathF.Abs(xs[k + 1] - xs[k] - advance) < JustifyTolerance;
    }

    private const float JustifyTolerance = 0.001f;

    /// <summary>The characters of an item that letter spacing moves: each surrogate pair once.</summary>
    private static int Glyphs(Item it)
    {
        int count = it.Length;
        for (int i = it.Start, end = it.Start + it.Length - 1; i < end; i++)
            if (char.IsHighSurrogate(it.Source[i]) && char.IsLowSurrogate(it.Source[i + 1])) count--;
        return count;
    }

    /// <summary>
    /// Chrome's justification context: whether the character before had an opportunity after it, and what kind of
    /// character it was, which decide whether the next one gets one before it.
    /// </summary>
    private struct JustificationContext
    {
        private const byte Normal = 0, AtomicInline = 1, Cursive = 2;
        private readonly TextJustify _mode;
        private bool _isAfter;
        private byte _previous;

        public JustificationContext(TextJustify mode)
        {
            _mode = mode;
            _isAfter = true; // nothing before the line's first character
            _previous = Normal;
        }

        /// <summary>Whether the last character had an opportunity after it.</summary>
        public readonly bool IsAfter => _isAfter;

        /// <summary>The opportunities of the next character: <see cref="JustifyBefore"/> and <see cref="JustifyAfter"/>.</summary>
        public byte Check(int cp)
        {
            if (JustificationCharacters.IsDefaultIgnorable(cp)) return 0;
            byte type = cp == ObjectReplacement ? AtomicInline : JustificationCharacters.IsCursive(cp) ? Cursive : Normal;
            byte previous = _previous;
            _previous = type;
            switch (_mode)
            {
                case TextJustify.None:
                    _isAfter = false;
                    return 0;
                case TextJustify.InterCharacter when type != Normal:
                {
                    // Joined letters and inline boxes take space only before them, after a character of another kind.
                    bool before = !_isAfter && previous != type;
                    _isAfter = false;
                    return before ? JustifyBefore : (byte)0;
                }
                case TextJustify.InterCharacter:
                    return Both();
                case TextJustify.InterWord:
                    _isAfter = TreatAsSpace(cp);
                    return _isAfter ? JustifyAfter : (byte)0;
                default:
                    if (TreatAsSpace(cp))
                    {
                        _isAfter = true;
                        return JustifyAfter;
                    }
                    if (!JustificationCharacters.IsCjkIdeographOrSymbol(cp))
                    {
                        _isAfter = false;
                        return 0;
                    }
                    return Both();
            }
        }

        /// <summary>Space after the character, and before it too unless the one before already has some after it.</summary>
        private byte Both()
        {
            byte flags = (byte)((_isAfter ? 0 : JustifyBefore) | JustifyAfter);
            _isAfter = true;
            return flags;
        }

        private static bool TreatAsSpace(int cp) => cp is ' ' or '\t' or '\n' or 0xA0;
    }
}
