using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using PdfPecker.Text;

namespace PdfPecker.Fonts;

// The Indic scripts (Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada and Malayalam), ported from
// HarfBuzz's Indic shaper (hb-ot-shaper-indic.cc): the characters are normalized and split into syllables, each
// syllable is reordered into visual order (pre-base matras and reph) before the basic forms are substituted, and
// reordered again once they have been, before the presentation forms.
internal static partial class ComplexShaper
{
    private enum RephPosition : byte
    {
        AfterMain = IndicPos.AfterMain, BeforeSub = IndicPos.BeforeSub, AfterSub = IndicPos.AfterSub,
        BeforePost = IndicPos.BeforePost, AfterPost = IndicPos.AfterPost,
    }

    private enum RephMode : byte
    {
        Implicit,  // reph formed out of an initial Ra, Halant
        Explicit,  // reph formed out of an initial Ra, Halant, ZWJ
        LogRepha,  // reph encoded as a character of its own, which needs reordering
    }

    // Whether the below-base forms apply to the consonants before the base as well as after it.
    private enum BelowFormsMode : byte { PreAndPost, PostOnly }

    /// <summary>What sets the Indic scripts apart, as in HarfBuzz's indic_configs.</summary>
    private sealed record IndicConfig(int Virama, RephPosition RephPosition, RephMode RephMode, BelowFormsMode BelowForms,
        string NewTag, string OldTag);

    private static IndicConfig? IndicConfigOf(Script script) => script switch
    {
        Script.Devanagari => new(0x094D, RephPosition.BeforePost, RephMode.Implicit, BelowFormsMode.PreAndPost, "dev2", "deva"),
        Script.Bengali => new(0x09CD, RephPosition.AfterSub, RephMode.Implicit, BelowFormsMode.PreAndPost, "bng2", "beng"),
        Script.Gurmukhi => new(0x0A4D, RephPosition.BeforeSub, RephMode.Implicit, BelowFormsMode.PreAndPost, "gur2", "guru"),
        Script.Gujarati => new(0x0ACD, RephPosition.BeforePost, RephMode.Implicit, BelowFormsMode.PreAndPost, "gjr2", "gujr"),
        Script.Oriya => new(0x0B4D, RephPosition.AfterMain, RephMode.Implicit, BelowFormsMode.PreAndPost, "ory2", "orya"),
        Script.Tamil => new(0x0BCD, RephPosition.AfterPost, RephMode.Implicit, BelowFormsMode.PreAndPost, "tml2", "taml"),
        Script.Telugu => new(0x0C4D, RephPosition.AfterPost, RephMode.Explicit, BelowFormsMode.PostOnly, "tel2", "telu"),
        Script.Kannada => new(0x0CCD, RephPosition.AfterPost, RephMode.Implicit, BelowFormsMode.PostOnly, "knd2", "knda"),
        Script.Malayalam => new(0x0D4D, RephPosition.AfterMain, RephMode.LogRepha, BelowFormsMode.PreAndPost, "mlm2", "mlym"),
        _ => null,
    };

    private static bool IsIndic(Script script) => script is >= Script.Devanagari and <= Script.Malayalam;

    // The mask bits of the Indic features that apply to some glyphs of a syllable only.
    private const uint Rphf = 1 << 9, Pref = 1 << 10, Blwf = 1 << 11, Abvf = 1 << 12, Half = 1 << 13, Pstf = 1 << 14, IndicInit = 1 << 15;

    // The basic features, each a GSUB stage of its own between the two reorderings, then the presentation features and
    // the common ones, applied together after the final reordering ('liga' is off for Indic text, as in HarfBuzz).
    private static readonly (string Feature, uint Mask)[] IndicBasicFeatures =
    [
        ("nukt", AllGlyphs), ("akhn", AllGlyphs), ("rphf", Rphf), ("rkrf", AllGlyphs), ("pref", Pref), ("blwf", Blwf),
        ("abvf", Abvf), ("half", Half), ("pstf", Pstf), ("vatu", AllGlyphs), ("cjct", AllGlyphs),
    ];
    private static readonly (string Feature, uint Mask)[] IndicPresentationFeatures =
    [
        ("init", IndicInit), ("pres", AllGlyphs), ("abvs", AllGlyphs), ("blws", AllGlyphs), ("psts", AllGlyphs), ("haln", AllGlyphs),
    ];
    private static readonly (string Feature, uint Mask)[] IndicCommonFeatures =
        [("rlig", AllGlyphs), ("calt", AllGlyphs), ("clig", AllGlyphs), ("rclt", AllGlyphs)];

    /// <summary>A GSUB stage of the Indic shaper: its lookups, the glyphs each applies to, and whether it stays inside a syllable.</summary>
    private readonly record struct StageLookup(int Lookup, uint Mask, bool PerSyllable, bool ManualJoiners);

    /// <summary>How a font shapes one Indic script: its GSUB stages and what the reorderings need to know of it.</summary>
    private sealed class IndicPlan
    {
        public required Script Script { get; init; }
        public required IndicConfig Config { get; init; }

        /// <summary>Whether the font has the old script tag only (deva, beng, ...), whose fonts expect the old order.</summary>
        public bool OldSpec { get; init; }

