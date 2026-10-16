using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfPecker.Fonts;

/// <summary>
/// Unpacks WOFF2 files (https://www.w3.org/TR/WOFF2/) into plain sfnt fonts: Brotli from the BCL, plus our own
/// reconstruction of the transformed glyf, loca and hmtx tables. A WOFF2 collection gives one sfnt per font.
/// </summary>
internal static class Woff2Decoder
{
    /// <summary>The most a font may unpack to, the same budget WOFF 1.0 files get.</summary>
    internal const long MaxSize = 64L * 1024 * 1024;

    private const uint TagGlyf = 0x676C7966, TagLoca = 0x6C6F6361, TagHmtx = 0x686D7478, TagHhea = 0x68686561, TagMaxp = 0x6D617870,
        TagHead = 0x68656164;

    private static readonly uint[] KnownTags = Array.ConvertAll(new[]
    {
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
        "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    }, FontProgram.Tag);

    private sealed class Entry
    {
        public uint Tag;
        public bool Transformed;
        public int OrigLength, StreamOffset, StreamLength;
    }

    /// <summary>Returns the sfnt of every font in the file: one for a plain WOFF2, several for a collection.</summary>
    public static List<byte[]> Decode(byte[] file)
    {
        var r = new Reader(file, 0, file.Length);
        if (r.U32() != 0x774F4632) throw new FormatException("Not a WOFF2 file."); // 'wOF2'
        uint flavor = r.U32();
        r.U32(); // length
        int numTables = r.U16();
        r.U16(); // reserved
        long totalSfntSize = r.U32();
        long compressedSize = r.U32();
        r.Skip(24); // version, metadata and private data blocks
        if (numTables == 0) throw new FormatException("The WOFF2 file has no tables.");
        if (totalSfntSize > MaxSize) throw new FormatException("The WOFF2 font is too large.");

        var entries = new Entry[numTables];
        long streamSize = 0;
        for (int i = 0; i < numTables; i++)
        {
            int flags = r.U8();
            uint tag = (flags & 63) == 63 ? r.U32() : KnownTags[flags & 63];
            int version = flags >> 6;
            var e = new Entry { Tag = tag, OrigLength = r.Base128() };
            // glyf and loca are transformed by default (version 3 means stored as is); every other table is the reverse.
            e.Transformed = tag is TagGlyf or TagLoca ? version == 0 : version != 0;
            if (tag is TagGlyf or TagLoca ? version is 1 or 2 : version > (tag == TagHmtx ? 1 : 0))
                throw new FormatException("Unknown WOFF2 table transform.");
            e.StreamLength = e.Transformed ? r.Base128() : e.OrigLength;
            if (tag == TagLoca && e.Transformed && e.StreamLength != 0) throw new FormatException("Damaged WOFF2 loca table.");
            e.StreamOffset = (int)streamSize;
            streamSize += e.StreamLength;
            if (streamSize > MaxSize) throw new FormatException("The WOFF2 font is too large.");
            entries[i] = e;
        }

        var fonts = new List<(uint Flavor, int[] Tables)>();
        if (flavor == 0x74746366) // 'ttcf'
        {
            r.U32(); // version
            int numFonts = r.U255();
            if (numFonts == 0) throw new FormatException("The WOFF2 collection has no fonts.");
            for (int f = 0; f < numFonts; f++)
            {
                int n = r.U255();
                uint fontFlavor = r.U32();
                var indices = new int[n];
                for (int i = 0; i < n; i++)
                    if ((indices[i] = r.U255()) >= numTables) throw new FormatException("Damaged WOFF2 collection directory.");
                fonts.Add((fontFlavor, indices));
            }
        }
        else fonts.Add((flavor, Enumerable.Range(0, numTables).ToArray()));

        // The tables are one Brotli stream. The declared size is allocated up front: keep it to what the compressed
        // data can plausibly produce, as for WOFF 1.0, so a small file cannot claim a large buffer.
        int start = r.Position;
        if (compressedSize > file.Length - start) throw new FormatException("The WOFF2 file is truncated.");
        if (streamSize > compressedSize * 1100 + 1024) throw new FormatException("Damaged WOFF2 data.");
        var stream = new byte[streamSize];
        try
        {
            using var brotli = new BrotliStream(new MemoryStream(file, start, (int)compressedSize), CompressionMode.Decompress);
            brotli.ReadExactly(stream);
        }
        catch (Exception ex) when (ex is InvalidOperationException or EndOfStreamException)
        {
            // BrotliStream reports corrupt data as an invalid operation; to the caller it is a damaged font file.
            throw new FormatException("Damaged WOFF2 data.", ex);
        }

        var tables = new byte[]?[numTables];
        var sfnts = new List<byte[]>(fonts.Count);
        foreach (var (fontFlavor, indices) in fonts)
        {
            int Find(uint tag) => Array.FindIndex(indices, i => entries[i].Tag == tag) is var k and >= 0 ? indices[k] : -1;
            int glyf = Find(TagGlyf), loca = Find(TagLoca), hmtx = Find(TagHmtx);
            if (glyf >= 0 && entries[glyf].Transformed && tables[glyf] == null)
            {
                if (loca < 0 || !entries[loca].Transformed) throw new FormatException("Damaged WOFF2 glyf table.");
                var g = entries[glyf];
                (tables[glyf], tables[loca]) = ReconstructGlyf(new Reader(stream, g.StreamOffset, g.StreamLength), g.OrigLength);
                if (tables[loca]!.Length != entries[loca].OrigLength) throw new FormatException("Damaged WOFF2 loca table.");
            }
            else if (loca >= 0 && entries[loca].Transformed && tables[loca] == null) throw new FormatException("Damaged WOFF2 loca table.");
            foreach (int i in indices)
                if (tables[i] == null && !entries[i].Transformed)
                    tables[i] = stream.AsSpan(entries[i].StreamOffset, entries[i].StreamLength).ToArray();
            if (hmtx >= 0 && entries[hmtx].Transformed && tables[hmtx] == null)
            {
                int hhea = Find(TagHhea), maxp = Find(TagMaxp), head = Find(TagHead);
                if (glyf < 0 || loca < 0 || hhea < 0 || maxp < 0) throw new FormatException("Damaged WOFF2 hmtx table.");
                var h = entries[hmtx];
                tables[hmtx] = ReconstructHmtx(new Reader(stream, h.StreamOffset, h.StreamLength), tables[glyf]!, tables[loca]!,
                    tables[hhea]!, tables[maxp]!, head >= 0 ? tables[head] : null);
            }
            var list = new List<(uint Tag, byte[] Data)>(indices.Length);
            foreach (int i in indices)
                if (!list.Exists(t => t.Tag == entries[i].Tag)) list.Add((entries[i].Tag, tables[i]!));
            sfnts.Add(SfntBuilder.Build(fontFlavor, list));
        }
        return sfnts;
    }

    // ------------------------------------------------------------------ glyf and loca

    /// <summary>Rebuilds the glyf and loca tables from the transformed glyf data (WOFF2 section 5.1).</summary>
    private static (byte[] Glyf, byte[] Loca) ReconstructGlyf(Reader r, int origLength)
    {
        int transformedSize = r.Remaining;
        r.U16(); // reserved
        int options = r.U16();
        int numGlyphs = r.U16();
        int indexFormat = r.U16();
        var sizes = new long[7];
        for (int i = 0; i < sizes.Length; i++) sizes[i] = r.U32();
        var nContours = r.Sub(sizes[0]);
        var nPoints = r.Sub(sizes[1]);
        var flags = r.Sub(sizes[2]);
        var glyphs = r.Sub(sizes[3]);
        var composites = r.Sub(sizes[4]);
        var bboxes = r.Sub(sizes[5]);
        var instructions = r.Sub(sizes[6]);
        Reader? overlap = (options & 1) != 0 ? r.Sub((numGlyphs + 7) >> 3) : null;
        var bboxBitmap = bboxes.Sub(((numGlyphs + 31) >> 5) << 2);

        // Start at the declared size, but no more than the data could plausibly expand to.
        var o = new Writer((int)Math.Clamp(origLength + 4L * numGlyphs, 1024, transformedSize * 4L + 1024));
        var loca = new int[numGlyphs + 1];
        int[] endPts = new int[16], dxs = new int[64], dys = new int[64];
        var pf = new byte[64];
        for (int g = 0; g < numGlyphs; g++)
        {
            loca[g] = o.Length;
            int contours = nContours.S16();
            bool hasBbox = (bboxBitmap.ByteAt(g >> 3) & (0x80 >> (g & 7))) != 0;
            if (contours == 0)
            {
                if (hasBbox) throw new FormatException("Damaged WOFF2 glyph.");
                continue;
            }
            int glyphStart = o.Length;
            o.Skip(10); // header, filled in below
            int xMin, yMin, xMax, yMax;
            if (contours < 0)
            {
                // Composite: the component records are copied as they are; the bounding box is always stored.
                if (contours != -1 || !hasBbox) throw new FormatException("Damaged WOFF2 composite glyph.");
                int compStart = composites.Position;
                bool haveInstructions = false;
                int f;
                do
                {
                    f = composites.U16();
                    composites.U16(); // glyph index
                    int args = (f & 0x0001) != 0 ? 4 : 2;
                    if ((f & 0x0008) != 0) args += 2;
                    else if ((f & 0x0040) != 0) args += 4;
                    else if ((f & 0x0080) != 0) args += 8;
                    composites.Skip(args);
                    haveInstructions |= (f & 0x0100) != 0;
                } while ((f & 0x0020) != 0);
                o.Write(composites.Slice(compStart, composites.Position - compStart));
                if (haveInstructions)
                {
                    int len = glyphs.U255();
                    o.U16(len);
                    o.Write(instructions.Bytes(len));
                }
                (xMin, yMin, xMax, yMax) = (bboxes.S16(), bboxes.S16(), bboxes.S16(), bboxes.S16());
            }
            else
            {
                int total = 0;
                if (endPts.Length < contours) endPts = new int[Math.Max(contours, endPts.Length * 2)];
                for (int c = 0; c < contours; c++)
                {
                    total += nPoints.U255();
                    if (total > 0xFFFF) throw new FormatException("Damaged WOFF2 glyph.");
                    endPts[c] = total - 1;
                }
                if (dxs.Length < total) (dxs, dys, pf) = (new int[total * 2], new int[total * 2], new byte[total * 2]);
                var pointFlags = flags.Bytes(total);
                var gs = glyphs.Rest;
                int gp = 0, x = 0, y = 0;
                (xMin, yMin, xMax, yMax) = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
                for (int p = 0; p < total; p++)
                {
                    int flag = pointFlags[p], f = flag & 0x7F, dx, dy;
                    // The triplet encoding (WOFF2 section 5.2): the flag picks how many bytes follow and how they split.
                    if (f < 10) (dx, dy) = (0, ((f & 14) << 7) + gs[gp++]);
                    else if (f < 20) (dx, dy) = ((((f - 10) & 14) << 7) + gs[gp++], 0);
                    else if (f < 84)
                    {
                        int b0 = f - 20, b1 = gs[gp++];
                        (dx, dy) = (1 + (b0 & 0x30) + (b1 >> 4), 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F));
                    }
                    else if (f < 120)
                    {
                        int b0 = f - 84;
                        (dx, dy) = (1 + ((b0 / 12) << 8) + gs[gp], 1 + (((b0 % 12) >> 2) << 8) + gs[gp + 1]);
                        gp += 2;
                    }
                    else if (f < 124)
                    {
                        int b2 = gs[gp + 1];
                        (dx, dy) = ((gs[gp] << 4) + (b2 >> 4), ((b2 & 0x0F) << 8) + gs[gp + 2]);
                        gp += 3;
                    }
                    else
                    {
                        (dx, dy) = (gs[gp] << 8 | gs[gp + 1], gs[gp + 2] << 8 | gs[gp + 3]);
                        gp += 4;
                    }
                    if (f < 10) { if ((f & 1) == 0) dy = -dy; }
                    else if (f < 20) { if ((f & 1) == 0) dx = -dx; }
                    else
                    {
                        if ((f & 1) == 0) dx = -dx;
                        if ((f & 2) == 0) dy = -dy;
                    }
                    x += dx;
                    y += dy;
                    if (x < xMin) xMin = x;
                    if (x > xMax) xMax = x;
                    if (y < yMin) yMin = y;
                    if (y > yMax) yMax = y;
                    dxs[p] = dx;
                    dys[p] = dy;
                    pf[p] = (byte)(flag >> 7 ^ 1); // ON_CURVE_POINT
                }
                glyphs.Skip(gp);
                int instructionLength = glyphs.U255();
                if (hasBbox) (xMin, yMin, xMax, yMax) = (bboxes.S16(), bboxes.S16(), bboxes.S16(), bboxes.S16());
                else if (total == 0) (xMin, yMin, xMax, yMax) = (0, 0, 0, 0);
                for (int c = 0; c < contours; c++) o.U16(endPts[c]);
                o.U16(instructionLength);
                o.Write(instructions.Bytes(instructionLength));
                bool overlaps = overlap != null && (overlap.ByteAt(g >> 3) & (0x80 >> (g & 7))) != 0;
                WritePoints(o, dxs, dys, pf, total, overlaps);
            }
            o.Patch16(glyphStart, contours);
            o.Patch16(glyphStart + 2, Short(xMin));
            o.Patch16(glyphStart + 4, Short(yMin));
            o.Patch16(glyphStart + 6, Short(xMax));
            o.Patch16(glyphStart + 8, Short(yMax));
            o.Align4();
            if (o.Length > MaxSize) throw new FormatException("The WOFF2 font is too large.");
        }
        loca[numGlyphs] = o.Length;

        var locaBytes = new byte[(numGlyphs + 1) * (indexFormat == 0 ? 2 : 4)];
        for (int g = 0; g <= numGlyphs; g++)
        {
            if (indexFormat == 0)
            {
                if (loca[g] > 0x1FFFE) throw new FormatException("Damaged WOFF2 loca table.");
                BinaryPrimitives.WriteUInt16BigEndian(locaBytes.AsSpan(g * 2), (ushort)(loca[g] >> 1));
            }
            else BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(g * 4), (uint)loca[g]);
        }
        return (o.ToArray(), locaBytes);
    }

