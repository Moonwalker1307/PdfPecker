namespace PdfPecker.Fonts;

/// <summary>
/// A glyph being shaped by <see cref="ComplexShaper"/>: its glyph id, the characters it stands for, the features that
/// apply to it, and its position. Positions are in font units, as HarfBuzz keeps them.
/// </summary>
internal struct GlyphInfo
{
    public int Gid;
    public int Cluster, Length;   // the characters in the source string; Length 0 for the extra glyphs of a decomposition
    public int CodePoint;         // the character it was made from (the first one for a ligature)
    public uint Mask;             // the features that apply to it, one bit each
    public int Props;             // OpenTypeLayout.Base/Ligature/Mark, with the mark attachment class in bits 8-15
    public int LigId, LigComp, LigComps; // ligature tracking: marks remember the component they follow
    public int XAdvance, YAdvance, XOffset, YOffset;
    public int AttachChain;       // relative index of the glyph this one is attached to (0: none)
    public byte AttachType;       // OpenTypeLayout.AttachMark or AttachCursive
}

/// <summary>
/// The GSUB, GPOS and GDEF tables of a font, and the code that applies their lookups to a run of glyphs: all GSUB lookup
/// types (alternates take the first alternate) and all GPOS lookup types, with lookup flags, mark attachment classes
/// and mark filtering sets. Device tables and variations are not applied. Malformed tables throw
/// IndexOutOfRangeException, which the shaper turns into unshaped text.
/// </summary>
internal sealed class OpenTypeLayout
{
    public const int Base = 2, Ligature = 4, Mark = 8; // glyph props, as LookupFlag's ignore bits
    public const byte AttachMark = 1, AttachCursive = 2;

    private const int RightToLeft = 1, IgnoreFlags = 0x0E, UseMarkFilteringSet = 0x10, MarkAttachmentType = 0xFF00;

    private readonly byte[] _d;
    private readonly int _gsub = -1, _gpos = -1;
    private readonly int _glyphClassDef = -1, _markAttachClassDef = -1, _markGlyphSets = -1;

    public OpenTypeLayout(FontProgram font)
    {
        _d = font.Data;
        if (font.Table("GSUB") is { Length: >= 10 } gsub && U16(gsub.Offset) == 1) _gsub = gsub.Offset;
        if (font.Table("GPOS") is { Length: >= 10 } gpos && U16(gpos.Offset) == 1) _gpos = gpos.Offset;
        if (font.Table("GDEF") is { Length: >= 12 } gdef && U16(gdef.Offset) == 1)
        {
            int g = gdef.Offset;
            if (U16(g + 4) != 0) _glyphClassDef = g + U16(g + 4);
            if (U16(g + 10) != 0) _markAttachClassDef = g + U16(g + 10);
            if (U16(g + 2) >= 2 && gdef.Length >= 14 && U16(g + 12) != 0) _markGlyphSets = g + U16(g + 12);
        }
    }

    public bool HasGsub => _gsub >= 0;
    public bool HasGpos => _gpos >= 0;
    public bool HasGlyphClasses => _glyphClassDef >= 0;

    /// <summary>The glyph props of a glyph from GDEF: base, ligature or mark (with its attachment class), or 0.</summary>
    public int GlyphProps(int gid)
    {
        if (_glyphClassDef < 0) return 0;
        switch (ClassOf(_glyphClassDef, gid))
        {
            case 1: return Base;
            case 2: return Ligature;
            case 3: return Mark | (_markAttachClassDef >= 0 ? ClassOf(_markAttachClassDef, gid) << 8 : 0);
            default: return 0; // component glyphs and unclassified ones
        }
    }

    // ------------------------------------------------------------------ scripts and features

    /// <summary>
    /// The lookups of a feature for a script (its default language system), in lookup order; empty when the font does
    /// not have the feature. The script falls back to DFLT, dflt and latn, as HarfBuzz does.
    /// </summary>
    public List<int> FeatureLookups(bool gpos, uint script, uint feature)
    {
        var result = new List<int>();
        int table = gpos ? _gpos : _gsub;
        if (table < 0) return result;
        int langSys = LangSys(table, script);
        if (langSys < 0) return result;
        int features = table + U16(table + 6);
        int featureCount = U16(features);
        int required = U16(langSys + 2);
        for (int i = -1, n = U16(langSys + 4); i < n; i++)
        {
            int fi = i < 0 ? required : U16(langSys + 6 + i * 2);
            if (fi == 0xFFFF || fi >= featureCount || U32(features + 2 + fi * 6) != feature) continue;
            int f = features + U16(features + 2 + fi * 6 + 4);
            for (int j = 0, m = U16(f + 2); j < m; j++) result.Add(U16(f + 4 + j * 2));
        }
        return result;
    }

