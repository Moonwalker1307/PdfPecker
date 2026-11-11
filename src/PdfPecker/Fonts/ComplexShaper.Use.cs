namespace PdfPecker.Fonts;

// Sinhala and Tibetan, ported from HarfBuzz's Universal Shaping Engine (hb-ot-shaper-use.cc and its machine), which
// shapes them as Microsoft's USE specification describes: the text splits into clusters, the reph and pre-base forms
// the font substitutes are recorded, and once the basic features have run the reph moves towards the end of its cluster
// and the pre-base vowels and forms move to its start.
internal static partial class ComplexShaper
{
    // HarfBuzz's USE categories, the values of its machine and of UseTables.
    private static class UseCat
    {
        public const byte O = 0, B = 1, N = 4, GB = 5, CGJ = 6, SUB = 11, H = 12, HN = 13, ZWNJ = 14, WJ = 16, R = 18,
            VPre = 22, VMPre = 23, FAbv = 24, FBlw = 25, FPst = 26, MAbv = 27, MBlw = 28, MPst = 29, MPre = 30, CMAbv = 31,
            CMBlw = 32, VAbv = 33, VBlw = 34, VPst = 35, VMAbv = 37, VMBlw = 38, VMPst = 39, SMAbv = 41, SMBlw = 42, CS = 43,
            IS = 44, FMAbv = 45, FMBlw = 46, FMPst = 47, Sk = 48, G = 49, J = 50, SB = 51, SE = 52, HVM = 53, HM = 54, HR = 55,
            RK = 56;
    }

    private static class UseSyllable
    {
        public const byte ViramaTerminated = 0, SakotTerminated = 1, Standard = 2, NumberJoinerTerminated = 3, Numeral = 4,
            Symbol = 5, Hieroglyph = 6, Broken = 7, NonCluster = 8;
    }

    // The categories after which the reph stops moving.
    private static readonly ulong UsePostBase = Flag(UseCat.FAbv) | Flag(UseCat.FBlw) | Flag(UseCat.FPst) | Flag(UseCat.FMAbv)
        | Flag(UseCat.FMBlw) | Flag(UseCat.FMPst) | Flag(UseCat.MAbv) | Flag(UseCat.MBlw) | Flag(UseCat.MPst) | Flag(UseCat.MPre)
        | Flag(UseCat.VAbv) | Flag(UseCat.VBlw) | Flag(UseCat.VPst) | Flag(UseCat.VPre) | Flag(UseCat.VMAbv) | Flag(UseCat.VMBlw)
        | Flag(UseCat.VMPst) | Flag(UseCat.VMPre);

    // The syllables that are reordered.
    private const int UseReordered = 1 << UseSyllable.ViramaTerminated | 1 << UseSyllable.SakotTerminated | 1 << UseSyllable.Standard
        | 1 << UseSyllable.Symbol | 1 << UseSyllable.Broken;

    // The orthographic unit shaping features, applied together.
    private static readonly string[] UseBasicFeatures = ["rkrf", "abvf", "blwf", "half", "pstf", "vatu", "cjct"];

    // rvrn; the default glyph pre-processing (locl, ccmp, nukt, akhn); rphf; pref; the orthographic unit shaping
    // features; the topographical forms; then the presentation features.
    private static StageLookup[][] UseStages(OpenTypeLayout? layout, uint tag) =>
    [
        SyllabicStage(layout, tag, [("rvrn", AllGlyphs, false, Joiners.Auto)]),
        SyllabicStage(layout, tag,
        [
            ("locl", AllGlyphs, true, Joiners.Auto), ("ccmp", AllGlyphs, true, Joiners.Auto), ("nukt", AllGlyphs, true, Joiners.Auto),
            ("akhn", AllGlyphs, true, Joiners.ManualZwj),
        ]),
        SyllabicStage(layout, tag, [("rphf", Rphf, true, Joiners.ManualZwj)]),
        SyllabicStage(layout, tag, [("pref", AllGlyphs, true, Joiners.ManualZwj)]),
        SyllabicStage(layout, tag,
            [.. UseBasicFeatures.Select(f => (f, AllGlyphs, true, Joiners.ManualZwj))]),
        SyllabicStage(layout, tag,
            [("isol", Isol, false, Joiners.Auto), ("init", Init, false, Joiners.Auto), ("medi", Medi, false, Joiners.Auto), ("fina", Fina, false, Joiners.Auto)]),
        SyllabicPresentationStage(layout, tag, ["abvs", "blws", "haln", "pres", "psts"], Joiners.ManualZwj, liga: true),
    ];