    /// <summary>
    /// Writes a simple glyph's flags and coordinates in the usual compact TrueType form. On entry <c>pf</c> holds
    /// each point's on-curve bit; the rest of its flag is filled in here.
    /// </summary>
    private static void WritePoints(Writer o, int[] dxs, int[] dys, byte[] pf, int n, bool overlaps)
    {
        for (int i = 0; i < n; i++)
        {
            int dx = dxs[i], dy = dys[i], f = pf[i];
            f |= dx == 0 ? 0x10 : dx is > -256 and < 256 ? 0x02 | (dx > 0 ? 0x10 : 0) : 0;
            f |= dy == 0 ? 0x20 : dy is > -256 and < 256 ? 0x04 | (dy > 0 ? 0x20 : 0) : 0;
            pf[i] = (byte)f;
        }
        if (overlaps && n > 0) pf[0] |= 0x40;
        var span = o.Reserve(n * 5);
        int at = 0;
        for (int i = 0; i < n;)
        {
            int run = 1;
            while (i + run < n && pf[i + run] == pf[i] && run < 256) run++;
            if (run > 1)
            {
                span[at++] = (byte)(pf[i] | 0x08);
                span[at++] = (byte)(run - 1);
            }
            else span[at++] = pf[i];
            i += run;
        }
        for (int axis = 0; axis < 2; axis++)
        {
            var deltas = axis == 0 ? dxs : dys;
            int shortBit = axis == 0 ? 0x02 : 0x04, sameBit = axis == 0 ? 0x10 : 0x20;
            for (int i = 0; i < n; i++)
            {
                int d = deltas[i];
                if ((pf[i] & shortBit) != 0) span[at++] = (byte)Math.Abs(d);
                else if ((pf[i] & sameBit) == 0)
                {
                    BinaryPrimitives.WriteInt16BigEndian(span[at..], (short)Short(d));
                    at += 2;
                }
            }
        }
        o.Commit(at);
    }

