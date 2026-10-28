using System.Globalization;
using System.Text;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// text-emphasis: a mark over or under each character, and the room the marks need. As in Chrome, the marks are drawn
/// in the text's font at half its size, and they take room from the space around the text first: a line whose marks
/// reach past it moves down (or the next line does) only by what that space cannot hold.
/// </summary>
internal sealed partial class LayoutEngine
{
    /// <summary>The size of the marks of text in style <paramref name="s"/>: half the font size, rounded to whole CSS pixels as in Chrome.</summary>
    private static float EmphasisMarkSize(ComputedStyle s) => MathF.Round(s.TextSize / CssPixel * 0.5f, MidpointRounding.AwayFromZero) * CssPixel;

    /// <summary>The descent of a font's em box, as a fraction of the em: from its typographic metrics, or else its line metrics.</summary>
    private static float EmDescentOf(TextFont font)
    {
        float d = font.Metric(FontMetric.EmDescent);
        if (d > 0) return d;
        float h = font.Ascent - font.Descent;
        return h > 0 ? Math.Clamp(-font.Descent / h, 0, 1) : 0;
    }

    /// <summary>
    /// The room above a block's first line that emphasis marks may take before the line moves down: the block's top
    /// padding, and when it has no top border and starts no formatting context of its own the margin above it too
    /// (its own top margin collapsed with the bottom margin of the block before it), as in Chrome.
    /// </summary>
    private static float EmphasisRoomAbove(Element block)
    {
        var s = block.Style;
        float padding = Fixed(s.PaddingTop);
        if (s.BorderTop != 0 || s.FormattingRoot || s.Floated || s.OutOfFlow || s.IsFlex || s.IsGrid || s.Display is Display.TableCell or Display.TableCaption
            || block.Parent?.Style is { IsFlex: true } or { IsGrid: true })
            return padding;
        float margin = Fixed(s.MarginTop);
        if (block.PreviousElementSibling is { Style: { Display: not Display.None, IsBlockLevel: true } before }) margin = CollapsedMargin(margin, Fixed(before.MarginBottom));
        return Math.Max(padding, padding + margin);

        static float Fixed(Length l) => l.IsPercent || l.IsAuto ? 0 : l.Value;
    }

    /// <summary>Two adjoining margins collapsed: the largest positive one plus the most negative one.</summary>
    private static float CollapsedMargin(float a, float b) => Math.Max(0, Math.Max(a, b)) + Math.Min(0, Math.Min(a, b));

    private static bool HasEmphasis(List<Line> lines, int n)
    {
        for (int li = 0; li < n; li++)
            foreach (var it in lines[li].Items)
                if (it.Kind == ItemKind.Text && it.Style.EmphasisShape != EmphasisShape.None && it.Style.EmphasisMark != null) return true;
        return false;
    }

    /// <summary>
    /// Makes room for the emphasis marks of the lines: each line's ascent grows by how far its marks reach above it,
    /// less the space left below the line before it, and its descent by how far its marks reach below it; the line
    /// after that moves back up into whatever space it has above its text. <paramref name="roomAbove"/> is the space
    /// the first line may take above the block. This is Chrome's annotation overflow, which ruby shares.
    /// </summary>
    private static void MakeRoomForEmphasis(List<Line> lines, int n, float[] lineA, float[] lineD, ComputedStyle block, float roomAbove)
    {
        if (!HasEmphasis(lines, n)) return;
        // The space the line before left below its text, or (negative) how far its marks reach below it.
        float before = roomAbove;
        for (int li = 0; li < n; li++)
        {
            if (float.IsNaN(lineA[li])) continue;
            EmphasisExtent(lines[li], block, lineA[li], lineD[li], out float over, out float under, out float spaceOver, out float spaceUnder);
            float shift = over;
            if (before < 0 && spaceOver > 0) shift = -Math.Min(spaceOver, -before);
            if (over > 0 && before > 0) shift = Math.Max(0, over - before);
            lineA[li] += shift;
            lineD[li] += under;
            before = under > 0 ? -under : spaceUnder;
        }
    }

