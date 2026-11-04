namespace PdfPecker.Fonts;

// Word boundaries: which glyphs the lookups the shaper applies to text of the scripts that are not complex can change
// across a space, so that text can be shaped a word at a time and still come out as shaped whole (see
// EmbeddedFont.ShapeWords).
internal sealed partial class OpenTypeLayout
{
    // The features the shaper applies to text of the scripts that are not complex, and the scripts it looks them up
    // in (see ComplexShaper's plans).
    private static readonly uint[] DefaultSubstitutions =
        [.. new[] { "rvrn", "ccmp", "locl", "rtlm", "rlig", "calt", "rclt", "liga", "clig" }.Select(FontProgram.Tag)];
    private static readonly uint[] DefaultPositionings =
        [.. new[] { "abvm", "blwm", "curs", "dist", "kern", "mark", "mkmk" }.Select(FontProgram.Tag)];
    private static readonly uint[] SimpleScripts = [.. new[] { "latn", "grek", "cyrl", "DFLT" }.Select(FontProgram.Tag)];

    // Closing the changed glyphs over the substitutions takes a pass per step of a chain of substitutions; real fonts
    // need two or three.
    private const int MaxClosurePasses = 16;

    /// <summary>
    /// How the lookups reach across a space. A space is never changed, joined into a ligature or kerned with the glyph
    /// after it, so a rule reaches across one only as context: Changed holds the glyphs (one bit per glyph id) that a
    /// rule which can match a space may change, together with every glyph a substitution can turn into one of them,
    /// and Reach is the most glyphs such a rule looks at on either side of a glyph it changes.
    /// </summary>
    public sealed class WordContext(ulong[] changed, int reach)
    {
        public int Reach { get; } = reach;

        public bool MayChange(int gid) => (changed[gid >> 6] >> (gid & 63) & 1) != 0;
    }

    // The state of one scan: the glyphs found, the reach, and the reads left of the digests' budget.
    private sealed class WordScan(int space)
    {
        public readonly int Space = space;
        public readonly ulong[] Bits = new ulong[(ushort.MaxValue + 1) / 64];
        public readonly List<(int Type, int Subtable)> Substitutions = new();
        public int Reach;
        private int _reads;

        public bool Spend() => ++_reads <= MaxDigestReads;

        public bool Has(int gid) => (Bits[gid >> 6] >> (gid & 63) & 1) != 0;

        public void Set(int gid) => Bits[gid >> 6] |= 1UL << (gid & 63);
    }

