namespace PdfPecker.Fonts;

// The GSUB lookup types: single, multiple, alternate, ligature and reverse chaining substitutions. Contextual ones are
// in OpenTypeLayout.Contextual.cs and extensions are resolved in ApplySubtables.
internal sealed partial class OpenTypeLayout
{
    private const int SingleSubstitution = 1, MultipleSubstitution = 2, AlternateSubstitution = 3, LigatureSubstitution = 4;
    private const int ContextSubstitution = 5, ChainedContextSubstitution = 6, ReverseChainingSubstitution = 8;

    // Single substitution: a delta added to the glyph id (format 1), or a substitute per coverage index (format 2).
    private const int DeltaFormat = 1, SubstituteListFormat = 2;
    private const int DeltaGlyphIdField = 4, SubstituteCountField = 4, SubstitutesField = 6;

    // Multiple, alternate and ligature substitutions (format 1 only) have an offset per coverage index to a sequence,
    // an alternate set or a ligature set, each a count and then glyphs or offsets.
    private const int SetCountField = 4, SetOffsetsField = 6;
    private const int ArrayCountField = 0, ArrayEntriesField = 2;

    // A ligature: the ligature glyph, its component count and the components after the first.
    private const int LigatureGlyphField = 0, ComponentCountField = 2, ComponentsField = 4;
    private const int MaxLigatureComponents = 16;

    // Reverse chaining single substitution (format 1 only): the backtrack and lookahead coverages, then the substitutes.
    private const int BacktrackCountField = 4;

    private bool ApplyGsub(LookupState c, int type, int sub, ref int i)
    {
        switch (type)
        {
            case SingleSubstitution: return SingleSubst(c, sub, ref i);
            case MultipleSubstitution: return MultipleSubst(c, sub, ref i);
            case AlternateSubstitution: return AlternateSubst(c, sub, ref i);
            case LigatureSubstitution: return LigatureSubst(c, sub, ref i);
            case ContextSubstitution:
            case ChainedContextSubstitution: return ContextLookup(c, sub, type == ChainedContextSubstitution, ref i);
            default: return false;
        }
    }

    private bool SingleSubst(LookupState c, int sub, ref int i)
    {
        int gid = c.Buf[i].Gid;
        int index = CoverageOf(sub, gid);
        if (index < 0) return false;
        int glyph = _reader.U16(sub + FormatField) switch
        {
            DeltaFormat => (gid + _reader.S16(sub + DeltaGlyphIdField)) & 0xFFFF, // glyph ids wrap around at 16 bits
            SubstituteListFormat when index < _reader.U16(sub + SubstituteCountField) => _reader.U16(sub + SubstitutesField + index * 2),
            _ => -1,
        };
        if (glyph < 0) return false;
        Replace(c, i, glyph);
        i++;
        return true;
    }

    // Replaces the glyph with a sequence of glyphs; an empty sequence deletes it.
    private bool MultipleSubst(LookupState c, int sub, ref int i)
    {
        var buf = c.Buf;
        int sequence = SubstitutionSet(sub, buf[i].Gid);
        if (sequence < 0) return false;
        int count = _reader.U16(sequence + ArrayCountField);
        if (count == 0)
        {
            buf.RemoveAt(i); // a deletion
            return true;
        }
        if (buf.Count - 1 > c.MaxLength - count) return false; // the run would grow past its limit
        var g = buf[i];
        bool ligature = (g.Props & Ligature) != 0;
        // Each glyph is read as it is inserted, so a sequence cut off by the end of the font leaves the glyphs before.
        for (int k = 0; k < count; k++)
        {
            var ng = g;
            ng.Gid = _reader.U16(sequence + ArrayEntriesField + k * 2);
            ng.Props = Props(ng.Gid, g.Props);
            if (k > 0) ng.Length = 0;
            // The glyphs of a decomposed ligature become its components, as HarfBuzz numbers them.
            if (ligature && count > 1) ng.LigComp = k + 1;
            if (k == 0) buf[i] = ng;
            else buf.Insert(i + k, ng);
        }
        i += count;
        return true;
    }

    // Replaces the glyph with the first of its alternates.
    private bool AlternateSubst(LookupState c, int sub, ref int i)
    {
        int alternates = SubstitutionSet(sub, c.Buf[i].Gid);
        if (alternates < 0 || _reader.U16(alternates + ArrayCountField) == 0) return false;
        Replace(c, i, _reader.U16(alternates + ArrayEntriesField));
        i++;
        return true;
    }

    // The sequence, alternate set or ligature set of a format 1 substitution for a glyph, or -1 when there is none.
    private int SubstitutionSet(int sub, int gid)
    {
        int index = CoverageOf(sub, gid);
        if (index < 0 || _reader.U16(sub + FormatField) != OnlyFormat || index >= _reader.U16(sub + SetCountField)) return -1;
        return sub + _reader.U16(sub + SetOffsetsField + index * 2);
    }

    private void Replace(LookupState c, int i, int glyph)
    {
        var buf = c.Buf;
        var g = buf[i];
        g.Gid = glyph;
        g.Props = Props(glyph, g.Props);
        buf[i] = g;
    }

