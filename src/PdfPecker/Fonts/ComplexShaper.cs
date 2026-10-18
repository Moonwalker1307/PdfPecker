using PdfPecker.Text;

namespace PdfPecker.Fonts;

/// <summary>
/// OpenType shaping for scripts whose glyphs depend on their neighbours, along the lines of HarfBuzz: Arabic, Syriac,
/// N'Ko and other joining scripts get their initial, medial, final and isolated forms and required ligatures;
/// Hebrew and the joining scripts get mark positioning (GPOS mark, mkmk), cursive attachment and kerning. Fonts
/// without OpenType forms fall back to the Unicode Arabic presentation forms. Glyphs come out in logical order, with
/// positions for the direction the run is drawn in.
/// </summary>
/// <remarks>
/// The class is split by concern: this file has the scripts and the order of the shaping steps, Plan picks the GSUB and
/// GPOS lookups of a font and script, Characters turns the text into glyphs and orders and composes the marks, Joining
/// gives the joining scripts their forms, and Positioning places the glyphs once the lookups have run.
/// </remarks>
internal static partial class ComplexShaper
{
    // The blocks of the scripts this shaper handles: Hebrew to Arabic Extended-A, the alphabetic presentation forms from
    // Hebrew on with Arabic Presentation Forms-A, and Arabic Presentation Forms-B.
    private const char ComplexBlocksFirst = '֐', ComplexBlocksLast = 'ࣿ';
    private const char PresentationFormsAFirst = 'יִ', PresentationFormsALast = '﷿';
    private const char PresentationFormsBFirst = 'ﹰ', PresentationFormsBLast = '﻿';

    /// <summary>True when the text has characters of a script this shaper handles.</summary>
    public static bool Handles(string s, int start, int length)
    {
        for (int i = start, end = start + length; i < end; i++)
            if (IsComplex(s[i])) return true;
        return false;
    }

    private static bool IsComplex(char c)
        => c is (>= ComplexBlocksFirst and <= ComplexBlocksLast)
            or (>= PresentationFormsAFirst and <= PresentationFormsALast)
            or (>= PresentationFormsBFirst and <= PresentationFormsBLast);

    /// <summary>The direction complex text is shaped for when the caller does not know it: its first strong character's.</summary>
    public static bool GuessRtl(string s, int start, int length)
    {
        for (int i = start, end = start + length; i < end; i++)
        {
            switch (UnicodeTables.Bidi(s[i]))
            {
                case BidiClass.L: return false;
                case BidiClass.R or BidiClass.AL: return true;
            }
        }
        return false;
    }

    private enum Script : byte { Hebrew, Arabic, Syriac, Nko, Mandaic, Other }

    private static Script ScriptOf(int cp) => cp switch
    {
        >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F => Script.Hebrew,
        >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x0870 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF
            or >= 0x10EC0 and <= 0x10EFF => Script.Arabic,
        >= 0x0700 and <= 0x074F or >= 0x0860 and <= 0x086F => Script.Syriac,
        >= 0x07C0 and <= 0x07FF => Script.Nko,
        >= 0x0840 and <= 0x085F => Script.Mandaic,
        _ => Script.Other,
    };

    /// <summary>The script of the text: that of its first character that belongs to one.</summary>
    private static Script ScriptOfText(string s, int start, int length)
    {
        for (int i = start, end = start + length; i < end; i++)
            if (ScriptOf(s[i]) is var script and not Script.Other) return script;
        return Script.Other;
    }

    private static uint ScriptTag(Script script) => FontProgram.Tag(script switch
    {
        Script.Hebrew => "hebr",
        Script.Arabic => "arab",
        Script.Syriac => "syrc",
        Script.Nko => "nko ",
        Script.Mandaic => "mand",
        _ => "DFLT",
    });

    private static bool Joins(Script script) => script is Script.Arabic or Script.Syriac or Script.Nko or Script.Mandaic;

    [ThreadStatic] private static GlyphRun? t_run;

    /// <summary>
    /// Shapes the text into glyphs in logical order, with advances and offsets in 1/1000 em for drawing it in the
    /// given direction.
    /// </summary>
    public static void Shape(FontProgram font, string s, int start, int length, bool rtl, List<ShapedGlyph> output)
    {
        output.Clear();
        var run = t_run ??= new GlyphRun();
        run.Clear();
        var layout = font.Layout;
        var script = ScriptOfText(s, start, length);

        AddText(font, layout, run, s, start, length, rtl);
        ReorderMarks(run, script);
        Compose(font, layout, run);
        var plan = GetPlan(font, layout, script);
        if (Joins(script)) ArabicJoining(run, script);
        // Joiners and other invisible controls have done their part; they are not drawn.
        run.RemoveAll(static g => UnicodeTables.IsDefaultIgnorable(g.CodePoint));
        if (Joins(script) && !plan.HasForms) PresentationForms(font, run, ligate: !plan.HasRlig);

        var lookups = new LookupPass(layout, run, rtl);
        lookups.Apply(plan.Gsub, gpos: false);
        SetAdvances(font, run);
        lookups.Apply(plan.Gpos, gpos: true);
        if (!plan.HasGposKern && font.Kerning is { } kerning) ApplyKerning(kerning, run);
        PlaceMarks(run, layout, plan, rtl);
        OpenTypeLayout.PropagateAttachments(run, rtl);

        WriteOutput(font, run, output);
    }

    /// <summary>
    /// The GSUB and GPOS lookups of one shaping call, which share the ligature ids they hand out and one work budget,
    /// so a font cannot spend it twice over, and may make the run only so much longer than the text.
    /// </summary>
    private struct LookupPass
    {
        private readonly OpenTypeLayout? _layout;
        private readonly GlyphRun _run;
        private readonly bool _rtl;
        private readonly int _maxLength;
        private int _nextLigatureId = 1;
        private int _operations;

        public LookupPass(OpenTypeLayout? layout, GlyphRun run, bool rtl)
        {
            _layout = layout;
            _run = run;
            _rtl = rtl;
            _operations = OpenTypeLayout.OperationBudget(run.Count);
            _maxLength = OpenTypeLayout.MaxRunLength(run.Count);
        }

        /// <summary>
        /// Applies the lookups in order. A malformed subtable stops them all, and the glyphs stay as the lookups
        /// before it substituted and positioned them.
        /// </summary>
        public void Apply((int Lookup, uint Mask)[] lookups, bool gpos)
        {
            if (_layout == null) return;
            try
            {
                foreach (var (lookup, mask) in lookups)
                    _layout.ApplyLookup(_run, gpos, lookup, mask, _rtl, ref _nextLigatureId, ref _operations, _maxLength);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
            {
            }
        }
    }

    private static bool IsMark(int props) => (props & OpenTypeLayout.Mark) != 0;
}
