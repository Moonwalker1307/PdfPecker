namespace PdfPecker.Fonts;

// Contextual and chained contextual lookups (GSUB 5 and 6, GPOS 7 and 8): rules that match a sequence of glyphs, by
// glyph id (format 1), class (format 2) or coverage (format 3), and then apply other lookups inside it.
internal sealed partial class OpenTypeLayout
{
    private const int GlyphRulesFormat = 1, ClassRulesFormat = 2, CoverageRulesFormat = 3;

    // Format 1: a rule set per coverage index. Format 2: class definitions and a rule set per class of the first glyph;
    // a chained subtable has backtrack, input and lookahead class definitions.
    private const int RuleSetCountField = 4;
    private const int ClassDefField = 4, ClassRuleSetCountField = 6;
    private const int BacktrackClassDefField = 4, InputClassDefField = 6, LookaheadClassDefField = 8, ChainClassRuleSetCountField = 10;
    private const int RuleSetOffsetsField = 2;

    // A rule of format 1 or 2: the input count, the lookup record count, the input after its first glyph, the records.
    private const int RuleInputCountField = 0, RuleLookupCountField = 2, RuleInputField = 4;

    // Format 3: the input count, the lookup record count, a coverage per input glyph, then the records. A chained
    // format 3 subtable has counted arrays of backtrack, input and lookahead coverages, then the counted records.
    private const int CoverageInputCountField = 2, CoverageLookupCountField = 4, CoverageInputField = 6;
    private const int ChainArraysField = 2;

    // A lookup record: the input position it applies at and the lookup's index in the lookup list.
    private const int LookupRecordLength = 4, RecordSequenceIndexField = 0, RecordLookupIndexField = 2;

    // Input sequences longer than this never match, and nested lookups stop at this depth, so a rule that applies
    // itself ends.
    private const int MaxInputGlyphs = 64, MaxNestingDepth = 8;

    /// <summary>
    /// How the 16-bit values of a sequence match glyphs: as glyph ids, as classes of a class definition table, or as
    /// offsets of coverage tables from a subtable.
    /// </summary>
    private readonly record struct SequenceValues(int ClassDef, int CoverageBase)
    {
        public static SequenceValues GlyphIds => new(-1, -1);

        public static SequenceValues Classes(int classDef) => new(classDef, -1);

        public static SequenceValues Coverages(int subtable) => new(-1, subtable);
    }

    /// <summary>A count read from the font and the offset of the array of values after it.</summary>
    private readonly record struct CountedArray(int Count, int Start);

    /// <summary>
    /// Walks the counted arrays that follow each other in a chained rule or subtable: backtrack, input, lookahead and
    /// lookup records, or the coverages and substitutes of a reverse chaining substitution.
    /// </summary>
    private ref struct CountedArrays(TableReader reader, int position)
    {
        private int _position = position;

        /// <summary>
        /// Reads the next count and moves past its array of 16-bit values. An input sequence's array leaves out its
        /// first glyph, which the coverage or rule set already matched.
        /// </summary>
        public CountedArray Next(bool firstGlyphOmitted = false)
        {
            int count = reader.U16(_position);
            var array = new CountedArray(count, _position + 2);
            _position += 2 + (firstGlyphOmitted ? count - 1 : count) * 2;
            return array;
        }
    }

    private bool ContextLookup(LookupState c, int sub, bool chained, ref int i)
    {
        int gid = c.Buf[i].Gid;
        int format = _reader.U16(sub + FormatField);
        if (format == CoverageRulesFormat) return chained ? ChainCoverageRules(c, sub, ref i) : CoverageRules(c, sub, ref i);
        int index = CoverageOf(sub, gid);
        if (index < 0) return false;
        return format switch
        {
            GlyphRulesFormat => GlyphRules(c, sub, index, chained, ref i),
            ClassRulesFormat => ClassRules(c, sub, gid, chained, ref i),
            _ => false,
        };
    }

    // Format 1: the rules of the first glyph's coverage index, by glyph id.
    private bool GlyphRules(LookupState c, int sub, int index, bool chained, ref int i)
    {
        int set = RuleSet(sub, RuleSetCountField, index);
        var glyphs = SequenceValues.GlyphIds;
        return set >= 0 && ApplyRuleSet(c, set, chained, glyphs, glyphs, glyphs, ref i);
    }