    // The props of a substituted glyph: GDEF's, or the old glyph's when the font has no glyph classes.
    private int Props(int gid, int old) => _glyphClassDef >= 0 ? GlyphProps(gid) : old;

    // Replaces the glyph and the ones after it with the first ligature of its set whose components follow.
    private bool LigatureSubst(LookupState c, int sub, ref int i)
    {
        int ligatures = SubstitutionSet(sub, c.Buf[i].Gid);
        if (ligatures < 0) return false;
        Span<int> positions = stackalloc int[MaxLigatureComponents];
        for (int l = 0, count = _reader.U16(ligatures + ArrayCountField); l < count && Spend(c); l++)
        {
            int ligature = ligatures + _reader.U16(ligatures + ArrayEntriesField + l * 2);
            int components = _reader.U16(ligature + ComponentCountField);
            if (components < 1 || components > MaxLigatureComponents || !MatchComponents(c, ligature, i, positions[..components])) continue;
            Ligate(c, positions[..components], _reader.U16(ligature + LigatureGlyphField));
            i++;
            return true;
        }
        return false;
    }

    // Whether the ligature's components follow the glyph at i, skipping what the lookup ignores; their positions go
    // into the span, one per component.
    private bool MatchComponents(LookupState c, int ligature, int i, Span<int> positions)
    {
        var buf = c.Buf;
        positions[0] = i;
        for (int k = 1; k < positions.Length; k++)
        {
            int j = NextMatch(c, positions[k - 1]);
            if (j < 0 || (buf[j].Mask & c.LookupMask) == 0 || buf[j].Gid != _reader.U16(ligature + ComponentsField + (k - 1) * 2)) return false;
            positions[k] = j;
        }
        return true;
    }

    // Replaces the glyphs at the positions with a ligature; marks skipped between them stay after it.
    private void Ligate(LookupState c, ReadOnlySpan<int> pos, int glyph)
    {
        var buf = c.Buf;
        var first = buf[pos[0]];
        bool allMarks = true;
        foreach (int p in pos) if ((buf[p].Props & Mark) == 0) allMarks = false;
        int ligId = allMarks ? 0 : c.NextLigId++;
        var last = buf[pos[^1]];
        int end = Math.Max(first.Cluster + first.Length, last.Cluster + last.Length);
        int compsBefore = 0;
        // Marks between the components belong to the component before them.
        var kept = c.KeptMarks;
        kept.Clear();
        for (int k = 0; k < pos.Length; k++)
        {
            var g = buf[pos[k]];
            compsBefore += Math.Max(1, g.LigComps);
            if (k + 1 < pos.Length)
                for (int j = pos[k] + 1; j < pos[k + 1]; j++)
                {
                    var m = buf[j];
                    if (ligId != 0 && (m.Props & Mark) != 0)
                    {
                        m.LigId = ligId;
                        m.LigComp = compsBefore;
                    }
                    kept.Add(m);
                }
        }
        // Marks right after the last component that belonged to it (a ligature of a ligature) move to the new ligature.
        int lastLigId = last.LigId, lastComps = last.LigComps;
        var lig = first;
        lig.Gid = glyph;
        lig.Props = _glyphClassDef >= 0 ? GlyphProps(glyph) : allMarks ? first.Props : Ligature;
        lig.Length = end - first.Cluster;
        lig.LigId = ligId;
        lig.LigComp = 0;
        lig.LigComps = compsBefore;
        int start = pos[0], stop = pos[^1];
        buf.RemoveRange(start, stop - start + 1);
        buf.Insert(start, lig);
        buf.InsertRange(start + 1, kept);
        if (ligId != 0 && lastLigId != 0)
            for (int j = start + 1 + kept.Count; j < buf.Count && buf[j].LigId == lastLigId && (buf[j].Props & Mark) != 0; j++)
            {
                var m = buf[j];
                m.LigId = ligId;
                m.LigComp = compsBefore - lastComps + Math.Min(Math.Max(m.LigComp, 1), lastComps);
                buf[j] = m;
            }
    }

    // Replaces the glyph at i with its substitute when the glyphs around it are covered by the backtrack and lookahead
    // coverages. The run is walked backwards, so the lookahead sees substituted glyphs.
    private bool ReverseChainSingle(LookupState c, int sub, int i)
    {
        if (_reader.U16(sub + FormatField) != OnlyFormat) return false;
        int index = CoverageOf(sub, c.Buf[i].Gid);
        if (index < 0) return false;
        var arrays = new CountedArrays(_reader, sub + BacktrackCountField);
        var backtrack = arrays.Next();
        if (!MatchBacktrack(c, i, backtrack, SequenceValues.Coverages(sub))) return false;
        var lookahead = arrays.Next();
        if (!MatchLookahead(c, i, lookahead, SequenceValues.Coverages(sub))) return false;
        var substitutes = arrays.Next();
        if (index >= substitutes.Count) return false;
        Replace(c, i, _reader.U16(substitutes.Start + index * 2));
        return true;
    }
}
