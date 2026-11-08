using PdfPecker.Text;

namespace PdfPecker.Fonts;

// From characters to glyphs: decomposition of characters the font lacks, mirroring, the order of combining marks and
// the composition of a letter with its mark.
internal static partial class ComplexShaper
{
    // A character the font lacks is decomposed this many times at most, which is deeper than any decomposition goes.
    private const int MaxDecompositionDepth = 4;

    // No character below this is a combining mark.
    private const int FirstMark = 0x0300;

    // The combining classes of the marks below and above, among which the Arabic modifier combining marks move to the
    // front (see MoveModifierMarks).
    private const int CombiningClassBelow = 220, CombiningClassAbove = 230;

    // A longer run of marks keeps the order it was written in, as HarfBuzz leaves it (HB_OT_SHAPE_MAX_COMBINING_MARKS).
    // Only hostile text has such runs, and sorting them by insertion would take the square of their length.
    private const int MaxReorderedMarks = 32;

    // Adds the glyphs of the text. Indic text is decomposed wherever the font has the parts (decomposeFirst), as
    // HarfBuzz normalizes it, so that split vowel signs and nukta letters reach the font in pieces; other text only
    // where the font lacks the character.
    private static void AddText(FontProgram font, OpenTypeLayout? layout, GlyphRun run, string s, int start, int length, bool rtl,
        bool decomposeFirst)
    {
        for (int i = start, end = start + length; i < end;)
        {
            var (cp, units) = CodePointAt(s, i, end);
            if (!decomposeFirst || !TryAddIndicDecomposition(font, layout, run, cp, i, units, depth: 0))
                AddCharacter(font, layout, run, cp, i, units, rtl, depth: 0);
            i += units;
        }
    }

    /// <summary>
    /// Adds the glyphs of an Indic character's decomposition when the font has its last part and its first part, or the
    /// first part's own decomposition. The parts after the first take no characters of the text. Returns false, having
    /// added nothing, otherwise.
    /// </summary>
    private static bool TryAddIndicDecomposition(FontProgram font, OpenTypeLayout? layout, GlyphRun run, int cp, int cluster, int length, int depth)
    {
        if (depth >= MaxDecompositionDepth || !DecomposeIndic(cp, out int first, out int second) || font.GlyphId(second) == 0) return false;
        if (!TryAddIndicDecomposition(font, layout, run, first, cluster, length, depth + 1))
        {
            if (font.GlyphId(first) == 0) return false;
            AddCharacter(font, layout, run, first, cluster, length, rtl: false, depth + 1);
        }
        AddCharacter(font, layout, run, second, cluster, 0, rtl: false, depth + 1);
        return true;
    }

    // The character at an index and how many UTF-16 units it takes; an unpaired surrogate stands for itself.
    private static (int CodePoint, int Length) CodePointAt(string s, int index, int end)
    {
        char c = s[index];
        return char.IsHighSurrogate(c) && index + 1 < end && char.IsLowSurrogate(s[index + 1])
            ? (char.ConvertToUtf32(c, s[index + 1]), 2)
            : (c, 1);
    }

    // Adds the glyph of a character, or of its decomposition when the font lacks the character but has its parts.
    private static void AddCharacter(FontProgram font, OpenTypeLayout? layout, GlyphRun run, int cp, int cluster, int length, bool rtl, int depth)
    {
        int gid = font.GlyphId(cp);
        if (gid == 0 && depth < MaxDecompositionDepth && TryAddDecomposition(font, layout, run, cp, cluster, length, rtl, depth)) return;

        var glyph = new GlyphInfo { CodePoint = cp, Cluster = cluster, Length = length, Mask = Global };
        if (rtl && UnicodeTables.Mirror(cp) is var mirror && mirror != cp)
        {
            // A mirrored character takes its mirror's glyph, or the font's rtlm form.
            if (font.GlyphId(mirror) is var mirrorGid and not 0) gid = mirrorGid;
            else glyph.Mask |= Rtlm;
        }
        glyph.Gid = gid;
        glyph.Props = layout is { HasGlyphClasses: true } ? layout.GlyphProps(gid)
            : UnicodeTables.IsMark(cp) ? OpenTypeLayout.Mark : OpenTypeLayout.Base;
        run.Add(glyph);
    }

    /// <summary>
    /// Adds the glyphs of a character's decomposition, the second part taking no characters of the text, when the font
    /// has the second part and the first part comes out as a glyph. Returns false, having added nothing, otherwise.
    /// </summary>
    private static bool TryAddDecomposition(FontProgram font, OpenTypeLayout? layout, GlyphRun run, int cp, int cluster, int length, bool rtl, int depth)
    {
        if (!UnicodeTables.Decompose(cp, out int first, out int second) || (second != 0 && font.GlyphId(second) == 0)) return false;
        int count = run.Count;
        AddCharacter(font, layout, run, first, cluster, length, rtl, depth + 1);
        if (run[count].Gid == 0)
        {
            run.RemoveRange(count, run.Count - count);
            return false;
        }
        if (second != 0) AddCharacter(font, layout, run, second, cluster, 0, rtl, depth + 1);
        return true;
    }

