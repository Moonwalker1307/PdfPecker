using System.Runtime.InteropServices;

namespace PdfPecker.Fonts;

// Khmer, Myanmar, Sinhala and Tibetan, which HarfBuzz shapes with its Khmer and Myanmar shapers and its Universal Shaping
// Engine (USE): what the three have in common. Each normalizes the text as the Indic shaper does, gives the characters
// the categories of its tables, splits the text into syllables, reorders each syllable once around its GSUB stages, and
// runs the presentation features over the whole run after them. Khmer, Myanmar and Use hold what sets each apart.
internal static partial class ComplexShaper
{
    private enum SyllabicShaper : byte { Khmer, Myanmar, Use }

    // How a stage's lookups treat the joiners: skip both (HarfBuzz's default), match the zero-width joiner only where the
    // rules name it (F_MANUAL_ZWJ), or both (F_MANUAL_JOINERS).
    private enum Joiners : byte { Auto, ManualZwj, Manual }

    // The mask bit of the Khmer cfar feature, for the consonants after a Coeng Ro.
    private const uint Cfar = 1 << 16;

    /// <summary>How a font shapes Khmer, Myanmar, Sinhala or Tibetan: its GSUB stages and the masks of its features.</summary>
    private sealed class SyllabicPlan
    {
        public required Script Script { get; init; }
        public required SyllabicShaper Shaper { get; init; }

        /// <summary>The GSUB stages in order; each shaper says what runs between them.</summary>
        public StageLookup[][] Stages { get; init; } = [];

        // The mask bits of the features that apply to some glyphs only, 0 when the font lacks the feature.
        public uint RphfMask { get; init; }
        public uint PrefMask { get; init; }
        public uint BlwfMask { get; init; }
        public uint AbvfMask { get; init; }
        public uint PstfMask { get; init; }
        public uint CfarMask { get; init; }
        public uint IsolMask { get; init; }
        public uint InitMask { get; init; }
        public uint MediMask { get; init; }
        public uint FinaMask { get; init; }
    }

    private static bool IsSyllabic(Script script) => script is >= Script.Khmer and <= Script.Tibetan;

    private static SyllabicShaper ShaperOf(Script script) => script switch
    {
        Script.Khmer => SyllabicShaper.Khmer,
        Script.Myanmar => SyllabicShaper.Myanmar,
        _ => SyllabicShaper.Use,
    };

    // The syllabic plan of a script, with the positioning features of the script's tag in GPOS, as for the Indic scripts.
    private static Plan BuildSyllabicScriptPlan(FontProgram font, OpenTypeLayout? layout, Script script)
    {
        var syllabic = BuildSyllabicPlan(layout, script, out uint gsubTag);
        if (layout == null) return syllabic == null ? Plan.Empty : new Plan { Syllabic = syllabic };
        if (syllabic == null) return BuildPlan(layout, script, gsubTag);
        uint gposTag = ScriptTag(script);
        var gpos = new List<(int Lookup, uint Mask)>();
        AddStage(gpos, layout, gpos: true, gposTag, PositioningFeatures);
        return new Plan
        {
            Gpos = [.. gpos],
            MarkLookups = MarkLookupsOf(layout, gposTag),
            HasMarkPositioning = layout.HasFeature(true, gposTag, FontProgram.Tag("mark")),
            HasGposKern = layout.HasFeature(true, gposTag, FontProgram.Tag("kern")),
            HasScriptGsub = true,
            Syllabic = syllabic,
        };
    }

    /// <summary>
    /// The syllabic plan of a font for a script, or null when HarfBuzz would shape the text with its default shaper: a
    /// font whose GSUB has only the default script or Latin for Myanmar, Sinhala or Tibetan, or only the old Myanmar
    /// tag 'mymr', from before Myanmar had a shaping specification. Khmer always gets the Khmer shaper.
    /// </summary>
    private static SyllabicPlan? BuildSyllabicPlan(OpenTypeLayout? layout, Script script, out uint gsubTag)
    {
        var shaper = ShaperOf(script);
        gsubTag = ScriptTag(script);
        if (shaper != SyllabicShaper.Khmer && layout is { HasGsub: true } && !layout.HasScript(false, gsubTag))
        {
            uint old = FontProgram.Tag("mymr");
            if (script == Script.Myanmar && layout.HasScript(false, old))
            {
                gsubTag = old;
                return null;
            }
            if (layout.HasScript(false, FontProgram.Tag("DFLT")) || layout.HasScript(false, FontProgram.Tag("dflt"))
                || layout.HasScript(false, FontProgram.Tag("latn")))
                return null;
        }

        uint tag = gsubTag;
        uint MaskIf(string feature, uint mask) => layout != null && layout.HasFeature(false, tag, FontProgram.Tag(feature)) ? mask : 0;
        var stages = shaper switch
        {
            SyllabicShaper.Khmer => KhmerStages(layout, tag),
            SyllabicShaper.Myanmar => MyanmarStages(layout, tag),
            _ => UseStages(layout, tag),
        };
        return shaper switch
        {
            SyllabicShaper.Khmer => new SyllabicPlan
            {
                Script = script, Shaper = shaper, Stages = stages,
                PrefMask = MaskIf("pref", Pref), BlwfMask = MaskIf("blwf", Blwf), AbvfMask = MaskIf("abvf", Abvf),
                PstfMask = MaskIf("pstf", Pstf), CfarMask = MaskIf("cfar", Cfar),
            },
            SyllabicShaper.Myanmar => new SyllabicPlan { Script = script, Shaper = shaper, Stages = stages },
            _ => new SyllabicPlan
            {
                Script = script, Shaper = shaper, Stages = stages,
                RphfMask = MaskIf("rphf", Rphf),
                IsolMask = MaskIf("isol", Isol), InitMask = MaskIf("init", Init), MediMask = MaskIf("medi", Medi),
                FinaMask = MaskIf("fina", Fina),
            },
        };
    }

