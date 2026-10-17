namespace PdfPecker.Fonts;

// Running a lookup over the glyphs: its flags, which glyphs it skips, its subtables, and the coverage tables they share.
internal sealed partial class OpenTypeLayout
{
    // A lookup: its type, its flags, its subtables, and the mark filtering set when its flags ask for one.
    private const int LookupTypeField = 0, LookupFlagField = 2, SubtableCountField = 4, SubtableOffsetsField = 6;

    // Lookup flags. A glyph whose props share an ignore bit with the flags is skipped; marks can also be limited to an
    // attachment class or a mark filtering set. The set's index is kept in the flag's upper half once it is read.
    private const int RightToLeft = 0x1, IgnoreFlags = 0x0E, UseMarkFilteringSet = 0x10, MarkAttachmentType = 0xFF00;
    private const int MarkFilteringSetShift = 16;

    // The extension lookup types, whose subtables hold the real type and a 32-bit offset to the real subtable.
    private const int ExtensionSubstitution = 7, ExtensionPositioning = 9, ExtensionLookupTypeField = 2;

    // Every subtable starts with its format, and most continue with the offset of their coverage table. The lookup
    // types that have one format only number it 1.
    private const int FormatField = 0, CoverageField = 2;
    private const int OnlyFormat = 1;

    // Coverage tables: a sorted list of glyphs (format 1) or of ranges with the coverage index of their first glyph (format 2).
    private const int CoverageListFormat = 1, CoverageRangeFormat = 2;
    private const int CoverageCountField = 2, CoverageEntriesField = 4;
    private const int CoverageRangeLength = 6, CoverageRangeEndField = 2, CoverageRangeStartIndexField = 4;

    // Mark glyph sets in GDEF: a format, a count and 32-bit offsets of one coverage table per set.
    private const int MarkGlyphSetCountField = 2, MarkGlyphSetCoveragesField = 4;

    // The work a shaping call may do in its lookups, counted in steps (see OperationBudget). Real fonts stay far below:
    // Noto Nastaliq Urdu, the heaviest of the Arabic, Hebrew, Syriac and N'Ko fonts measured, takes under 900 steps per
    // glyph for its hardest words and under 400 over long text. A crafted font stops after some tens of milliseconds
    // per thousand glyphs instead of running for hours.
    private const int BaseOperations = 100_000, OperationsPerGlyph = 5_000;

    /// <summary>The run being shaped and the lookup being applied to it, kept per thread so applying one allocates nothing.</summary>
    private sealed class LookupState
    {
        public List<GlyphInfo> Buf = null!;
        public bool Gpos;
        public bool Rtl;
        public uint LookupMask;
        public int LookupFlag;
        public int Depth;
        public int NextLigId = 1;

        /// <summary>The steps the shaping call has left; see <see cref="OperationBudget"/>.</summary>
        public int Operations;

        /// <summary>The marks a ligature keeps after it, reused from ligature to ligature.</summary>
        public readonly List<GlyphInfo> KeptMarks = new();
    }

    [ThreadStatic] private static LookupState? t_state;

    /// <summary>
    /// The steps the lookups of one shaping call of <paramref name="glyphs"/> glyphs may take: every subtable, rule and
    /// ligature tried, lookup record followed and glyph looked at for a match counts one.
    /// </summary>
    /// <remarks>
    /// Contextual rules apply lookups that can apply contextual rules again, up to the nesting limit, and a lookup's
    /// subtables and a rule set's rules are offsets that may all point to the same one, so a small font can multiply
    /// the work for each glyph without bound. Once the budget is spent no lookup applies any further, and the glyphs
    /// stay as substituted and positioned so far, as they do when a malformed subtable stops the shaping.
    /// </remarks>
    public static int OperationBudget(int glyphs) => (int)Math.Min(int.MaxValue, BaseOperations + (long)OperationsPerGlyph * glyphs);

    /// <summary>
    /// Applies one lookup over the whole run, to the glyphs that have one of the mask bits, taking its steps from
    /// <paramref name="operations"/>, which <see cref="OperationBudget"/> starts for the shaping call.
    /// </summary>
    public void ApplyLookup(List<GlyphInfo> buf, bool gpos, int lookupIndex, uint mask, bool rtl, ref int nextLigId, ref int operations)
    {
        int table = gpos ? _gpos : _gsub;
        if (table < 0 || operations <= 0) return;
        int lookup = LookupOffset(table, lookupIndex);
        if (lookup < 0) return;
        var c = t_state ??= new LookupState();
        c.Buf = buf;
        c.Gpos = gpos;
        c.Rtl = rtl;
        c.LookupMask = mask;
        c.Depth = 0;
        c.NextLigId = nextLigId;
        c.Operations = operations;
        int type = _reader.U16(lookup + LookupTypeField);
        c.LookupFlag = LookupFlagOf(lookup);
        if (!gpos && SubtableType(lookup, type) == ReverseChainingSubstitution) ApplyBackwards(c, lookup);
        else ApplyForwards(c, lookup);
        nextLigId = c.NextLigId;
        operations = c.Operations;
    }

    /// <summary>Counts one step of the shaping call; false once its budget is spent, and from then on.</summary>
    private static bool Spend(LookupState c)
    {
        if (c.Operations <= 0) return false;
        c.Operations--;
        return true;
    }

    // Reverse chaining substitutions go from the end of the run to its start, and replace one glyph at a time.
    private void ApplyBackwards(LookupState c, int lookup)
    {
        var buf = c.Buf;
        for (int i = buf.Count - 1; i >= 0 && c.Operations > 0; i--)
            if ((buf[i].Mask & c.LookupMask) != 0 && Matches(c, buf[i])) ApplySubtables(c, lookup, ref i, reverse: true);
    }

