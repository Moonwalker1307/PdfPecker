using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PdfPecker.Fonts;

// Feature selection: which GSUB and GPOS lookups of a font shape a script, in which order, and for which glyphs.
internal static partial class ComplexShaper
{
    // The bits of a glyph's feature mask. Global features apply to every glyph; the joining forms and rtlm only to the
    // glyphs given their bit.
    private const uint Global = 1, Isol = 1 << 1, Fina = 1 << 2, Fin2 = 1 << 3, Fin3 = 1 << 4, Medi = 1 << 5, Med2 = 1 << 6,
        Init = 1 << 7, Rtlm = 1 << 8;
    private const uint AllGlyphs = 0xFFFFFFFF;

    // The GSUB stages of the joining scripts after rvrn, as HarfBuzz's Arabic shaper orders them. Each joining form is a
    // stage of its own, so that one form's lookups apply to the whole run before the next form's.
    private static readonly (string Feature, uint Mask)[] JoiningPreForms = [("ccmp", AllGlyphs), ("locl", AllGlyphs), ("rtlm", Rtlm)];
    private static readonly (string Feature, uint Mask)[] JoiningForms =
        [("isol", Isol), ("fina", Fina), ("fin2", Fin2), ("fin3", Fin3), ("medi", Medi), ("med2", Med2), ("init", Init)];
    private static readonly (string Feature, uint Mask)[] JoiningLigatures =
        [("calt", AllGlyphs), ("rclt", AllGlyphs), ("liga", AllGlyphs), ("clig", AllGlyphs), ("mset", AllGlyphs)];

    // The one GSUB stage after rvrn of the other scripts.
    private static readonly (string Feature, uint Mask)[] OtherSubstitutions =
    [
        ("ccmp", AllGlyphs), ("locl", AllGlyphs), ("rtlm", Rtlm), ("rlig", AllGlyphs), ("calt", AllGlyphs), ("rclt", AllGlyphs),
        ("liga", AllGlyphs), ("clig", AllGlyphs),
    ];

    // The GPOS features of every script, applied as one stage.
    private static readonly (string Feature, uint Mask)[] PositioningFeatures =
    [
        ("abvm", AllGlyphs), ("blwm", AllGlyphs), ("curs", AllGlyphs), ("dist", AllGlyphs), ("kern", AllGlyphs), ("mark", AllGlyphs),
        ("mkmk", AllGlyphs),
    ];

    /// <summary>The lookups a font applies to a script, with the mask of the glyphs each one applies to.</summary>
    private sealed class Plan
    {
        /// <summary>The plan of a font without layout tables, or whose tables could not be read: no lookups.</summary>
        public static readonly Plan Empty = new();

        /// <summary>The GSUB lookups, stage after stage, each stage's in the order of the lookup list.</summary>
        public (int Lookup, uint Mask)[] Gsub { get; init; } = [];

        /// <summary>The GPOS lookups, in the order of the lookup list.</summary>
        public (int Lookup, uint Mask)[] Gpos { get; init; } = [];

        /// <summary>Whether the font has any of the joining forms, without which the presentation forms stand in.</summary>
        public bool HasForms { get; init; }

        /// <summary>Whether the font makes the required ligatures, without which lam and alef are ligated here.</summary>
        public bool HasRlig { get; init; }

        /// <summary>Whether GPOS places marks, without which marks are put on their base here.</summary>
        public bool HasMarkPositioning { get; init; }

        /// <summary>Whether GPOS kerns, without which the kern table applies.</summary>
        public bool HasGposKern { get; init; }
    }

    // Keyed weakly by the font, so that a font registered for one conversion is not kept alive by its plans for the
    // life of the process.
    private static readonly ConditionalWeakTable<FontProgram, ConcurrentDictionary<Script, Plan>> s_plans = new();

    private static Plan GetPlan(FontProgram font, OpenTypeLayout? layout, Script script)
        => s_plans.GetValue(font, static _ => new ConcurrentDictionary<Script, Plan>()).GetOrAdd(script, static (script, layout) =>
        {
            // OpenTypeLayout checks the lists a plan reads when it loads the font; should a damaged font still get
            // past that, its text is shaped without the layout tables rather than failing the document. The plan is
            // cached, so this runs once per font and script.
            try { return layout == null ? Plan.Empty : BuildPlan(layout, script); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException) { return Plan.Empty; }
        }, layout);

    private static Plan BuildPlan(OpenTypeLayout layout, Script script)
    {
        uint tag = ScriptTag(script);
        var gsub = new List<(int Lookup, uint Mask)>();
        AddStage(gsub, layout, gpos: false, tag, [("rvrn", AllGlyphs)]);
        bool hasForms = false, hasRlig;
        if (Joins(script))
        {
            AddStage(gsub, layout, gpos: false, tag, JoiningPreForms);
            foreach (var form in JoiningForms) hasForms |= AddStage(gsub, layout, gpos: false, tag, [form]);
            hasRlig = AddStage(gsub, layout, gpos: false, tag, [("rlig", AllGlyphs)]);
            AddStage(gsub, layout, gpos: false, tag, JoiningLigatures);
        }
        else
        {
            hasRlig = layout.HasFeature(false, tag, FontProgram.Tag("rlig"));
            AddStage(gsub, layout, gpos: false, tag, OtherSubstitutions);
        }

        var gpos = new List<(int Lookup, uint Mask)>();
        AddStage(gpos, layout, gpos: true, tag, PositioningFeatures);
        return new Plan
        {
            Gsub = [.. gsub],
            Gpos = [.. gpos],
            HasForms = hasForms,
            HasRlig = hasRlig,
            HasMarkPositioning = layout.HasFeature(true, tag, FontProgram.Tag("mark")),
            HasGposKern = layout.HasFeature(true, tag, FontProgram.Tag("kern")),
        };
    }

    /// <summary>
    /// Adds a stage: the lookups of its features in the order of the lookup list, each once, with the masks of all the
    /// features that use it. Returns whether any of the features has lookups.
    /// </summary>
    private static bool AddStage(List<(int Lookup, uint Mask)> lookups, OpenTypeLayout layout, bool gpos, uint script,
        (string Feature, uint Mask)[] features)
    {
        // A damaged font may list the same lookups many times over, so each is merged in one dictionary lookup and the
        // stage is sorted once at the end.
        var stage = new Dictionary<int, uint>();
        foreach (var (feature, mask) in features)
            foreach (int lookup in layout.FeatureLookups(gpos, script, FontProgram.Tag(feature)))
                CollectionsMarshal.GetValueRefOrAddDefault(stage, lookup, out _) |= mask;
        int first = lookups.Count;
        foreach (var (lookup, mask) in stage) lookups.Add((lookup, mask));
        CollectionsMarshal.AsSpan(lookups)[first..].Sort(static (a, b) => a.Lookup.CompareTo(b.Lookup));
        return stage.Count > 0;
    }
}