    // The common features after the shaper's own, in the last stage, as HarfBuzz adds them after the shaper's features.
    private static readonly string[] SyllabicCommonFeatures = ["rlig", "calt", "clig", "liga", "rclt"];

    // The last stage: the shaper's presentation features and the common ones. Khmer turns 'liga' off, as Indic does.
    private static StageLookup[] SyllabicPresentationStage(OpenTypeLayout? layout, uint tag, string[] features, Joiners joiners, bool liga)
        => SyllabicStage(layout, tag,
            [
                .. features.Select(f => (f, AllGlyphs, false, joiners)),
                .. SyllabicCommonFeatures.Where(f => liga || f != "liga").Select(f => (f, AllGlyphs, false, Joiners.Auto)),
            ]);

    /// <summary>
    /// The lookups of a stage, each once with the masks of all the features that use it, in the order of the lookup list.
    /// A lookup stays inside a syllable, or leaves a joiner to its rules, when one of its features does.
    /// </summary>
    private static StageLookup[] SyllabicStage(OpenTypeLayout? layout, uint tag,
        (string Feature, uint Mask, bool PerSyllable, Joiners Joiners)[] features)
    {
        if (layout == null) return [];
        var stage = new Dictionary<int, (uint Mask, bool PerSyllable, Joiners Joiners)>();
        foreach (var (feature, mask, perSyllable, joiners) in features)
            foreach (int lookup in layout.FeatureLookups(gpos: false, tag, FontProgram.Tag(feature)))
            {
                ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(stage, lookup, out _);
                entry = (entry.Mask | mask, entry.PerSyllable || perSyllable, (Joiners)Math.Max((byte)entry.Joiners, (byte)joiners));
            }
        var lookups = stage.Select(e => new StageLookup(e.Key, e.Value.Mask, e.Value.PerSyllable,
            e.Value.Joiners == Joiners.Manual, e.Value.Joiners == Joiners.ManualZwj)).ToArray();
        Array.Sort(lookups, static (a, b) => a.Lookup.CompareTo(b.Lookup));
        return lookups;
    }

    /// <summary>Shapes a run of Khmer, Myanmar, Sinhala or Tibetan, as HarfBuzz's shaper for the script does.</summary>
    private static void ShapeSyllabic(FontProgram font, OpenTypeLayout? layout, GlyphRun run, Plan plan, string s, int start, int length,
        bool rtl, List<ShapedGlyph> output)
    {
        var syllabic = plan.Syllabic!;
        AddText(font, layout, run, s, start, length, rtl, decomposeFirst: true);
        if (syllabic.Script == Script.Sinhala) SeparateVowelLookalikes(font, layout, run, s, start, length);
        // HarfBuzz reorders and composes only when a mark follows the run's first character; otherwise what it
        // decomposed stays decomposed.
        if (HasMarkAfterFirst(s, start, length))
        {
            ReorderMarks(run, syllabic.Script);
            UnhideGraphemeJoiners(run);
            Compose(font, layout, run, syllabic.Script);
        }

        var lookups = new LookupPass(layout, run, rtl);
        switch (syllabic.Shaper)
        {
            case SyllabicShaper.Khmer: ShapeKhmer(font, layout, run, syllabic, ref lookups); break;
            case SyllabicShaper.Myanmar: ShapeMyanmar(font, layout, run, syllabic, ref lookups); break;
            default: ShapeUse(font, layout, run, syllabic, ref lookups); break;
        }

        SetAdvances(font, run);
        // Myanmar and the Universal Shaping Engine zero the marks' advances before GPOS (HarfBuzz's
        // ZERO_WIDTH_MARKS_BY_GDEF_EARLY); Khmer leaves them as the font has them.
        if (syllabic.Shaper != SyllabicShaper.Khmer) ZeroMarkAdvances(run);
        lookups.Apply(plan.Gpos, gpos: true, plan.MarkLookups);
        if (!plan.HasGposKern && font.Kerning is { } kerning) ApplyKerning(kerning, run);
        OpenTypeLayout.PropagateAttachments(run, rtl);
        RemoveIgnorables(run);
        WriteOutput(font, run, output);
    }

