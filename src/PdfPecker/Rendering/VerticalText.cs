namespace PdfPecker.Rendering;

/// <summary>
/// Finds vertical writing drawn with a horizontal font: Chinese and Japanese set top to bottom, as Chrome prints
/// <c>writing-mode: vertical-rl</c>, with every letter upright and placed on its own under the one before. Read as drawn,
/// each letter is a line of its own and the columns come out as rows, so such runs get the direction they are read in.
/// </summary>
internal static class VerticalText
{
    /// <summary>The fewest letters one under another taken as vertical writing rather than, say, a table's column of single letters.</summary>
    private const int MinRun = 3;

    /// <summary>
    /// Turns runs of full-width letters, each placed under the one before it in the content, into text that runs down
    /// the page: its direction points down, its origin is the top of the letter's em box, centred on the column as
    /// rotated text is on its baseline, and it reaches the next letter.
    /// </summary>
    public static void Apply(List<TextGlyph> glyphs)
    {
        int start = 0;
        for (int i = 1; i <= glyphs.Count; i++)
        {
            if (i < glyphs.Count && Follows(glyphs[i - 1], glyphs[i])) continue;
            if (i - start >= MinRun) Turn(glyphs, start, i);
            start = i;
        }
    }

    /// <summary>Whether <paramref name="next"/> is the letter under <paramref name="g"/> in a column of upright full-width letters.</summary>
    private static bool Follows(in TextGlyph g, in TextGlyph next)
    {
        if (!Upright(g) || !Upright(next) || Math.Abs(g.Size - next.Size) > 0.01 * g.Size) return false;
        double size = g.Size, step = next.Y - g.Y;
        // Punctuation is set closer, and letter spacing can set letters further apart.
        return Math.Abs(next.X - g.X) < 0.05 * size && step > 0.4 * size && step < 1.6 * size;
    }

    /// <summary>A letter drawn upright whose advance is as wide as its size, as Chinese and Japanese letters are.</summary>
    private static bool Upright(in TextGlyph g)
        => g.DirX > 0.999 && Math.Abs(g.DirY) < 0.001 && g.Size > 0 && g.EndX - g.X > 0.8 * g.Size && g.EndX - g.X < 1.2 * g.Size
           && g.Text.Length > 0 && Wide(char.IsHighSurrogate(g.Text[0]) && g.Text.Length > 1 && char.IsLowSurrogate(g.Text[1]) ? char.ConvertToUtf32(g.Text[0], g.Text[1]) : g.Text[0]);

    /// <summary>Letters set upright in vertical writing: CJK ideographs, kana, Hangul and full-width punctuation and forms.</summary>
    private static bool Wide(int cp) => cp is >= 0x3000 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF
        or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0x20000 and <= 0x3FFFF;

    private static void Turn(List<TextGlyph> glyphs, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            var g = glyphs[i];
            double size = g.Size, x = g.X + 0.5 * size, top = g.Y - 0.88 * size;
            double step = i + 1 < end ? glyphs[i + 1].Y - g.Y : g.EndX - g.X;
            g.X = g.EndX = x;
            g.Y = top;
            g.EndY = top + step;
            g.DirX = 0;
            g.DirY = 1;
            // The letters of a column follow each other; only the first was placed after a gap.
            if (i > start) g.Moved = false;
            glyphs[i] = g;
        }
    }
}