    /// <summary>True when the font's script has the feature.</summary>
    public bool HasFeature(bool gpos, uint script, uint feature) => FeatureLookups(gpos, script, feature).Count > 0;

    private int LangSys(int table, uint script)
    {
        int scripts = table + U16(table + 4);
        int n = U16(scripts);
        foreach (uint tag in new[] { script, FontProgram.Tag("DFLT"), FontProgram.Tag("dflt"), FontProgram.Tag("latn") })
            for (int i = 0; i < n; i++)
            {
                if (U32(scripts + 2 + i * 6) != tag) continue;
                int s = scripts + U16(scripts + 2 + i * 6 + 4);
                return U16(s) != 0 ? s + U16(s) : -1;
            }
        return -1;
    }

    // ------------------------------------------------------------------ applying lookups

    private sealed class Context
    {
        public List<GlyphInfo> Buf = null!;
        public bool Gpos;
        public bool Rtl;
        public uint LookupMask;
        public int LookupFlag;
        public int Depth;
        public int NextLigId = 1;
    }

    [ThreadStatic] private static Context? t_ctx;

    private static Context Ctx => t_ctx ??= new Context();

    /// <summary>Applies one lookup over the whole run, to the glyphs that have one of the mask bits.</summary>
    public void ApplyLookup(List<GlyphInfo> buf, bool gpos, int lookupIndex, uint mask, bool rtl, ref int nextLigId)
    {
        int table = gpos ? _gpos : _gsub;
        if (table < 0) return;
        int lookups = table + U16(table + 8);
        if (lookupIndex >= U16(lookups)) return;
        int lookup = lookups + U16(lookups + 2 + lookupIndex * 2);
        var c = Ctx;
        c.Buf = buf;
        c.Gpos = gpos;
        c.Rtl = rtl;
        c.LookupMask = mask;
        c.Depth = 0;
        c.NextLigId = nextLigId;
        int type = U16(lookup), flag = LookupFlagOf(lookup);
        c.LookupFlag = flag;
        if (!gpos && ResolveType(lookup, type) == 8)
        {
            // Reverse chaining substitutions go from the end of the run to its start.
            for (int i = buf.Count - 1; i >= 0; i--)
                if ((buf[i].Mask & mask) != 0 && Matches(c, buf[i])) ApplySubtables(c, lookup, ref i, reverse: true);
        }
        else
        {
            int i = 0;
            while (i < buf.Count)
            {
                if ((buf[i].Mask & mask) != 0 && Matches(c, buf[i]))
                {
                    int at = i, count = buf.Count;
                    // A deletion leaves i on the next glyph; everything else moves past what it consumed.
                    if (ApplySubtables(c, lookup, ref i, reverse: false)) { if (i <= at && buf.Count >= count) i = at + 1; continue; }
                }
                i++;
            }
        }
        nextLigId = c.NextLigId;
    }

    private int LookupFlagOf(int lookup)
    {
        int flag = U16(lookup + 2);
        if ((flag & UseMarkFilteringSet) != 0) flag |= U16(lookup + 6 + U16(lookup + 4) * 2) << 16;
        return flag;
    }

    // The type of a lookup's subtables, looking through extension lookups.
    private int ResolveType(int lookup, int type)
    {
        int ext = 7 + (Ctx.Gpos ? 2 : 0);
        if (type != ext || U16(lookup + 4) == 0) return type;
        int sub = lookup + U16(lookup + 6);
        return U16(sub + 2);
    }

    // Tries the lookup's subtables at position i until one applies. Moves i past what was consumed.
    private bool ApplySubtables(Context c, int lookup, ref int i, bool reverse)
    {
        int type = U16(lookup);
        for (int s = 0, n = U16(lookup + 4); s < n; s++)
        {
            int sub = lookup + U16(lookup + 6 + s * 2);
            int subType = type;
            if (type == (c.Gpos ? 9 : 7))
            {
                subType = U16(sub + 2);
                sub += (int)U32(sub + 4);
            }
            if (reverse ? ReverseChainSingle(c, sub, i) : c.Gpos ? ApplyGpos(c, subType, sub, ref i) : ApplyGsub(c, subType, sub, ref i))
                return true;
        }
        return false;
    }

