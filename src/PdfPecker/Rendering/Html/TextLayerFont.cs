using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PdfPecker.Fonts;

namespace PdfPecker.Rendering.Html;

/// <summary>
/// The web font of the HTML text layer: two empty glyphs, one an em wide for every character that takes room and one
/// of no width for combining marks and invisible format characters. The width of any text in it is then known before
/// the browser lays it out (<see cref="Ems"/>), so a word can be stretched with <c>scaleX</c> to cover exactly the
/// glyphs the page image shows, without measuring text in the browser. The glyphs draw nothing, so the layer stays
/// invisible whatever colour it is given, while selection and search still find the text.
/// </summary>
/// <remarks>
/// <para>
/// The character map is a few format 13 groups, each mapping a range of code points to one glyph, which is how blank
/// fonts such as Adobe Blank cover all of Unicode in a small file.
/// </para>
/// <para>
/// Browsers shape text with HarfBuzz, which would otherwise change the glyph count: it composes a letter and its
/// accent into one glyph when the font has the composed character, as this one has every character; it gives marks no
/// width or not depending on the script; and for Arabic in a font without Arabic features it substitutes presentation
/// forms and ligatures itself, as it puts Thai and Lao marks on private-use characters. Marks having no width in the
/// font itself makes composing change nothing, the GDEF classes make every shaper treat the glyphs alike, and GSUB
/// features for those scripts that do nothing turn its own shaping of them off.
/// </para>
/// </remarks>
internal static class TextLayerFont
{
    public const int UnitsPerEm = 1000;

    /// <summary>The ascent as a fraction of the font size; the baseline sits this far below the top of a line box.</summary>
    public const double Ascent = 0.8;

    private const int WideGlyph = 1, ZeroGlyph = 2;

    private static string? _dataUri;

    /// <summary>The font as a <c>data:</c> URI for an <c>@font-face</c> rule.</summary>
    public static string DataUri => _dataUri ??= "data:font/ttf;base64," + Convert.ToBase64String(Build());

    /// <summary>The width of <paramref name="text"/> in this font, in ems.</summary>
    public static int Ems(string text)
    {
        int ems = 0;
        foreach (var rune in text.EnumerateRunes())
            if (!IsZeroWidth(rune.Value)) ems++;
        return ems;
    }

    /// <summary>
    /// Combining marks, and the format characters and other default ignorable code points that shaping hides, take no
    /// room; everything else, spaces included, is one em.
    /// </summary>
    private static bool IsZeroWidth(int cp)
    {
        if (cp is (>= 0xD800 and <= 0xDFFF) or > 0x10FFFF) return false;
        if (cp is 0x115F or 0x1160 or 0x3164 or 0xFFA0 or (>= 0xE0000 and <= 0xE0FFF)) return true;
        return CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format;
    }

    internal static byte[] Build()
    {
        short ascent = (short)(Ascent * UnitsPerEm), descent = (short)(UnitsPerEm - ascent);
        var tables = new List<(uint Tag, byte[] Data)>
        {
            (Tag("GDEF"), Gdef()),
            (Tag("GSUB"), Gsub()),
            (Tag("OS/2"), Os2(ascent, descent)),
            (Tag("cmap"), Cmap()),
            // The glyphs are empty; a glyf table of one empty word keeps sanitizers that refuse empty tables content.
            (Tag("glyf"), new byte[4]),
            (Tag("head"), Head()),
            (Tag("hhea"), Hhea(ascent, descent)),
            (Tag("hmtx"), new byte[] { UnitsPerEm >> 8, UnitsPerEm & 255, 0, 0, UnitsPerEm >> 8, UnitsPerEm & 255, 0, 0, 0, 0, 0, 0 }),
            (Tag("loca"), new byte[8]),
            (Tag("maxp"), Maxp()),
            (Tag("name"), Name()),
            (Tag("post"), Post()),
        };
        return SfntBuilder.Build(0x00010000, tables);
    }

    private static uint Tag(string tag) => (uint)tag[0] << 24 | (uint)tag[1] << 16 | (uint)tag[2] << 8 | tag[3];

