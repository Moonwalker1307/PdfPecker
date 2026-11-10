namespace PdfPecker.Fonts;

// Myanmar, ported from HarfBuzz's Myanmar shaper (hb-ot-shaper-myanmar.cc and its machine): kinzi (Nga, Asat, Virama
// before a consonant) moves after the base, Medial Ra and the vowel E move before it, and the marks after the base
// take their places below and after it. The basic features apply one at a time, after the reordering.
internal static partial class ComplexShaper
{
    // HarfBuzz's Myanmar categories, the values of its machine and of the Indic table.
    private static class MyanmarCat
    {
        public const byte C = 1, IV = 2, DB = 3, H = 4, ZWNJ = 5, ZWJ = 6, SM = 8, A = 9, GB = 10, DottedCircle = 11, Ra = 15,
            CS = 18, VAbv = 20, VBlw = 21, VPre = 22, VPst = 23, As = 32, MH = 35, MR = 36, MW = 37, MY = 38, PT = 39, VS = 40,
            ML = 41, SMPst = 57;
    }

    private static class MyanmarSyllable
    {
        public const byte Consonant = 0, Broken = 1, NonMyanmar = 2;
    }

    private static readonly ulong MyanmarConsonants = Flag(MyanmarCat.C) | Flag(MyanmarCat.CS) | Flag(MyanmarCat.Ra) | Flag(MyanmarCat.IV)
        | Flag(MyanmarCat.GB) | Flag(MyanmarCat.DottedCircle);

    // rvrn; locl and ccmp; then, after the reordering, rphf, pref, blwf and pstf one stage each, inside the syllable;
    // then the presentation features.
    private static StageLookup[][] MyanmarStages(OpenTypeLayout? layout, uint tag) =>
    [
        SyllabicStage(layout, tag, [("rvrn", AllGlyphs, false, Joiners.Auto)]),
        SyllabicStage(layout, tag, [("locl", AllGlyphs, true, Joiners.Auto), ("ccmp", AllGlyphs, true, Joiners.Auto)]),
        SyllabicStage(layout, tag, [("rphf", AllGlyphs, true, Joiners.ManualZwj)]),
        SyllabicStage(layout, tag, [("pref", AllGlyphs, true, Joiners.ManualZwj)]),
        SyllabicStage(layout, tag, [("blwf", AllGlyphs, true, Joiners.ManualZwj)]),
        SyllabicStage(layout, tag, [("pstf", AllGlyphs, true, Joiners.ManualZwj)]),
        SyllabicPresentationStage(layout, tag, ["pres", "abvs", "blws", "psts"], Joiners.ManualZwj, liga: true),
    ];

    private static void ShapeMyanmar(FontProgram font, OpenTypeLayout? layout, GlyphRun run, SyllabicPlan plan, ref LookupPass lookups)
    {
        SetIndicProperties(run);
        lookups.Apply(plan.Stages[0]);
        FindSyllables(run, static masks => new MyanmarMachine(masks).Longest());
        lookups.Apply(plan.Stages[1]);
        InsertDottedCircles(font, layout, run, MyanmarSyllable.Broken, MyanmarCat.DottedCircle, -1, IndicPos.Start);
        ForEachSyllable(run, ReorderMyanmar);
        for (int stage = 2; stage < plan.Stages.Length; stage++) lookups.Apply(plan.Stages[stage]);
    }

    // A glyph whose category matches. A ligature matches nothing: once glyphs ligate, all bets are off.
    private static bool IsMyanmarConsonant(in GlyphInfo g)
        => (g.Substitutions & OpenTypeLayout.Ligated) == 0 && (Flag(g.IndicCategory) & MyanmarConsonants) != 0;