    // Whether a glyph takes part in the current lookup, per its flags (HarfBuzz's check_glyph_property).
    private bool Matches(Context c, in GlyphInfo g)
    {
        int props = g.Props, flag = c.LookupFlag;
        if ((props & flag & IgnoreFlags) != 0) return false;
        if ((props & Mark) != 0)
        {
            if ((flag & UseMarkFilteringSet) != 0) return MarkSetCovers(flag >>> 16, g.Gid);
            if ((flag & MarkAttachmentType) != 0) return (flag & MarkAttachmentType) == (props & MarkAttachmentType);
        }
        return true;
    }

    private bool MarkSetCovers(int set, int gid)
    {
        if (_markGlyphSets < 0 || set >= U16(_markGlyphSets + 2)) return false;
        int cov = _markGlyphSets + (int)U32(_markGlyphSets + 4 + set * 4);
        return CoverageIndex(cov, gid) >= 0;
    }

    private int NextMatch(Context c, int i)
    {
        var buf = c.Buf;
        for (int j = i + 1; j < buf.Count; j++)
            if (Matches(c, buf[j])) return j;
        return -1;
    }

    private int PrevMatch(Context c, int i)
    {
        var buf = c.Buf;
        for (int j = i - 1; j >= 0; j--)
            if (Matches(c, buf[j])) return j;
        return -1;
    }

    // ------------------------------------------------------------------ GSUB

    private bool ApplyGsub(Context c, int type, int sub, ref int i)
    {
        var buf = c.Buf;
        int gid = buf[i].Gid;
        switch (type)
        {
            case 1:
            {
                int index = CoverageIndex(sub + U16(sub + 2), gid);
                if (index < 0) return false;
                int format = U16(sub);
                int glyph = format == 1 ? (gid + (short)U16(sub + 4)) & 0xFFFF : format == 2 && index < U16(sub + 4) ? U16(sub + 6 + index * 2) : -1;
                if (glyph < 0) return false;
                Replace(i, glyph);
                i++;
                return true;
            }
            case 2:
            {
                int index = CoverageIndex(sub + U16(sub + 2), gid);
                if (index < 0 || U16(sub) != 1 || index >= U16(sub + 4)) return false;
                int seq = sub + U16(sub + 6 + index * 2);
                int count = U16(seq);
                if (count == 0)
                {
                    buf.RemoveAt(i); // a deletion
                    return true;
                }
                var g = buf[i];
                bool lig = (g.Props & Ligature) != 0;
                for (int k = 0; k < count; k++)
                {
                    var ng = g;
                    ng.Gid = U16(seq + 2 + k * 2);
                    ng.Props = Props(ng.Gid, g.Props);
                    if (k > 0) ng.Length = 0;
                    // The glyphs of a decomposed ligature become its components, as HarfBuzz numbers them.
                    if (lig && count > 1) { ng.LigComp = k + 1; }
                    if (k == 0) buf[i] = ng;
                    else buf.Insert(i + k, ng);
                }
                i += count;
                return true;
            }
            case 3:
            {
                int index = CoverageIndex(sub + U16(sub + 2), gid);
                if (index < 0 || U16(sub) != 1 || index >= U16(sub + 4)) return false;
                int set = sub + U16(sub + 6 + index * 2);
                if (U16(set) == 0) return false;
                Replace(i, U16(set + 2));
                i++;
                return true;
            }
            case 4:
                return LigatureSubst(c, sub, ref i);
            case 5:
            case 6:
                return ContextLookup(c, sub, type == 6, ref i);
        }
        return false;
    }

    private void Replace(int i, int glyph)
    {
        var buf = Ctx.Buf;
        var g = buf[i];
        g.Gid = glyph;
        g.Props = Props(glyph, g.Props);
        buf[i] = g;
    }

    // The props of a substituted glyph: GDEF's, or the old glyph's when the font has no glyph classes.
    private int Props(int gid, int old) => _glyphClassDef >= 0 ? GlyphProps(gid) : old;

    private bool LigatureSubst(Context c, int sub, ref int i)
    {
        var buf = c.Buf;
        int index = CoverageIndex(sub + U16(sub + 2), buf[i].Gid);
        if (index < 0 || U16(sub) != 1 || index >= U16(sub + 4)) return false;
        int set = sub + U16(sub + 6 + index * 2);
        Span<int> pos = stackalloc int[16];
        for (int l = 0, n = U16(set); l < n; l++)
        {
            int lig = set + U16(set + 2 + l * 2);
            int count = U16(lig + 2);
            if (count < 1 || count > 16) continue;
            pos[0] = i;
            int k = 1;
            for (; k < count; k++)
            {
                int j = NextMatch(c, pos[k - 1]);
                if (j < 0 || (buf[j].Mask & c.LookupMask) == 0 || buf[j].Gid != U16(lig + 4 + (k - 1) * 2)) break;
                pos[k] = j;
            }
            if (k < count) continue;
            Ligate(c, pos[..count], U16(lig));
            i++;
            return true;
        }
        return false;
    }

