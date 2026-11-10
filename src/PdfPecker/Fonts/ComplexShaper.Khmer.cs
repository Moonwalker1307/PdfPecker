namespace PdfPecker.Fonts;

// Khmer, ported from HarfBuzz's Khmer shaper (hb-ot-shaper-khmer.cc and its machine): Coeng (the subscript sign) and
// Ro move before the base with the pre-base form, and the vowel E, alone or split off a two-part vowel, moves before
// the syllable. The basic features apply all at once, before the presentation features.
internal static partial class ComplexShaper
{
    // HarfBuzz's Khmer categories, the values of its machine and of the Indic table. Coeng is H.
    private static class KhmerCat
    {
        public const byte C = 1, V = 2, H = 4, ZWNJ = 5, ZWJ = 6, Placeholder = 10, DottedCircle = 11, Ra = 15,
            VAbv = 20, VBlw = 21, VPre = 22, VPst = 23, Robatic = 25, Xgroup = 26, Ygroup = 27;
    }

    private static class KhmerSyllable
    {
        public const byte Consonant = 0, Broken = 1, NonKhmer = 2;
    }

    // rvrn; locl, ccmp and the basic features in one stage, inside the syllable; then the presentation features.
    private static StageLookup[][] KhmerStages(OpenTypeLayout? layout, uint tag) =>
    [
        SyllabicStage(layout, tag, [("rvrn", AllGlyphs, false, Joiners.Auto)]),
        SyllabicStage(layout, tag,
        [
            ("locl", AllGlyphs, true, Joiners.Auto), ("ccmp", AllGlyphs, true, Joiners.Auto),
            ("pref", Pref, true, Joiners.Manual), ("blwf", Blwf, true, Joiners.Manual), ("abvf", Abvf, true, Joiners.Manual),
            ("pstf", Pstf, true, Joiners.Manual), ("cfar", Cfar, true, Joiners.Manual),
        ]),
        SyllabicPresentationStage(layout, tag, ["pres", "abvs", "blws", "psts"], Joiners.Manual, liga: false),
    ];

    private static void ShapeKhmer(FontProgram font, OpenTypeLayout? layout, GlyphRun run, SyllabicPlan plan, ref LookupPass lookups)
    {
        SetIndicProperties(run);
        lookups.Apply(plan.Stages[0]);
        FindSyllables(run, static masks => new KhmerMachine(masks).Longest());
        InsertDottedCircles(font, layout, run, KhmerSyllable.Broken, KhmerCat.DottedCircle, -1, IndicPos.End);
        ForEachSyllable(run, (glyphs, from, to) => ReorderKhmer(plan, glyphs, from, to));
        lookups.Apply(plan.Stages[1]);
        lookups.Apply(plan.Stages[2]);
    }

    // HarfBuzz's reorder_consonant_syllable for Khmer, which also serves the broken clusters with their dotted circle.
    private static void ReorderKhmer(SyllabicPlan plan, Span<GlyphInfo> info, int start, int end)
    {
        if ((info[start].Syllable & 0x0F) == KhmerSyllable.NonKhmer) return;

        // The below-base, above-base and post-base forms apply after the first glyph.
        uint postBase = plan.BlwfMask | plan.AbvfMask | plan.PstfMask;
        for (int i = start + 1; i < end; i++) info[i].Mask |= postBase;

        int coengs = 0;
        for (int i = start + 1; i < end; i++)
        {
            // Of the first two Coeng and consonant pairs, Coeng and Ro moves before the base and takes the pre-base form,
            // and what comes after it the cfar form.
            if (info[i].IndicCategory == KhmerCat.H && coengs <= 2 && i + 1 < end)
            {
                coengs++;
                if (info[i + 1].IndicCategory == KhmerCat.Ra)
                {
                    info[i].Mask |= plan.PrefMask;
                    info[i + 1].Mask |= plan.PrefMask;
                    MoveBack(info, i, start);
                    MoveBack(info, i + 1, start + 1);
                    if (plan.CfarMask != 0)
                        for (int j = i + 2; j < end; j++) info[j].Mask |= plan.CfarMask;
                    coengs = 2;
                }
            }
            // The left part of a vowel moves to the start.
            else if (info[i].IndicCategory == KhmerCat.VPre) MoveBack(info, i, start);
        }
    }

    /// <summary>HarfBuzz's Khmer syllable machine (hb-ot-shaper-khmer-machine.rl) over sets of positions.</summary>
    private readonly ref struct KhmerMachine(Span<ulong> masks)
    {
        private readonly Patterns _p = new(masks);

        public (int Length, byte Type) Longest()
        {
            int best = 0;
            byte type = KhmerSyllable.NonKhmer;
            Patterns.Consider(ConsonantSyllable(Patterns.Empty), KhmerSyllable.Consonant, ref best, ref type);
            Patterns.Consider(BrokenCluster(Patterns.Empty), KhmerSyllable.Broken, ref best, ref type);
            // Otherwise "other": the glyph on its own.
            return (Math.Max(best, 1), type);
        }

        // c = C | Ra | V
        private ulong C(ulong s) => _p.One(s, KhmerCat.C) | _p.One(s, KhmerCat.Ra) | _p.One(s, KhmerCat.V);

        // joiner = ZWJ | ZWNJ
        private ulong Joiner(ulong s) => _p.One(s, KhmerCat.ZWJ) | _p.One(s, KhmerCat.ZWNJ);

        // cn = c ((ZWJ | ZWNJ)? Robatic)?
        private ulong Cn(ulong s)
        {
            ulong c = C(s);
            return c | _p.One(c | Joiner(c), KhmerCat.Robatic);
        }

        // xgroup = (joiner* Xgroup)*
        private ulong XGroup(ulong s)
        {
            for (ulong next = s | _p.One(JoinerStar(s), KhmerCat.Xgroup); next != s; next = s | _p.One(JoinerStar(s), KhmerCat.Xgroup)) s = next;
            return s;
        }

        private ulong JoinerStar(ulong s)
        {
            for (ulong next = s | Joiner(s); next != s; next = s | Joiner(s)) s = next;
            return s;
        }

        // matra_group = VPre? xgroup VBlw? xgroup (joiner? VAbv)? xgroup VPst?
        private ulong MatraGroup(ulong s)
        {
            ulong t = XGroup(s | _p.One(s, KhmerCat.VPre));
            t = XGroup(t | _p.One(t, KhmerCat.VBlw));
            t = XGroup(t | _p.One(t | Joiner(t), KhmerCat.VAbv));
            return t | _p.One(t, KhmerCat.VPst);
        }

        // syllable_tail = xgroup matra_group xgroup (H c)? ygroup
        private ulong SyllableTail(ulong s)
        {
            ulong t = XGroup(MatraGroup(XGroup(s)));
            t |= C(_p.One(t, KhmerCat.H));
            return _p.Star(t, KhmerCat.Ygroup);
        }

        // broken_cluster = Robatic? (H cn)* (H | syllable_tail)
        private ulong BrokenCluster(ulong s)
        {
            ulong t = s | _p.One(s, KhmerCat.Robatic);
            for (ulong next = t | Cn(_p.One(t, KhmerCat.H)); next != t; next = t | Cn(_p.One(t, KhmerCat.H))) t = next;
            return _p.One(t, KhmerCat.H) | SyllableTail(t);
        }

        // consonant_syllable = (cn | PLACEHOLDER | DOTTEDCIRCLE) broken_cluster
        private ulong ConsonantSyllable(ulong s)
            => BrokenCluster(Cn(s) | _p.One(s, KhmerCat.Placeholder) | _p.One(s, KhmerCat.DottedCircle));
    }
}