        /// <summary>Whether a would-substitute test only counts rules without context, as HarfBuzz decides it per script.</summary>
        public bool ZeroContext { get; init; }

        public int ViramaGlyph { get; init; }

        /// <summary>rvrn; locl and ccmp; the eleven basic features; then the presentation and common features.</summary>
        public StageLookup[][] Stages { get; init; } = [];

        public int[] RphfLookups { get; init; } = [];
        public int[] PrefLookups { get; init; } = [];
        public int[] BlwfLookups { get; init; } = [];
        public int[] PstfLookups { get; init; } = [];
        public int[] VatuLookups { get; init; } = [];

        // The mask bits of the features the font has; 0 for the ones it lacks.
        public uint RphfMask { get; init; }
        public uint PrefMask { get; init; }
        public uint BlwfMask { get; init; }
        public uint AbvfMask { get; init; }
        public uint HalfMask { get; init; }
        public uint PstfMask { get; init; }
        public uint InitMask { get; init; }

        /// <summary>The position each consonant glyph takes from the font's below-base, post-base and pre-base forms.</summary>
        public ConcurrentDictionary<int, byte> ConsonantPositions { get; } = new();
    }

    // The first stage the basic features take, and the stage after them.
    private const int FirstBasicStage = 2, PresentationStage = FirstBasicStage + 11;

    /// <summary>
    /// The Indic plan of a font for a script, or null when the font was designed for the default script and should be
    /// shaped as any other script (HarfBuzz's choice too). The new script tag wins over the old one.
    /// </summary>
    private static IndicPlan? BuildIndicPlan(FontProgram font, OpenTypeLayout? layout, Script script, out uint gsubTag)
    {
        var config = IndicConfigOf(script)!;
        uint newTag = FontProgram.Tag(config.NewTag), oldTag = FontProgram.Tag(config.OldTag);
        gsubTag = newTag;
        bool oldSpec = false;
        if (layout is { HasGsub: true })
        {
            if (layout.HasScript(false, newTag)) gsubTag = newTag;
            else if (layout.HasScript(false, oldTag)) (gsubTag, oldSpec) = (oldTag, true);
            else return null;
        }
        else oldSpec = true; // no script found, which HarfBuzz shapes as the old spec

        var stages = new StageLookup[PresentationStage + 1][];
        // The Indic features handle the joiners themselves; the common ones let the lookups skip them.
        stages[0] = Stage(layout, gsubTag, [("rvrn", AllGlyphs, false, false)]);
        stages[1] = Stage(layout, gsubTag, [("locl", AllGlyphs, true, false), ("ccmp", AllGlyphs, true, false)]);
        for (int k = 0; k < IndicBasicFeatures.Length; k++)
            stages[FirstBasicStage + k] = Stage(layout, gsubTag, [(IndicBasicFeatures[k].Feature, IndicBasicFeatures[k].Mask, true, true)]);
        stages[PresentationStage] = Stage(layout, gsubTag,
            [.. IndicPresentationFeatures.Select(f => (f.Feature, f.Mask, true, true)), .. IndicCommonFeatures.Select(f => (f.Feature, f.Mask, false, false))]);

        uint tag = gsubTag;
        uint MaskIf(string feature, uint mask) => layout != null && layout.HasFeature(false, tag, FontProgram.Tag(feature)) ? mask : 0;
        int[] Lookups(int stage) => [.. stages[stage].Select(l => l.Lookup)];
        return new IndicPlan
        {
            Script = script,
            Config = config,
            OldSpec = oldSpec,
            // HarfBuzz's heuristic, from what Windows does: the new spec matches without context, except in Malayalam.
            ZeroContext = !oldSpec && script != Script.Malayalam,
            ViramaGlyph = font.GlyphId(config.Virama),
            Stages = stages,
            RphfLookups = Lookups(FirstBasicStage + 2),
            PrefLookups = Lookups(FirstBasicStage + 4),
            BlwfLookups = Lookups(FirstBasicStage + 5),
            PstfLookups = Lookups(FirstBasicStage + 8),
            VatuLookups = Lookups(FirstBasicStage + 9),
            RphfMask = MaskIf("rphf", Rphf),
            PrefMask = MaskIf("pref", Pref),
            BlwfMask = MaskIf("blwf", Blwf),
            AbvfMask = MaskIf("abvf", Abvf),
            HalfMask = MaskIf("half", Half),
            PstfMask = MaskIf("pstf", Pstf),
            InitMask = MaskIf("init", IndicInit),
        };
    }

    // The lookups of a stage, each once with the masks of all the features that use it, in the order of the lookup list.
    // A lookup stays inside a syllable, or leaves the joiners to its rules, when one of its features does.
    private static StageLookup[] Stage(OpenTypeLayout? layout, uint tag, (string Feature, uint Mask, bool PerSyllable, bool ManualJoiners)[] features)
    {
        if (layout == null) return [];
        var stage = new Dictionary<int, (uint Mask, bool PerSyllable, bool ManualJoiners)>();
        foreach (var (feature, mask, perSyllable, manualJoiners) in features)
            foreach (int lookup in layout.FeatureLookups(gpos: false, tag, FontProgram.Tag(feature)))
            {
                ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(stage, lookup, out _);
                entry = (entry.Mask | mask, entry.PerSyllable || perSyllable, entry.ManualJoiners || manualJoiners);
            }
        var lookups = stage.Select(e => new StageLookup(e.Key, e.Value.Mask, e.Value.PerSyllable, e.Value.ManualJoiners)).ToArray();
        Array.Sort(lookups, static (a, b) => a.Lookup.CompareTo(b.Lookup));
        return lookups;
    }