    // Replaces the glyphs at the positions with a ligature; marks skipped between them stay after it.
    private void Ligate(Context c, ReadOnlySpan<int> pos, int glyph)
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
        var kept = new List<GlyphInfo>();
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

    private bool ReverseChainSingle(Context c, int sub, int i)
    {
        var buf = c.Buf;
        if (U16(sub) != 1) return false;
        int index = CoverageIndex(sub + U16(sub + 2), buf[i].Gid);
        if (index < 0) return false;
        int p = sub + 4;
        int back = U16(p);
        int j = i;
        for (int k = 0; k < back; k++)
        {
            j = PrevMatch(c, j);
            if (j < 0 || CoverageIndex(sub + U16(p + 2 + k * 2), buf[j].Gid) < 0) return false;
        }
        p += 2 + back * 2;
        int ahead = U16(p);
        j = i;
        for (int k = 0; k < ahead; k++)
        {
            j = NextMatch(c, j);
            if (j < 0 || CoverageIndex(sub + U16(p + 2 + k * 2), buf[j].Gid) < 0) return false;
        }
        p += 2 + ahead * 2;
        if (index >= U16(p)) return false;
        Replace(i, U16(p + 2 + index * 2));
        return true;
    }

    // ------------------------------------------------------------------ contextual lookups (GSUB 5/6, GPOS 7/8)

    private bool ContextLookup(Context c, int sub, bool chained, ref int i)
    {
        var buf = c.Buf;
        int gid = buf[i].Gid;
        int format = U16(sub);
        if (format == 3) return chained ? ChainFormat3(c, sub, ref i) : ContextFormat3(c, sub, ref i);
        int index = CoverageIndex(sub + U16(sub + 2), gid);
        if (index < 0) return false;
        if (format == 1)
        {
            if (index >= U16(sub + 4)) return false;
            int setOff = U16(sub + 6 + index * 2);
            if (setOff == 0) return false;
            int set = sub + setOff;
            for (int r = 0, n = U16(set); r < n; r++)
            {
                int rule = set + U16(set + 2 + r * 2);
                if (chained ? ChainRule(c, rule, -1, -1, -1, ref i) : Rule(c, rule, -1, ref i)) return true;
            }
            return false;
        }
        if (format == 2)
        {
            int backDef = -1, inputDef, aheadDef = -1, setCountAt;
            if (chained)
            {
                backDef = sub + U16(sub + 4);
                inputDef = sub + U16(sub + 6);
                aheadDef = sub + U16(sub + 8);
                setCountAt = sub + 10;
            }
            else
            {
                inputDef = sub + U16(sub + 4);
                setCountAt = sub + 6;
            }
            int cls = ClassOf(inputDef, gid);
            if (cls >= U16(setCountAt)) return false;
            int setOff = U16(setCountAt + 2 + cls * 2);
            if (setOff == 0) return false;
            int set = sub + setOff;
            for (int r = 0, n = U16(set); r < n; r++)
            {
                int rule = set + U16(set + 2 + r * 2);
                if (chained ? ChainRule(c, rule, backDef, inputDef, aheadDef, ref i) : Rule(c, rule, inputDef, ref i)) return true;
            }
        }
        return false;
    }

    // A glyph matches a rule's value: a glyph id, or a class when classDef is set.
    private bool MatchValue(int gid, int value, int classDef) => classDef < 0 ? gid == value : ClassOf(classDef, gid) == value;

    // A context rule (format 1 or 2): input glyphs (after the first) and lookup records.
    private bool Rule(Context c, int rule, int classDef, ref int i)
    {
        int inputCount = U16(rule), lookupCount = U16(rule + 2);
        if (inputCount == 0) return false;
        Span<int> pos = stackalloc int[Math.Min(inputCount, 64)];
        if (!MatchInput(c, i, inputCount, rule + 4, classDef, -1, pos, out _)) return false;
        ApplyRecords(c, pos, rule + 4 + (inputCount - 1) * 2, lookupCount, ref i);
        return true;
    }