    private static void ShapeUse(FontProgram font, OpenTypeLayout? layout, GlyphRun run, SyllabicPlan plan, ref LookupPass lookups)
    {
        var glyphs = run.AsSpan();
        for (int i = 0; i < glyphs.Length; i++) glyphs[i].IndicCategory = UseTables.Get(glyphs[i].CodePoint);
        lookups.Apply(plan.Stages[0]);
        FindUseClusters(run);
        SetUseMasks(run, plan);
        lookups.Apply(plan.Stages[1]);
        ClearSubstituted(run);
        lookups.Apply(plan.Stages[2]);
        RecordReph(run, plan);
        ClearSubstituted(run);
        lookups.Apply(plan.Stages[3]);
        RecordPreBase(run);
        lookups.Apply(plan.Stages[4]);
        InsertDottedCircles(font, layout, run, UseSyllable.Broken, UseCat.B, UseCat.R, IndicPos.End);
        ForEachSyllable(run, ReorderUse);
        lookups.Apply(plan.Stages[5]);
        lookups.Apply(plan.Stages[6]);
    }

    private static void ClearSubstituted(GlyphRun run)
    {
        var glyphs = run.AsSpan();
        for (int i = 0; i < glyphs.Length; i++) glyphs[i].Substitutions &= unchecked((byte)~OpenTypeLayout.Substituted);
    }

    /// <summary>
    /// Splits the run into clusters with HarfBuzz's USE machine. The machine does not see grapheme joiners (the category
    /// CGJ, which also has the zero-width joiner and the variation selectors) or a non-joiner before a mark; they belong
    /// to the cluster of the glyph before them.
    /// </summary>
    private static void FindUseClusters(GlyphRun run)
    {
        var glyphs = run.AsSpan();
        Span<int> seen = glyphs.Length <= 256 ? stackalloc int[glyphs.Length] : new int[glyphs.Length];
        int count = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            if (glyphs[i].IndicCategory == UseCat.CGJ) continue;
            if (glyphs[i].IndicCategory == UseCat.ZWNJ)
            {
                int next = i + 1;
                while (next < glyphs.Length && glyphs[next].IndicCategory == UseCat.CGJ) next++;
                if (next < glyphs.Length && IsUnicodeMark(glyphs[next].CodePoint)) continue;
            }
            seen[count++] = i;
        }

