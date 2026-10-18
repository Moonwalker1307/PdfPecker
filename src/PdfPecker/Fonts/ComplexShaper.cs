using System.Globalization;
using PdfPecker.Text;

namespace PdfPecker.Fonts;

/// <summary>
/// OpenType shaping for scripts whose glyphs depend on their neighbours, along the lines of HarfBuzz: Arabic, Syriac,
/// N'Ko and other joining scripts get their initial, medial, final and isolated forms and required ligatures;
/// Hebrew and the joining scripts get mark positioning (GPOS mark, mkmk), cursive attachment and kerning. Fonts
/// without OpenType forms fall back to the Unicode Arabic presentation forms. The Indic scripts get their syllables
/// reordered and their conjuncts, half forms, reph and matras formed; Thai and Lao get Sara Am decomposed and, in fonts
/// without Thai lookups, their marks moved by the fonts' private-use glyphs. Text with combining marks in the other
/// scripts, such as Latin, Greek and Cyrillic, is composed where the font has the letter and gets its marks placed by
/// GPOS. Glyphs come out in logical order, with
/// positions for the direction the run is drawn in.
/// </summary>
/// <remarks>
/// The class is split by concern: this file has the scripts and the order of the shaping steps, Plan picks the GSUB and
/// GPOS lookups of a font and script, Characters turns the text into glyphs and orders and composes the marks, Joining
/// gives the joining scripts their forms, Indic and IndicSyllables shape the Indic scripts, Thai shapes Thai and Lao,
/// and Positioning places the glyphs once the lookups have run.
/// </remarks>
internal static partial class ComplexShaper
{
    // The blocks of the scripts this shaper handles: Hebrew to Arabic Extended-A, the alphabetic presentation forms from
    // Hebrew on with Arabic Presentation Forms-A, Arabic Presentation Forms-B, and Arabic Extended-C past the BMP.
    private const char ComplexBlocksFirst = '֐', ComplexBlocksLast = 'ࣿ';
    private const char PresentationFormsAFirst = 'יִ', PresentationFormsALast = '﷿';
    private const char PresentationFormsBFirst = 'ﹰ', PresentationFormsBLast = '﻿';
    private const int ArabicExtendedCFirst = 0x10EC0, ArabicExtendedCLast = 0x10EFF;

    // The nine Indic blocks from Devanagari to Malayalam, Thai and Lao, and Devanagari Extended.
    private const char IndicFirst = '\u0900', IndicLast = '\u0D7F', ThaiFirst = '\u0E00', LaoLast = '\u0EFF';
    private const char DevanagariExtendedFirst = '\uA8E0', DevanagariExtendedLast = '\uA8FF';

    /// <summary>True when the text has characters of a script this shaper handles.</summary>
    public static bool Handles(string s, int start, int length)
    {
        for (int i = start, end = start + length; i < end;)
        {
            var (cp, units) = CodePointAt(s, i, end);
            if (IsComplex(cp)) return true;
            i += units;
        }
        return false;
    }

    private static bool IsComplex(int cp)
        => cp is (>= ComplexBlocksFirst and <= ComplexBlocksLast)
            or (>= PresentationFormsAFirst and <= PresentationFormsALast)
            or (>= PresentationFormsBFirst and <= PresentationFormsBLast)
            or (>= ArabicExtendedCFirst and <= ArabicExtendedCLast)
            or (>= IndicFirst and <= IndicLast) or (>= ThaiFirst and <= LaoLast)
            or (>= DevanagariExtendedFirst and <= DevanagariExtendedLast)
            || IsCombiningMark(cp);

    // Combining marks, which the font's GPOS places on letters that have no precomposed form, such as q̃ or ą́.
    private static bool IsCombiningMark(int cp)
        => cp is (>= 0x0300 and <= 0x036F) or (>= 0x1AB0 and <= 0x1AFF) or (>= 0x1DC0 and <= 0x1DFF) or (>= 0x20D0 and <= 0x20FF)
            or (>= 0xFE20 and <= 0xFE2F);

    /// <summary>The direction complex text is shaped for when the caller does not know it: its first strong character's.</summary>
    public static bool GuessRtl(string s, int start, int length)
    {
        for (int i = start, end = start + length; i < end;)
        {
            var (cp, units) = CodePointAt(s, i, end);
            switch (UnicodeTables.Bidi(cp))
            {
                case BidiClass.L: return false;
                case BidiClass.R or BidiClass.AL: return true;
            }
            i += units;
        }
        return false;
    }