    /// <summary>Shapes a run of one Indic script, as HarfBuzz's Indic shaper does.</summary>
    private static void ShapeIndic(FontProgram font, OpenTypeLayout? layout, GlyphRun run, Plan plan, string s, int start, int length,
        bool rtl, List<ShapedGlyph> output)
    {
        var indic = plan.Indic!;
        AddText(font, layout, run, s, start, length, rtl, decomposeFirst: true);
        SeparateVowelLookalikes(font, layout, run, s, start, length);
        ReorderMarks(run, indic.Script);
        ComposeIndic(font, layout, run);
        SetIndicProperties(run);
        FindSyllables(run);

        var lookups = new LookupPass(layout, run, rtl);
        lookups.Apply(indic.Stages[0]);
        lookups.Apply(indic.Stages[1]);
        UpdateConsonantPositions(indic, layout, run);
        InsertDottedCircles(font, layout, run);
        ForEachSyllable(run, (glyphs, from, to) => InitialReordering(indic, layout, glyphs, from, to));
        for (int stage = FirstBasicStage; stage < PresentationStage; stage++) lookups.Apply(indic.Stages[stage]);
        ForEachSyllable(run, (glyphs, from, to) => FinalReordering(indic, glyphs, from, to));
        lookups.Apply(indic.Stages[PresentationStage]);

        SetAdvances(font, run);
        lookups.Apply(plan.Gpos, gpos: true, plan.MarkLookups);
        if (!plan.HasGposKern && font.Kerning is { } kerning) ApplyKerning(kerning, run);
        // Indic marks keep their advances (HarfBuzz zeroes none), and fonts give them none.
        OpenTypeLayout.PropagateAttachments(run, rtl);
        RemoveIgnorables(run);
        WriteOutput(font, run, output);
    }

    // An independent vowel and a vowel sign that would draw as another independent vowel (HarfBuzz's vowel constraints,
    // from the Universal Shaping Engine specification), and the vowel sign after which a dotted circle goes.
    private static bool LooksLikeAnotherVowel(int vowel, int sign) => (vowel, sign) switch
    {
        (0x0905, 0x093A or 0x093B or 0x093E or 0x0945 or 0x0946 or 0x0949 or 0x094A or 0x094B or 0x094C or 0x094F or 0x0956 or 0x0957) => true,
        (0x0906, 0x093A or 0x0945 or 0x0946 or 0x0947 or 0x0948) => true,
        (0x0909, 0x0941) or (0x090F, 0x0945 or 0x0946 or 0x0947) => true,
        (0x0985, 0x09BE) or (0x098B, 0x09C3) or (0x098C, 0x09E2) => true,
        (0x0A05, 0x0A3E or 0x0A48 or 0x0A4C) or (0x0A72, 0x0A3F or 0x0A40 or 0x0A47) or (0x0A73, 0x0A41 or 0x0A42 or 0x0A4B) => true,
        (0x0A85, 0x0ABE or 0x0AC5 or 0x0AC7 or 0x0AC8 or 0x0AC9 or 0x0ACB or 0x0ACC) or (0x0AC5, 0x0ABE) => true,
        (0x0B05, 0x0B3E) or (0x0B0F or 0x0B13, 0x0B57) => true,
        (0x0B85, 0x0BC2) => true,
        (0x0C12, 0x0C4C or 0x0C55) or (0x0C3F or 0x0C46 or 0x0C4A, 0x0C55) => true,
        (0x0C89 or 0x0C8B, 0x0CBE) or (0x0C92, 0x0CCC) => true,
        (0x0D07 or 0x0D09, 0x0D57) or (0x0D0E, 0x0D46) or (0x0D12, 0x0D3E or 0x0D57) => true,
        _ => false,
    };

    /// <summary>
    /// Puts a dotted circle between an independent vowel and a vowel sign that together would look like another vowel
    /// (and in Devanagari between Ra, Virama and the vowel I), so that the spoof shows, as HarfBuzz does when the font
    /// has the dotted circle. The characters are compared as the text has them, before normalization.
    /// </summary>
    private static void SeparateVowelLookalikes(FontProgram font, OpenTypeLayout? layout, GlyphRun run, string s, int start, int length)
    {
        int end = start + length, gid = 0, glyph = 0;
        for (int i = start; i < end;)
        {
            var (cp, units) = CodePointAt(s, i, end);
            int signAt = i + units;
            if (signAt >= end) break;
            var (next, nextUnits) = CodePointAt(s, signAt, end);
            if (cp == 0x0930 && next == 0x094D && signAt + nextUnits < end && s[signAt + nextUnits] == 0x0907) signAt += nextUnits;
            else if (!LooksLikeAnotherVowel(cp, next))
            {
                i = signAt;
                continue;
            }
            if (gid == 0 && (gid = font.GlyphId(0x25CC)) == 0) return;
            // The dotted circle goes before the glyph that starts with the sign.
            while (glyph < run.Count && !(run[glyph].Cluster == signAt && run[glyph].Length > 0)) glyph++;
            if (glyph == run.Count) return;
            run.Insert(glyph, new GlyphInfo
            {
                Gid = gid, CodePoint = 0x25CC, Cluster = signAt, Length = 0, Mask = Global,
                Props = layout is { HasGlyphClasses: true } ? layout.GlyphProps(gid) : OpenTypeLayout.Base,
            });
            // The sign is not the first of another pair.
            i = signAt + CodePointAt(s, signAt, end).Length;
        }
    }

