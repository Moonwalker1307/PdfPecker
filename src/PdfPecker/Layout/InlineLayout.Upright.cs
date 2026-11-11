using System.Globalization;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Pdf;
using PdfPecker.Svg;
using PdfPecker.Text;

namespace PdfPecker.Layout;

/// <summary>
/// Upright characters in vertical text. A vertical-rl or vertical-lr box is laid out as horizontal lines that are then
/// turned a quarter clockwise, which turns every glyph with them. With text-orientation: mixed (the initial value) the
/// characters UAX #50 stands upright (Chinese, Japanese, Korean, symbols, fullwidth forms) are turned back where they
/// stand, each in a cell one em long and centred across the line, and take the font's vertical forms (the vert
/// feature), as Chrome draws them; text-orientation: upright stands every character so, and sideways none.
/// text-combine-upright: all sets an inline element's text across the line in one em, squeezed to fit as Chrome does.
/// </summary>
internal sealed partial class InlineLayout
{
    /// <summary>
    /// Whether text of this style is drawn in a vertical-rl or vertical-lr line: laid out turned a quarter clockwise,
    /// where upright characters and text-combine-upright apply.
    /// </summary>
    private bool InVerticalLine(ComputedStyle s) => _engine.FlowTurn == 90 && s.WritingMode is WritingMode.VerticalRl or WritingMode.VerticalLr;

    /// <summary>Whether some characters of text in this style stand upright (see <see cref="Item.Upright"/>).</summary>
    private bool HasUprightText(ComputedStyle s) => s.TextOrientation != TextOrientation.Sideways && InVerticalLine(s);

    /// <summary>
    /// How far the central baseline of vertical text in this style is above its alphabetic baseline: the middle of the
    /// font's ascent and descent, on which upright characters and pictures are centred.
    /// </summary>
    internal static float CentralBaseline(ComputedStyle s) => (s.Font.Ascent + s.Font.Descent) / 2 * s.TextSize;

    /// <summary>The OpenType feature of the glyphs a font draws for vertical text.</summary>
    private const string VerticalForms = "vert";

