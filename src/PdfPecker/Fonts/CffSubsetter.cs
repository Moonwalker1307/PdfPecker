using PdfPecker.Fonts.Cff;

namespace PdfPecker.Fonts;

/// <summary>
/// Shrinks a CFF font program (the outlines of an OpenType .otf font) to the glyphs a document uses. Glyph ids are
/// kept, so the charset and FDSelect tables are copied as they are; unused glyphs get an empty charstring. Subroutines
/// are kept whole. Returns null when the font has a structure this does not handle; the caller then embeds it whole.
/// </summary>
internal static partial class CffSubsetter
{
    // Top, Font and Private DICT operators.
    private const int CharsetOperator = 15, EncodingOperator = 16, CharStringsOperator = 17, PrivateOperator = 18;
    private const int SubrsOperator = 19;
    private const int FdArrayOperator = CffDict.Escape + 36, FdSelectOperator = CffDict.Escape + 37;

    // The charstring every unused glyph gets: a bare endchar.
    private static readonly byte[] EmptyCharString = { EndCharOperator };

    public static byte[]? Subset(byte[] cff, ICollection<int> keep)
    {
        try
        {
            return SubsetCore(cff, keep);
        }
        catch (Exception ex) when (FontProgram.IsFormatError(ex) || ex is InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[]? SubsetCore(byte[] data, ICollection<int> keep)
    {
        if (SourceFont.Read(data) is not { } font) return null;
        var usage = new SubroutineUsage(data, font.GlobalSubrs, font.Privates);
        var charStrings = SubsetCharStrings(font, keep, usage);

        // Subroutines no used glyph calls become empty; their numbering (and so the charstrings) stays the same.
        var localSubrs = new byte[]?[font.Privates.Count];
        for (int i = 0; i < localSubrs.Length; i++)
            if (font.Privates[i].Subrs is { } subrs) localSubrs[i] = PruneSubroutines(data, subrs, usage.LocalUsed[i]);
        var globalSubrs = PruneSubroutines(data, font.GlobalSubrs, usage.GlobalUsed);

        return Write(font, globalSubrs, charStrings, localSubrs);
    }

    /// <summary>
    /// The new CharStrings INDEX: .notdef and the kept glyphs keep their programs, whose subroutine calls are recorded,
    /// and the rest become a bare endchar.
    /// </summary>
    private static byte[] SubsetCharStrings(SourceFont font, ICollection<int> keep, SubroutineUsage usage)
    {
        int glyphCount = font.CharStrings.Count;
        var used = new bool[glyphCount];
        used[0] = true;
        foreach (int glyph in keep)
            if ((uint)glyph < (uint)glyphCount) used[glyph] = true;

        var items = new byte[glyphCount][];
        for (int glyph = 0; glyph < glyphCount; glyph++)
        {
            if (!used[glyph])
            {
                items[glyph] = EmptyCharString;
                continue;
            }
            var charString = font.CharStrings.Item(font.Data, glyph);
            items[glyph] = charString.ToArray();
            usage.MarkGlyph(charString, font.FontDictOf(glyph));
        }
        return CffIndex.Write(items);
    }

    private static byte[] PruneSubroutines(byte[] data, CffIndex subrs, bool[] used)
    {
        var items = new byte[subrs.Count][];
        for (int i = 0; i < items.Length; i++) items[i] = used[i] ? subrs.Item(data, i).ToArray() : Array.Empty<byte>();
        return CffIndex.Write(items);
    }
}