    private enum Script : byte
    {
        Hebrew, Arabic, Syriac, Nko, Mandaic, Thaana, Samaritan, Latin, Greek, Cyrillic, Other,
        Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada, Malayalam, Thai, Lao,
    }

    private static Script ScriptOf(int cp) => cp switch
    {
        >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F => Script.Hebrew,
        >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x0870 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF
            or >= 0x10EC0 and <= 0x10EFF => Script.Arabic,
        >= 0x0700 and <= 0x074F or >= 0x0860 and <= 0x086F => Script.Syriac,
        >= 0x07C0 and <= 0x07FF => Script.Nko,
        >= 0x0840 and <= 0x085F => Script.Mandaic,
        >= 0x0780 and <= 0x07BF => Script.Thaana,
        >= 0x0800 and <= 0x083F => Script.Samaritan,
        >= 0x0900 and <= 0x097F or >= 0xA8E0 and <= 0xA8FF => Script.Devanagari,
        >= 0x0980 and <= 0x09FF => Script.Bengali,
        >= 0x0A00 and <= 0x0A7F => Script.Gurmukhi,
        >= 0x0A80 and <= 0x0AFF => Script.Gujarati,
        >= 0x0B00 and <= 0x0B7F => Script.Oriya,
        >= 0x0B80 and <= 0x0BFF => Script.Tamil,
        >= 0x0C00 and <= 0x0C7F => Script.Telugu,
        >= 0x0C80 and <= 0x0CFF => Script.Kannada,
        >= 0x0D00 and <= 0x0D7F => Script.Malayalam,
        >= 0x0E00 and <= 0x0E7F => Script.Thai,
        >= 0x0E80 and <= 0x0EFF => Script.Lao,
        _ => Script.Other,
    };

    // The scripts shaped in runs of their own, as HarfBuzz is given one script at a time.
    private static bool ShapedApart(Script script) => script >= Script.Devanagari;

    /// <summary>
    /// The script of the text: that of its first character that belongs to one of the complex scripts, or else of its
    /// first Latin, Greek or Cyrillic letter, whose fonts may list their mark positioning under that script only.
    /// </summary>
    private static Script ScriptOfText(string s, int start, int length)
    {
        var letters = Script.Other;
        for (int i = start, end = start + length; i < end;)
        {
            var (cp, units) = CodePointAt(s, i, end);
            if (ScriptOf(cp) is var script and not Script.Other) return script;
            if (letters == Script.Other) letters = LetterScript(cp);
            i += units;
        }
        return letters;
    }

    private static Script LetterScript(int cp) => cp switch
    {
        >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 0x00C0 and <= 0x024F and not (0xD7 or 0xF7) or >= 0x1E00 and <= 0x1EFF
            or >= 0x2C60 and <= 0x2C7F or >= 0xA720 and <= 0xA7FF => Script.Latin,
        >= 0x0370 and <= 0x03FF or >= 0x1F00 and <= 0x1FFF => Script.Greek,
        >= 0x0400 and <= 0x052F or >= 0x1C80 and <= 0x1C8F or >= 0x2DE0 and <= 0x2DFF or >= 0xA640 and <= 0xA69F => Script.Cyrillic,
        _ => Script.Other,
    };