    private static int Short(int v) => v is >= short.MinValue and <= short.MaxValue ? v : throw new FormatException("Damaged WOFF2 glyph.");

    // ------------------------------------------------------------------ hmtx

    /// <summary>Rebuilds hmtx, taking left side bearings that were left out from the glyphs' xMin (WOFF2 section 5.4).</summary>
    private static byte[] ReconstructHmtx(Reader r, byte[] glyf, byte[] loca, byte[] hhea, byte[] maxp, byte[]? head)
    {
        int flags = r.U8();
        int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(4));
        int numHMetrics = BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34));
        if (numHMetrics < 1 || numHMetrics > numGlyphs) throw new FormatException("Damaged WOFF2 hmtx table.");
        bool longLoca = head != null && head.Length >= 52 ? BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)) != 0 : loca.Length >= (numGlyphs + 1) * 4;
        int XMin(int g)
        {
            int at = longLoca ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(g * 4)) : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(g * 2)) * 2;
            int end = longLoca ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(g * 4 + 4)) : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(g * 2 + 2)) * 2;
            return end - at >= 10 ? BinaryPrimitives.ReadInt16BigEndian(glyf.AsSpan(at + 2)) : 0;
        }
        var advances = new int[numHMetrics];
        for (int i = 0; i < numHMetrics; i++) advances[i] = r.U16();
        var o = new byte[numHMetrics * 4 + (numGlyphs - numHMetrics) * 2];
        for (int i = 0; i < numHMetrics; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(i * 4), (ushort)advances[i]);
            BinaryPrimitives.WriteInt16BigEndian(o.AsSpan(i * 4 + 2), (short)((flags & 1) != 0 ? XMin(i) : r.S16()));
        }
        for (int g = numHMetrics; g < numGlyphs; g++)
            BinaryPrimitives.WriteInt16BigEndian(o.AsSpan(numHMetrics * 4 + (g - numHMetrics) * 2), (short)((flags & 2) != 0 ? XMin(g) : r.S16()));
        return o;
    }

    // ------------------------------------------------------------------ binary helpers

    /// <summary>A bounds-checked big-endian reader over part of a byte array.</summary>
    private sealed class Reader
    {
        private readonly byte[] data;
        private readonly int start, _end;
        public int Position { get; private set; }

        public Reader(byte[] data, int start, int length)
        {
            this.data = data;
            this.start = Position = start;
            _end = start + length;
        }

        public int Remaining => _end - Position;

        private int Take(int n)
        {
            if (n < 0 || n > _end - Position) throw new FormatException("The WOFF2 data is truncated.");
            int at = Position;
            Position += n;
            return at;
        }

        public void Skip(int n) => Take(n);
        public int U8() => data[Take(1)];
        public int U16() => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(Take(2)));
        public short S16() => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(Take(2)));
        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(Take(4)));
        public ReadOnlySpan<byte> Bytes(int n) => data.AsSpan(Take(n), n);
        public ReadOnlySpan<byte> Rest => data.AsSpan(Position, _end - Position);
        public ReadOnlySpan<byte> Slice(int at, int n) => data.AsSpan(at, n);
        public int ByteAt(int i) => i < _end - start ? data[start + i] : throw new FormatException("The WOFF2 data is truncated.");

        public Reader Sub(long n)
        {
            if (n > int.MaxValue) throw new FormatException("The WOFF2 data is truncated.");
            return new Reader(data, Take((int)n), (int)n);
        }

        /// <summary>UIntBase128: 1 to 5 bytes, 7 bits each, no leading zeros, no overflow.</summary>
        public int Base128()
        {
            uint v = 0;
            for (int i = 0; i < 5; i++)
            {
                int b = U8();
                if (i == 0 && b == 0x80 || (v & 0xFE000000) != 0) throw new FormatException("Damaged WOFF2 number.");
                v = v << 7 | (uint)(b & 0x7F);
                if ((b & 0x80) == 0) return v <= int.MaxValue ? (int)v : throw new FormatException("Damaged WOFF2 number.");
            }
            throw new FormatException("Damaged WOFF2 number.");
        }

        /// <summary>255UInt16: one byte, or a marker byte followed by one or two more.</summary>
        public int U255()
        {
            int code = U8();
            return code switch
            {
                253 => U16(),
                255 => U8() + 253,
                254 => U8() + 506,
                _ => code,
            };
        }
    }

    /// <summary>A growable big-endian byte buffer.</summary>
    private sealed class Writer(int capacity)
    {
        private byte[] _buf = new byte[capacity];
        public int Length { get; private set; }

        private Span<byte> Grow(int n)
        {
            if (Length + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
            var s = _buf.AsSpan(Length, n);
            Length += n;
            return s;
        }

        public void U8(int v) => Grow(1)[0] = (byte)v;

        /// <summary>Space for up to <paramref name="n"/> bytes; <see cref="Commit"/> says how many were written.</summary>
        public Span<byte> Reserve(int n)
        {
            if (Length + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
            return _buf.AsSpan(Length, n);
        }

        public void Commit(int n) => Length += n;
        public void U16(int v) => BinaryPrimitives.WriteUInt16BigEndian(Grow(2), (ushort)v);
        public void Write(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Grow(bytes.Length));
        public void Skip(int n) => Grow(n).Clear();
        public void Align4() => Skip((4 - (Length & 3)) & 3);
        public void Patch16(int at, int v) => BinaryPrimitives.WriteUInt16BigEndian(_buf.AsSpan(at), (ushort)v);
        public byte[] ToArray() => _buf.AsSpan(0, Length).ToArray();
    }
}
