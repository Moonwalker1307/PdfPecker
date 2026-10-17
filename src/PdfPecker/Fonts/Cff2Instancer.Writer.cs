using System.Globalization;
using System.Text;
using PdfPecker.Fonts.Cff;

namespace PdfPecker.Fonts;

/// <summary>Writing the instance as a CID-keyed CFF font.</summary>
internal static partial class Cff2Instancer
{
    // CFF 1.0 with a four-byte header and four-byte offsets.
    private static readonly byte[] CffHeader = { 1, 0, 4, 4 };

    // The registry and ordering are the first two strings of the String INDEX, so their string ids follow the 391
    // standard strings: Adobe-Identity-0, where every CID is its glyph id.
    private const int AdobeSid = 391, IdentitySid = 392, IdentitySupplement = 0;

    // Top and Font DICT operators.
    private const int RosOperator = CffDict.Escape + 30, CidCountOperator = CffDict.Escape + 34, CharsetOperator = 15;

    // The Private DICT: defaultWidthX 0 and nominalWidthX 0, so every charstring's width is written in full.
    private static readonly byte[] PrivateDictBytes = { 139, 20, 139, 21 };

    // A charset of format 2 is a range of CIDs from 1, with the number of glyphs after the first one; format 0 with
    // no entries covers a font of only .notdef. The FDSelect is format 3 with one range: every glyph uses Font DICT 0.
    private const byte CharsetRanges16 = 2, CharsetPerGlyph = 0, FdSelectRangesFormat = 3;

    // A DICT integer: one byte for -107 to 107, otherwise the five-byte form.
    private const int DictOneByteLimit = 107, DictOneByteBias = 139;

    // A DICT real is the number's characters as nibbles after the prefix byte, closed by an 0xF nibble.
    private const byte RealPrefix = 30;
    private const int DecimalPointNibble = 0xA, ExponentNibble = 0xB, NegativeExponentNibble = 0xC, MinusNibble = 0xE, EndNibble = 0xF;

    /// <summary>A CID-keyed CFF font (Adobe-Identity-0, CID = glyph id) with one Font DICT and no subroutines.</summary>
    private static byte[] WriteCff(string name, byte[][] glyphs, double[]? matrix)
    {
        int n = glyphs.Length;
        var nameIndex = CffIndex.Write(new[] { Encoding.ASCII.GetBytes(name) });
        var strings = CffIndex.Write(new[] { "Adobe"u8.ToArray(), "Identity"u8.ToArray() });
        var gsubrs = CffIndex.Write(Array.Empty<byte[]>());
        byte[] charset = n > 1 ? new byte[] { CharsetRanges16, 0, 1, (byte)((n - 2) >> 8), (byte)(n - 2) } : new byte[] { CharsetPerGlyph };
        var fdSelect = new byte[] { FdSelectRangesFormat, 0, 1, 0, 0, 0, (byte)(n >> 8), (byte)n };
        var charStrings = CffIndex.Write(glyphs);

        // Offsets are written as 5-byte numbers, so the DICTs have the same size before and after they are known.
        int topSize = CffIndex.Write(new[] { WriteTopDict(n, matrix, 0, 0, 0, 0) }).Length;
        int fdArraySize = CffIndex.Write(new[] { WriteFontDict(0) }).Length;
        int charsetOff = CffHeader.Length + nameIndex.Length + topSize + strings.Length + gsubrs.Length;
        int fdSelectOff = charsetOff + charset.Length;
        int charStringsOff = fdSelectOff + fdSelect.Length;
        int fdArrayOff = charStringsOff + charStrings.Length;
        int privateOff = fdArrayOff + fdArraySize;

        var cff = new byte[privateOff + PrivateDictBytes.Length];
        int at = 0;
        foreach (var part in new[]
        {
            CffHeader, nameIndex, CffIndex.Write(new[] { WriteTopDict(n, matrix, charsetOff, fdSelectOff, charStringsOff, fdArrayOff) }), strings,
            gsubrs, charset, fdSelect, charStrings, CffIndex.Write(new[] { WriteFontDict(privateOff) }), PrivateDictBytes,
        })
        {
            part.CopyTo(cff, at);
            at += part.Length;
        }
        return cff;
    }

    private static byte[] WriteTopDict(int glyphCount, double[]? matrix, int charsetOff, int fdSelectOff, int charStringsOff, int fdArrayOff)
    {
        var t = new List<byte>();
        DictInt(t, AdobeSid); DictInt(t, IdentitySid); DictInt(t, IdentitySupplement); CffDict.WriteOperator(t, RosOperator);
        DictInt(t, glyphCount); CffDict.WriteOperator(t, CidCountOperator);
        if (matrix != null)
        {
            foreach (var v in matrix) DictReal(t, v);
            CffDict.WriteOperator(t, FontMatrixOperator);
        }
        CffDict.WriteInt32(t, charsetOff); CffDict.WriteOperator(t, CharsetOperator);
        CffDict.WriteInt32(t, fdSelectOff); CffDict.WriteOperator(t, FdSelectOperator);
        CffDict.WriteInt32(t, charStringsOff); CffDict.WriteOperator(t, CharStringsOperator);
        CffDict.WriteInt32(t, fdArrayOff); CffDict.WriteOperator(t, FdArrayOperator);
        return t.ToArray();
    }

    private static byte[] WriteFontDict(int privateOff)
    {
        var t = new List<byte>();
        CffDict.WriteInt32(t, PrivateDictBytes.Length);
        CffDict.WriteInt32(t, privateOff);
        CffDict.WriteOperator(t, PrivateOperator);
        return t.ToArray();
    }

    private static void DictInt(List<byte> o, int v)
    {
        if (v >= -DictOneByteLimit && v <= DictOneByteLimit) o.Add((byte)(v + DictOneByteBias));
        else CffDict.WriteInt32(o, v);
    }

    // The shortest round-trip form of the number, with its exponent as the b (E) or c (E-) nibble.
    private static void DictReal(List<byte> o, double v)
    {
        o.Add(RealPrefix);
        var s = v.ToString("R", CultureInfo.InvariantCulture).Replace("E-", "c").Replace("E+", "b").Replace("E", "b");
        var nibbles = new List<int>();
        foreach (char c in s)
            nibbles.Add(c switch { '.' => DecimalPointNibble, 'b' => ExponentNibble, 'c' => NegativeExponentNibble, '-' => MinusNibble, _ => c - '0' });
        nibbles.Add(EndNibble);
        if (nibbles.Count % 2 == 1) nibbles.Add(EndNibble);
        for (int i = 0; i < nibbles.Count; i += 2) o.Add((byte)(nibbles[i] << 4 | nibbles[i + 1]));
    }
}