    private static byte[] Head()
    {
        var b = new byte[54];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x00010000); // version
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], 0x00010000); // fontRevision
        BinaryPrimitives.WriteUInt32BigEndian(s[12..], 0x5F0F3CF5); // magicNumber
        BinaryPrimitives.WriteUInt16BigEndian(s[16..], 0x000B); // flags: baseline and left sidebearing at 0, integer scaling
        BinaryPrimitives.WriteUInt16BigEndian(s[18..], UnitsPerEm);
        // Created and modified stay 0, so the output does not depend on the clock.
        BinaryPrimitives.WriteUInt16BigEndian(s[46..], 8); // lowestRecPPEM
        BinaryPrimitives.WriteInt16BigEndian(s[48..], 2); // fontDirectionHint
        return b; // indexToLocFormat 0: short offsets
    }

    private static byte[] Hhea(short ascent, short descent)
    {
        var b = new byte[36];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(s[4..], ascent);
        BinaryPrimitives.WriteInt16BigEndian(s[6..], (short)-descent);
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], UnitsPerEm); // advanceWidthMax
        BinaryPrimitives.WriteInt16BigEndian(s[18..], 1); // caretSlopeRise
        BinaryPrimitives.WriteUInt16BigEndian(s[34..], 3); // numberOfHMetrics
        return b;
    }

    private static byte[] Maxp()
    {
        var b = new byte[32];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], 3); // numGlyphs
        BinaryPrimitives.WriteUInt16BigEndian(s[14..], 1); // maxZones
        return b;
    }

    private static byte[] Os2(short ascent, short descent)
    {
        var b = new byte[96];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s, 4); // version
        BinaryPrimitives.WriteInt16BigEndian(s[2..], UnitsPerEm); // xAvgCharWidth
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], 400); // usWeightClass
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], 5); // usWidthClass
        "PDPK"u8.CopyTo(s[58..]); // achVendID
        BinaryPrimitives.WriteUInt16BigEndian(s[62..], 0x00C0); // fsSelection: regular, use typo metrics
        BinaryPrimitives.WriteUInt16BigEndian(s[64..], 0x0000); // usFirstCharIndex
        BinaryPrimitives.WriteUInt16BigEndian(s[66..], 0xFFFF); // usLastCharIndex
        BinaryPrimitives.WriteInt16BigEndian(s[68..], ascent); // sTypoAscender
        BinaryPrimitives.WriteInt16BigEndian(s[70..], (short)-descent); // sTypoDescender
        BinaryPrimitives.WriteUInt16BigEndian(s[74..], (ushort)ascent); // usWinAscent
        BinaryPrimitives.WriteUInt16BigEndian(s[76..], (ushort)descent); // usWinDescent
        BinaryPrimitives.WriteUInt32BigEndian(s[78..], 1); // ulCodePageRange1: Latin 1
        BinaryPrimitives.WriteUInt16BigEndian(s[92..], 32); // usBreakChar
        return b;
    }

    private static byte[] Cmap()
    {
        // Runs of code points that map to the same glyph, from U+0000 to U+10FFFF.
        var groups = new List<(int Start, int End, int Glyph)>();
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            int glyph = IsZeroWidth(cp) ? ZeroGlyph : WideGlyph;
            if (groups.Count > 0 && groups[^1].Glyph == glyph) groups[^1] = (groups[^1].Start, cp, glyph);
            else groups.Add((cp, cp, glyph));
        }
        // Two encoding records, Unicode full repertoire and Windows UCS-4, sharing one format 13 subtable.
        const int SubtableOffset = 4 + 2 * 8;
        int length = 16 + 12 * groups.Count;
        var b = new byte[SubtableOffset + length];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], 2);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], 0); // platform Unicode
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], 6); // full repertoire
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], SubtableOffset);
        BinaryPrimitives.WriteUInt16BigEndian(s[12..], 3); // platform Windows
        BinaryPrimitives.WriteUInt16BigEndian(s[14..], 10); // UCS-4
        BinaryPrimitives.WriteUInt32BigEndian(s[16..], SubtableOffset);
        var t = s[SubtableOffset..];
        BinaryPrimitives.WriteUInt16BigEndian(t, 13);
        BinaryPrimitives.WriteUInt32BigEndian(t[4..], (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(t[12..], (uint)groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            var g = t[(16 + 12 * i)..];
            BinaryPrimitives.WriteUInt32BigEndian(g, (uint)groups[i].Start);
            BinaryPrimitives.WriteUInt32BigEndian(g[4..], (uint)groups[i].End);
            BinaryPrimitives.WriteUInt32BigEndian(g[8..], (uint)groups[i].Glyph);
        }
        return b;
    }

    /// <summary>Glyph classes: the wide glyph is a base glyph, the zero-width one a mark.</summary>
    private static byte[] Gdef()
    {
        var b = new byte[12 + 10];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], 12); // glyphClassDefOffset
        var c = s[12..];
        BinaryPrimitives.WriteUInt16BigEndian(c, 1); // ClassDef format 1
        BinaryPrimitives.WriteUInt16BigEndian(c[2..], WideGlyph); // startGlyphID
        BinaryPrimitives.WriteUInt16BigEndian(c[4..], 2); // glyphCount
        BinaryPrimitives.WriteUInt16BigEndian(c[6..], 1); // base
        BinaryPrimitives.WriteUInt16BigEndian(c[8..], 3); // mark
        return b;
    }

    /// <summary>
    /// The Arabic joining features, and the required ligatures, with no lookups, for the Arabic, Thai and Lao scripts:
    /// present, so browsers do not shape Arabic themselves or move Thai and Lao marks to private-use glyphs, and doing
    /// nothing, so every character stays one glyph.
    /// </summary>
    private static byte[] Gsub()
    {
        string[] features = { "fina", "init", "isol", "medi", "rlig" };
        string[] scripts = { "DFLT", "arab", "lao ", "thai" };
        // A LangSys that lists every feature, shared by both scripts.
        int langSysLength = 6 + 2 * features.Length;
        int scriptListLength = 2 + 6 * scripts.Length + 4 + langSysLength; // one Script table, then its LangSys
        int featureListLength = 2 + 6 * features.Length + 4; // one empty Feature table shared by all
        const int Header = 10;
        int scriptList = Header, featureList = scriptList + scriptListLength, lookupList = featureList + featureListLength;
        var b = new byte[lookupList + 2];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], (ushort)scriptList);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], (ushort)featureList);
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], (ushort)lookupList);

        var sl = s[scriptList..];
        BinaryPrimitives.WriteUInt16BigEndian(sl, (ushort)scripts.Length);
        int script = 2 + 6 * scripts.Length;
        for (int i = 0; i < scripts.Length; i++)
        {
            Encoding.ASCII.GetBytes(scripts[i]).CopyTo(sl[(2 + 6 * i)..]);
            BinaryPrimitives.WriteUInt16BigEndian(sl[(6 + 6 * i)..], (ushort)script);
        }
        BinaryPrimitives.WriteUInt16BigEndian(sl[script..], 4); // defaultLangSys, right after the Script table
        var ls = sl[(script + 4)..];
        BinaryPrimitives.WriteUInt16BigEndian(ls[2..], 0xFFFF); // no required feature
        BinaryPrimitives.WriteUInt16BigEndian(ls[4..], (ushort)features.Length);
        for (int i = 0; i < features.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(ls[(6 + 2 * i)..], (ushort)i);

        var fl = s[featureList..];
        BinaryPrimitives.WriteUInt16BigEndian(fl, (ushort)features.Length);
        int feature = 2 + 6 * features.Length;
        for (int i = 0; i < features.Length; i++)
        {
            Encoding.ASCII.GetBytes(features[i]).CopyTo(fl[(2 + 6 * i)..]);
            BinaryPrimitives.WriteUInt16BigEndian(fl[(6 + 6 * i)..], (ushort)feature);
        }
        return b; // the Feature table and the LookupList are all zeros: no parameters, no lookups
    }

    private static byte[] Name()
    {
        // Family, subfamily, unique ID, full name and PostScript name, in the Windows Unicode encoding.
        string[] names = { "PdfPecker Text Layer", "Regular", "PdfPecker Text Layer", "PdfPecker Text Layer", "PdfPeckerTextLayer" };
        int[] ids = { 1, 2, 3, 4, 6 };
        var strings = names.Select(Encoding.BigEndianUnicode.GetBytes).ToArray();
        int header = 6 + 12 * names.Length;
        var b = new byte[header + strings.Sum(x => x.Length)];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)names.Length);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], (ushort)header);
        int offset = 0;
        for (int i = 0; i < names.Length; i++)
        {
            var r = s[(6 + 12 * i)..];
            BinaryPrimitives.WriteUInt16BigEndian(r, 3); // Windows
            BinaryPrimitives.WriteUInt16BigEndian(r[2..], 1); // Unicode BMP
            BinaryPrimitives.WriteUInt16BigEndian(r[4..], 0x0409); // English (United States)
            BinaryPrimitives.WriteUInt16BigEndian(r[6..], (ushort)ids[i]);
            BinaryPrimitives.WriteUInt16BigEndian(r[8..], (ushort)strings[i].Length);
            BinaryPrimitives.WriteUInt16BigEndian(r[10..], (ushort)offset);
            strings[i].CopyTo(b, header + offset);
            offset += strings[i].Length;
        }
        return b;
    }

    private static byte[] Post()
    {
        var b = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(b, 0x00030000); // version 3: no glyph names
        return b;
    }
}
