using PdfPecker.Css;
using PdfPecker.Text;

namespace PdfPecker.Layout;

/// <summary>
/// Bidirectional text in inline layout: the embedding levels of a block's inline content (UAX #9 over its text, with
/// direction and unicode-bidi as CSS maps them to bidi controls), and the visual order of each line.
/// </summary>
internal sealed partial class LayoutEngine
{
    // The bidi controls that open and close an inline element's unicode-bidi (CSS Writing Modes 3, §2.4.2).
    private static (string? Open, string? Close) BidiControls(ComputedStyle s) => s.UnicodeBidi switch
    {
        UnicodeBidi.Embed => (s.Rtl ? "‫" : "‪", "‬"),
        UnicodeBidi.Isolate => (s.Rtl ? "⁧" : "⁦", "⁩"),
        UnicodeBidi.BidiOverride => (s.Rtl ? "‮" : "‭", "‬"),
        UnicodeBidi.IsolateOverride => (s.Rtl ? "⁧‮" : "⁦‭", "‬⁩"),
        UnicodeBidi.Plaintext => ("⁨", "⁩"),
        _ => (null, null),
    };

    private void AddBidiControl(CollectState st, ComputedStyle s, string controls)
    {
        var it = Rent(ItemKind.Bidi, s);
        it.Source = controls;
        it.Length = controls.Length;
        st.Items.Add(it);
    }

    private readonly List<BidiClass> _bidiTypes = new();
    private readonly List<int> _bidiCps = new();
    private readonly List<int> _bidiItemStart = new();