    /// <summary>
    /// The characters that stay with the one before them where it stands or is turned: combining marks, variation
    /// selectors and the other default ignorable characters.
    /// </summary>
    private static int ClusterLength(string text, int i, int end)
    {
        int n = char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
        while (i + n < end)
        {
            int cp = CodePoint(text, i + n, end, out int units);
            var category = CharUnicodeInfo.GetUnicodeCategory(cp);
            if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark)
                && !TextFont.IsHidden(cp)) break;
            n += units;
        }
        return n;
    }

    private static int CodePoint(string text, int i, int end, out int units)
    {
        if (char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1]))
        {
            units = 2;
            return char.ConvertToUtf32(text[i], text[i + 1]);
        }
        units = 1;
        return text[i];
    }

    /// <summary>The font that draws a character: the one of a fallback chain that has it.</summary>
    private static TextFont Leaf(TextFont font, int cp) => font is FontChain chain ? chain.Pick(cp) : font;

    /// <summary>
    /// Whether the character <paramref name="cp"/> stands upright in text of this orientation. Characters whose UAX #50
    /// orientation is Tr (brackets, dashes, the long vowel mark) stand upright only in a font with vertical forms, which
    /// turn them itself; without them they are turned with the line. White space and controls are always turned.
    /// </summary>
    private static bool StandsUpright(TextFont font, TextOrientation orientation, int cp)
    {
        if (cp <= ' ' || cp is 0x85 or 0xA0) return false;
        if (orientation == TextOrientation.Upright) return true;
        return UnicodeTables.Vertical(cp) switch
        {
            VerticalOrientation.U or VerticalOrientation.Tu => true,
            VerticalOrientation.Tr => Leaf(font, cp).Supports(VerticalForms),
            _ => false,
        };
    }

    /// <summary>
    /// How much longer than the font's horizontal advances <paramref name="length"/> characters of an upright item are:
    /// each upright character takes one em along the line, as the vertical advance of CJK fonts (and Chrome's for a
    /// font without vertical metrics, near enough) is.
    /// </summary>
    private static float UprightExtra(ComputedStyle s, string text, int start, int length)
    {
        var font = s.Font;
        float size = s.TextSize, extra = 0;
        for (int i = start, end = start + length; i < end;)
        {
            int n = ClusterLength(text, i, end);
            if (StandsUpright(font, s.TextOrientation, CodePoint(text, i, end, out _)))
                extra += size - font.MeasureUnits(text, i, n) * size / 1000f;
            i += n;
        }
        return extra;
    }

    /// <summary>The width of <paramref name="length"/> characters of a text item from <paramref name="start"/> in its source.</summary>
    private static float ItemWidth(Item it, int start, int length)
    {
        var s = it.Style;
        float w = TextWidth(s.Font, it.Source, start, length, s.TextSize, s.LetterSpacing);
        return it.Upright ? w + UprightExtra(s, it.Source, start, length) : w;
    }

    /// <summary>The advance of the character of a text item at <paramref name="index"/> in its source, with letter spacing.</summary>
    private static float ItemCharAdvance(Item it, int index)
    {
        var s = it.Style;
        char c = it.Source[index];
        if (!it.Upright || char.IsLowSurrogate(c)) return CharAdvance(s.Font, c, s.TextSize, s.LetterSpacing);
        int cp = CodePoint(it.Source, index, it.Source.Length, out _);
        if (!StandsUpright(s.Font, s.TextOrientation, cp)) return CharAdvance(s.Font, c, s.TextSize, s.LetterSpacing);
        // The em of an upright character goes to its first half when it is a surrogate pair, and a mark after it adds none.
        bool mark = CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.SpacingCombiningMark || TextFont.IsHidden(cp);
        return (mark ? 0 : s.TextSize) + (TextFont.Spaced(c) ? s.LetterSpacing : 0);
    }

    private readonly Dictionary<TextFont, TextFont> _verticalForms = new(ReferenceEqualityComparer.Instance);

    /// <summary>The font with its vertical forms, kept for the layout, since a font the process shares makes a new one each time.</summary>
    private TextFont WithVerticalForms(TextFont font)
    {
        if (!_verticalForms.TryGetValue(font, out var forms)) _verticalForms[font] = forms = font.WithFeatures(VerticalForms);
        return forms;
    }

    /// <summary>Whether any character of the items from <paramref name="start"/> to <paramref name="end"/> stands upright.</summary>
    private static bool AnyUpright(List<Item> items, int start, int end)
    {
        for (int k = start; k < end; k++)
        {
            var it = items[k];
            if (!it.Upright || it.Kind != ItemKind.Text) continue;
            for (int i = it.Start, e = it.Start + it.Length; i < e; i++)
                if (StandsUpright(it.Style.Font, it.Style.TextOrientation, CodePoint(it.Source, i, e, out _))) return true;
        }
        return false;
    }

    /// <summary>
    /// Draws a run of text that has upright characters: the turned characters as text along the line, as the rest of a
    /// line is drawn, and each upright one turned back a quarter in a cell one em long, centred across the line on the
    /// middle of the font's ascent and descent (Chrome's central baseline), with the top of its em box at the start of
    /// the cell. Each item starts where the line put it, so that what was measured and what is drawn agree.
    /// </summary>
    private void DrawUprightRun(List<Item> items, int start, int end, float[] xs, float rx, float baseline, float lineTop,
        uint color, float wordSpacing, float letterSpacing)
    {
        var style = items[start].Style;
        var font = style.Font;
        float size = style.TextSize;
        float center = baseline - CentralBaseline(style);
        var upright = _uprightGlyphs;
        upright.Clear();
        for (int k = start; k < end; k++)
        {
            var it = items[k];
            if (it.Kind != ItemKind.Text || it.Length == 0) continue;
            float x = xs[k] + rx - it.TrimStart;
            string src = it.Source;
            int i = it.Start, last = it.Start + it.Length, turned = -1;
            while (i < last)
            {
                int n = ClusterLength(src, i, last);
                int cp = CodePoint(src, i, last, out _);
                if (!StandsUpright(font, style.TextOrientation, cp))
                {
                    if (turned < 0) turned = i;
                    i += n;
                    continue;
                }
                if (turned >= 0) x = DrawTurned(style, src, turned, i - turned, x, baseline, lineTop, color, wordSpacing, letterSpacing, it);
                turned = -1;
                upright.Add((x, src.Substring(i, n), Leaf(font, cp), it.Tag));
                x += size + letterSpacing * TextFont.SpacedCharacters(src, i, n);
                i += n;
            }
            if (turned >= 0) DrawTurned(style, src, turned, last - turned, x, baseline, lineTop, color, wordSpacing, letterSpacing, it);
        }
        if (upright.Count == 0) return;
        // Turned back about (px, center): a point (px + u, center + v) lands at (px + v, center - u) on the line.
        float px = upright[0].X;
        int from = _engine.Ops.Count;
        var forms = WithVerticalForms(font);
        foreach (var (x, text, leaf, tag) in upright)
        {
            var drawn = WithVerticalForms(leaf);
            float w = drawn.MeasureUnits(text, 0, text.Length) * size / 1000f;
            float emAscent = (1 - leaf.Metric(FontMetric.EmDescent)) * size;
            _engine.Ops.Add(new TextOp(px - w / 2, center + (x - px) + emAscent, lineTop, forms, size, color, text, 0, 0) { Tag = tag }.WithStroke(style));
        }
        _engine.TurnBack(from, px, center, -90);
    }

    private readonly List<(float X, string Text, TextFont Leaf, ContentSlot? Tag)> _uprightGlyphs = new();

    /// <summary>Draws turned characters of an item from <paramref name="x"/> and returns where they end.</summary>
    private float DrawTurned(ComputedStyle style, string src, int start, int length, float x, float baseline, float lineTop, uint color,
        float wordSpacing, float letterSpacing, Item it)
    {
        var text = src.Substring(start, length);
        _engine.Ops.Add(new TextOp(x, baseline, lineTop, style.Font, style.TextSize, color, text, wordSpacing, letterSpacing) { Tag = it.Tag }
            .WithStroke(style));
        return x + TextWidth(style.Font, src, start, length, style.TextSize, letterSpacing);
    }

    // ------------------------------------------------------------------ text-combine-upright

    /// <summary>
    /// Adds the text of an element with text-combine-upright: all in a vertical line as one inline box one em long: the
    /// text is set across the line, centred in the em, and squeezed along its length when it is wider than one em, as
    /// Chrome does for fonts without the third- and quarter-width forms. White space in it collapses to single spaces.
    /// </summary>
    private void AddCombinedText(CollectState st, string text, ComputedStyle style, string? href, InlineDeco? deco)
    {
        var collapsed = CollapseForCombining(text, IsCollapsible(style));
        if (collapsed.Length == 0) return;
        var font = style.Font;
        float size = style.TextSize;
        font.LineMetrics(size, out float ascent, out float descent);
        float textW = TextWidth(font, collapsed, 0, collapsed.Length, size, style.LetterSpacing);
        // The em along the line and the line's height across it; the text's centre is the middle of both.
        float cx = size / 2, cy = ascent - CentralBaseline(style);
        float squeeze = textW > size && textW > 0 ? size / textW : 1;
        float textBaseline = cy + (0.5f - font.Metric(FontMetric.EmDescent)) * size;
        var op = new TextOp(cx - textW / 2, textBaseline, 0, font, size, CssColor.Fade(style.TextFillColor ?? style.Color, style.InlineFade), collapsed,
            style.WordSpacing, style.LetterSpacing) { Tag = _textTag }.WithStroke(style);
        var layer = new LayerOp
        {
            Transform = squeeze < 1 ? Matrix.Scale(squeeze, 1).Then(LayoutEngine.QuarterTurn(-90)) : LayoutEngine.QuarterTurn(-90),
            OriginX = cx,
            OriginY = cy,
        };
        if (style.Visible) layer.Ops.Add(op);
        var box = Rent(ItemKind.Box, style);
        box.Width = size;
        box.Height = ascent + descent;
        box.BoxBaseline = ascent;
        box.BoxOps = [layer];
        box.Href = href;
        box.Deco = deco;
        // A line may break before and after the combined text, as between ideographs.
        if (IsBreakable(style))
        {
            if (st.Last is { Kind: ItemKind.Text, BreakAfter: BreakAfter.None } prev) prev.BreakAfter = BreakAfter.Break;
            box.BreakAfter = BreakAfter.Break;
        }
        st.Items.Add(box);
        st.LastWasSpace = false;
    }

    /// <summary>The text with its white space collapsed to single spaces and taken off both ends, when it collapses.</summary>
    private static string CollapseForCombining(string text, bool collapse)
    {
        if (!collapse) return text.Replace('\n', ' ').Replace('\t', ' ');
        var sb = new System.Text.StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (IsSpace(c)) { space = sb.Length > 0; continue; }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