    // HarfBuzz's initial_reordering_consonant_syllable for Myanmar, which also serves the broken clusters.
    private static void ReorderMyanmar(Span<GlyphInfo> info, int start, int end)
    {
        if ((info[start].Syllable & 0x0F) == MyanmarSyllable.NonMyanmar) return;

        // The base is the first consonant, after a kinzi (Ra, Asat, Halant) at the start.
        int baseIndex = end, limit = start;
        bool hasReph = false;
        if (start + 3 <= end && info[start].IndicCategory == MyanmarCat.Ra && info[start + 1].IndicCategory == MyanmarCat.As
            && info[start + 2].IndicCategory == MyanmarCat.H)
        {
            limit += 3;
            baseIndex = start;
            hasReph = true;
        }
        if (!hasReph) baseIndex = limit;
        for (int i = limit; i < end; i++)
            if (IsMyanmarConsonant(info[i]))
            {
                baseIndex = i;
                break;
            }

        // The kinzi goes after the main consonant, what comes before the base before it, and the marks after the base
        // to their places.
        int k = start;
        for (; k < start + (hasReph ? 3 : 0); k++) info[k].IndicPosition = IndicPos.AfterMain;
        for (; k < baseIndex; k++) info[k].IndicPosition = IndicPos.PreC;
        if (k < end) info[k++].IndicPosition = IndicPos.BaseC;
        byte pos = IndicPos.AfterMain;
        for (; k < end; k++)
        {
            byte category = info[k].IndicCategory;
            if (category == MyanmarCat.MR) info[k].IndicPosition = IndicPos.PreC; // pre-base reordering
            else if (category == MyanmarCat.VPre) info[k].IndicPosition = IndicPos.PreM; // left matra
            else if (category == MyanmarCat.VS) info[k].IndicPosition = info[k - 1].IndicPosition;
            else if (pos == IndicPos.AfterMain && category == MyanmarCat.VBlw) info[k].IndicPosition = pos = IndicPos.BelowC;
            else if (pos == IndicPos.BelowC && category == MyanmarCat.A) info[k].IndicPosition = IndicPos.BeforeSub;
            else if (pos == IndicPos.BelowC && category == MyanmarCat.VBlw) info[k].IndicPosition = pos;
            else if (pos == IndicPos.BelowC) info[k].IndicPosition = pos = IndicPos.AfterSub;
            else info[k].IndicPosition = pos;
        }

        SortByPosition(info, start, end);

        // A sequence of left matras goes the other way round, each with the variation selectors after it.
        int first = end, last = end;
        for (int i = start; i < end; i++)
            if (info[i].IndicPosition == IndicPos.PreM)
            {
                if (first == end) first = i;
                last = i;
            }
        if (first < last)
        {
            info[first..(last + 1)].Reverse();
            int from = first;
            for (int j = from; j <= last; j++)
                if (info[j].IndicCategory == MyanmarCat.VPre)
                {
                    info[from..(j + 1)].Reverse();
                    from = j + 1;
                }
        }
    }