    private delegate void SyllableAction(Span<GlyphInfo> glyphs, int start, int end);

    private static void ForEachSyllable(GlyphRun run, SyllableAction action)
    {
        var glyphs = run.AsSpan();
        for (int start = 0; start < glyphs.Length;)
        {
            int end = start + 1;
            while (end < glyphs.Length && glyphs[end].Syllable == glyphs[start].Syllable) end++;
            action(glyphs, start, end);
            start = end;
        }
    }

    // The characters an Indic letter and a nukta (or a length mark) compose to. Unicode excludes the other nukta letters
    // from composition; HarfBuzz composes Bengali Yya all the same, and composes nothing whose first part is a mark, so
    // that split vowel signs stay split.
    private static int ComposeIndicPair(int first, int second) => (first, second) switch
    {
        (0x0928, 0x093C) => 0x0929,
        (0x0930, 0x093C) => 0x0931,
        (0x0933, 0x093C) => 0x0934,
        (0x09AF, 0x09BC) => 0x09DF,
        (0x0B92, 0x0BD7) => 0x0B94,
        _ => 0,
    };

    /// <summary>
    /// The canonical decomposition that Indic normalization uses, which leaves Devanagari Rra, Bengali Rra and Rha and
    /// Tamil Au whole, as HarfBuzz does.
    /// </summary>
    private static bool DecomposeIndic(int cp, out int first, out int second)
    {
        first = second = 0;
        if (cp is 0x0931 or 0x09DC or 0x09DD or 0x0B94 || cp is < 0x0900 or > 0x0D7F) return false;
        var table = IndicTables.Decompositions;
        for (int k = 0; k < table.Length; k += 3)
            if (table[k] == cp)
            {
                first = table[k + 1];
                second = table[k + 2];
                return true;
            }
        return false;
    }

    // A letter and the mark right after it (or after marks of a lower combining class) become one character when the
    // font has it, as HarfBuzz recomposes Indic text.
    private static void ComposeIndic(FontProgram font, OpenTypeLayout? layout, GlyphRun run)
    {
        for (int i = 0; i + 1 < run.Count; i++)
        {
            var letter = run[i];
            if (UnicodeTables.CombiningClass(letter.CodePoint) != 0 || IsUnicodeMark(letter.CodePoint)) continue;
            var mark = run[i + 1];
            if (!IsUnicodeMark(mark.CodePoint) || ComposeIndicPair(letter.CodePoint, mark.CodePoint) is not (var composed and not 0)
                || font.GlyphId(composed) is not (var gid and not 0))
                continue;
            letter.CodePoint = composed;
            letter.Gid = gid;
            letter.Props = layout is { HasGlyphClasses: true } ? layout.GlyphProps(gid) : OpenTypeLayout.Base;
            letter.Length = Math.Max(letter.Cluster + letter.Length, mark.Cluster + mark.Length) - letter.Cluster;
            run[i] = letter;
            run.RemoveAt(i + 1);
        }
    }

    private static bool IsUnicodeMark(int cp)
        => CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    // A glyph whose category matches. A ligature matches nothing: once glyphs ligate, all bets are off.
    private static bool IsOneOf(in GlyphInfo g, ulong categories)
        => (g.Substitutions & OpenTypeLayout.Ligated) == 0 && (Flag(g.IndicCategory) & categories) != 0;

    // Vowels and placeholders count as consonants: they cannot be in a consonant syllable, and so the consonant
    // syllable logic serves the vowel syllables too.
    private static readonly ulong ConsonantCategories = Flag(IndicCat.C) | Flag(IndicCat.CS) | Flag(IndicCat.Ra) | Flag(IndicCat.CM)
        | Flag(IndicCat.V) | Flag(IndicCat.Placeholder) | Flag(IndicCat.DottedCircle);
    private static readonly ulong JoinerCategories = Flag(IndicCat.ZWJ) | Flag(IndicCat.ZWNJ);
    private static readonly ulong MatraCategories = Flag(IndicCat.M) | Flag(IndicCat.MPst);

    private static bool IsConsonant(in GlyphInfo g) => IsOneOf(g, ConsonantCategories);
    private static bool IsJoiner(in GlyphInfo g) => IsOneOf(g, JoinerCategories);
    private static bool IsHalant(in GlyphInfo g) => IsOneOf(g, Flag(IndicCat.H));

    private static bool LigatedAndDidntMultiply(in GlyphInfo g)
        => (g.Substitutions & (OpenTypeLayout.Ligated | OpenTypeLayout.Multiplied)) == OpenTypeLayout.Ligated;