    // A grapheme joiner that kept no marks from being reordered need not block lookups either (HarfBuzz issue 554).
    private static void UnhideGraphemeJoiners(GlyphRun run)
    {
        var glyphs = run.AsSpan();
        for (int i = 1; i + 1 < glyphs.Length; i++)
        {
            if (glyphs[i].CodePoint != 0x034F) continue;
            int next = CombiningClass(run, i + 1);
            if (next == 0 || CombiningClass(run, i - 1) <= next) glyphs[i].Substitutions |= OpenTypeLayout.Unhidden;
        }
    }

    private static bool HasMarkAfterFirst(string s, int start, int length)
    {
        int end = start + length;
        for (int i = start + (char.IsSurrogatePair(s, start) ? 2 : 1); i < end; i++)
        {
            int cp = char.IsHighSurrogate(s[i]) && i + 1 < end && char.IsLowSurrogate(s[i + 1]) ? char.ConvertToUtf32(s[i], s[++i]) : s[i];
            if (IsUnicodeMark(cp)) return true;
        }
        return false;
    }

    private static void ZeroMarkAdvances(GlyphRun run)
    {
        var glyphs = run.AsSpan();
        for (int i = 0; i < glyphs.Length; i++)
            if (IsMark(glyphs[i].Props)) glyphs[i].XAdvance = 0;
    }

    /// <summary>
    /// Splits the run into syllables with a machine of patterns: at each glyph the longest match, the first pattern on a
    /// tie, and a glyph on its own of the given type when none matches, as Ragel's scanners do in HarfBuzz. The machine
    /// gets the categories of the next glyphs (at most <see cref="MaxSyllableLength"/>) as sets of positions.
    /// </summary>
    private static void FindSyllables(GlyphRun run, SyllableMatcher match)
    {
        var glyphs = run.AsSpan();
        Span<ulong> masks = stackalloc ulong[64];
        int serial = 1;
        for (int i = 0; i < glyphs.Length;)
        {
            int window = Math.Min(MaxSyllableLength, glyphs.Length - i);
            masks.Clear();
            for (int k = 0; k < window; k++) masks[glyphs[i + k].IndicCategory & 63] |= 1UL << k;
            var (length, type) = match(masks);
            for (int k = i; k < i + length; k++) glyphs[k].Syllable = (byte)(serial << 4 | type);
            serial = serial == 15 ? 1 : serial + 1;
            i += length;
        }
    }

    private delegate (int Length, byte Type) SyllableMatcher(Span<ulong> masks);

    /// <summary>The building blocks of the syllable machines, over sets of positions as in <see cref="SyllableMachine"/>.</summary>
    private readonly ref struct Patterns(Span<ulong> masks)
    {
        public const ulong Empty = 1;

        private readonly Span<ulong> _masks = masks;

        /// <summary>One glyph of the category after any of the positions.</summary>
        public ulong One(ulong s, int category) => (s & _masks[category]) << 1;

        /// <summary>Any number of glyphs of the category.</summary>
        public ulong Star(ulong s, int category)
        {
            for (ulong next = s | One(s, category); next != s; next = s | One(s, category)) s = next;
            return s;
        }

        /// <summary>The length of the longest match: the highest position, 0 for none.</summary>
        public static int Longest(ulong ends) => 63 - System.Numerics.BitOperations.LeadingZeroCount(ends | 1);

        // A pattern wins with a longer match than the ones before it; on a tie the earlier one stays.
        public static void Consider(ulong ends, byte patternType, ref int best, ref byte type)
        {
            int length = Longest(ends);
            if (length <= best) return;
            best = length;
            type = patternType;
        }
    }

    // Moves the glyph at from to the position to, before it, shifting the glyphs between up by one.
    private static void MoveBack(Span<GlyphInfo> info, int from, int to)
    {
        var g = info[from];
        info.Slice(to, from - to).CopyTo(info[(to + 1)..]);
        info[to] = g;
    }

    // Moves the glyph at from to the position to, after it, shifting the glyphs between down by one.
    private static void MoveForward(Span<GlyphInfo> info, int from, int to)
    {
        var g = info[from];
        info.Slice(from + 1, to - from).CopyTo(info[from..]);
        info[to] = g;
    }
}