    /// <summary>
    /// How far a line's text and marks reach past its box above (<paramref name="over"/>) and below
    /// (<paramref name="under"/>), and the space they leave inside it. Text counts with its em box, the marks with
    /// their size; a line whose content is shorter than the block's font size counts as filling the box but its half
    /// leading, as in Chrome.
    /// </summary>
    private static void EmphasisExtent(Line line, ComputedStyle block, float lineA, float lineD,
        out float over, out float under, out float spaceOver, out float spaceUnder)
    {
        float top = -lineA, bottom = lineD;
        float contentOver = 0, contentUnder = 0;
        bool marksOver = false, marksUnder = false;
        foreach (var it in line.Items)
        {
            if (it.Kind is not (ItemKind.Text or ItemKind.Space)) continue;
            var s = it.Style;
            // The text's box, its rounded ascent and descent, made smaller towards its em box by whole pixels, as Chrome
            // measures it.
            s.Font.LineMetrics(s.TextSize, out float ascent, out float descent);
            float emD = EmDescentOf(s.Font) * s.TextSize, emA = s.TextSize - emD;
            float itemOver = s.BaselineShift - ascent + FlooredPixels(ascent - emA), itemUnder = s.BaselineShift + descent - FlooredPixels(descent - emD);
            if (it.Kind == ItemKind.Text && s.EmphasisShape != EmphasisShape.None && s.EmphasisMark != null)
            {
                if (s.EmphasisUnder) { itemUnder += EmphasisMarkSize(s); marksUnder = true; }
                else { itemOver -= EmphasisMarkSize(s); marksOver = true; }
            }
            contentOver = Math.Min(contentOver, itemOver);
            contentUnder = Math.Max(contentUnder, itemUnder);
        }
        if (contentUnder - contentOver < block.TextSize)
        {
            float halfLeading = Math.Max(0, (lineA + lineD - block.TextSize) / 2);
            contentOver = top + halfLeading;
            contentUnder = bottom - halfLeading;
        }
        // Without marks, text taller than the line overflows it as it always does.
        if (contentOver < top && !marksOver) contentOver = top;
        if (contentUnder > bottom && !marksUnder) contentUnder = bottom;
        over = Math.Max(0, top - contentOver);
        under = Math.Max(0, contentUnder - bottom);
        spaceOver = Math.Max(0, contentOver - top);
        spaceUnder = Math.Max(0, bottom - contentUnder);
    }

    /// <summary>A length rounded down to whole CSS pixels, and no less than zero.</summary>
    private static float FlooredPixels(float length) => Math.Max(0, MathF.Floor(length / CssPixel + Eps)) * CssPixel;

    /// <summary>
    /// Draws the emphasis marks of the text items from <paramref name="start"/> to <paramref name="end"/>, centred on
    /// each character that takes one, just outside the font's ascent (or descent) as Chrome puts them. The marks are
    /// artifacts in a tagged PDF.
    /// </summary>
    private void DrawEmphasisMarks(List<Item> items, int start, int end, float[] xs, float rx, float baseline, float lineTop, ComputedStyle style, string mark)
    {
        var font = style.Font;
        float size = EmphasisMarkSize(style);
        float markW = TextWidth(font, mark, 0, mark.Length, size, 0);
        float markEmD = EmDescentOf(font) * size;
        font.LineMetrics(style.TextSize, out float ascent, out float descent);
        float markBaseline = style.EmphasisUnder
            ? baseline + MathF.Ceiling((descent + size - markEmD) / CssPixel - Eps) * CssPixel
            : baseline - MathF.Ceiling((ascent + markEmD) / CssPixel - Eps) * CssPixel;
        uint color = CssColor.Fade(style.EmphasisColor ?? style.TextFillColor ?? style.Color, style.InlineFade);
        // Few fonts have the sesame marks, which are CJK characters, so a sesame no font draws is drawn as a shape.
        bool sesameShape = style.EmphasisShape != EmphasisShape.String && mark[0] is '\uFE45' or '\uFE46' && !Draws(font, mark[0]);
        float emCenter = markBaseline - (size - markEmD) + size / 2;
        float ls = style.LetterSpacing;
        for (int k = start; k < end; k++)
        {
            var it = items[k];
            if (it.Kind != ItemKind.Text || it.Length == 0) continue;
            string src = it.Source;
            int from = it.Start, to = it.Start + it.Length;
            // The advances of the characters on their own, scaled to the item's width, which shaping and kerning set.
            float total = TextWidth(font, src, from, it.Length, style.TextSize, ls);
            float scale = total > 0 ? it.Width / total : 0;
            bool rtl = (it.Level & 1) != 0;
            float x0 = xs[k] + rx, pos = 0;
            for (int j = from; j < to;)
            {
                Rune.DecodeFromUtf16(src.AsSpan(j, to - j), out var rune, out int len);
                int cp = rune.Value;
                float adv = TextWidth(font, src, j, len, style.TextSize, ls) * scale;
                if (TakesEmphasis(cp))
                {
                    float center = pos + (adv - ls) / 2;
                    float cx = rtl ? x0 + it.Width - center : x0 + center;
                    if (sesameShape) _ops.Add(SesameShape(cx, emCenter, size, color, style.EmphasisOpen));
                    else _ops.Add(new TextOp(cx - markW / 2, markBaseline, lineTop, font, size, color, mark, 0, 0));
                }
                pos += adv;
                j += len;
            }
        }
    }