    /// <summary>
    /// Gives each consonant the position its below-base, post-base or pre-base form puts it in, as the font forms them
    /// with the virama on either side (some fonts copied old-spec lookups into new-spec tables).
    /// </summary>
    private static void UpdateConsonantPositions(IndicPlan plan, OpenTypeLayout? layout, GlyphRun run)
    {
        if (plan.ViramaGlyph == 0 || layout == null) return;
        var glyphs = run.AsSpan();
        for (int i = 0; i < glyphs.Length; i++)
            if (glyphs[i].IndicPosition == IndicPos.BaseC)
                glyphs[i].IndicPosition = plan.ConsonantPositions.GetOrAdd(glyphs[i].Gid,
                    static (consonant, state) => ConsonantPosition(state.plan, state.layout, consonant), (plan, layout));
    }

    private static byte ConsonantPosition(IndicPlan plan, OpenTypeLayout layout, int consonant)
    {
        int[] glyphs = [plan.ViramaGlyph, consonant, plan.ViramaGlyph];
        bool Either(int[] lookups)
            => layout.WouldSubstitute(lookups, glyphs.AsSpan(0, 2), plan.ZeroContext) || layout.WouldSubstitute(lookups, glyphs.AsSpan(1), plan.ZeroContext);
        if (Either(plan.BlwfLookups) || Either(plan.VatuLookups)) return IndicPos.BelowC;
        if (Either(plan.PstfLookups) || Either(plan.PrefLookups)) return IndicPos.PostC;
        return IndicPos.BaseC;
    }

