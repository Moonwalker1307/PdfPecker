using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Text;

namespace PdfPecker.Layout;

/// <summary>
/// text-decoration-skip-ink: an underline or overline stops short of the glyphs it crosses, as in Chrome. Each glyph
/// whose ink reaches into the line's band cuts a gap from where its ink starts to where it ends there, widened by the
/// line's thickness (at most 13 CSS pixels) on both sides. CJK ideographs and Hangul cut no gaps unless the value is all.
/// </summary>
internal sealed partial class LayoutEngine
{
    // Chrome ignores ink that reaches less than half a pixel into the line, and widens the gaps by the line's
    // thickness, up to 13 pixels.
    private const float InkBandInset = 0.5f * CssPixel, MaxInkDilation = 13 * CssPixel;

    // A synthetic italic is slanted by this much, as the writer draws it.
    private const float SyntheticItalicSkew = 0.2f;

    private readonly List<FontRun> _inkRuns = new();
    private readonly List<ShapedGlyph> _inkGlyphs = new();
    private readonly List<(float Left, float Right)> _inkGaps = new();

    /// <summary>
    /// The gaps, left to right and merged, that a decoration line between <paramref name="top"/> and
    /// <paramref name="bottom"/> leaves for the glyphs of <paramref name="text"/>; null when the line has none.
    /// </summary>
    private List<(float Left, float Right)>? InkGaps(ComputedStyle s, TextOp? text, float top, float bottom, float thickness)
    {
        if (text == null || s.SkipInk == SkipInk.None || text.Size <= 0 || text.Text.Length == 0) return null;
        float low = top + InkBandInset, high = bottom - InkBandInset;
        if (high < low) low = high = (top + bottom) / 2;
        var gaps = _inkGaps;
        gaps.Clear();
        float dilation = Math.Min(thickness, MaxInkDilation);
        float size = text.Size, scale = size / 1000f;
        // Heights above the baseline, in ems.
        float bandLow = (text.Baseline - high) / size, bandHigh = (text.Baseline - low) / size;
        string str = text.Text;

        _inkRuns.Clear();
        text.Font.Split(str, _inkRuns);
        float runX = text.X;
        for (int r = 0; r < _inkRuns.Count; r++)
        {
            var run = _inkRuns[text.Rtl ? _inkRuns.Count - 1 - r : r];
            float pen = runX;
            switch (run.Font)
            {
                case EmbeddedFont font:
                {
                    bool positioned = font.Shape(str, run.Start, run.Length, _inkGlyphs, text.Rtl);
                    if (text.Rtl) _inkGlyphs.Reverse();
                    bool kern = !positioned && font.HasKerning;
                    float skew = font.SyntheticItalic ? SyntheticItalicSkew : 0;
                    int previous = -1;
                    foreach (var g in _inkGlyphs)
                    {
                        if (kern && previous >= 0) pen += (text.Rtl ? font.Program.Kern1000(g.Gid, previous) : font.Program.Kern1000(previous, g.Gid)) * scale;
                        previous = g.Gid;
                        float dy = positioned ? g.Dy * scale : 0;
                        if (SkipsInk(s, str, g.Start)
                            && GlyphInk.Intercept(font.Program, g.Gid, bandLow - dy / size, bandHigh - dy / size, skew, out float left, out float right))
                        {
                            float origin = pen + (positioned ? g.Dx * scale : 0);
                            gaps.Add((origin + left * size - dilation, origin + right * size + dilation));
                        }
                        pen += (positioned ? g.Advance : font.Program.Advance1000(g.Gid)) * scale + text.CharSpacing
                            + (g.Length > 0 && str[g.Start] == ' ' ? text.WordSpacing : 0);
                    }
                    break;
                }
                case StandardFont font:
                {
                    // A standard font has the outlines of its bundled replacement, which has the same widths.
                    string visual = text.Rtl ? Bidi.VisualText(str.Substring(run.Start, run.Length)) : str.Substring(run.Start, run.Length);
                    for (int i = 0; i < visual.Length; i++)
                    {
                        char c = visual[i];
                        if (i > 0 && font.HasKerning) pen += font.KernUnits(visual[i - 1], c) * scale;
                        if (SkipsInk(s, visual, i)
                            && GlyphInk.Intercept(font, c, bandLow, bandHigh, 0, out float left, out float right))
                            gaps.Add((pen + left * size - dilation, pen + right * size + dilation));
                        pen += font.CharWidth(c, size) + text.CharSpacing + (c == ' ' ? text.WordSpacing : 0);
                    }
                    break;
                }
            }
            runX += RunWidth(run.Font, text, run.Start, run.Length);
        }
        if (gaps.Count == 0) return null;
        gaps.Sort((a, b) => a.Left.CompareTo(b.Left));
        int merged = 0;
        for (int k = 1; k < gaps.Count; k++)
        {
            if (gaps[k].Left <= gaps[merged].Right) gaps[merged] = (gaps[merged].Left, Math.Max(gaps[merged].Right, gaps[k].Right));
            else gaps[++merged] = gaps[k];
        }
        gaps.RemoveRange(merged + 1, gaps.Count - merged - 1);
        return gaps;
    }

    /// <summary>Whether the glyph of the character at <paramref name="i"/> cuts a gap: not a CJK one, unless all ink is skipped.</summary>
    private static bool SkipsInk(ComputedStyle s, string text, int i)
    {
        if (s.SkipInk == SkipInk.All) return true;
        int cp = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
        return !JustificationCharacters.IsCjkIdeographOrSymbol(cp)
            && cp is not ((>= 0x1100 and <= 0x11FF) or (>= 0x3130 and <= 0x318F) or (>= 0xA960 and <= 0xA97F) or (>= 0xAC00 and <= 0xD7FF) or (>= 0x10080 and <= 0x100FF));
    }

    /// <summary>How far a run of text moves the pen, as the writer draws it: with the letter and word spacing.</summary>
    private static float RunWidth(TextFont font, TextOp t, int start, int length)
    {
        float w = font.MeasureUnits(t.Text, start, length) * t.Size / 1000f;
        for (int i = start, end = start + length; i < end; i++)
        {
            if (char.IsHighSurrogate(t.Text[i]) && i + 1 < end && char.IsLowSurrogate(t.Text[i + 1])) i++;
            w += t.CharSpacing;
            if (t.Text[i] == ' ') w += t.WordSpacing;
        }
        return w;
    }
}