    /// <summary>HarfBuzz's Myanmar syllable machine (hb-ot-shaper-myanmar-machine.rl) over sets of positions.</summary>
    private readonly ref struct MyanmarMachine(Span<ulong> masks)
    {
        private readonly Patterns _p = new(masks);

        public (int Length, byte Type) Longest()
        {
            const ulong e = Patterns.Empty;
            int best = 0;
            byte type = MyanmarSyllable.NonMyanmar;
            Patterns.Consider(ConsonantSyllable(e), MyanmarSyllable.Consonant, ref best, ref type);
            Patterns.Consider(J(e) | _p.One(e, MyanmarCat.SMPst), MyanmarSyllable.NonMyanmar, ref best, ref type);
            Patterns.Consider(BrokenCluster(e), MyanmarSyllable.Broken, ref best, ref type);
            // Otherwise "other": the glyph on its own.
            return (Math.Max(best, 1), type);
        }

        private ulong Opt(ulong s, int category) => s | _p.One(s, category);

        // j = ZWJ | ZWNJ
        private ulong J(ulong s) => _p.One(s, MyanmarCat.ZWJ) | _p.One(s, MyanmarCat.ZWNJ);

        // k = Ra As H
        private ulong K(ulong s) => _p.One(_p.One(_p.One(s, MyanmarCat.Ra), MyanmarCat.As), MyanmarCat.H);

        // c = C | Ra
        private ulong C(ulong s) => _p.One(s, MyanmarCat.C) | _p.One(s, MyanmarCat.Ra);

        // (DB As?)?
        private ulong DotBelow(ulong s)
        {
            ulong t = _p.One(s, MyanmarCat.DB);
            return s | t | _p.One(t, MyanmarCat.As);
        }

        // medial_group = MY? As? MR? ((MW MH? ML? | MH ML? | ML) As?)?
        private ulong MedialGroup(ulong s)
        {
            ulong t = Opt(Opt(Opt(s, MyanmarCat.MY), MyanmarCat.As), MyanmarCat.MR);
            ulong w = Opt(Opt(_p.One(t, MyanmarCat.MW), MyanmarCat.MH), MyanmarCat.ML);
            ulong h = Opt(_p.One(t, MyanmarCat.MH), MyanmarCat.ML);
            ulong l = _p.One(t, MyanmarCat.ML);
            return t | Opt(w | h | l, MyanmarCat.As);
        }

        // main_vowel_group = (VPre VS?)* VAbv* VBlw* A* (DB As?)?
        private ulong MainVowelGroup(ulong s)
        {
            for (ulong next = s | Opt(_p.One(s, MyanmarCat.VPre), MyanmarCat.VS); next != s; next = s | Opt(_p.One(s, MyanmarCat.VPre), MyanmarCat.VS)) s = next;
            ulong t = _p.Star(_p.Star(_p.Star(s, MyanmarCat.VAbv), MyanmarCat.VBlw), MyanmarCat.A);
            return DotBelow(t);
        }

        // post_vowel_group = VPst MH? ML? As* VAbv* A* (DB As?)?
        private ulong PostVowelGroup(ulong s)
        {
            ulong t = Opt(Opt(_p.One(s, MyanmarCat.VPst), MyanmarCat.MH), MyanmarCat.ML);
            t = _p.Star(_p.Star(_p.Star(t, MyanmarCat.As), MyanmarCat.VAbv), MyanmarCat.A);
            return DotBelow(t);
        }

        // tone_group = sm | PT A* DB? As?
        private ulong ToneGroup(ulong s)
        {
            ulong sm = _p.One(s, MyanmarCat.SM) | _p.One(s, MyanmarCat.SMPst);
            ulong t = Opt(Opt(_p.Star(_p.One(s, MyanmarCat.PT), MyanmarCat.A), MyanmarCat.DB), MyanmarCat.As);
            return sm | t;
        }

        // complex_syllable_tail = As* medial_group main_vowel_group post_vowel_group* tone_group* j?
        private ulong ComplexSyllableTail(ulong s)
        {
            ulong t = MainVowelGroup(MedialGroup(_p.Star(s, MyanmarCat.As)));
            for (ulong next = t | PostVowelGroup(t); next != t; next = t | PostVowelGroup(t)) t = next;
            for (ulong next = t | ToneGroup(t); next != t; next = t | ToneGroup(t)) t = next;
            return t | J(t);
        }

        // syllable_tail = (H (c | IV) VS?)* (H | complex_syllable_tail)
        private ulong SyllableTail(ulong s)
        {
            for (ulong next = s | Stacked(s); next != s; next = s | Stacked(s)) s = next;
            return _p.One(s, MyanmarCat.H) | ComplexSyllableTail(s);
        }

        private ulong Stacked(ulong s)
        {
            ulong h = _p.One(s, MyanmarCat.H);
            return Opt(C(h) | _p.One(h, MyanmarCat.IV), MyanmarCat.VS);
        }

        // consonant_syllable = (k | CS)? (c | IV | GB | DOTTEDCIRCLE) VS? syllable_tail
        private ulong ConsonantSyllable(ulong s)
        {
            ulong t = s | K(s) | _p.One(s, MyanmarCat.CS);
            ulong b = C(t) | _p.One(t, MyanmarCat.IV) | _p.One(t, MyanmarCat.GB) | _p.One(t, MyanmarCat.DottedCircle);
            return SyllableTail(Opt(b, MyanmarCat.VS));
        }

        // broken_cluster = k? VS? syllable_tail
        private ulong BrokenCluster(ulong s) => SyllableTail(Opt(s | K(s), MyanmarCat.VS));
    }
}