    private static uint ScriptTag(Script script) => FontProgram.Tag(script switch
    {
        _ when IndicConfigOf(script) is { } config => config.NewTag,
        Script.Thai => "thai",
        Script.Lao => "lao ",
        Script.Hebrew => "hebr",
        Script.Arabic => "arab",
        Script.Syriac => "syrc",
        Script.Nko => "nko ",
        Script.Mandaic => "mand",
        Script.Thaana => "thaa",
        Script.Samaritan => "samr",
        Script.Latin => "latn",
        Script.Greek => "grek",
        Script.Cyrillic => "cyrl",
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
        for (int i = start, end = start + length; i < end;)
        {
            int runEnd = ScriptRunEnd(s, i, end, out var script);
            if (!ShapedApart(script)) script = ScriptOfText(s, i, runEnd - i);
            ShapeRun(font, s, i, runEnd - i, rtl, script, output);
            i = runEnd;
        }
    }

    /// <summary>
    /// The end of the run of text from <paramref name="start"/> that is shaped together, and its script: a run of one
    /// of the scripts shaped apart (Indic, Thai, Lao), or a run of all the others, which are shaped together as they
    /// always have been. Spaces, punctuation, digits, marks and joiners belong to the run they follow, or at the start
    /// to the one after them.
    /// </summary>
    private static int ScriptRunEnd(string s, int start, int end, out Script script)
    {
        Script? current = null;
        int i = start;
        while (i < end)
        {
            var (cp, units) = CodePointAt(s, i, end);
            if (RunScript(cp) is { } next)
            {
                if (current is { } c && c != next) break;
                current = next;
            }
            i += units;
        }
        script = current ?? Script.Other;
        return i;
    }

    // The script a character makes its run, or null for one that belongs to the run around it. The Devanagari dandas
    // are used by the other Indic scripts too.
    private static Script? RunScript(int cp)
    {
        var script = ScriptOf(cp);
        if (ShapedApart(script)) return cp is 0x0964 or 0x0965 ? null : script;
        return CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter ? Script.Other : null;
    }

    private static void ShapeRun(FontProgram font, string s, int start, int length, bool rtl, Script script, List<ShapedGlyph> output)
    {
        var run = t_run ??= new GlyphRun();
        run.Clear();
        var layout = font.Layout;
        var plan = GetPlan(font, layout, script);
        if (plan.Indic != null)
        {
            ShapeIndic(font, layout, run, plan, s, start, length, rtl, output);
            return;
        }

        AddText(font, layout, run, s, start, length, rtl, decomposeFirst: false);
        if (script is Script.Thai or Script.Lao) DecomposeSaraAm(run, font, layout);
        ReorderMarks(run, script);
        Compose(font, layout, run);
        if (Joins(script)) ArabicJoining(run, script);
        // Joiners and other invisible controls have done their part; they are not drawn. Thai and Lao keep them as
        // long as HarfBuzz does, which keeps the marks on either side apart.
        bool keepIgnorables = script is Script.Thai or Script.Lao;
        if (!keepIgnorables) RemoveIgnorables(run);
        if (Joins(script) && !plan.HasForms) PresentationForms(font, run, ligate: !plan.HasRlig);
        if (script == Script.Thai && !plan.HasScriptGsub) ThaiPrivateUseForms(font, layout, run);

        var lookups = new LookupPass(layout, run, rtl);
        lookups.Apply(plan.Gsub, gpos: false);
        SetAdvances(font, run);
        lookups.Apply(plan.Gpos, gpos: true, plan.MarkLookups);
        if (!plan.HasGposKern && font.Kerning is { } kerning) ApplyKerning(kerning, run);
        PlaceMarks(run, layout, plan, rtl);
        OpenTypeLayout.PropagateAttachments(run, rtl);
        if (keepIgnorables) RemoveIgnorables(run);

        WriteOutput(font, run, output);
    }

    private static void RemoveIgnorables(GlyphRun run) => run.RemoveAll(static g => UnicodeTables.IsDefaultIgnorable(g.CodePoint));

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
        public void Apply((int Lookup, uint Mask)[] lookups, bool gpos, int[]? manualJoinerLookups = null)
        {
            if (_layout == null) return;
            try
            {
                foreach (var (lookup, mask) in lookups)
                {
                    bool manualJoiners = manualJoinerLookups != null && Array.BinarySearch(manualJoinerLookups, lookup) >= 0;
                    _layout.ApplyLookup(_run, gpos, lookup, mask, _rtl, ref _nextLigatureId, ref _operations, _maxLength, manualJoiners: manualJoiners);
                }
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
            {
            }
        }

        /// <summary>Applies the lookups of an Indic stage, as the GSUB lookups of the other scripts are applied.</summary>
        public void Apply(StageLookup[] lookups)
        {
            if (_layout == null) return;
            try
            {
                foreach (var (lookup, mask, perSyllable, manualJoiners) in lookups)
                    _layout.ApplyLookup(_run, false, lookup, mask, _rtl, ref _nextLigatureId, ref _operations, _maxLength, perSyllable, manualJoiners);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
            {
            }
        }
    }

    private static bool IsMark(int props) => (props & OpenTypeLayout.Mark) != 0;
}