    // Format 2: the rules of the first glyph's class, by class.
    private bool ClassRules(LookupState c, int sub, int gid, bool chained, ref int i)
    {
        // Only a chained subtable has backtrack and lookahead classes.
        var backtrack = SequenceValues.GlyphIds;
        var lookahead = SequenceValues.GlyphIds;
        SequenceValues input;
        int setCountField;
        if (chained)
        {
            backtrack = SequenceValues.Classes(sub + _reader.U16(sub + BacktrackClassDefField));
            input = SequenceValues.Classes(sub + _reader.U16(sub + InputClassDefField));
            lookahead = SequenceValues.Classes(sub + _reader.U16(sub + LookaheadClassDefField));
            setCountField = ChainClassRuleSetCountField;
        }
        else
        {
            input = SequenceValues.Classes(sub + _reader.U16(sub + ClassDefField));
            setCountField = ClassRuleSetCountField;
        }
        int set = RuleSet(sub, setCountField, ClassOf(input.ClassDef, gid));
        return set >= 0 && ApplyRuleSet(c, set, chained, backtrack, input, lookahead, ref i);
    }

    // The rule set at an index of the subtable's rule set offsets (a count field and then the offsets), or -1 when the
    // index is past them or the set is absent.
    private int RuleSet(int sub, int countField, int index)
    {
        if (index >= _reader.U16(sub + countField)) return -1;
        int offset = _reader.U16(sub + countField + RuleSetOffsetsField + index * 2);
        return offset != 0 ? sub + offset : -1;
    }

    // Applies the first rule of the set that matches at i.
    private bool ApplyRuleSet(LookupState c, int set, bool chained, SequenceValues backtrack, SequenceValues input, SequenceValues lookahead, ref int i)
    {
        for (int r = 0, count = _reader.U16(set + ArrayCountField); r < count; r++)
        {
            int rule = set + _reader.U16(set + ArrayEntriesField + r * 2);
            if (chained ? ChainRule(c, rule, backtrack, input, lookahead, ref i) : Rule(c, rule, input, ref i)) return true;
        }
        return false;
    }

    // A context rule (format 1 or 2): input glyphs (after the first) and lookup records.
    private bool Rule(LookupState c, int rule, SequenceValues values, ref int i)
    {
        int inputCount = _reader.U16(rule + RuleInputCountField), lookupCount = _reader.U16(rule + RuleLookupCountField);
        if (inputCount == 0) return false;
        Span<int> positions = stackalloc int[Math.Min(inputCount, MaxInputGlyphs)];
        if (!MatchInput(c, i, new CountedArray(inputCount, rule + RuleInputField), values, positions, out _)) return false;
        ApplyRecords(c, positions, new CountedArray(lookupCount, rule + RuleInputField + (inputCount - 1) * 2), ref i);
        return true;
    }

    // A chained context rule (format 1 or 2): the input is matched first, then the backtrack and the lookahead.
    private bool ChainRule(LookupState c, int rule, SequenceValues backtrackValues, SequenceValues inputValues, SequenceValues lookaheadValues, ref int i)
    {
        var arrays = new CountedArrays(_reader, rule);
        var backtrack = arrays.Next();
        var input = arrays.Next(firstGlyphOmitted: true);
        if (input.Count == 0) return false;
        var lookahead = arrays.Next();
        var records = arrays.Next();
        Span<int> positions = stackalloc int[Math.Min(input.Count, MaxInputGlyphs)];
        if (!MatchInput(c, i, input, inputValues, positions, out int lastInput)
            || !MatchBacktrack(c, i, backtrack, backtrackValues)
            || !MatchLookahead(c, lastInput, lookahead, lookaheadValues))
            return false;
        ApplyRecords(c, positions, records, ref i);
        return true;
    }

    // A context subtable of format 3: a coverage per input glyph, and lookup records.
    private bool CoverageRules(LookupState c, int sub, ref int i)
    {
        int inputCount = _reader.U16(sub + CoverageInputCountField), lookupCount = _reader.U16(sub + CoverageLookupCountField);
        if (inputCount == 0 || CoverageIndex(sub + _reader.U16(sub + CoverageInputField), c.Buf[i].Gid) < 0) return false;
        Span<int> positions = stackalloc int[Math.Min(inputCount, MaxInputGlyphs)];
        if (!MatchInput(c, i, new CountedArray(inputCount, sub + CoverageInputField + 2), SequenceValues.Coverages(sub), positions, out _)) return false;
        ApplyRecords(c, positions, new CountedArray(lookupCount, sub + CoverageInputField + inputCount * 2), ref i);
        return true;
    }