    /// <summary>Whether a font, or the fonts it falls back to, has a glyph for a character.</summary>
    private static bool Draws(TextFont font, char c) => font switch
    {
        FontChain chain => Draws(chain.Pick(c), c),
        EmbeddedFont embedded => embedded.Covers(c),
        _ => StandardFont.CanEncode(c),
    };

    // The sesame mark as a shape, in fractions of the mark's em from the em box's centre: a stroke from a point at the
    // top left down to a round end at the bottom right, like the ﹅ of CJK fonts.
    private const float SesameTipX = -0.1f, SesameTipY = -0.15f, SesameEndX = 0.04f, SesameEndY = 0.07f, SesameRadius = 0.075f;
    private const int SesameArcPoints = 10;

    /// <summary>A sesame mark centred at (<paramref name="cx"/>, <paramref name="cy"/>), filled or (open) outlined.</summary>
    private static PathOp SesameShape(float cx, float cy, float size, uint color, bool open)
    {
        float dx = SesameEndX - SesameTipX, dy = SesameEndY - SesameTipY, length = MathF.Sqrt(dx * dx + dy * dy);
        float toward = MathF.Atan2(dy, dx), spread = MathF.PI / 2 + MathF.Asin(SesameRadius / length);
        var loop = new float[(SesameArcPoints + 2) * 2];
        loop[0] = cx + SesameTipX * size;
        loop[1] = cy + SesameTipY * size;
        // Round the far side of the end, from where the stroke's upper edge meets it to where its lower edge does.
        for (int k = 0; k <= SesameArcPoints; k++)
        {
            float a = toward - spread + 2 * spread * k / SesameArcPoints;
            loop[2 + 2 * k] = cx + (SesameEndX + SesameRadius * MathF.Cos(a)) * size;
            loop[3 + 2 * k] = cy + (SesameEndY + SesameRadius * MathF.Sin(a)) * size;
        }
        loop[^2] = loop[0];
        loop[^1] = loop[1];
        return new PathOp([loop], null, color) { StrokeWidth = open ? size * SesameOpenStroke : 0 };
    }

    private const float SesameOpenStroke = 0.05f;

    /// <summary>
    /// Whether a character gets an emphasis mark: not white space, control and format characters, nor punctuation
    /// other than the symbols the CSS spec lists (Chrome's CanReceiveTextEmphasis); combining marks share the mark of
    /// the letter they combine with.
    /// </summary>
    private static bool TakesEmphasis(int cp)
    {
        switch (cp)
        {
            case '#' or '%' or '&' or '@' or 0xA7 or 0xB6 or 0x609 or 0x60A or 0x66A or 0x2030 or 0x2031 or 0x204A or 0x204B
                or 0x2053 or 0x303D or 0xFE5F or 0xFE60 or 0xFE6A or 0xFE6B or 0xFF03 or 0xFF05 or 0xFF06 or 0xFF20:
                return true;
            case 0x1361 or 0x10100 or 0x10101 or 0x1039F or 0xF0B or 0xF0C:
                return false;
        }
        return CharUnicodeInfo.GetUnicodeCategory(cp) switch
        {
            UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.Surrogate or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.OtherPunctuation
                or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation => false,
            _ => true,
        };
    }
}