    // HarfBuzz's initial_reordering_consonant_syllable, which also serves the vowel syllables, standalone clusters and
    // broken clusters (with their dotted circle).
    private static void InitialReordering(IndicPlan plan, OpenTypeLayout? layout, Span<GlyphInfo> info, int start, int end)
    {
        if ((info[start].Syllable & 0x0F) is SyllableType.Symbol or SyllableType.NonIndic) return;
        var script = plan.Script;

        // For compatibility with legacy usage in Kannada, Ra, H, ZWJ behaves as Ra, ZWJ, H.
        if (script == Script.Kannada && start + 3 <= end && IsOneOf(info[start], Flag(IndicCat.Ra))
            && IsOneOf(info[start + 1], Flag(IndicCat.H)) && IsOneOf(info[start + 2], Flag(IndicCat.ZWJ)))
            (info[start + 1], info[start + 2]) = (info[start + 2], info[start + 1]);

        // 1. Find the base consonant: from the end of the syllable, the first consonant without a below-base or
        // post-base form (post-base forms come after below-base forms), or the first consonant. A syllable that
        // starts with Ra, Halant and has more consonants keeps the Ra out of the candidates, for the reph.
        int baseIndex = end;
        bool hasReph = false;
        int limit = start;
        if (plan.RphfMask != 0 && start + 3 <= end
            && ((plan.Config.RephMode == RephMode.Implicit && !IsJoiner(info[start + 2]))
                || (plan.Config.RephMode == RephMode.Explicit && info[start + 2].IndicCategory == IndicCat.ZWJ)))
        {
            ReadOnlySpan<int> glyphs = [info[start].Gid, info[start + 1].Gid, info[start + 2].Gid];
            if (layout != null && (layout.WouldSubstitute(plan.RphfLookups, glyphs[..2], plan.ZeroContext)
                || (plan.Config.RephMode == RephMode.Explicit && layout.WouldSubstitute(plan.RphfLookups, glyphs, plan.ZeroContext))))
            {
                limit += 2;
                while (limit < end && IsJoiner(info[limit])) limit++;
                baseIndex = start;
                hasReph = true;
            }
        }
        else if (plan.Config.RephMode == RephMode.LogRepha && info[start].IndicCategory == IndicCat.Repha)
        {
            limit += 1;
            while (limit < end && IsJoiner(info[limit])) limit++;
            baseIndex = start;
            hasReph = true;
        }

        int i = end;
        bool seenBelow = false;
        do
        {
            i--;
            if (IsConsonant(info[i]))
            {
                if (info[i].IndicPosition != IndicPos.BelowC && (info[i].IndicPosition != IndicPos.PostC || seenBelow))
                {
                    baseIndex = i;
                    break;
                }
                if (info[i].IndicPosition == IndicPos.BelowC) seenBelow = true;
                // Pre-base reordering Ra are post-base here, so the test above has skipped them already.
                baseIndex = i;
            }
            // A ZWJ after a Halant stops the search and asks for a half form; a ZWJ before a Halant asks for a
            // subjoined form, and the search goes on (Bengali Ra, H, Ya forms Ya-phalaa so).
            else if (start < i && info[i].IndicCategory == IndicCat.ZWJ && info[i - 1].IndicCategory == IndicCat.H) break;
        }
        while (i > limit);

        // With no other consonant, the reph is not formed and Ra is the base.
        if (hasReph && baseIndex == start && limit - baseIndex <= 2) hasReph = false;

        // 2 and 3. Matras were decomposed and marks put into canonical order by the normalization.
        for (i = start; i < baseIndex; i++) info[i].IndicPosition = Math.Min(IndicPos.PreC, info[i].IndicPosition);
        if (baseIndex < end) info[baseIndex].IndicPosition = IndicPos.BaseC;
        if (hasReph) info[start].IndicPosition = IndicPos.RaToBecomeReph;

        // Old-spec fonts expect the first post-base Halant after the last consonant (Kannada only when there is no
        // Halant after the last consonant already).
        if (plan.OldSpec)
        {
            bool disallowDoubleHalants = script == Script.Kannada;
            for (i = baseIndex + 1; i < end; i++)
                if (info[i].IndicCategory == IndicCat.H)
                {
                    int j;
                    for (j = end - 1; j > i; j--)
                        if (IsConsonant(info[j]) || (disallowDoubleHalants && info[j].IndicCategory == IndicCat.H)) break;
                    if (info[j].IndicCategory != IndicCat.H && j > i)
                    {
                        var halant = info[i];
                        info.Slice(i + 1, j - i).CopyTo(info[i..]);
                        info[j] = halant;
                    }
                    break;
                }
        }

        // Marks move with the character before them.
        byte lastPosition = IndicPos.Start;
        ulong attached = JoinerCategories | Flag(IndicCat.N) | Flag(IndicCat.RS) | Flag(IndicCat.CM) | Flag(IndicCat.H);
        for (i = start; i < end; i++)
        {
            if ((Flag(info[i].IndicCategory) & attached) != 0)
            {
                info[i].IndicPosition = lastPosition;
                // Uniscribe does not move a Halant with a left matra, and neither does HarfBuzz.
                if (info[i].IndicCategory == IndicCat.H && info[i].IndicPosition == IndicPos.PreM)
                    for (int j = i; j > start; j--)
                        if (info[j - 1].IndicPosition != IndicPos.PreM)
                        {
                            info[i].IndicPosition = info[j - 1].IndicPosition;
                            break;
                        }
            }
            else if (info[i].IndicPosition != IndicPos.Smvd)
            {
                if (info[i].IndicCategory == IndicCat.MPst && i > start && info[i - 1].IndicCategory == IndicCat.SM)
                    info[i - 1].IndicPosition = info[i].IndicPosition;
                lastPosition = info[i].IndicPosition;
            }
        }

        // A post-base consonant owns what comes before it since the last consonant or matra.
        int last = baseIndex;
        for (i = baseIndex + 1; i < end; i++)
            if (IsConsonant(info[i]))
            {
                for (int j = last + 1; j < i; j++)
                    if (info[j].IndicPosition < IndicPos.Smvd) info[j].IndicPosition = info[i].IndicPosition;
                last = i;
            }
            else if ((Flag(info[i].IndicCategory) & MatraCategories) != 0) last = i;

        SortByPosition(info, start, end);

        // Find the base again, and put a sequence of left matras the other way round.
        int firstLeftMatra = end, lastLeftMatra = end;
        baseIndex = end;
        for (i = start; i < end; i++)
        {
            if (info[i].IndicPosition == IndicPos.BaseC)
            {
                baseIndex = i;
                break;
            }
            if (info[i].IndicPosition == IndicPos.PreM)
            {
                if (firstLeftMatra == end) firstLeftMatra = i;
                lastLeftMatra = i;
            }
        }
        if (firstLeftMatra < lastLeftMatra)
        {
            info[firstLeftMatra..(lastLeftMatra + 1)].Reverse();
            // Nuktas and the like go back after their matra.
            int k = firstLeftMatra;
            for (int j = k; j <= lastLeftMatra; j++)
                if ((Flag(info[j].IndicCategory) & MatraCategories) != 0)
                {
                    info[k..(j + 1)].Reverse();
                    k = j + 1;
                }
        }

        // The features each glyph takes: the reph, the forms before the base, and the forms after it.
        for (i = start; i < end && info[i].IndicPosition == IndicPos.RaToBecomeReph; i++) info[i].Mask |= plan.RphfMask;
        uint preBase = plan.HalfMask;
        if (!plan.OldSpec && plan.Config.BelowForms == BelowFormsMode.PreAndPost) preBase |= plan.BlwfMask;
        for (i = start; i < baseIndex; i++) info[i].Mask |= preBase;
        uint postBase = plan.BlwfMask | plan.AbvfMask | plan.PstfMask;
        for (i = baseIndex + 1; i < end; i++) info[i].Mask |= postBase;

        // The old-spec eyelash Ra: below-base forms apply to Ra, H before the base, unless a ZWJ asks for the half form.
        if (plan.OldSpec && script == Script.Devanagari)
            for (i = start; i + 1 < baseIndex; i++)
                if (info[i].IndicCategory == IndicCat.Ra && info[i + 1].IndicCategory == IndicCat.H
                    && (i + 2 == baseIndex || info[i + 2].IndicCategory != IndicCat.ZWJ))
                {
                    info[i].Mask |= plan.BlwfMask;
                    info[i + 1].Mask |= plan.BlwfMask;
                }

        // The first Halant, Ra after the base that the font forms a pre-base form of.
        if (plan.PrefMask != 0 && layout != null && baseIndex + 2 < end)
            for (i = baseIndex + 1; i + 1 < end; i++)
                if (layout.WouldSubstitute(plan.PrefLookups, [info[i].Gid, info[i + 1].Gid], plan.ZeroContext))
                {
                    info[i].Mask |= plan.PrefMask;
                    info[i + 1].Mask |= plan.PrefMask;
                    break;
                }

        // A ZWNJ keeps the consonant before it from its half form; joiners block the conjunct forms by being there.
        for (i = start + 1; i < end; i++)
            if (IsJoiner(info[i]))
            {
                bool nonJoiner = info[i].IndicCategory == IndicCat.ZWNJ;
                int j = i;
                do
                {
                    j--;
                    if (nonJoiner) info[j].Mask &= ~plan.HalfMask;
                }
                while (j > start && !IsConsonant(info[j]));
            }
    }