    // A chained context subtable of format 3: coverages of the backtrack, the input and the lookahead, and lookup records.
    private bool ChainCoverageRules(LookupState c, int sub, ref int i)
    {
        var arrays = new CountedArrays(_reader, sub + ChainArraysField);
        var backtrack = arrays.Next();
        var input = arrays.Next();
        var lookahead = arrays.Next();
        var records = arrays.Next();
        if (input.Count == 0 || CoverageIndex(sub + _reader.U16(input.Start), c.Buf[i].Gid) < 0) return false;
        var coverages = SequenceValues.Coverages(sub);
        Span<int> positions = stackalloc int[Math.Min(input.Count, MaxInputGlyphs)];
        if (!MatchInput(c, i, input with { Start = input.Start + 2 }, coverages, positions, out int lastInput)
            || !MatchBacktrack(c, i, backtrack, coverages)
            || !MatchLookahead(c, lastInput, lookahead, coverages))
            return false;
        ApplyRecords(c, positions, records, ref i);
        return true;
    }

    // Whether a glyph matches a value of a sequence.
    private bool ValueMatches(int gid, int value, SequenceValues values)
    {
        if (values.CoverageBase >= 0) return CoverageIndex(values.CoverageBase + value, gid) >= 0;
        return values.ClassDef < 0 ? gid == value : ClassOf(values.ClassDef, gid) == value;
    }

    // Matches the input sequence after its first glyph, whose count is the array's and whose values (after the first)
    // start at the array's start. The input glyphs must have the lookup's mask. Their positions go into the span.
    private bool MatchInput(LookupState c, int i, CountedArray input, SequenceValues values, Span<int> positions, out int last)
    {
        last = i;
        if (input.Count > positions.Length) return false;
        positions[0] = i;
        var buf = c.Buf;
        for (int k = 1; k < input.Count; k++)
        {
            int j = NextMatch(c, positions[k - 1]);
            if (j < 0 || (buf[j].Mask & c.LookupMask) == 0) return false;
            if (!ValueMatches(buf[j].Gid, _reader.U16(input.Start + (k - 1) * 2), values)) return false;
            positions[k] = j;
        }
        last = positions[input.Count - 1];
        return true;
    }

    // Whether the glyphs before i, skipping what the lookup ignores, match the backtrack sequence, nearest first.
    private bool MatchBacktrack(LookupState c, int i, CountedArray backtrack, SequenceValues values)
    {
        for (int k = 0, j = i; k < backtrack.Count; k++)
        {
            j = PreviousMatch(c, j);
            if (j < 0 || !ValueMatches(c.Buf[j].Gid, _reader.U16(backtrack.Start + k * 2), values)) return false;
        }
        return true;
    }

    // Whether the glyphs after the last input glyph, skipping what the lookup ignores, match the lookahead sequence.
    private bool MatchLookahead(LookupState c, int last, CountedArray lookahead, SequenceValues values)
    {
        for (int k = 0, j = last; k < lookahead.Count; k++)
        {
            j = NextMatch(c, j);
            if (j < 0 || !ValueMatches(c.Buf[j].Gid, _reader.U16(lookahead.Start + k * 2), values)) return false;
        }
        return true;
    }

    // Applies the lookup records of a matched rule at their input positions, then moves past the input.
    private void ApplyRecords(LookupState c, Span<int> positions, CountedArray records, ref int i)
    {
        int end = positions[^1] + 1;
        if (c.Depth < MaxNestingDepth)
        {
            int table = c.Gpos ? _gpos : _gsub;
            for (int r = 0; r < records.Count; r++)
            {
                int record = records.Start + r * LookupRecordLength;
                int sequenceIndex = _reader.U16(record + RecordSequenceIndexField), lookupIndex = _reader.U16(record + RecordLookupIndexField);
                if (sequenceIndex >= positions.Length) continue;
                int lookup = LookupOffset(table, lookupIndex);
                if (lookup < 0) continue;
                int at = positions[sequenceIndex];
                if (at >= c.Buf.Count) continue;
                int delta = ApplyNestedLookup(c, lookup, at);
                if (delta != 0)
                {
                    // A nested ligature or decomposition moved the glyphs after it.
                    for (int q = sequenceIndex + 1; q < positions.Length; q++) positions[q] = Math.Max(at + 1, positions[q] + delta);
                    end += delta;
                }
            }
        }
        i = Math.Max(end, i + 1);
    }

    // Applies a lookup of a rule at one glyph with that lookup's flags, and returns how many glyphs it added or removed.
    private int ApplyNestedLookup(LookupState c, int lookup, int at)
    {
        var buf = c.Buf;
        int savedFlag = c.LookupFlag;
        c.LookupFlag = LookupFlagOf(lookup);
        c.Depth++;
        int before = buf.Count;
        int k = at;
        if (Matches(c, buf[at])) ApplySubtables(c, lookup, ref k, reverse: false);
        c.Depth--;
        c.LookupFlag = savedFlag;
        return buf.Count - before;
    }
}