    /// <summary>
    /// Gives every item its embedding level and paragraph level, splitting words whose characters get different
    /// levels. Content that is left to right throughout keeps level 0 and is not touched.
    /// </summary>
    private void ResolveBidi(List<Item> items, ComputedStyle block)
    {
        bool needed = block.Rtl || block.UnicodeBidi == UnicodeBidi.Plaintext;
        for (int i = 0; i < items.Count && !needed; i++)
        {
            var it = items[i];
            if (it.Kind == ItemKind.Bidi) needed = true;
            else if (it.Kind == ItemKind.Text)
                for (int k = it.Start, end = it.Start + it.Length; k < end; k++)
                    if (it.Source[k] >= 0x0590 && UnicodeTables.NeedsBidi(it.Source[k])) { needed = true; break; }
        }
        if (!needed) return;

        // One bidi unit per character of text, and one per other item.
        var types = _bidiTypes;
        var cps = _bidiCps;
        var starts = _bidiItemStart;
        types.Clear();
        cps.Clear();
        starts.Clear();
        foreach (var it in items)
        {
            starts.Add(types.Count);
            switch (it.Kind)
            {
                case ItemKind.Text:
                case ItemKind.Bidi:
                    for (int k = it.Start, end = it.Start + it.Length; k < end; k++)
                    {
                        int cp = it.Source[k];
                        if (char.IsHighSurrogate(it.Source[k]) && k + 1 < end && char.IsLowSurrogate(it.Source[k + 1]))
                        {
                            cp = char.ConvertToUtf32(it.Source[k], it.Source[k + 1]);
                            var t = UnicodeTables.Bidi(cp);
                            types.Add(t);
                            cps.Add(cp);
                            types.Add(t);
                            cps.Add(0);
                            k++;
                            continue;
                        }
                        types.Add(UnicodeTables.Bidi(cp));
                        cps.Add(cp);
                    }
                    break;
                case ItemKind.Space:
                    types.Add(BidiClass.WS);
                    cps.Add(' ');
                    break;
                case ItemKind.Break:
                    types.Add(BidiClass.B);
                    cps.Add('\n');
                    break;
                case ItemKind.Image:
                case ItemKind.Box:
                case ItemKind.Spacer:
                    types.Add(BidiClass.ON);
                    cps.Add(0xFFFC);
                    break;
                default: // anchors, floats and positioned boxes take no part
                    types.Add(BidiClass.BN);
                    cps.Add(0);
                    break;
            }
        }
        starts.Add(types.Count);

        // Paragraphs end at forced line breaks; each is resolved on its own, with the block's direction (or, for
        // unicode-bidi: plaintext, its first strong character's).
        var levels = new byte[types.Count];
        var paras = new byte[types.Count];
        var typeArray = types.ToArray();
        int segStart = 0;
        for (int u = 0; u <= typeArray.Length; u++)
        {
            if (u < typeArray.Length && typeArray[u] != BidiClass.B) continue;
            int len = u - segStart;
            int para = block.Rtl ? 1 : 0;
            if (len > 0)
            {
                var segTypes = typeArray.AsSpan(segStart, len).ToArray();
                if (block.UnicodeBidi == UnicodeBidi.Plaintext && HasStrong(segTypes)) para = Bidi.FirstStrongLevel(segTypes);
                var segLevels = Bidi.ResolveLevels(segTypes, cps.GetRange(segStart, len).ToArray(), para);
                segLevels.CopyTo(levels, segStart);
            }
            for (int k = segStart; k < Math.Min(u + 1, levels.Length); k++) paras[k] = (byte)para;
            if (u < levels.Length) levels[u] = (byte)para;
            segStart = u + 1;
        }

        // Items take their characters' level; a word whose characters differ is split where the level changes.
        var result = new List<Item>(items.Count + 4);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            int u0 = starts[i], u1 = starts[i + 1];
            it.Para = paras[Math.Min(u0, paras.Length - 1)];
            if (u1 <= u0)
            {
                it.Level = it.Para;
                result.Add(it);
                continue;
            }
            it.Level = levels[u0];
            int split = -1;
            if (it.Kind == ItemKind.Text)
                for (int u = u0 + 1; u < u1; u++)
                    if (levels[u] != levels[u0]) { split = u; break; }
            if (split < 0)
            {
                result.Add(it);
                continue;
            }
            var font = it.Style.Font;
            float size = it.Style.TextSize, ls = it.Style.LetterSpacing;
            int from = u0;
            while (from < u1)
            {
                int to = from + 1;
                while (to < u1 && levels[to] == levels[from]) to++;
                int offset = from - u0, length = to - from;
                float w = font.MeasureUnits(it.Source, it.Start + offset, length) * size / 1000f + ls * length;
                var piece = it.Slice(offset, length, w);
                piece.Level = levels[from];
                // Only the end of the word keeps its break opportunity.
                if (to < u1) piece.BreakAfter = BreakAfter.None;
                result.Add(piece);
                from = to;
            }
        }
        items.Clear();
        items.AddRange(result);
    }

    /// <summary>A list marker's text as drawn left to right in a right-to-left list.</summary>
    private static string VisualMarkerText(string text)
    {
        var types = new BidiClass[text.Length];
        var cps = new int[text.Length];
        for (int i = 0; i < text.Length; i++) { cps[i] = text[i]; types[i] = UnicodeTables.Bidi(text[i]); }
        var levels = Bidi.ResolveLevels(types, cps, 1);
        Bidi.ResetWhitespace(types, levels, 1);
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (int k in Bidi.VisualOrder(levels))
            sb.Append((levels[k] & 1) != 0 && UnicodeTables.Mirror(text[k]) is int m && m < 0x10000 ? (char)m : text[k]);
        return sb.ToString();
    }

    private static bool HasStrong(BidiClass[] types)
    {
        foreach (var t in types)
            if (t is BidiClass.L or BidiClass.R or BidiClass.AL) return true;
        return false;
    }

    private byte[] _lineLevels = new byte[32];

    /// <summary>
    /// Puts a line's items in visual order (rule L2), after giving trailing spaces and controls the paragraph level
    /// (rule L1). Returns whether the paragraph runs right to left. Lines of left-to-right content stay as they are.
    /// </summary>
    private static bool ReorderLine(List<Item> items, ref byte[] buffer)
    {
        bool any = false;
        foreach (var it in items)
            if (it.Level != 0 || it.Para != 0) { any = true; break; }
        if (!any || items.Count == 0) return false;
        int para = items[0].Para;
        for (int k = items.Count - 1; k >= 0; k--)
        {
            var it = items[k];
            if (it.Kind is not (ItemKind.Space or ItemKind.Bidi or ItemKind.Anchor or ItemKind.Absolute or ItemKind.Float or ItemKind.Break)) break;
            it.Level = (byte)para;
        }
        if (buffer.Length < items.Count) buffer = new byte[items.Count * 2];
        for (int k = 0; k < items.Count; k++) buffer[k] = items[k].Level;
        var order = Bidi.VisualOrder(buffer.AsSpan(0, items.Count));
        var copy = items.ToArray();
        for (int k = 0; k < order.Length; k++) items[k] = copy[order[k]];
        return (para & 1) != 0;
    }
}
