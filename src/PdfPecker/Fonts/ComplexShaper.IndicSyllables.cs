namespace PdfPecker.Fonts;

// Indic syllables: the categories and positions of the characters, the syllables they form (HarfBuzz's Ragel machine,
// hb-ot-shaper-indic-machine.rl, run as bit-parallel sets of positions), and the dotted circles that broken syllables get.
internal static partial class ComplexShaper
{
    // HarfBuzz's Indic categories (I_Cat), with the same values as its machine and generated table.
    private static class IndicCat
    {
        public const byte X = 0, C = 1, V = 2, N = 3, H = 4, ZWNJ = 5, ZWJ = 6, M = 7, SM = 8, A = 9, Placeholder = 10,
            DottedCircle = 11, RS = 12, MPst = 13, Repha = 14, Ra = 15, CM = 16, Symbol = 17, CS = 18, SMPst = 57;
    }

    // Visual positions in a syllable from left to right (HarfBuzz's ot_position_t).
    private static class IndicPos
    {
        public const byte Start = 0, RaToBecomeReph = 1, PreM = 2, PreC = 3, BaseC = 4, AfterMain = 5, AboveC = 6,
            BeforeSub = 7, BelowC = 8, AfterSub = 9, BeforePost = 10, PostC = 11, AfterPost = 12, Smvd = 13, End = 14;
    }

    // The kinds of syllable, kept in the low nibble of GlyphInfo.Syllable.
    private static class SyllableType
    {
        public const byte Consonant = 0, Vowel = 1, Standalone = 2, Symbol = 3, Broken = 4, NonIndic = 5;
    }

    private static ulong Flag(int value) => 1UL << value;

    // A syllable longer than this is cut, so that finding the syllables stays linear in the length of the text. Real
    // syllables are a few characters; HarfBuzz itself treats ones over 127 glyphs as out of the ordinary.
    private const int MaxSyllableLength = 62;

    private static void SetIndicProperties(GlyphRun run)
    {
        var glyphs = run.AsSpan();
        for (int i = 0; i < glyphs.Length; i++)
        {
            ushort value = IndicTables.Get(glyphs[i].CodePoint);
            glyphs[i].IndicCategory = (byte)value;
            glyphs[i].IndicPosition = (byte)(value >> 8);
        }
    }

    /// <summary>
    /// Splits the run into syllables as HarfBuzz's scanner does: at each glyph the longest of the syllable patterns that
    /// matches, the first pattern on a tie, and a glyph on its own when none does. Each glyph gets the syllable's serial
    /// number, 1 to 15 and round again, and type.
    /// </summary>
    private static void FindSyllables(GlyphRun run)
    {
        var glyphs = run.AsSpan();
        Span<ulong> masks = stackalloc ulong[64];
        int serial = 1;
        for (int i = 0; i < glyphs.Length;)
        {
            int window = Math.Min(MaxSyllableLength, glyphs.Length - i);
            masks.Clear();
            for (int k = 0; k < window; k++) masks[glyphs[i + k].IndicCategory & 63] |= 1UL << k;
            var (length, type) = new SyllableMachine(masks).Longest();
            for (int k = i; k < i + length; k++) glyphs[k].Syllable = (byte)(serial << 4 | type);
            serial = serial == 15 ? 1 : serial + 1;
            i += length;
        }
    }

