using System.Buffers.Binary;
using PdfPecker.Fonts.Cff;

namespace PdfPecker.Fonts;

/// <summary>
/// Turns a CFF2 (PostScript-flavoured variable) font into a static CFF table at one instance. Every glyph's
/// charstring is run with its blends resolved and subroutines expanded, and written again as plain moveto, lineto and
/// curveto operators in a CID-keyed CFF (version 1) font, which PDF readers and the rest of this library understand;
/// few PDF readers can draw CFF2. Hints are left out, as the TrueType instances leave out their instructions.
/// </summary>
internal static partial class Cff2Instancer
{
    public sealed record Result(byte[] Cff, int[] Advances, short[] Lsbs, (short XMin, short YMin, short XMax, short YMax)?[] Boxes);

    // The CFF2 header: major and minor version, header size and the length of the top DICT, which follows it. The
    // global subroutines come right after the top DICT.
    private const int Cff2MajorVersion = 2, MinHeaderLength = 5;
    private const int HeaderSizeField = 2, TopDictLengthField = 3;

    // DICT operators.
    private const int CharStringsOperator = 17, PrivateOperator = 18, SubrsOperator = 19, VsindexOperator = 22, VstoreOperator = 24;
    private const int FontMatrixOperator = CffDict.Escape + 7, FdArrayOperator = CffDict.Escape + 36, FdSelectOperator = CffDict.Escape + 37;
    private const int FontMatrixLength = 6;

    // The variation store is preceded by its length.
    private const int VstoreLengthLength = 2;

    // FDSelect: format 0 has one Font DICT number per glyph; formats 3 and 4 have ranges of glyphs, each a first
    // glyph and a Font DICT number, closed by a sentinel glyph. Format 4 has 32-bit glyph and 16-bit FD numbers.
    private const int FdSelectPerGlyph = 0, FdSelectRanges = 3, FdSelectWideRanges = 4;
    private const int FdSelectFormatLength = 1;
    private const int RangeCountLength = 2, RangeLength = 3, RangeFdField = 2;
    private const int WideRangeCountLength = 4, WideRangeLength = 6, WideRangeFdField = 4;
    private const int MaxFdIndex = byte.MaxValue;

    // HVAR: the offsets of its variation store and of the advance width mapping.
    private const int HvarMinimumLength = 20, HvarStoreField = 4, HvarAdvanceMapField = 8;

    /// <summary>A Font DICT's Private DICT: its local subroutines and its default variation store subtable.</summary>
    private readonly record struct FontDict(CffIndex Subrs, int VsIndex);

    public static Result Instance(FontProgram p, float[] coords, string fontName)
    {
        var t = p.Table("CFF2")!.Value;
        var d = p.Data.AsSpan(t.Offset, t.Length).ToArray();
        if (d.Length < MinHeaderLength || d[0] != Cff2MajorVersion) throw new FormatException("Not a CFF2 table.");
        int headerSize = d[HeaderSizeField], topLength = d[TopDictLengthField] << 8 | d[TopDictLengthField + 1];
        if (headerSize + topLength > d.Length) throw new FormatException("Bad CFF2 header.");
        var top = CffDict.ParseCff2(d, headerSize, headerSize + topLength, null);
        int gsubrPos = headerSize + topLength;
        var gsubrs = CffIndex.ReadCff2(d, ref gsubrPos);
        var store = ReadStore(d, top, coords);

        if (!top.TryGetValue(CharStringsOperator, out var cs) || cs.Length == 0) throw new FormatException("CFF2 without CharStrings.");
        int csPos = (int)cs[0];
        var charStrings = CffIndex.ReadCff2(d, ref csPos);
        int n = charStrings.Count;
        var fontDicts = ReadFontDicts(d, top, store);
        var fdOf = new byte[n];
        if (top.TryGetValue(FdSelectOperator, out var fdSelect) && fdSelect.Length > 0) ReadFdSelect(d, (int)fdSelect[0], fdOf);
        var advanceVariations = AdvanceVariations.FromHvar(p, coords);

        var glyphs = new byte[n][];
        var advances = new int[n];
        var lsbs = new short[n];
        var boxes = new (short, short, short, short)?[n];
        var run = new Charstring(d, gsubrs, store);
        for (int g = 0; g < n; g++)
        {
            int advance = (int)MathF.Round(p.Advance(g) + (advanceVariations?.Delta(g) ?? 0));
            advances[g] = Math.Max(0, advance);
            var fd = fontDicts[fdOf[g] < fontDicts.Count ? fdOf[g] : 0];
            var (s, e) = charStrings.Range(g);
            run.Run(s, e, fd.Subrs, fd.VsIndex);
            glyphs[g] = run.Encode(advances[g], out var box);
            boxes[g] = box;
            // The left side bearing is the outline's own.
            lsbs[g] = box?.Item1 ?? 0;
        }

        double[]? matrix = top.TryGetValue(FontMatrixOperator, out var fm) && fm.Length == FontMatrixLength ? fm : null;
        return new Result(WriteCff(fontName, glyphs, matrix), advances, lsbs, boxes);
    }

    // ---- Reading CFF2 ----

    // The store that blends refer to, evaluated at the instance; it ends with its declared length or the table.
    private static VariationStore? ReadStore(byte[] d, Dictionary<int, double[]> top, float[] coords)
    {
        if (!top.TryGetValue(VstoreOperator, out var vs) || vs.Length == 0) return null;
        int o = (int)vs[0];
        if (o + VstoreLengthLength > d.Length) throw new FormatException("Bad CFF2 vstore.");
        int start = o + VstoreLengthLength;
        return new VariationStore(d, start, Math.Min(d.Length, start + (d[o] << 8 | d[o + 1])), coords);
    }

