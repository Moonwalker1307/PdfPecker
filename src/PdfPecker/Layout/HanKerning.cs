using System.Runtime.CompilerServices;
using PdfPecker.Fonts;
using PdfPecker.Text;

namespace PdfPecker.Layout;

/// <summary>
/// What a font tells text-spacing-trim, as Chrome's HanKerning reads it: whether the font can trim fullwidth punctuation
/// at all (it has the halt feature), which side of the em its dots, colons, semicolons and quotes are drawn on, and how
/// much its halt feature takes off each punctuation glyph.
/// </summary>
internal static class HanKerning
{
    private static readonly ConditionalWeakTable<FontProgram, FontData> Fonts = new();

    // Chrome reads the classes of the font-dependent punctuation from these glyphs: the dots (ideographic and fullwidth
    // comma and full stop), the colon, the semicolon, and the opening and closing quotes.
    private static readonly int[] Dots = [0x3001, 0x3002, 0xFF0C, 0xFF0E];
    private const int FullwidthColon = 0xFF1A, FullwidthSemicolon = 0xFF1B;
    private static readonly int[] OpenQuotes = [0x201C, 0x2018], CloseQuotes = [0x201D, 0x2019];

    /// <summary>The class of a character drawn with a font: Other when the font cannot trim punctuation.</summary>
    public static TextSpacingClass Classify(TextFont font, int cp)
    {
        var type = TextSpacingCharacters.Classify(cp);
        if (type == TextSpacingClass.Other) return type;
        var data = DataFor(font, cp);
        if (data == null) return TextSpacingClass.Other;
        return type switch
        {
            TextSpacingClass.Dot => data.Dot,
            TextSpacingClass.Colon => data.Colon,
            TextSpacingClass.Semicolon => data.Semicolon,
            TextSpacingClass.OpenQuote => data.FullwidthQuotes ? TextSpacingClass.Open : TextSpacingClass.OpenNarrow,
            TextSpacingClass.CloseQuote => data.FullwidthQuotes ? TextSpacingClass.Close : TextSpacingClass.CloseNarrow,
            _ => type,
        };
    }

    /// <summary>
    /// How much the font's halt feature takes off a character's advance, in thousandths of an em: the blank half of a
    /// fullwidth bracket or dot. 0 when the font has no such adjustment for it.
    /// </summary>
    public static float TrimUnits(TextFont font, int cp) => DataFor(font, cp)?.Trim(cp) ?? 0;

    /// <summary>The data of the font that draws a character, or null when that font cannot trim punctuation.</summary>
    private static FontData? DataFor(TextFont font, int cp)
    {
        if (font is FontChain chain) font = chain.Pick(cp);
        if (font is not EmbeddedFont embedded) return null;
        var data = Fonts.GetValue(embedded.Program, p => new FontData(p));
        return data.Applies ? data : null;
    }

    /// <summary>
    /// A font's halt lookups and the classes of its font-dependent punctuation, found from where the glyphs' ink sits
    /// in their advance.
    /// </summary>
    private sealed class FontData
    {
        private readonly FontProgram _program;
        private readonly OpenTypeLayout? _layout;
        private readonly List<int> _halt = new();
        private readonly Dictionary<int, float> _trims = new();
        private readonly GlyphRun _run = new();

        public readonly bool Applies;
        public readonly TextSpacingClass Dot, Colon, Semicolon;
        public readonly bool FullwidthQuotes;

        public FontData(FontProgram program)
        {
            _program = program;
            _layout = program.Layout;
            if (_layout == null) return;
            _halt = _layout.FeatureLookups(gpos: true, FontProgram.Tag("hani"), FontProgram.Tag("halt"));
            if (_halt.Count == 0) return;
            Applies = true;
            Dot = GroupClass(Dots);
            Colon = GroupClass([FullwidthColon]);
            Semicolon = GroupClass([FullwidthSemicolon]);
            // Quotes are fullwidth in Chinese fonts and proportional in Japanese ones; proportional ones count as narrow.
            FullwidthQuotes = GroupClass(OpenQuotes) == TextSpacingClass.Open && GroupClass(CloseQuotes) == TextSpacingClass.Close;
        }

        /// <summary>
        /// The class of a group of characters the font draws alike: the one their glyphs share, or Other when their
        /// advances or classes differ. Characters the font does not have are left out.
        /// </summary>
        private TextSpacingClass GroupClass(int[] chars)
        {
            TextSpacingClass? type = null;
            int advance = 0;
            foreach (int cp in chars)
            {
                int gid = _program.GlyphId(cp);
                if (gid == 0) continue;
                int a = _program.Advance(gid);
                var t = GlyphClass(gid, a / 2f / _program.UnitsPerEm);
                if (type == null)
                {
                    type = t;
                    advance = a;
                }
                else if (a != advance || t != type) return TextSpacingClass.Other;
            }
            return type ?? TextSpacingClass.Other;
        }

        /// <summary>
        /// Where a glyph's ink sits in its advance: all in the left half (closing), all in the right half (opening),
        /// or narrow and in the middle. A glyph without ink counts as closing, as its empty box does in Chrome.
        /// </summary>
        private TextSpacingClass GlyphClass(int gid, float halfEm)
        {
            if (!GlyphInk.Extent(_program, gid, out float left, out float right)) left = right = 0;
            if (right <= halfEm) return TextSpacingClass.Close;
            if (left >= halfEm) return TextSpacingClass.Open;
            if (right - left <= halfEm && left >= halfEm / 2) return TextSpacingClass.Middle;
            return TextSpacingClass.Other;
        }

        /// <summary>What halt takes off the advance of a character's glyph, in thousandths of an em.</summary>
        public float Trim(int cp)
        {
            int gid = _program.GlyphId(cp);
            if (gid == 0) return 0;
            lock (_trims)
            {
                if (_trims.TryGetValue(gid, out float trim)) return trim;
                trim = HaltAdvance(gid) * -1000f / _program.UnitsPerEm;
                // Only a trim that leaves the glyph some of its advance is one; anything else is a damaged font.
                if (!(trim > 0 && trim < _program.Advance1000(gid))) trim = 0;
                _trims[gid] = trim;
                return trim;
            }
        }

        /// <summary>The change halt makes to a glyph's advance, in font units.</summary>
        private int HaltAdvance(int gid)
        {
            var run = _run;
            run.Clear();
            run.Add(new GlyphInfo { Gid = gid, Length = 1, Mask = 1, Props = _layout!.GlyphProps(gid) });
            int ligatureId = 1, operations = OpenTypeLayout.OperationBudget(1);
            try
            {
                foreach (int lookup in _halt)
                    _layout.ApplyLookup(run, gpos: true, lookup, 1, rtl: false, ref ligatureId, ref operations, OpenTypeLayout.MaxRunLength(1));
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
            {
                return 0;
            }
            return run.Count == 1 ? run[0].XAdvance : 0;
        }
    }
}
