using PdfPecker.Text;

namespace PdfPecker.Fonts;

// From characters to glyphs: decomposition of characters the font lacks, mirroring, the order of combining marks and
// the composition of a letter with its mark.
internal static partial class ComplexShaper
{
    // A character the font lacks is decomposed this many times at most, which is deeper than any decomposition goes.
    private const int MaxDecompositionDepth = 4;

    // The characters a letter and its mark compose to are Arabic to Arabic Extended-A, which is also where the letters are.
    private const int ComposedFirst = 0x0600, ComposedLast = 0x08FF;

    // The combining classes of the marks below and above, among which the Arabic modifier combining marks move to the
    // front (see MoveModifierMarks).
    private const int CombiningClassBelow = 220, CombiningClassAbove = 230;

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

    // A letter and the mark right after it become the precomposed letter when the font has it, as HarfBuzz composes
    // them (waw and hamza above, alef and madda, ...).
    private static void Compose(FontProgram font, OpenTypeLayout? layout, GlyphRun run)
    {
        for (int i = 0; i + 1 < run.Count; i++)
        {
            var letter = run[i];
            if (letter.CodePoint is < ComposedFirst or > ComposedLast || IsMark(letter.Props)) continue;
            while (i + 1 < run.Count && UnicodeTables.Compose(letter.CodePoint, run[i + 1].CodePoint) is var composed and not 0
                && font.GlyphId(composed) is var gid and not 0)
            {
                var mark = run[i + 1];
                letter.CodePoint = composed;
                letter.Gid = gid;
                letter.Props = layout is { HasGlyphClasses: true } ? layout.GlyphProps(gid) : OpenTypeLayout.Base;
                letter.Length = Math.Max(letter.Cluster + letter.Length, mark.Cluster + mark.Length) - letter.Cluster;
                run[i] = letter;
                run.RemoveAt(i + 1);
            }
        }
    }

    private static int CombiningClass(GlyphRun run, int index) => UnicodeTables.ModifiedCombiningClass(run[index].CodePoint);

    // Sorts each run of combining marks by (modified) combining class, as normalization does; in Arabic, modifier
    // combining marks such as hamza above then move to the front of their run, as HarfBuzz orders them.
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
            if (i - start < 2) continue;
            SortByCombiningClass(run, start, i);
            if (script == Script.Arabic) MoveModifierMarks(run, start, i);
        }
    }

    // A stable insertion sort: runs of marks are short.
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