    private bool ChainRule(Context c, int rule, int backDef, int inputDef, int aheadDef, ref int i)
    {
        int p = rule;
        int back = U16(p);
        int backAt = p + 2;
        p += 2 + back * 2;
        int inputCount = U16(p);
        if (inputCount == 0) return false;
        int inputAt = p + 2;
        p += 2 + (inputCount - 1) * 2;
        int ahead = U16(p);
        int aheadAt = p + 2;
        p += 2 + ahead * 2;
        int lookupCount = U16(p);
        Span<int> pos = stackalloc int[Math.Min(inputCount, 64)];
        if (!MatchInput(c, i, inputCount, inputAt, inputDef, -1, pos, out int lastInput)) return false;
        int j = i;
        for (int k = 0; k < back; k++)
        {
            j = PrevMatch(c, j);
            if (j < 0 || !MatchValue(c.Buf[j].Gid, U16(backAt + k * 2), backDef)) return false;
        }
        j = lastInput;
        for (int k = 0; k < ahead; k++)
        {
            j = NextMatch(c, j);
            if (j < 0 || !MatchValue(c.Buf[j].Gid, U16(aheadAt + k * 2), aheadDef)) return false;
        }
        ApplyRecords(c, pos, p + 2, lookupCount, ref i);
        return true;
    }

    private bool ContextFormat3(Context c, int sub, ref int i)
    {
        int inputCount = U16(sub + 2), lookupCount = U16(sub + 4);
        if (inputCount == 0 || CoverageIndex(sub + U16(sub + 6), c.Buf[i].Gid) < 0) return false;
        Span<int> pos = stackalloc int[Math.Min(inputCount, 64)];
        if (!MatchInput(c, i, inputCount, sub + 8, -1, sub, pos, out _)) return false;
        ApplyRecords(c, pos, sub + 6 + inputCount * 2, lookupCount, ref i);
        return true;
    }

    private bool ChainFormat3(Context c, int sub, ref int i)
    {
        var buf = c.Buf;
        int p = sub + 2;
        int back = U16(p);
        int backAt = p + 2;
        p += 2 + back * 2;
        int inputCount = U16(p);
        int inputAt = p + 2;
        p += 2 + inputCount * 2;
        int ahead = U16(p);
        int aheadAt = p + 2;
        p += 2 + ahead * 2;
        int lookupCount = U16(p);
        if (inputCount == 0 || CoverageIndex(sub + U16(inputAt), buf[i].Gid) < 0) return false;
        Span<int> pos = stackalloc int[Math.Min(inputCount, 64)];
        if (!MatchInput(c, i, inputCount, inputAt + 2, -1, sub, pos, out int lastInput)) return false;
        int j = i;
        for (int k = 0; k < back; k++)
        {
            j = PrevMatch(c, j);
            if (j < 0 || CoverageIndex(sub + U16(backAt + k * 2), buf[j].Gid) < 0) return false;
        }
        j = lastInput;
        for (int k = 0; k < ahead; k++)
        {
            j = NextMatch(c, j);
            if (j < 0 || CoverageIndex(sub + U16(aheadAt + k * 2), buf[j].Gid) < 0) return false;
        }
        ApplyRecords(c, pos, p + 2, lookupCount, ref i);
        return true;
    }

    // Matches the input sequence after its first glyph: glyph ids, classes (classDef) or coverages (offsets from
    // coverageBase). The input glyphs must have the lookup's mask.
    private bool MatchInput(Context c, int i, int count, int valuesAt, int classDef, int coverageBase, Span<int> pos, out int last)
    {
        last = i;
        if (count > pos.Length) return false;
        pos[0] = i;
        var buf = c.Buf;
        for (int k = 1; k < count; k++)
        {
            int j = NextMatch(c, pos[k - 1]);
            if (j < 0 || (buf[j].Mask & c.LookupMask) == 0) return false;
            int value = U16(valuesAt + (k - 1) * 2);
            bool ok = coverageBase >= 0 ? CoverageIndex(coverageBase + value, buf[j].Gid) >= 0 : MatchValue(buf[j].Gid, value, classDef);
            if (!ok) return false;
            pos[k] = j;
        }
        last = pos[count - 1];
        return true;
    }

