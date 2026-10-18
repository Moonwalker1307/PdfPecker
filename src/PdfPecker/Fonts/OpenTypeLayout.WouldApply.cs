namespace PdfPecker.Fonts;

// Whether GSUB lookups would substitute a sequence of glyphs on its own, without a run: the Indic shaper asks this of
// the reph, below-base, post-base and pre-base forms to learn how a font forms a syllable before shaping it.
internal sealed partial class OpenTypeLayout
{
    /// <summary>
    /// Whether one of the GSUB lookups would substitute exactly these glyphs, as HarfBuzz's would_substitute decides it:
    /// single, multiple and alternate substitutions of one glyph, ligatures of all of them, and contextual rules whose
    /// input is all of them. With <paramref name="zeroContext"/>, chained rules that need glyphs before or after the
    /// input do not count. Lookup flags are not applied.
    /// </summary>
    public bool WouldSubstitute(int[] lookups, ReadOnlySpan<int> glyphs, bool zeroContext)
    {
        if (_gsub < 0 || glyphs.IsEmpty) return false;
        // A damaged font may point anywhere; a lookup that cannot be read substitutes nothing.
        try
        {
            foreach (int index in lookups)
            {
                int lookup = LookupOffset(_gsub, index);
                if (lookup < 0) continue;
                var subtables = new LayoutLookup(_reader, lookup);
                for (int s = 0; s < subtables.SubtableCount; s++)
                {
                    var (type, subtable) = subtables.Subtable(s, ExtensionSubstitution);
                    if (SubtableWouldApply(type, subtable, glyphs, zeroContext)) return true;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
        }
        return false;
    }

    private bool SubtableWouldApply(int type, int sub, ReadOnlySpan<int> glyphs, bool zeroContext)
    {
        switch (type)
        {
            case SingleSubstitution:
            case MultipleSubstitution:
            case AlternateSubstitution:
            case ReverseChainingSubstitution:
                return glyphs.Length == 1 && CoverageOf(sub, glyphs[0]) >= 0;
            case LigatureSubstitution:
                return LigatureWouldApply(sub, glyphs);
            case ContextSubstitution:
                return ContextWouldApply(sub, glyphs, chained: false, zeroContext);
            case ChainedContextSubstitution:
                return ContextWouldApply(sub, glyphs, chained: true, zeroContext);
            default:
                return false;
        }
    }

    // A ligature of the first glyph's set whose components are the other glyphs.
    private bool LigatureWouldApply(int sub, ReadOnlySpan<int> glyphs)
    {
        int ligatures = SubstitutionSet(sub, glyphs[0]);
        if (ligatures < 0) return false;
        for (int l = 0, count = _reader.U16(ligatures + ArrayCountField); l < count; l++)
        {
            int ligature = ligatures + _reader.U16(ligatures + ArrayEntriesField + l * 2);
            if (_reader.U16(ligature + ComponentCountField) != glyphs.Length) continue;
            bool match = true;
            for (int k = 1; k < glyphs.Length && match; k++)
                match = _reader.U16(ligature + ComponentsField + (k - 1) * 2) == glyphs[k];
            if (match) return true;
        }
        return false;
    }

    private bool ContextWouldApply(int sub, ReadOnlySpan<int> glyphs, bool chained, bool zeroContext)
    {
        int format = _reader.U16(sub + FormatField);
        if (format == CoverageRulesFormat)
        {
            CountedArray input;
            if (chained)
            {
                var arrays = new CountedArrays(_reader, sub + ChainArraysField);
                var backtrack = arrays.Next();
                input = arrays.Next();
                var lookahead = arrays.Next();
                if (zeroContext && (backtrack.Count != 0 || lookahead.Count != 0)) return false;
            }
            else input = new CountedArray(_reader.U16(sub + CoverageInputCountField), sub + CoverageInputField);
            if (input.Count != glyphs.Length) return false;
            for (int k = 0; k < glyphs.Length; k++)
                if (!ValueMatches(glyphs[k], _reader.U16(input.Start + k * 2), SequenceValues.Coverages(sub))) return false;
            return true;
        }

        int index = CoverageOf(sub, glyphs[0]);
        if (index < 0) return false;
        int set;
        SequenceValues values;
        if (format == GlyphRulesFormat)
        {
            set = RuleSet(sub, RuleSetCountField, index);
            values = SequenceValues.GlyphIds;
        }
        else if (format == ClassRulesFormat)
        {
            values = SequenceValues.Classes(sub + _reader.U16(sub + (chained ? InputClassDefField : ClassDefField)));
            set = RuleSet(sub, chained ? ChainClassRuleSetCountField : ClassRuleSetCountField, ClassOf(values.ClassDef, glyphs[0]));
        }
        else return false;
        if (set < 0) return false;

        for (int r = 0, count = _reader.U16(set + ArrayCountField); r < count; r++)
        {
            int rule = set + _reader.U16(set + ArrayEntriesField + r * 2);
            CountedArray input;
            if (chained)
            {
                var arrays = new CountedArrays(_reader, rule);
                var backtrack = arrays.Next();
                input = arrays.Next(firstGlyphOmitted: true);
                var lookahead = arrays.Next();
                if (zeroContext && (backtrack.Count != 0 || lookahead.Count != 0)) continue;
            }
            else input = new CountedArray(_reader.U16(rule + RuleInputCountField), rule + RuleInputField);
            if (input.Count != glyphs.Length) continue;
            bool match = true;
            for (int k = 1; k < glyphs.Length && match; k++)
                match = ValueMatches(glyphs[k], _reader.U16(input.Start + (k - 1) * 2), values);
            if (match) return true;
        }
        return false;
    }
}