    // A stable insertion sort by visual position: syllables are short.
    private static void SortByPosition(Span<GlyphInfo> info, int start, int end)
    {
        for (int k = start + 1; k < end; k++)
        {
            var g = info[k];
            int j = k - 1;
            for (; j >= start && info[j].IndicPosition > g.IndicPosition; j--) info[j + 1] = info[j];
            info[j + 1] = g;
        }
    }

    // HarfBuzz's final_reordering_syllable_indic: once the basic forms are formed, pre-base matras move closer to the
    // base, the reph moves to its place after it, and pre-base reordering consonants move before it.
    private static void FinalReordering(IndicPlan plan, Span<GlyphInfo> info, int start, int end)
    {
        var script = plan.Script;
        // The reorderings rely on halants; a virama that a ligature took apart again is a halant once more.
        if (plan.ViramaGlyph != 0)
            for (int i = start; i < end; i++)
                if (info[i].Gid == plan.ViramaGlyph
                    && (info[i].Substitutions & (OpenTypeLayout.Ligated | OpenTypeLayout.Multiplied)) == (OpenTypeLayout.Ligated | OpenTypeLayout.Multiplied))
                {
                    info[i].IndicCategory = IndicCat.H;
                    info[i].Substitutions &= unchecked((byte)~(OpenTypeLayout.Ligated | OpenTypeLayout.Multiplied));
                }

        bool tryPref = plan.PrefMask != 0;
        int baseIndex = FinalBase(plan, info, start, end, ref tryPref);

        // Pre-base matras move to after the last standalone halant before the base (not one a ZWJ follows), or stay.
        if (start + 1 < end && start < baseIndex)
        {
            int newPos = baseIndex == end ? baseIndex - 2 : baseIndex - 1;
            // Malayalam and Tamil have no half forms: what 'half' forms there are chillus or ligated explicit viramas,
            // and the matra goes after them.
            if (script != Script.Malayalam && script != Script.Tamil)
            {
                while (true)
                {
                    while (newPos > start && !IsOneOf(info[newPos], MatraCategories | Flag(IndicCat.H))) newPos--;
                    if (IsHalant(info[newPos]) && info[newPos].IndicPosition != IndicPos.PreM)
                    {
                        if (newPos + 1 < end && info[newPos + 1].IndicCategory == IndicCat.ZWJ && newPos > start)
                        {
                            newPos--;
                            continue;
                        }
                    }
                    else newPos = start;
                    break;
                }
            }
            if (start < newPos && info[newPos].IndicPosition != IndicPos.PreM)
                for (int i = newPos; i > start; i--)
                    if (info[i - 1].IndicPosition == IndicPos.PreM)
                    {
                        int oldPos = i - 1;
                        if (oldPos < baseIndex && baseIndex <= newPos) baseIndex--;
                        var matra = info[oldPos];
                        info.Slice(oldPos + 1, newPos - oldPos).CopyTo(info[oldPos..]);
                        info[newPos] = matra;
                        newPos--;
                    }
        }

        // The reph moves when it was formed from Ra, H (and so ligated), or when it is a character of its own that did
        // not ligate (a font that ligates it means to place it without reordering).
        if (start + 1 < end && info[start].IndicPosition == IndicPos.RaToBecomeReph
            && (info[start].IndicCategory == IndicCat.Repha) ^ LigatedAndDidntMultiply(info[start]))
        {
            int newRephPos = RephTarget(plan, info, start, end, baseIndex);
            var reph = info[start];
            info.Slice(start + 1, newRephPos - start).CopyTo(info[start..]);
            info[newRephPos] = reph;
            if (start < baseIndex && baseIndex <= newRephPos) baseIndex--;
        }

        // A pre-base reordering consonant that the font formed moves to where a pre-base matra would, or before the base.
        if (tryPref && baseIndex + 1 < end)
            for (int i = baseIndex + 1; i < end; i++)
                if ((info[i].Mask & plan.PrefMask) != 0)
                {
                    if (LigatedAndDidntMultiply(info[i]))
                    {
                        int newPos = baseIndex;
                        if (script != Script.Malayalam && script != Script.Tamil)
                            while (newPos > start && !IsOneOf(info[newPos - 1], MatraCategories | Flag(IndicCat.H))) newPos--;
                        if (newPos > start && IsHalant(info[newPos - 1]) && newPos < end && IsJoiner(info[newPos])) newPos++;
                        int oldPos = i;
                        var consonant = info[oldPos];
                        info.Slice(newPos, oldPos - newPos).CopyTo(info[(newPos + 1)..]);
                        info[newPos] = consonant;
                        if (newPos <= baseIndex && baseIndex < oldPos) baseIndex++;
                    }
                    break;
                }

        // A left matra at the start of a word takes its initial form.
        if (info[start].IndicPosition == IndicPos.PreM && (start == 0 || !ContinuesWord(info[start - 1].CodePoint)))
            info[start].Mask |= plan.InitMask;
    }