    // Applies the lookup records of a matched rule at their input positions, then moves past the input.
    private void ApplyRecords(Context c, Span<int> pos, int records, int count, ref int i)
    {
        var buf = c.Buf;
        int end = pos[^1] + 1;
        if (c.Depth < 8)
        {
            int table = c.Gpos ? _gpos : _gsub;
            int lookups = table + U16(table + 8);
            for (int r = 0; r < count; r++)
            {
                int seq = U16(records + r * 4), li = U16(records + r * 4 + 2);
                if (seq >= pos.Length || li >= U16(lookups)) continue;
                int at = pos[seq];
                if (at >= buf.Count) continue;
                int lookup = lookups + U16(lookups + 2 + li * 2);
                int savedFlag = c.LookupFlag;
                c.LookupFlag = LookupFlagOf(lookup);
                c.Depth++;
                int before = buf.Count;
                int k = at;
                if (Matches(c, buf[at])) ApplySubtables(c, lookup, ref k, reverse: false);
                c.Depth--;
                c.LookupFlag = savedFlag;
                int delta = buf.Count - before;
                if (delta != 0)
                {
                    // A nested ligature or decomposition moved the glyphs after it.
                    for (int q = seq + 1; q < pos.Length; q++) pos[q] = Math.Max(at + 1, pos[q] + delta);
                    end += delta;
                }
            }
        }
        i = Math.Max(end, i + 1);
    }

    // ------------------------------------------------------------------ GPOS

    private bool ApplyGpos(Context c, int type, int sub, ref int i)
    {
        var buf = c.Buf;
        int gid = buf[i].Gid;
        switch (type)
        {
            case 1:
            {
                int index = CoverageIndex(sub + U16(sub + 2), gid);
                if (index < 0) return false;
                int format = U16(sub), vf = U16(sub + 4);
                int record = format == 1 ? sub + 6 : format == 2 && index < U16(sub + 6) ? sub + 8 + index * ValueSize(vf) : -1;
                if (record < 0) return false;
                ApplyValue(i, vf, record);
                i++;
                return true;
            }
            case 2:
                return PairPos(c, sub, ref i);
            case 3:
                return CursivePos(c, sub, ref i);
            case 4:
            case 5:
            case 6:
                return MarkPos(c, type, sub, ref i);
            case 7:
            case 8:
                return ContextLookup(c, sub, type == 8, ref i);
        }
        return false;
    }

    private static int ValueSize(int format) => System.Numerics.BitOperations.PopCount((uint)format & 0xFF) * 2;

    private void ApplyValue(int i, int format, int record)
    {
        var buf = Ctx.Buf;
        var g = buf[i];
        int p = record;
        if ((format & 1) != 0) { g.XOffset += (short)U16(p); p += 2; }
        if ((format & 2) != 0) { g.YOffset += (short)U16(p); p += 2; }
        if ((format & 4) != 0) { g.XAdvance += (short)U16(p); p += 2; }
        if ((format & 8) != 0) { g.YAdvance += (short)U16(p); }
        buf[i] = g;
    }