    private void ApplyForwards(LookupState c, int lookup)
    {
        var buf = c.Buf;
        int i = 0;
        while (i < buf.Count && c.Operations > 0)
        {
            if ((buf[i].Mask & c.LookupMask) != 0 && Matches(c, buf[i]))
            {
                int at = i, count = buf.Count;
                // A deletion leaves i on the next glyph; everything else moves past what it consumed.
                if (ApplySubtables(c, lookup, ref i, reverse: false))
                {
                    if (i <= at && buf.Count >= count) i = at + 1;
                    continue;
                }
            }
            i++;
        }
    }

    // The lookup's flags, with the index of its mark filtering set in the upper half when it has one.
    private int LookupFlagOf(int lookup)
    {
        int flag = _reader.U16(lookup + LookupFlagField);
        if ((flag & UseMarkFilteringSet) != 0)
            flag |= _reader.U16(lookup + SubtableOffsetsField + _reader.U16(lookup + SubtableCountField) * 2) << MarkFilteringSetShift;
        return flag;
    }

    // The type of a GSUB lookup's subtables, looking through an extension lookup to its first subtable.
    private int SubtableType(int lookup, int type)
    {
        if (type != ExtensionSubstitution || _reader.U16(lookup + SubtableCountField) == 0) return type;
        int subtable = lookup + _reader.U16(lookup + SubtableOffsetsField);
        return _reader.U16(subtable + ExtensionLookupTypeField);
    }

    // Tries the lookup's subtables at position i until one applies. Moves i past what was consumed.
    private bool ApplySubtables(LookupState c, int lookup, ref int i, bool reverse)
    {
        var subtables = new LayoutLookup(_reader, lookup);
        int extension = c.Gpos ? ExtensionPositioning : ExtensionSubstitution;
        for (int s = 0; s < subtables.SubtableCount && Spend(c); s++)
        {
            var (type, subtable) = subtables.Subtable(s, extension);
            bool applied = reverse ? ReverseChainSingle(c, subtable, i)
                : c.Gpos ? ApplyGpos(c, type, subtable, ref i)
                : ApplyGsub(c, type, subtable, ref i);
            if (applied) return true;
        }
        return false;
    }

    // Whether a glyph takes part in the current lookup, per its flags (HarfBuzz's check_glyph_property).
    private bool Matches(LookupState c, in GlyphInfo g)
    {
        int props = g.Props, flag = c.LookupFlag;
        if ((props & flag & IgnoreFlags) != 0) return false;
        if ((props & Mark) != 0)
        {
            if ((flag & UseMarkFilteringSet) != 0) return MarkSetCovers(flag >>> MarkFilteringSetShift, g.Gid);
            if ((flag & MarkAttachmentType) != 0) return (flag & MarkAttachmentType) == (props & MarkAttachmentType);
        }
        return true;
    }

    private bool MarkSetCovers(int set, int gid)
    {
        if (_markGlyphSets < 0 || set >= _reader.U16(_markGlyphSets + MarkGlyphSetCountField)) return false;
        int coverage = _markGlyphSets + (int)_reader.U32(_markGlyphSets + MarkGlyphSetCoveragesField + set * 4);
        return CoverageIndex(coverage, gid) >= 0;
    }

    // The next glyph after i that the lookup does not skip, or -1.
    private int NextMatch(LookupState c, int i)
    {
        var buf = c.Buf;
        for (int j = i + 1; j < buf.Count && Spend(c); j++)
            if (Matches(c, buf[j])) return j;
        return -1;
    }

    // The glyph before i that the lookup does not skip, or -1.
    private int PreviousMatch(LookupState c, int i)
    {
        var buf = c.Buf;
        for (int j = i - 1; j >= 0 && Spend(c); j--)
            if (Matches(c, buf[j])) return j;
        return -1;
    }

    // The coverage index of a glyph in the coverage table a subtable's coverage field points to.
    private int CoverageOf(int subtable, int gid) => CoverageIndex(subtable + _reader.U16(subtable + CoverageField), gid);

    /// <summary>
    /// The coverage index of a glyph, or -1 when the table does not cover it. Unlike <see cref="CoverageTable"/>, a
    /// range's end is read only when the glyph is not before its start: a range cut off by the end of the font then
    /// fails or not exactly as it always has, which decides how much of a damaged lookup is applied.
    /// </summary>
    private int CoverageIndex(int coverage, int gid)
    {
        int format = _reader.U16(coverage), count = _reader.U16(coverage + CoverageCountField);
        int low = 0, high = count - 1;
        if (format == CoverageListFormat)
        {
            while (low <= high)
            {
                int middle = (low + high) >> 1, glyph = _reader.U16(coverage + CoverageEntriesField + middle * 2);
                if (glyph < gid) low = middle + 1;
                else if (glyph > gid) high = middle - 1;
                else return middle;
            }
        }
        else if (format == CoverageRangeFormat)
        {
            while (low <= high)
            {
                int middle = (low + high) >> 1, range = coverage + CoverageEntriesField + middle * CoverageRangeLength;
                if (gid < _reader.U16(range)) high = middle - 1;
                else if (gid > _reader.U16(range + CoverageRangeEndField)) low = middle + 1;
                else return _reader.U16(range + CoverageRangeStartIndexField) + gid - _reader.U16(range);
            }
        }
        return -1;
    }
}