        // Glyphs before the first one the machine sees belong to no syllable (0), as in HarfBuzz, so a lookup that starts
        // on one of them may reach into the syllable after it.
        int serial = 1;
        for (int k = 0, end = count == 0 ? glyphs.Length : seen[0]; k < end; k++) glyphs[k].Syllable = 0;
        Span<ulong> masks = stackalloc ulong[64];
        for (int i = 0; i < count;)
        {
            int window = Math.Min(MaxSyllableLength, count - i);
            masks.Clear();
            for (int k = 0; k < window; k++) masks[glyphs[seen[i + k]].IndicCategory & 63] |= 1UL << k;
            var (length, type) = new UseMachine(masks).Longest();
            int from = seen[i], to = i + length < count ? seen[i + length] : glyphs.Length;
            for (int k = from; k < to; k++) glyphs[k].Syllable = (byte)(serial << 4 | type);
            serial = serial == 15 ? 1 : serial + 1;
            i += length;
        }
    }

    /// <summary>
    /// The reph form applies to the first glyph of a cluster that starts with a repha, and otherwise to its first three;
    /// the topographical forms to the clusters by their place among the clusters around them, as HarfBuzz gives them.
    /// </summary>
    private static void SetUseMasks(GlyphRun run, SyllabicPlan plan)
    {
        var info = run.AsSpan();
        if (plan.RphfMask != 0)
            ForEachSyllable(run, (glyphs, start, end) =>
            {
                int limit = glyphs[start].IndicCategory == UseCat.R ? 1 : Math.Min(3, end - start);
                for (int i = start; i < start + limit; i++) glyphs[i].Mask |= plan.RphfMask;
            });

        uint all = plan.IsolMask | plan.InitMask | plan.MediMask | plan.FinaMask;
        if (all == 0) return;
        // isol, init, medi, fina; -1 for none.
        Span<uint> forms = [plan.IsolMask, plan.InitMask, plan.MediMask, plan.FinaMask];
        const int IsolForm = 0, InitForm = 1, MediForm = 2, FinaForm = 3;
        int lastStart = 0, lastForm = -1;
        for (int start = 0; start < info.Length;)
        {
            int end = start + 1;
            while (end < info.Length && info[end].Syllable == info[start].Syllable) end++;
            byte type = (byte)(info[start].Syllable & 0x0F);
            if (type is UseSyllable.Hieroglyph or UseSyllable.NonCluster) lastForm = -1;
            else
            {
                bool join = lastForm is FinaForm or IsolForm;
                if (join)
                {
                    lastForm = lastForm == FinaForm ? MediForm : InitForm;
                    for (int i = lastStart; i < start; i++) info[i].Mask = (info[i].Mask & ~all) | forms[lastForm];
                }
                lastForm = join ? FinaForm : IsolForm;
                for (int i = start; i < end; i++) info[i].Mask = (info[i].Mask & ~all) | forms[lastForm];
            }
            lastStart = start;
            start = end;
        }
    }

    // A reph the font substituted counts as a repha from now on.
    private static void RecordReph(GlyphRun run, SyllabicPlan plan)
    {
        if (plan.RphfMask == 0) return;
        ForEachSyllable(run, (info, start, end) =>
        {
            for (int i = start; i < end && (info[i].Mask & plan.RphfMask) != 0; i++)
                if ((info[i].Substitutions & OpenTypeLayout.Substituted) != 0)
                {
                    info[i].IndicCategory = UseCat.R;
                    break;
                }
        });
    }

    // A pre-base form the font substituted moves as a pre-base vowel does.
    private static void RecordPreBase(GlyphRun run) => ForEachSyllable(run, static (info, start, end) =>
    {
        for (int i = start; i < end; i++)
            if ((info[i].Substitutions & OpenTypeLayout.Substituted) != 0)
            {
                info[i].IndicCategory = UseCat.VPre;
                break;
            }
    });

    private static bool IsUseHalant(in GlyphInfo g)
        => g.IndicCategory is UseCat.H or UseCat.HVM or UseCat.IS && (g.Substitutions & OpenTypeLayout.Ligated) == 0;

    // HarfBuzz's reorder_syllable_use: the reph moves to the end of its cluster, before the first post-base glyph, and
    // the pre-base vowels and forms move to the start, after the last halant.
    private static void ReorderUse(Span<GlyphInfo> info, int start, int end)
    {
        if ((UseReordered & 1 << (info[start].Syllable & 0x0F)) == 0) return;

        if (info[start].IndicCategory == UseCat.R && end - start > 1)
            for (int i = start + 1; i < end; i++)
            {
                bool postBase = (Flag(info[i].IndicCategory) & UsePostBase) != 0 || IsUseHalant(info[i]);
                if (postBase || i == end - 1)
                {
                    if (postBase) i--;
                    MoveForward(info, start, i);
                    break;
                }
            }

        int j = start;
        for (int i = start; i < end; i++)
        {
            if (IsUseHalant(info[i])) j = i + 1;
            // Only the first glyph of a multiple substitution moves.
            else if (info[i].IndicCategory is UseCat.VPre or UseCat.VMPre && info[i].LigComp == 0 && j < i) MoveBack(info, i, j);
        }
    }

    /// <summary>
    /// HarfBuzz's USE machine (hb-ot-shaper-use-machine.rl) over sets of positions. Its hieroglyph clusters need
    /// Egyptian hieroglyphs, which the scripts shaped here do not have, and are left out.
    /// </summary>
    private readonly ref struct UseMachine(Span<ulong> masks)
    {
        private readonly Patterns _p = new(masks);

        public (int Length, byte Type) Longest()
        {
            const ulong e = Patterns.Empty;
            int best = 0;
            byte type = UseSyllable.NonCluster;
            ulong start = ComplexSyllableStart(e);
            Patterns.Consider(Zwnj(ViramaTerminatedTail(start)), UseSyllable.ViramaTerminated, ref best, ref type);
            Patterns.Consider(Zwnj(SakotTerminatedTail(start)), UseSyllable.SakotTerminated, ref best, ref type);
            Patterns.Consider(Zwnj(ComplexSyllableTail(start)), UseSyllable.Standard, ref best, ref type);
            ulong number = _p.One(e, UseCat.N);
            Patterns.Consider(Zwnj(NumberJoinerTerminatedTail(number)), UseSyllable.NumberJoinerTerminated, ref best, ref type);
            Patterns.Consider(Zwnj(number | NumeralTail(number)), UseSyllable.Numeral, ref best, ref type);
            ulong symbol = _p.One(e, UseCat.O) | _p.One(e, UseCat.GB) | _p.One(e, UseCat.SB);
            Patterns.Consider(Zwnj(symbol | Tail(symbol)), UseSyllable.Symbol, ref best, ref type);
            Patterns.Consider(_p.One(e, UseCat.FMPst), UseSyllable.NonCluster, ref best, ref type);
            ulong broken = e | _p.One(e, UseCat.R);
            Patterns.Consider(Zwnj(Tail(broken) | NumberJoinerTerminatedTail(broken) | NumeralTail(broken)), UseSyllable.Broken, ref best, ref type);
            // Otherwise "other": the glyph on its own.
            return (Math.Max(best, 1), type);
        }

        private ulong Opt(ulong s, int category) => s | _p.One(s, category);

        // ZWNJ?
        private ulong Zwnj(ulong s) => Opt(s, UseCat.ZWNJ);

        // h = H | HVM | IS | Sk
        private ulong Halant(ulong s) => _p.One(s, UseCat.H) | _p.One(s, UseCat.HVM) | _p.One(s, UseCat.IS) | _p.One(s, UseCat.Sk);

        // CMAbv* CMBlw*
        private ulong ConsonantModifierMarks(ulong s) => _p.Star(_p.Star(s, UseCat.CMAbv), UseCat.CMBlw);

        // consonant_modifiers = CMAbv* CMBlw* ((h B | SUB) CMAbv* CMBlw*)*
        private ulong ConsonantModifiers(ulong s)
        {
            s = ConsonantModifierMarks(s);
            for (ulong next = s | Subjoined(s); next != s; next = s | Subjoined(s)) s = next;
            return s;
        }

        private ulong Subjoined(ulong s) => ConsonantModifierMarks(_p.One(Halant(s), UseCat.B) | _p.One(s, UseCat.SUB));

        // medial_consonants = MPre? MAbv? MBlw? MPst?
        private ulong MedialConsonants(ulong s) => Opt(Opt(Opt(Opt(s, UseCat.MPre), UseCat.MAbv), UseCat.MBlw), UseCat.MPst);

        // dependent_vowels = VPre* VAbv* VBlw* VPst* | H
        private ulong DependentVowels(ulong s)
            => _p.Star(_p.Star(_p.Star(_p.Star(s, UseCat.VPre), UseCat.VAbv), UseCat.VBlw), UseCat.VPst) | _p.One(s, UseCat.H);

        // vowel_modifiers = HVM? VMPre* VMAbv* VMBlw* VMPst*
        private ulong VowelModifiers(ulong s)
            => _p.Star(_p.Star(_p.Star(_p.Star(Opt(s, UseCat.HVM), UseCat.VMPre), UseCat.VMAbv), UseCat.VMBlw), UseCat.VMPst);

        // final_consonants = FAbv* FBlw* FPst*
        private ulong FinalConsonants(ulong s) => _p.Star(_p.Star(_p.Star(s, UseCat.FAbv), UseCat.FBlw), UseCat.FPst);

        // final_modifiers = FMAbv* FMBlw* | FMPst?
        private ulong FinalModifiers(ulong s) => _p.Star(_p.Star(s, UseCat.FMAbv), UseCat.FMBlw) | Opt(s, UseCat.FMPst);

        // complex_syllable_start = (R | CS)? (B | GB)
        private ulong ComplexSyllableStart(ulong s)
        {
            ulong t = s | _p.One(s, UseCat.R) | _p.One(s, UseCat.CS);
            return _p.One(t, UseCat.B) | _p.One(t, UseCat.GB);
        }

        // complex_syllable_middle = consonant_modifiers medial_consonants dependent_vowels vowel_modifiers (Sk B)*
        private ulong ComplexSyllableMiddle(ulong s)
        {
            s = VowelModifiers(DependentVowels(MedialConsonants(ConsonantModifiers(s))));
            for (ulong next = s | _p.One(_p.One(s, UseCat.Sk), UseCat.B); next != s; next = s | _p.One(_p.One(s, UseCat.Sk), UseCat.B)) s = next;
            return s;
        }

        // complex_syllable_tail = complex_syllable_middle final_consonants final_modifiers
        private ulong ComplexSyllableTail(ulong s) => FinalModifiers(FinalConsonants(ComplexSyllableMiddle(s)));

        // number_joiner_terminated_cluster_tail = (HN N)* HN
        private ulong NumberJoinerTerminatedTail(ulong s) => _p.One(NumberPairs(s), UseCat.HN);

        // numeral_cluster_tail = (HN N)+
        private ulong NumeralTail(ulong s) => NumberPairs(NumberPair(s));

        private ulong NumberPair(ulong s) => _p.One(_p.One(s, UseCat.HN), UseCat.N);

        private ulong NumberPairs(ulong s)
        {
            for (ulong next = s | NumberPair(s); next != s; next = s | NumberPair(s)) s = next;
            return s;
        }

        // symbol_cluster_tail = SMAbv+ SMBlw* | SMBlw+
        private ulong SymbolTail(ulong s)
            => _p.Star(_p.Star(_p.One(s, UseCat.SMAbv), UseCat.SMAbv), UseCat.SMBlw) | _p.Star(_p.One(s, UseCat.SMBlw), UseCat.SMBlw);

        // virama_terminated_cluster_tail = consonant_modifiers (IS | RK)
        private ulong ViramaTerminatedTail(ulong s)
        {
            ulong t = ConsonantModifiers(s);
            return _p.One(t, UseCat.IS) | _p.One(t, UseCat.RK);
        }

        // sakot_terminated_cluster_tail = complex_syllable_middle Sk
        private ulong SakotTerminatedTail(ulong s) => _p.One(ComplexSyllableMiddle(s), UseCat.Sk);

        // tail = complex_syllable_tail | sakot_terminated_cluster_tail | symbol_cluster_tail | virama_terminated_cluster_tail
        private ulong Tail(ulong s) => ComplexSyllableTail(s) | SakotTerminatedTail(s) | SymbolTail(s) | ViramaTerminatedTail(s);
    }
}