    // Font DICTs: each has a Private DICT with its local subroutines and default vsindex. A font without them has one
    // with neither.
    private static List<FontDict> ReadFontDicts(byte[] d, Dictionary<int, double[]> top, VariationStore? store)
    {
        var fontDicts = new List<FontDict>();
        if (top.TryGetValue(FdArrayOperator, out var fdArrayOff) && fdArrayOff.Length > 0)
        {
            int pos = (int)fdArrayOff[0];
            var fdArray = CffIndex.ReadCff2(d, ref pos);
            for (int i = 0; i < fdArray.Count; i++)
            {
                var (s, e) = fdArray.Range(i);
                fontDicts.Add(ReadPrivateDict(d, CffDict.ParseCff2(d, s, e, null), store));
            }
        }
        if (fontDicts.Count == 0) fontDicts.Add(default);
        return fontDicts;
    }

    private static FontDict ReadPrivateDict(byte[] d, Dictionary<int, double[]> fontDict, VariationStore? store)
    {
        if (!fontDict.TryGetValue(PrivateOperator, out var priv) || priv.Length < 2) return default;
        int size = (int)priv[0], off = (int)priv[1];
        if (off < 0 || size < 0 || off + size > d.Length) throw new FormatException("Bad CFF2 Private DICT.");
        var privateDict = CffDict.ParseCff2(d, off, off + size, store);
        int vsindex = privateDict.TryGetValue(VsindexOperator, out var v) && v.Length > 0 ? (int)v[0] : 0;
        CffIndex subrs = default;
        if (privateDict.TryGetValue(SubrsOperator, out var sub) && sub.Length > 0)
        {
            // The local subroutines' offset counts from the Private DICT.
            int sp = off + (int)sub[0];
            subrs = CffIndex.ReadCff2(d, ref sp);
        }
        return new FontDict(subrs, vsindex);
    }

    // Which Font DICT each glyph uses. Glyphs no range covers keep Font DICT 0; reading past the table throws.
    private static void ReadFdSelect(byte[] d, int o, byte[] fdOf)
    {
        switch (d[o])
        {
            case FdSelectPerGlyph:
                for (int g = 0; g < fdOf.Length; g++) fdOf[g] = d[o + FdSelectFormatLength + g];
                break;
            case FdSelectRanges:
                ReadFdRanges(d, o, fdOf, wide: false);
                break;
            case FdSelectWideRanges:
                ReadFdRanges(d, o, fdOf, wide: true);
                break;
            default:
                throw new FormatException("Unknown FDSelect format.");
        }
    }

    private static void ReadFdRanges(byte[] d, int o, byte[] fdOf, bool wide)
    {
        int count = wide ? ReadInt32(d, o + FdSelectFormatLength) : ReadUInt16(d, o + FdSelectFormatLength);
        int ranges = o + FdSelectFormatLength + (wide ? WideRangeCountLength : RangeCountLength);
        int rangeLength = wide ? WideRangeLength : RangeLength;
        for (int i = 0; i < count; i++)
        {
            int range = ranges + i * rangeLength;
            int first = wide ? ReadInt32(d, range) : ReadUInt16(d, range);
            int fd = wide ? ReadUInt16(d, range + WideRangeFdField) : d[range + RangeFdField];
            // The next range's first glyph, or the sentinel, ends this one.
            int next = wide ? ReadInt32(d, range + rangeLength) : ReadUInt16(d, range + rangeLength);
            for (int g = Math.Max(0, first); g < Math.Min(fdOf.Length, next); g++) fdOf[g] = (byte)Math.Min(fd, MaxFdIndex);
        }
    }

    private static int ReadUInt16(byte[] d, int at) => d[at] << 8 | d[at + 1];

    // A 32-bit glyph number, which a large one turns negative.
    private static int ReadInt32(byte[] d, int at) => (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at));

    /// <summary>HVAR's deltas for the advance widths: through its advance width mapping, or by glyph id without one.</summary>
    private sealed class AdvanceVariations
    {
        private readonly VariationStore _store;
        private readonly byte[] _data;
        private readonly int _map, _end;

        private AdvanceVariations(VariationStore store, byte[] data, int map, int end)
        {
            _store = store;
            _data = data;
            _map = map;
            _end = end;
        }

        /// <summary>The font's HVAR at the instance, or null when it has none or none with a store.</summary>
        public static AdvanceVariations? FromHvar(FontProgram p, float[] coords)
        {
            if (p.Table("HVAR") is not { Length: >= HvarMinimumLength } hvar) return null;
            uint storeOff = p.U32(hvar.Offset + HvarStoreField), mapOff = p.U32(hvar.Offset + HvarAdvanceMapField);
            if (storeOff == 0 || storeOff >= hvar.Length) return null;
            int end = hvar.Offset + hvar.Length;
            var store = new VariationStore(p.Data, hvar.Offset + (int)storeOff, end, coords);
            return new AdvanceVariations(store, p.Data, mapOff > 0 && mapOff < hvar.Length ? hvar.Offset + (int)mapOff : -1, end);
        }

        public float Delta(int glyph)
        {
            if (_map < 0) return _store.Delta(0, glyph);
            var (outer, inner) = VariationStore.MapIndex(_data, _map, _end, glyph);
            return _store.Delta(outer, inner);
        }
    }
}
