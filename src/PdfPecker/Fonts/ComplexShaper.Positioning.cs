using PdfPecker.Text;

namespace PdfPecker.Fonts;

// Positions: the font's advances, kerning from the kern table when GPOS has none, the marks' place, and the shaped
// glyphs in 1/1000 em.
internal static partial class ComplexShaper
{
    // Every glyph starts with the font's advance, which GPOS then adjusts. Joiners and other invisible controls that
    // are still there take no room, whatever glyph the font has for them.
    private static void SetAdvances(FontProgram font, GlyphRun run)
    {
        for (int i = 0; i < run.Count; i++)
        {
            var glyph = run[i];
            glyph.XAdvance = UnicodeTables.IsDefaultIgnorable(glyph.CodePoint) ? 0 : font.Advance(glyph.Gid);
            run[i] = glyph;
        }
    }

    // Without GPOS kerning, the kern table's pairs apply between each glyph that is not a mark and the next such glyph.
    private static void ApplyKerning(Kerning kerning, GlyphRun run)
    {
        for (int i = 0; i + 1 < run.Count; i++)
        {
            if (IsMark(run[i].Props)) continue;
            int next = i + 1;
            while (next < run.Count && IsMark(run[next].Props)) next++;
            if (next == run.Count) return;
            int adjustment = kerning.Pair(run[i].Gid, run[next].Gid);
            if (adjustment == 0) continue;
            var glyph = run[i];
            glyph.XAdvance += adjustment;
            run[i] = glyph;
        }
    }

    // Marks take no room (HarfBuzz zeroes them after GPOS). Marks that GPOS did not place are put on their base.
    private static void PlaceMarks(GlyphRun run, OpenTypeLayout? layout, Plan plan, bool rtl)
    {
        // Left to right without GPOS, a mark with an advance of its own moves back by it, over the glyph before it.
        bool moveBack = !rtl && layout is not { HasGpos: true };
        // Without mark positioning, a mark is drawn where it would follow its base left to right, as fonts design
        // marks to sit on the glyph before them.
        bool followBase = rtl && !plan.HasMarkPositioning;
        for (int i = 0; i < run.Count; i++)
        {
            var mark = run[i];
            if (!IsMark(mark.Props)) continue;
            if (moveBack) mark.XOffset -= mark.XAdvance;
            mark.XAdvance = 0;
            if (followBase && mark.AttachChain == 0 && PreviousBase(run, i) is var b and >= 0) mark.XOffset += run[b].XAdvance;
            run[i] = mark;
        }
    }

    // The index of the last glyph before the given one that is not a mark, or -1.
    private static int PreviousBase(GlyphRun run, int index)
    {
        int i = index - 1;
        while (i >= 0 && IsMark(run[i].Props)) i--;
        return i;
    }

    private static void WriteOutput(FontProgram font, GlyphRun run, List<ShapedGlyph> output)
    {
        float scale = 1000f / font.UnitsPerEm;
        for (int i = 0; i < run.Count; i++)
        {
            var g = run[i];
            output.Add(new ShapedGlyph(g.Gid, g.Cluster, g.Length, g.XAdvance * scale, g.XOffset * scale, g.YOffset * scale));
        }
    }
}