    /// <summary>
    /// The syllable patterns of HarfBuzz's Indic machine over sets of positions: bit k of a set means that the pattern
    /// so far can end after the first k glyphs of the window, and each category's mask has bit k when glyph k has it.
    /// </summary>
    private readonly ref struct SyllableMachine(Span<ulong> masks)
    {
        private readonly Span<ulong> _masks = masks;

        public (int Length, byte Type) Longest()
        {
            const ulong Empty = 1;
            int best = 0;
            byte type = SyllableType.NonIndic;
            Consider(ConsonantSyllable(Empty), SyllableType.Consonant, ref best, ref type);
            Consider(VowelSyllable(Empty), SyllableType.Vowel, ref best, ref type);
            Consider(StandaloneCluster(Empty), SyllableType.Standalone, ref best, ref type);
            Consider(SyllableTail(Symbol(Empty)), SyllableType.Symbol, ref best, ref type);
            Consider(One(Empty, IndicCat.SMPst), SyllableType.NonIndic, ref best, ref type);
            Consider(BrokenCluster(Empty), SyllableType.Broken, ref best, ref type);
            // Otherwise "other": the glyph on its own.
            return (Math.Max(best, 1), type);
        }

        // A pattern wins with a longer match than the ones before it; on a tie the earlier one stays.
        private static void Consider(ulong ends, byte patternType, ref int best, ref byte type)
        {
            int length = 63 - System.Numerics.BitOperations.LeadingZeroCount(ends | 1);
            if (length <= best) return;
            best = length;
            type = patternType;
        }

        private ulong One(ulong s, int category) => (s & _masks[category]) << 1;

        // c = C | Ra
        private ulong C(ulong s) => One(s, IndicCat.C) | One(s, IndicCat.Ra);

        // n = (ZWNJ? RS)? (N N?)?
        private ulong N(ulong s)
        {
            ulong t = s | One(s | One(s, IndicCat.ZWNJ), IndicCat.RS);
            ulong n = One(t, IndicCat.N);
            return t | n | One(n, IndicCat.N);
        }

        // z = ZWJ | ZWNJ
        private ulong Z(ulong s) => One(s, IndicCat.ZWJ) | One(s, IndicCat.ZWNJ);

        // z*
        private ulong ZStar(ulong s)
        {
            for (ulong next = s | Z(s); next != s; next = s | Z(s)) s = next;
            return s;
        }

        // reph = Ra H | Repha
        private ulong Reph(ulong s) => One(One(s, IndicCat.Ra), IndicCat.H) | One(s, IndicCat.Repha);

        // sm = SM | SMPst
        private ulong Sm(ulong s) => One(s, IndicCat.SM) | One(s, IndicCat.SMPst);

        // cn = c ZWJ? n?
        private ulong Cn(ulong s)
        {
            ulong t = C(s);
            return N(t | One(t, IndicCat.ZWJ));
        }

        // symbol = Symbol N?
        private ulong Symbol(ulong s)
        {
            ulong t = One(s, IndicCat.Symbol);
            return t | One(t, IndicCat.N);
        }

        // matra_group = z* (M | sm? MPst) N? H?
        private ulong MatraGroup(ulong s)
        {
            ulong z = ZStar(s);
            ulong t = One(z, IndicCat.M) | One(z | Sm(z), IndicCat.MPst);
            t |= One(t, IndicCat.N);
            return t | One(t, IndicCat.H);
        }

        // syllable_tail = (z? sm sm? ZWNJ?)? (A | VD)*
        private ulong SyllableTail(ulong s)
        {
            ulong t = Sm(s | Z(s));
            t |= Sm(t);
            t = s | t | One(t, IndicCat.ZWNJ);
            for (ulong next = t | One(t, IndicCat.A); next != t; next = t | One(t, IndicCat.A)) t = next;
            return t;
        }

        // halant_group = z? H (ZWJ N?)?
        private ulong HalantGroup(ulong s)
        {
            ulong h = One(s | Z(s), IndicCat.H);
            ulong j = One(h, IndicCat.ZWJ);
            return h | j | One(j, IndicCat.N);
        }

        // complex_syllable_tail = (halant_group cn)* medial_group halant_or_matra_group syllable_tail, where medial_group
        // = CM?, halant_or_matra_group = final_halant_group | matra_group* and final_halant_group = halant_group | H ZWNJ.
        private ulong ComplexSyllableTail(ulong s)
        {
            for (ulong next = s | Cn(HalantGroup(s)); next != s; next = s | Cn(HalantGroup(s))) s = next;
            s |= One(s, IndicCat.CM);
            ulong matras = s;
            for (ulong next = matras | MatraGroup(matras); next != matras; next = matras | MatraGroup(matras)) matras = next;
            ulong t = HalantGroup(s) | One(One(s, IndicCat.H), IndicCat.ZWNJ) | matras;
            return SyllableTail(t);
        }

        // consonant_syllable = (Repha | CS)? cn complex_syllable_tail
        private ulong ConsonantSyllable(ulong s)
            => ComplexSyllableTail(Cn(s | One(s, IndicCat.Repha) | One(s, IndicCat.CS)));

        // vowel_syllable = reph? V n? (ZWJ | complex_syllable_tail)
        private ulong VowelSyllable(ulong s)
        {
            ulong t = N(One(s | Reph(s), IndicCat.V));
            return One(t, IndicCat.ZWJ) | ComplexSyllableTail(t);
        }

        // standalone_cluster = ((Repha | CS)? PLACEHOLDER | reph? DOTTEDCIRCLE) n? complex_syllable_tail
        private ulong StandaloneCluster(ulong s)
        {
            ulong placeholder = One(s | One(s, IndicCat.Repha) | One(s, IndicCat.CS), IndicCat.Placeholder);
            ulong dottedCircle = One(s | Reph(s), IndicCat.DottedCircle);
            return ComplexSyllableTail(N(placeholder | dottedCircle));
        }

        // broken_cluster = reph? n? complex_syllable_tail
        private ulong BrokenCluster(ulong s) => ComplexSyllableTail(N(s | Reph(s)));
    }

    /// <summary>
    /// Puts a dotted circle into each broken syllable (a mark with nothing to sit on), after its reph if it starts with
    /// one, as HarfBuzz does when the font has the dotted circle.
    /// </summary>
    private static void InsertDottedCircles(FontProgram font, OpenTypeLayout? layout, GlyphRun run)
    {
        int gid = 0;
        int lastSyllable = -1;
        for (int i = 0; i < run.Count; i++)
        {
            var g = run[i];
            if (g.Syllable == lastSyllable || (g.Syllable & 0x0F) != SyllableType.Broken) continue;
            lastSyllable = g.Syllable;
            if (gid == 0 && (gid = font.GlyphId(0x25CC)) == 0) return;
            while (i < run.Count && run[i].Syllable == lastSyllable && run[i].IndicCategory == IndicCat.Repha) i++;
            run.Insert(i, new GlyphInfo
            {
                Gid = gid, CodePoint = 0x25CC, Cluster = g.Cluster, Length = 0, Mask = g.Mask, Syllable = g.Syllable,
                IndicCategory = IndicCat.DottedCircle, IndicPosition = IndicPos.End,
                Props = layout is { HasGlyphClasses: true } ? layout.GlyphProps(gid) : OpenTypeLayout.Base,
            });
        }
    }
}