    /// <summary>
    /// How the default lookups of the scripts that are not complex reach across a space, the font's glyph for it; null
    /// when they can do so otherwise than as context (a space that is substituted, ligated, kerned with what follows it
    /// or joined cursively, or a lookup that skips base glyphs), when a rule that matches a space may change any glyph
    /// at all, or when the lookups are too many to read within the digests' budget or damaged.
    /// </summary>
    public WordContext? WordContextOf(int space)
    {
        if (space <= 0 || space > ushort.MaxValue) return null;
        var scan = new WordScan(space);
        try
        {
            for (int t = 0; t < 2; t++)
            {
                bool gpos = t == 1;
                int table = gpos ? _gpos : _gsub;
                if (table < 0) continue;
                var pending = new Stack<int>();
                var seen = new HashSet<int>();
                foreach (uint script in SimpleScripts)
                    foreach (uint feature in gpos ? DefaultPositionings : DefaultSubstitutions)
                        foreach (int l in FeatureLookups(gpos, script, feature))
                            if (seen.Add(l)) pending.Push(l);
                // The lookups that contextual rules apply are scanned too, as they change glyphs as well.
                while (pending.Count > 0)
                {
                    int lookup = LookupOffset(table, pending.Pop());
                    if (lookup < 0) continue;
                    bool skipsBases = (_reader.U16(lookup + LookupFlagField) & Base) != 0;
                    var subtables = new LayoutLookup(_reader, lookup);
                    for (int s = 0; s < subtables.SubtableCount; s++)
                    {
                        if (!scan.Spend()) return null;
                        var (type, sub) = subtables.Subtable(s, gpos ? ExtensionPositioning : ExtensionSubstitution);
                        if (!(gpos ? ScanPositioning(scan, type, sub, skipsBases, pending, seen) : ScanSubstitution(scan, type, sub, skipsBases, pending, seen)))
                            return null;
                    }
                }
            }
            if (!CloseOverSubstitutions(scan)) return null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
        return new WordContext(scan.Bits, scan.Reach);
    }

    private bool ScanSubstitution(WordScan scan, int type, int sub, bool skipsBases, Stack<int> pending, HashSet<int> seen)
    {
        switch (type)
        {
            case SingleSubstitution or MultipleSubstitution or AlternateSubstitution:
                scan.Substitutions.Add((type, sub));
                return true;
            case LigatureSubstitution:
                scan.Substitutions.Add((type, sub));
                return !skipsBases && !LigatesSpace(scan, sub);
            case ContextSubstitution or ChainedContextSubstitution:
                return ScanContext(scan, sub, type == ChainedContextSubstitution, skipsBases, pending, seen);
            case ReverseChainingSubstitution:
                scan.Substitutions.Add((type, sub));
                return ScanReverseChaining(scan, sub, skipsBases);
            default:
                return true;
        }
    }

    private bool ScanPositioning(WordScan scan, int type, int sub, bool skipsBases, Stack<int> pending, HashSet<int> seen) => type switch
    {
        // Kerning between a glyph and the space after it stays inside a word as it is shaped; the space must not kern
        // with the next word, nor join it cursively.
        PairPositioning => !skipsBases && CoverageOf(sub, scan.Space) < 0,
        CursivePositioning => CoverageOf(sub, scan.Space) < 0,
        ContextPositioning or ChainedContextPositioning => ScanContext(scan, sub, type == ChainedContextPositioning, skipsBases, pending, seen),
        _ => true,
    };

    // Whether a ligature substitution has the space as any of its components.
    private bool LigatesSpace(WordScan scan, int sub)
    {
        if (_reader.U16(sub + FormatField) != OnlyFormat) return true;
        if (CoverageOf(sub, scan.Space) >= 0) return true;
        for (int set = 0, sets = _reader.U16(sub + SetCountField); set < sets; set++)
        {
            int ligatures = sub + _reader.U16(sub + SetOffsetsField + set * 2);
            for (int l = 0, count = _reader.U16(ligatures + ArrayCountField); l < count; l++)
            {
                int ligature = ligatures + _reader.U16(ligatures + ArrayEntriesField + l * 2);
                for (int k = 1, components = _reader.U16(ligature + ComponentCountField); k < components; k++)
                {
                    if (!scan.Spend()) return true;
                    if (_reader.U16(ligature + ComponentsField + (k - 1) * 2) == scan.Space) return true;
                }
            }
        }
        return false;
    }

    // The rules of a contextual subtable that can match a space: the glyphs of their input go into the scan, and the
    // lookups every rule applies are queued. False when a rule could change a space, or any glyph at all.
    private bool ScanContext(WordScan scan, int sub, bool chained, bool skipsBases, Stack<int> pending, HashSet<int> seen)
    {
        int format = _reader.U16(sub + FormatField);
        if (format == CoverageRulesFormat)
        {
            var coverages = SequenceValues.Coverages(sub);
            if (!chained)
            {
                var input = new CountedArray(_reader.U16(sub + CoverageInputCountField), sub + CoverageInputField);
                var records = new CountedArray(_reader.U16(sub + CoverageLookupCountField), sub + CoverageInputField + input.Count * 2);
                return ScanRule(scan, default, -1, input, default, coverages, coverages, coverages, records, skipsBases, pending, seen);
            }
            var arrays = new CountedArrays(_reader, sub + ChainArraysField);
            var backtrack = arrays.Next();
            var chainInput = arrays.Next();
            var lookahead = arrays.Next();
            return ScanRule(scan, backtrack, -1, chainInput, lookahead, coverages, coverages, coverages, arrays.Next(), skipsBases, pending, seen);
        }
        if (format != GlyphRulesFormat && format != ClassRulesFormat) return true;
        int coverage = sub + _reader.U16(sub + CoverageField);
        var backtrackValues = SequenceValues.GlyphIds;
        var inputValues = SequenceValues.GlyphIds;
        var lookaheadValues = SequenceValues.GlyphIds;
        int setCountField = RuleSetCountField;
        if (format == ClassRulesFormat)
        {
            if (chained)
            {
                backtrackValues = SequenceValues.Classes(sub + _reader.U16(sub + BacktrackClassDefField));
                inputValues = SequenceValues.Classes(sub + _reader.U16(sub + InputClassDefField));
                lookaheadValues = SequenceValues.Classes(sub + _reader.U16(sub + LookaheadClassDefField));
                setCountField = ChainClassRuleSetCountField;
            }
            else
            {
                inputValues = SequenceValues.Classes(sub + _reader.U16(sub + ClassDefField));
                setCountField = ClassRuleSetCountField;
            }
        }
        var firstGlyphs = format == GlyphRulesFormat ? CoveredGlyphs(scan, coverage) : null;
        for (int index = 0, sets = _reader.U16(sub + setCountField); index < sets; index++)
        {
            int set = RuleSet(sub, setCountField, index);
            if (set < 0) continue;
            // Format 1 indexes its rule sets by coverage index, format 2 by the class of the first glyph.
            int first = index;
            if (firstGlyphs != null)
            {
                if (index >= firstGlyphs.Count) break;
                first = firstGlyphs[index];
            }
            for (int r = 0, count = _reader.U16(set + ArrayCountField); r < count; r++)
            {
                if (!scan.Spend()) return false;
                int rule = set + _reader.U16(set + ArrayEntriesField + r * 2);
                bool ok;
                if (chained)
                {
                    var arrays = new CountedArrays(_reader, rule);
                    var backtrack = arrays.Next();
                    var input = arrays.Next(firstGlyphOmitted: true);
                    var lookahead = arrays.Next();
                    ok = ScanRule(scan, backtrack, first, input with { Start = input.Start - 2 }, lookahead, backtrackValues, inputValues,
                        lookaheadValues, arrays.Next(), skipsBases, pending, seen, coverage);
                }
                else
                {
                    int inputCount = _reader.U16(rule + RuleInputCountField);
                    var input = new CountedArray(inputCount, rule + RuleInputField - 2);
                    var records = new CountedArray(_reader.U16(rule + RuleLookupCountField), rule + RuleInputField + (inputCount - 1) * 2);
                    ok = ScanRule(scan, default, first, input, default, inputValues, inputValues, inputValues, records, skipsBases, pending, seen, coverage);
                }
                if (!ok) return false;
            }
        }
        return true;
    }

    // One rule. The input array holds a value per input glyph from Start, except that when firstValue is not -1 the
    // first glyph is that value (a glyph id or class matched through the coverage) and Start points one value early.
    private bool ScanRule(WordScan scan, CountedArray backtrack, int firstValue, CountedArray input, CountedArray lookahead,
        SequenceValues backtrackValues, SequenceValues inputValues, SequenceValues lookaheadValues, CountedArray records,
        bool skipsBases, Stack<int> pending, HashSet<int> seen, int firstCoverage = -1)
    {
        if (input.Count == 0) return true;
        for (int r = 0; r < records.Count; r++)
        {
            if (!scan.Spend()) return false;
            int lookup = _reader.U16(records.Start + r * LookupRecordLength + RecordLookupIndexField);
            if (seen.Add(lookup)) pending.Push(lookup);
        }
        // A rule that skips base glyphs can step over a space without matching it.
        bool space = skipsBases;
        for (int k = 0; k < backtrack.Count && !space; k++) space = Covers(scan, backtrackValues, _reader.U16(backtrack.Start + k * 2), scan.Space);
        for (int k = 0; k < lookahead.Count && !space; k++) space = Covers(scan, lookaheadValues, _reader.U16(lookahead.Start + k * 2), scan.Space);
        for (int k = 0; k < input.Count; k++)
        {
            bool first = k == 0 && firstValue >= 0;
            int value = first ? firstValue : _reader.U16(input.Start + k * 2);
            bool covered = Covers(scan, inputValues, value, scan.Space) && (!first || firstCoverage < 0 || CoverageIndex(firstCoverage, scan.Space) >= 0);
            if (covered) return false; // the rule could change the space itself
        }
        if (!space) return true;
        scan.Reach = Math.Max(scan.Reach, input.Count + Math.Max(backtrack.Count, lookahead.Count));
        for (int k = 0; k < input.Count; k++)
        {
            bool first = k == 0 && firstValue >= 0;
            int value = first ? firstValue : _reader.U16(input.Start + k * 2);
            if (inputValues.CoverageBase >= 0) { if (!AddCovered(scan, inputValues.CoverageBase + value)) return false; }
            else if (inputValues.ClassDef < 0) scan.Set(value);
            else if (value != 0) { if (!AddClass(scan, inputValues.ClassDef, value)) return false; }
            else if (!first || firstCoverage < 0) return false; // class 0 is every glyph the class definitions leave out
            else
            {
                // The first glyph is limited by the coverage, so class 0 there is the covered glyphs of no class.
                var covered = CoveredGlyphs(scan, firstCoverage);
                if (covered == null) return false;
                foreach (int g in covered)
                    if (ClassOf(inputValues.ClassDef, g) == 0) scan.Set(g);
            }
        }
        return true;
    }

    // Whether a value of a rule matches the glyph; the rule's reads count against the budget with the rule.
    private bool Covers(WordScan scan, SequenceValues values, int value, int gid) => scan.Spend() && ValueMatches(gid, value, values);

    // A reverse chaining substitution changes the glyphs of its coverage, with its backtrack and lookahead as context.
    private bool ScanReverseChaining(WordScan scan, int sub, bool skipsBases)
    {
        if (_reader.U16(sub + FormatField) != OnlyFormat) return true;
        int coverage = sub + _reader.U16(sub + CoverageField);
        if (CoverageIndex(coverage, scan.Space) >= 0) return false;
        var arrays = new CountedArrays(_reader, sub + BacktrackCountField);
        var backtrack = arrays.Next();
        var lookahead = arrays.Next();
        bool space = skipsBases;
        for (int k = 0; k < backtrack.Count && !space; k++) space = CoverageIndex(sub + _reader.U16(backtrack.Start + k * 2), scan.Space) >= 0;
        for (int k = 0; k < lookahead.Count && !space; k++) space = CoverageIndex(sub + _reader.U16(lookahead.Start + k * 2), scan.Space) >= 0;
        if (!space) return true;
        scan.Reach = Math.Max(scan.Reach, 1 + Math.Max(backtrack.Count, lookahead.Count));
        return AddCovered(scan, coverage);
    }

    // Adds every glyph that a substitution turns into a glyph already found, until no more are added: such a glyph
    // becomes one that a rule matching a space may change.
    private bool CloseOverSubstitutions(WordScan scan)
    {
        for (int pass = 0; pass < MaxClosurePasses; pass++)
        {
            bool added = false;
            foreach (var (type, sub) in scan.Substitutions)
            {
                var covered = CoveredGlyphs(scan, sub + _reader.U16(sub + CoverageField));
                if (covered == null) return false;
                int format = _reader.U16(sub + FormatField);
                for (int index = 0; index < covered.Count; index++)
                {
                    if (!scan.Spend()) return false;
                    int gid = covered[index];
                    bool reaches = type switch
                    {
                        SingleSubstitution when format == DeltaFormat => scan.Has((gid + _reader.S16(sub + DeltaGlyphIdField)) & 0xFFFF),
                        SingleSubstitution => index < _reader.U16(sub + SubstituteCountField) && scan.Has(_reader.U16(sub + SubstitutesField + index * 2)),
                        MultipleSubstitution or AlternateSubstitution => SetReaches(scan, sub, index),
                        LigatureSubstitution => LigatureReaches(scan, sub, index, gid, ref added),
                        ReverseChainingSubstitution => ReverseReaches(scan, sub, index),
                        _ => false,
                    };
                    if (reaches && !scan.Has(gid))
                    {
                        scan.Set(gid);
                        added = true;
                    }
                }
            }
            if (!added) return true;
        }
        return false;
    }

    // Whether the sequence or alternate set at a coverage index holds a glyph found.
    private bool SetReaches(WordScan scan, int sub, int index)
    {
        if (index >= _reader.U16(sub + SetCountField)) return false;
        int set = sub + _reader.U16(sub + SetOffsetsField + index * 2);
        for (int k = 0, count = _reader.U16(set + ArrayCountField); k < count; k++)
        {
            if (!scan.Spend()) return true;
            if (scan.Has(_reader.U16(set + ArrayEntriesField + k * 2))) return true;
        }
        return false;
    }

    // Whether a ligature of the first glyph is a glyph found; its components after the first are added here.
    private bool LigatureReaches(WordScan scan, int sub, int index, int first, ref bool added)
    {
        if (index >= _reader.U16(sub + SetCountField)) return false;
        bool reaches = false;
        int ligatures = sub + _reader.U16(sub + SetOffsetsField + index * 2);
        for (int l = 0, count = _reader.U16(ligatures + ArrayCountField); l < count; l++)
        {
            int ligature = ligatures + _reader.U16(ligatures + ArrayEntriesField + l * 2);
            if (!scan.Has(_reader.U16(ligature + LigatureGlyphField))) continue;
            reaches = true;
            for (int k = 1, components = _reader.U16(ligature + ComponentCountField); k < components; k++)
            {
                if (!scan.Spend()) return true;
                int g = _reader.U16(ligature + ComponentsField + (k - 1) * 2);
                if (!scan.Has(g))
                {
                    scan.Set(g);
                    added = true;
                }
            }
        }
        return reaches;
    }

    // Whether the substitute of a reverse chaining substitution at a coverage index is a glyph found.
    private bool ReverseReaches(WordScan scan, int sub, int index)
    {
        var arrays = new CountedArrays(_reader, sub + BacktrackCountField);
        arrays.Next();
        arrays.Next();
        var substitutes = arrays.Next();
        return index < substitutes.Count && scan.Has(_reader.U16(substitutes.Start + index * 2));
    }

    // The glyphs of a coverage table in coverage index order; null when the budget runs out or the format is unknown.
    private List<int>? CoveredGlyphs(WordScan scan, int coverage)
    {
        var glyphs = new List<int>();
        int format = _reader.U16(coverage), entries = _reader.U16(coverage + CoverageCountField);
        for (int e = 0; e < entries; e++)
        {
            if (!scan.Spend()) return null;
            if (format == CoverageListFormat) glyphs.Add(_reader.U16(coverage + CoverageEntriesField + e * 2));
            else if (format == CoverageRangeFormat)
            {
                int range = coverage + CoverageEntriesField + e * CoverageRangeLength;
                for (int g = _reader.U16(range), last = _reader.U16(range + CoverageRangeEndField); g <= last; g++)
                {
                    if (!scan.Spend()) return null;
                    glyphs.Add(g);
                }
            }
            else return null;
        }
        return glyphs;
    }

    private bool AddCovered(WordScan scan, int coverage)
    {
        var glyphs = CoveredGlyphs(scan, coverage);
        if (glyphs == null) return false;
        foreach (int g in glyphs) scan.Set(g);
        return true;
    }

    // Adds the glyphs of a class (not 0) of a class definition table.
    private bool AddClass(WordScan scan, int classDef, int cls)
    {
        int format = _reader.U16(classDef);
        if (format == ClassArrayFormat)
        {
            int start = _reader.U16(classDef + 2);
            for (int k = 0, count = _reader.U16(classDef + ClassArrayCountField); k < count; k++)
            {
                if (!scan.Spend()) return false;
                if (_reader.U16(classDef + ClassArrayHeaderLength + k * 2) == cls && start + k <= ushort.MaxValue) scan.Set(start + k);
            }
        }
        else if (format == ClassRangeFormat)
        {
            for (int k = 0, count = _reader.U16(classDef + ClassRangeCountField); k < count; k++)
            {
                if (!scan.Spend()) return false;
                int range = classDef + ClassRangesField + k * ClassRangeRecordLength;
                if (_reader.U16(range + 4) != cls) continue;
                for (int g = _reader.U16(range), last = _reader.U16(range + 2); g <= last; g++)
                {
                    if (!scan.Spend()) return false;
                    scan.Set(g);
                }
            }
        }
        return true;
    }
}