    private bool PairPos(Context c, int sub, ref int i)
    {
        var buf = c.Buf;
        int index = CoverageIndex(sub + U16(sub + 2), buf[i].Gid);
        if (index < 0) return false;
        int j = NextMatch(c, i);
        if (j < 0) return false;
        int format = U16(sub), vf1 = U16(sub + 4), vf2 = U16(sub + 6);
        int s1 = ValueSize(vf1), s2 = ValueSize(vf2);
        int second = buf[j].Gid;
        if (format == 1)
        {
            if (index >= U16(sub + 8)) return false;
            int set = sub + U16(sub + 10 + index * 2);
            int count = U16(set), size = 2 + s1 + s2;
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int rec = set + 2 + mid * size;
                int g = U16(rec);
                if (g < second) lo = mid + 1;
                else if (g > second) hi = mid - 1;
                else
                {
                    ApplyValue(i, vf1, rec + 2);
                    ApplyValue(j, vf2, rec + 2 + s1);
                    i = vf2 != 0 ? j + 1 : j;
                    return true;
                }
            }
            return false;
        }
        if (format == 2)
        {
            int class1 = ClassOf(sub + U16(sub + 8), buf[i].Gid), class2 = ClassOf(sub + U16(sub + 10), second);
            int count1 = U16(sub + 12), count2 = U16(sub + 14);
            if (class1 >= count1 || class2 >= count2) return false;
            int rec = sub + 16 + (class1 * count2 + class2) * (s1 + s2);
            ApplyValue(i, vf1, rec);
            ApplyValue(j, vf2, rec + s1);
            i = vf2 != 0 ? j + 1 : j;
            return true;
        }
        return false;
    }

    private (int X, int Y) Anchor(int anchor) => ((short)U16(anchor + 2), (short)U16(anchor + 4));

    private bool CursivePos(Context c, int sub, ref int i)
    {
        var buf = c.Buf;
        if (U16(sub) != 1) return false;
        int cov = sub + U16(sub + 2);
        int index = CoverageIndex(cov, buf[i].Gid);
        if (index < 0 || index >= U16(sub + 4)) return false;
        int entry = U16(sub + 6 + index * 4);
        if (entry == 0) return false;
        int prev = PrevMatch(c, i);
        if (prev < 0) return false;
        int prevIndex = CoverageIndex(cov, buf[prev].Gid);
        if (prevIndex < 0 || prevIndex >= U16(sub + 4)) return false;
        int exit = U16(sub + 6 + prevIndex * 4 + 2);
        if (exit == 0) return false;
        var (exitX, exitY) = Anchor(sub + exit);
        var (entryX, entryY) = Anchor(sub + entry);
        int pi = prev, j = i;
        var gi = buf[pi];
        var gj = buf[j];
        if (!c.Rtl)
        {
            gi.XAdvance = exitX + gi.XOffset;
            int d = entryX + gj.XOffset;
            gj.XAdvance -= d;
            gj.XOffset -= d;
        }
        else
        {
            int d = exitX + gi.XOffset;
            gi.XAdvance -= d;
            gi.XOffset -= d;
            gj.XAdvance = entryX + gj.XOffset;
        }
        buf[pi] = gi;
        buf[j] = gj;
        int child = pi, parent = j;
        int yOffset = entryY - exitY;
        if ((c.LookupFlag & RightToLeft) == 0)
        {
            (child, parent) = (parent, child);
            yOffset = -yOffset;
        }
        ReverseCursiveMinorOffset(buf, child, parent);
        var ch = buf[child];
        ch.AttachType = AttachCursive;
        ch.AttachChain = parent - child;
        ch.YOffset = yOffset;
        buf[child] = ch;
        if (buf[parent].AttachChain == -ch.AttachChain)
        {
            var pa = buf[parent];
            pa.AttachChain = 0;
            buf[parent] = pa;
        }
        i++;
        return true;
    }

    private static void ReverseCursiveMinorOffset(List<GlyphInfo> buf, int i, int newParent)
    {
        var g = buf[i];
        int chain = g.AttachChain;
        if (chain == 0 || (g.AttachType & AttachCursive) == 0) return;
        g.AttachChain = 0;
        buf[i] = g;
        int j = i + chain;
        if (j == newParent || (uint)j >= (uint)buf.Count) return;
        ReverseCursiveMinorOffset(buf, j, newParent);
        var gj = buf[j];
        gj.YOffset = -buf[i].YOffset;
        gj.AttachChain = -chain;
        gj.AttachType = g.AttachType;
        buf[j] = gj;
    }

    private bool MarkPos(Context c, int type, int sub, ref int i)
    {
        var buf = c.Buf;
        if (U16(sub) != 1) return false;
        int markIndex = CoverageIndex(sub + U16(sub + 2), buf[i].Gid);
        if (markIndex < 0) return false;
        int classCount = U16(sub + 6);
        int markArray = sub + U16(sub + 8), baseArray = sub + U16(sub + 10);
        if (markIndex >= U16(markArray)) return false;
        int markClass = U16(markArray + 2 + markIndex * 4);
        int markAnchor = markArray + U16(markArray + 2 + markIndex * 4 + 2);
        if (markClass >= classCount) return false;

        // The glyph to attach to: the previous base (or ligature) for types 4 and 5, skipping marks; the previous
        // mark for type 6.
        int j = -1;
        if (type == 6)
        {
            // The previous mark, skipping only what the lookup's mark filter excludes (HarfBuzz's MarkMarkPos).
            int saved = c.LookupFlag;
            c.LookupFlag &= ~IgnoreFlags;
            j = PrevMatch(c, i);
            c.LookupFlag = saved;
            if (j < 0 || (buf[j].Props & Mark) == 0) return false;
            var a = buf[i];
            var b = buf[j];
            // Both marks must belong to the same base or ligature component, unless one of them is a mark ligature.
            bool good = a.LigId == b.LigId
                ? a.LigId == 0 || a.LigComp == b.LigComp
                : a.LigId > 0 && a.LigComp == 0 || b.LigId > 0 && b.LigComp == 0;
            if (!good) return false;
        }
        else
        {
            for (int k = i - 1; k >= 0; k--)
            {
                if ((buf[k].Props & Mark) != 0) continue;
                j = k;
                break;
            }
            if (j < 0) return false;
        }
        int baseIndex = CoverageIndex(sub + U16(sub + 4), buf[j].Gid);
        if (baseIndex < 0 || baseIndex >= U16(baseArray)) return false;
        int anchorOff;
        int anchorsAt;
        if (type == 5)
        {
            int lig = baseArray + U16(baseArray + 2 + baseIndex * 2);
            int comps = U16(lig);
            if (comps == 0) return false;
            var m = buf[i];
            var l = buf[j];
            int comp = l.LigId != 0 && l.LigId == m.LigId && m.LigComp > 0 ? Math.Min(comps, m.LigComp) - 1 : comps - 1;
            anchorsAt = lig + 2 + comp * classCount * 2;
            anchorOff = U16(anchorsAt + markClass * 2);
            if (anchorOff == 0) return false;
            anchorOff += lig;
        }
        else
        {
            anchorsAt = baseArray + 2 + baseIndex * classCount * 2;
            anchorOff = U16(anchorsAt + markClass * 2);
            if (anchorOff == 0) return false;
            anchorOff += baseArray;
        }
        var (bx, by) = Anchor(anchorOff);
        var (mx, my) = Anchor(markAnchor);
        var g = buf[i];
        g.XOffset = bx - mx;
        g.YOffset = by - my;
        g.AttachType = AttachMark;
        g.AttachChain = j - i;
        buf[i] = g;
        i++;
        return true;
    }

    /// <summary>
    /// Adds the offsets of the glyphs that marks and cursive glyphs are attached to, as HarfBuzz finishes positioning;
    /// positions stay in logical order.
    /// </summary>
    public static void PropagateAttachments(List<GlyphInfo> buf, bool rtl)
    {
        for (int i = 0; i < buf.Count; i++) Propagate(buf, i, rtl, 64);
    }

    private static void Propagate(List<GlyphInfo> buf, int i, bool rtl, int nesting)
    {
        var g = buf[i];
        int chain = g.AttachChain;
        if (chain == 0 || nesting <= 0) return;
        g.AttachChain = 0;
        buf[i] = g;
        int j = i + chain;
        if ((uint)j >= (uint)buf.Count) return;
        Propagate(buf, j, rtl, nesting - 1);
        g = buf[i];
        var p = buf[j];
        if ((g.AttachType & AttachCursive) != 0) g.YOffset += p.YOffset;
        else
        {
            g.XOffset += p.XOffset;
            g.YOffset += p.YOffset;
            if (j < i)
            {
                if (!rtl)
                    for (int k = j; k < i; k++) { g.XOffset -= buf[k].XAdvance; g.YOffset -= buf[k].YAdvance; }
                else
                    for (int k = j + 1; k <= i; k++) { g.XOffset += buf[k].XAdvance; g.YOffset += buf[k].YAdvance; }
            }
        }
        buf[i] = g;
    }

    // ------------------------------------------------------------------ common tables

    private int CoverageIndex(int cov, int gid)
    {
        int format = U16(cov), n = U16(cov + 2);
        if (format == 1)
        {
            int lo = 0, hi = n - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1, g = U16(cov + 4 + mid * 2);
                if (g < gid) lo = mid + 1;
                else if (g > gid) hi = mid - 1;
                else return mid;
            }
        }
        else if (format == 2)
        {
            int lo = 0, hi = n - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1, r = cov + 4 + mid * 6;
                if (gid < U16(r)) hi = mid - 1;
                else if (gid > U16(r + 2)) lo = mid + 1;
                else return U16(r + 4) + gid - U16(r);
            }
        }
        return -1;
    }

    private int ClassOf(int def, int gid)
    {
        int format = U16(def);
        if (format == 1)
        {
            int start = U16(def + 2), n = U16(def + 4);
            return gid >= start && gid < start + n ? U16(def + 6 + (gid - start) * 2) : 0;
        }
        if (format == 2)
        {
            int lo = 0, hi = U16(def + 2) - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1, r = def + 4 + mid * 6;
                if (gid < U16(r)) hi = mid - 1;
                else if (gid > U16(r + 2)) lo = mid + 1;
                else return U16(r + 4);
            }
        }
        return 0;
    }

    private int U16(int o) => _d[o] << 8 | _d[o + 1];
    private uint U32(int o) => (uint)(_d[o] << 24 | _d[o + 1] << 16 | _d[o + 2] << 8 | _d[o + 3]);
}