    /// <summary>
    /// Composes each combining mark with the letter before it where the font has the character they make (e with acute,
    /// alef with madda, Vietnamese and polytonic Greek letters with a further accent, ...), as HarfBuzz recomposes text.
    /// The letter is the last character of combining class 0 before the mark, and the mark joins it when the marks
    /// between them have lower classes; a letter that is a mark itself, such as a split vowel sign, takes none. Two
    /// characters compose when Unicode normalization composes them, from the generated tables rather than the host's
    /// ICU, so the glyphs are the same on every host.
    /// </summary>
    private static void Compose(FontProgram font, OpenTypeLayout? layout, GlyphRun run, Script script)
    {
        if (run.Count < 2) return;
        bool arabic = script == Script.Arabic;
        int phase = CombiningClassBelow;
        int starter = 0, previousClass = CompositionClass(run[0].CodePoint, arabic, ref phase);
        for (int i = 1; i < run.Count;)
        {
            var mark = run[i];
            int combiningClass = CompositionClass(mark.CodePoint, arabic, ref phase);
            var letter = run[starter];
            if ((starter == i - 1 || previousClass < combiningClass) && mark.CodePoint >= FirstMark && IsUnicodeMark(mark.CodePoint)
                && !IsUnicodeMark(letter.CodePoint) && UnicodeNormalization.ComposePair(letter.CodePoint, mark.CodePoint) is var composed and not 0
                && font.GlyphId(composed) is var gid and not 0)
            {
                letter.CodePoint = composed;
                letter.Gid = gid;
                letter.Props = layout is { HasGlyphClasses: true } ? layout.GlyphProps(gid) : OpenTypeLayout.Base;
                letter.Length = Math.Max(letter.Cluster + letter.Length, mark.Cluster + mark.Length) - letter.Cluster;
                run[starter] = letter;
                run.RemoveAt(i);
                continue;
            }
            previousClass = combiningClass;
            if (combiningClass == 0) starter = i;
            i++;
        }
    }

    /// <summary>
    /// The combining class that decides whether a mark can reach the letter before it: the modified class, except that
    /// in Arabic the modifier marks that <see cref="MoveModifierMarks"/> put at the front of their run count as 22 (below)
    /// and 26 (above), lower than the harakat, as HarfBuzz renumbers them so that the run stays sorted.
    /// <paramref name="phase"/> follows the front of the run: the class of the modifier marks that may come next there,
    /// or 0 once they are past.
    /// </summary>
    private static int CompositionClass(int cp, bool arabic, ref int phase)
    {
        int combiningClass = UnicodeTables.ModifiedCombiningClass(cp);
        if (!arabic) return combiningClass;
        if (combiningClass == 0)
        {
            phase = CombiningClassBelow;
            return 0;
        }
        if (phase == CombiningClassBelow)
        {
            if (combiningClass == CombiningClassBelow && IsModifierMark(cp)) return 22;
            phase = CombiningClassAbove;
        }
        if (phase == CombiningClassAbove)
        {
            if (combiningClass == CombiningClassAbove && IsModifierMark(cp)) return 26;
            phase = 0;
        }
        return combiningClass;
    }

    private static int CombiningClass(GlyphRun run, int index) => UnicodeTables.ModifiedCombiningClass(run[index].CodePoint);

    // Sorts each run of combining marks by (modified) combining class, as normalization does; in Arabic, modifier
    // combining marks such as hamza above then move to the front of their run, as HarfBuzz orders them. Runs longer than
    // MaxReorderedMarks stay as they are.
    private static void ReorderMarks(GlyphRun run, Script script)
    {
        int i = 0;
        while (i < run.Count)
        {
            if (CombiningClass(run, i) == 0)
            {
                i++;
                continue;
            }
            int start = i;
            while (i < run.Count && CombiningClass(run, i) != 0) i++;
            if (i - start is < 2 or > MaxReorderedMarks) continue;
            SortByCombiningClass(run, start, i);
            if (script == Script.Arabic) MoveModifierMarks(run, start, i);
        }
    }

    // A stable insertion sort: the runs it sorts are at most MaxReorderedMarks long.
    private static void SortByCombiningClass(GlyphRun run, int start, int end)
    {
        for (int k = start + 1; k < end; k++)
        {
            var mark = run[k];
            int combiningClass = UnicodeTables.ModifiedCombiningClass(mark.CodePoint);
            int j = k - 1;
            for (; j >= start && CombiningClass(run, j) > combiningClass; j--) run[j + 1] = run[j];
            run[j + 1] = mark;
        }
    }

    private static bool IsModifierMark(int cp)
        => cp is 0x0654 or 0x0655 or 0x0658 or 0x06DC or 0x06E3 or 0x06E7 or 0x06E8 or 0x08CA or 0x08CB or 0x08CD or 0x08CE or 0x08CF or 0x08D3 or 0x08F3;

    // The modifier combining marks at the start of the marks below, and then of the marks above, move to the front of
    // the run of sorted marks (HarfBuzz's reorder_marks_arabic).
    private static void MoveModifierMarks(GlyphRun run, int start, int end)
    {
        int front = start, i = start;
        foreach (int combiningClass in (ReadOnlySpan<int>)[CombiningClassBelow, CombiningClassAbove])
        {
            while (i < end && CombiningClass(run, i) < combiningClass) i++;
            if (i == end) return;
            if (CombiningClass(run, i) > combiningClass) continue;
            int modifiers = i;
            while (modifiers < end && CombiningClass(run, modifiers) == combiningClass && IsModifierMark(run[modifiers].CodePoint)) modifiers++;
            int count = modifiers - i;
            if (count == 0) continue;
            var moved = run.GetRange(i, count);
            run.RemoveRange(i, count);
            run.InsertRange(front, moved);
            front += count;
            i = modifiers;
        }
    }
}