    // Whether a character before a left matra means it is not at the start of a word: HarfBuzz's general categories from
    // Format to Non_Spacing_Mark, that is letters, marks, format characters and unassigned ones.
    private static bool ContinuesWord(int cp) => CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.Format
        or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate
        or UnicodeCategory.LowercaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
        or UnicodeCategory.TitlecaseLetter or UnicodeCategory.UppercaseLetter or UnicodeCategory.SpacingCombiningMark
        or UnicodeCategory.EnclosingMark or UnicodeCategory.NonSpacingMark;

    // The base after the basic forms: the first glyph at or after the base position, adjusted for a pre-base form the
    // font did not form, for Malayalam's unformed below-base forms, and for joiners, nuktas and halants.
    private static int FinalBase(IndicPlan plan, Span<GlyphInfo> info, int start, int end, ref bool tryPref)
    {
        int baseIndex;
        for (baseIndex = start; baseIndex < end; baseIndex++)
            if (info[baseIndex].IndicPosition >= IndicPos.BaseC)
            {
                if (tryPref && baseIndex + 1 < end)
                {
                    for (int i = baseIndex + 1; i < end; i++)
                        if ((info[i].Mask & plan.PrefMask) != 0)
                        {
                            if (!((info[i].Substitutions & OpenTypeLayout.Substituted) != 0 && LigatedAndDidntMultiply(info[i])))
                            {
                                // A pre-base form candidate that formed nothing: the base is around here.
                                baseIndex = i;
                                while (baseIndex < end && IsHalant(info[baseIndex])) baseIndex++;
                                if (baseIndex < end) info[baseIndex].IndicPosition = IndicPos.BaseC;
                                tryPref = false;
                            }
                            break;
                        }
                    if (baseIndex == end) break;
                }
                // Malayalam skips over unformed below-base (but not post-base) forms.
                if (plan.Script == Script.Malayalam)
                    for (int i = baseIndex + 1; i < end; i++)
                    {
                        while (i < end && IsJoiner(info[i])) i++;
                        if (i == end || !IsHalant(info[i])) break;
                        i++;
                        while (i < end && IsJoiner(info[i])) i++;
                        if (i < end && IsConsonant(info[i]) && info[i].IndicPosition == IndicPos.BelowC)
                        {
                            baseIndex = i;
                            info[baseIndex].IndicPosition = IndicPos.BaseC;
                        }
                    }
                if (start < baseIndex && info[baseIndex].IndicPosition > IndicPos.BaseC) baseIndex--;
                break;
            }
        if (baseIndex == end && start < baseIndex && IsOneOf(info[baseIndex - 1], Flag(IndicCat.ZWJ))) baseIndex--;
        if (baseIndex < end)
            while (start < baseIndex && IsOneOf(info[baseIndex], Flag(IndicCat.N) | Flag(IndicCat.H))) baseIndex--;
        return baseIndex;
    }

    // Where the reph goes, by the steps of the Microsoft script development specifications as HarfBuzz reads them.
    private static int RephTarget(IndicPlan plan, Span<GlyphInfo> info, int start, int end, int baseIndex)
    {
        var rephPosition = plan.Config.RephPosition;
        int target;
        if (rephPosition != RephPosition.AfterPost)
        {
            // 2. After the first explicit halant between the first post-reph consonant and the last main consonant,
            // and after a joiner that follows it.
            if (AfterExplicitHalant(info, start, baseIndex) is var afterHalant and >= 0) return afterHalant;

            // 3. After the main consonant, past what is placed with it.
            if (rephPosition == RephPosition.AfterMain)
            {
                target = baseIndex;
                while (target + 1 < end && info[target + 1].IndicPosition <= IndicPos.AfterMain) target++;
                if (target < end) return target;
            }

            // 4. Before the first post-base consonant, or before the first matra, syllable modifier or vedic sign.
            if (rephPosition == RephPosition.AfterSub)
            {
                target = baseIndex;
                ulong stop = Flag(IndicPos.PostC) | Flag(IndicPos.AfterPost) | Flag(IndicPos.Smvd);
                while (target + 1 < end && (Flag(info[target + 1].IndicPosition) & stop) == 0) target++;
                if (target < end) return target;
            }
        }

        // 5. As step 2.
        if (AfterExplicitHalant(info, start, baseIndex) is var afterExplicitHalant and >= 0) return afterExplicitHalant;

        // 6. The end of the syllable, before its syllable modifiers; before a final Halant that follows a matra, so the
        // reph can interact with the matra.
        target = end - 1;
        while (target > start && info[target].IndicPosition == IndicPos.Smvd) target--;
        if (IsHalant(info[target]))
            for (int i = baseIndex + 1; i < target; i++)
                if ((Flag(info[i].IndicCategory) & MatraCategories) != 0) target--;
        return target;
    }

    private static int AfterExplicitHalant(Span<GlyphInfo> info, int start, int baseIndex)
    {
        int target = start + 1;
        while (target < baseIndex && !IsHalant(info[target])) target++;
        if (target >= baseIndex || !IsHalant(info[target])) return -1;
        if (target + 1 < baseIndex && IsJoiner(info[target + 1])) target++;
        return target;
    }
}
