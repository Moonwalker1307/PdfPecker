using PdfPecker.Fonts.Cff;

namespace PdfPecker.Fonts;

/// <summary>Writing the subset font: the parts of the source in their order, with the DICTs' offsets rewritten.</summary>
internal static partial class CffSubsetter
{
    // Room for a typical Top DICT and Font DICT, so the encoders rarely grow their buffers.
    private const int TopDictCapacity = 128, FontDictCapacity = 32;

    /// <summary>Where the parts that the Top DICT points at start in the subset font.</summary>
    private readonly record struct TopOffsets(int Charset, int FdSelect, int CharStrings, int FdArray, int Private);

    /// <summary>
    /// Lays out and writes the font: header, Name, Top DICT, Strings, Global Subrs, charset, FDSelect, CharStrings,
    /// FDArray and the Private DICTs, each with its local subroutines after it.
    /// </summary>
    private static byte[] Write(SourceFont font, byte[] globalSubrs, byte[] charStrings, byte[]?[] localSubrs)
    {
        var data = font.Data;
        // Offsets are written as five-byte numbers, so the DICTs have the same size before and after they are known.
        int topIndexLength = CffIndex.Write(new[] { EncodeTopDict(font, default) }).Length;
        int at = font.HeaderLength + IndexLength(font.Names) + topIndexLength + IndexLength(font.Strings) + globalSubrs.Length;
        int charsetAt = at;
        at += font.Charset?.Length ?? 0;
        int fdSelectAt = at;
        at += font.FdSelect?.Length ?? 0;
        int charStringsAt = at;
        at += charStrings.Length;
        int fdArrayAt = at;

        var privateAt = new int[font.Privates.Count];
        byte[]? fdArray = null;
        if (font.FontDicts is { } fontDicts)
        {
            // The Font DICTs point at their Private DICTs, which follow the FDArray; its size does not depend on them.
            at += CffIndex.Write(EncodeFontDicts(font, fontDicts, privateAt)).Length;
            for (int i = 0; i < privateAt.Length; i++)
            {
                privateAt[i] = at;
                at += PrivateLength(font, localSubrs, i);
            }
            fdArray = CffIndex.Write(EncodeFontDicts(font, fontDicts, privateAt));
        }
        else
        {
            privateAt[0] = at;
            at += PrivateLength(font, localSubrs, 0);
        }

        var offsets = new TopOffsets(charsetAt, fdSelectAt, charStringsAt, fdArrayAt, privateAt.Length > 0 ? privateAt[0] : 0);
        var topIndex = CffIndex.Write(new[] { EncodeTopDict(font, offsets) });

        var cff = new byte[at];
        int written = 0;
        Append(data.AsSpan(0, font.HeaderLength));
        Append(IndexBytes(data, font.Names));
        Append(topIndex);
        Append(IndexBytes(data, font.Strings));
        Append(globalSubrs);
        if (font.Charset is { } charset) Append(charset.Span);
        if (font.FdSelect is { } fdSelect) Append(fdSelect.Span);
        Append(charStrings);
        if (fdArray != null) Append(fdArray);
        for (int i = 0; i < font.Privates.Count; i++)
        {
            Append(font.Privates[i].Prefix(data));
            if (localSubrs[i] is { } subrs) Append(subrs);
        }
        if (written != at) throw new InvalidOperationException("CFF layout mismatch.");
        return cff;

        void Append(ReadOnlySpan<byte> part)
        {
            part.CopyTo(cff.AsSpan(written));
            written += part.Length;
        }
    }

    private static int IndexLength(CffIndex index) => index.End - index.Start;

    private static ReadOnlySpan<byte> IndexBytes(byte[] data, CffIndex index) => data.AsSpan(index.Start, IndexLength(index));

    // A Private DICT keeps its bytes up to the local subroutines, which follow them pruned.
    private static int PrivateLength(SourceFont font, byte[]?[] localSubrs, int i)
        => font.Privates[i].PrefixLength + (localSubrs[i]?.Length ?? 0);

    /// <summary>
    /// The Top DICT with its offsets rewritten. The Encoding is left out, because OpenType CFF fonts are encoded
    /// through the cmap and CIDs are glyph ids here; a CID-keyed font's Private DICTs belong to its Font DICTs.
    /// </summary>
    private static byte[] EncodeTopDict(SourceFont font, TopOffsets at)
    {
        var dict = new List<byte>(TopDictCapacity);
        foreach (var entry in font.Top.Entries)
        {
            switch (entry.Operator)
            {
                case EncodingOperator:
                case PrivateOperator when font.IsCidKeyed:
                    break;
                case CharsetOperator when font.Charset == null:
                    dict.AddRange(entry.Encoded(font.Data));
                    break;
                case CharsetOperator:
                    WriteOffsetEntry(dict, CharsetOperator, at.Charset);
                    break;
                case FdSelectOperator:
                    WriteOffsetEntry(dict, FdSelectOperator, at.FdSelect);
                    break;
                case CharStringsOperator:
                    WriteOffsetEntry(dict, CharStringsOperator, at.CharStrings);
                    break;
                case FdArrayOperator:
                    WriteOffsetEntry(dict, FdArrayOperator, at.FdArray);
                    break;
                case PrivateOperator:
                    WritePrivateEntry(dict, font.Privates[0].DictSize, at.Private);
                    break;
                default:
                    dict.AddRange(entry.Encoded(font.Data));
                    break;
            }
        }
        return dict.ToArray();
    }

    // The Font DICTs, each pointing at its Private DICT.
    private static byte[][] EncodeFontDicts(SourceFont font, List<Dict> fontDicts, int[] privateAt)
    {
        var encoded = new byte[fontDicts.Count][];
        for (int i = 0; i < encoded.Length; i++)
        {
            var dict = new List<byte>(FontDictCapacity);
            foreach (var entry in fontDicts[i].Entries)
            {
                if (entry.Operator == PrivateOperator) WritePrivateEntry(dict, font.Privates[i].DictSize, privateAt[i]);
                else dict.AddRange(entry.Encoded(font.Data));
            }
            encoded[i] = dict.ToArray();
        }
        return encoded;
    }

    private static void WriteOffsetEntry(List<byte> dict, int op, int offset)
    {
        CffDict.WriteInt32(dict, offset);
        CffDict.WriteOperator(dict, op);
    }

    private static void WritePrivateEntry(List<byte> dict, int size, int offset)
    {
        CffDict.WriteInt32(dict, size);
        CffDict.WriteInt32(dict, offset);
        CffDict.WriteOperator(dict, PrivateOperator);
    }
}
