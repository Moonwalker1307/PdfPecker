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

    private const uint Signature = 0x774F4632;        // 'wOF2'
    private const uint CollectionFlavor = 0x74746366; // 'ttcf'
    // After the signature, flavor, length, table count, reserved field, sfnt size and compressed size come the
    // version, metadata and private data fields, which are not needed to unpack the font.
    private const int UnusedHeaderFieldsLength = 24;

    // Brotli cannot expand data by more than about a thousandfold, so a larger claim is a damaged size.
    private const long MaxBrotliExpansion = 1100, BrotliExpansionSlack = 1024;

    private const uint TagGlyf = 0x676C7966, TagLoca = 0x6C6F6361, TagHmtx = 0x686D7478, TagHhea = 0x68686561, TagMaxp = 0x6D617870,
        TagHead = 0x68656164;

    // A directory entry's flags: the low bits index KnownTags (or say that a tag follows), the high bits are the
    // transform version. glyf and loca are transformed at version 0 and stored as is at version 3; hmtx is
    // transformed at version 1; every other table has only version 0, as is.
    private const int KnownTagMask = 63, TagFollowsCode = 63, TransformVersionShift = 6;
    private const int GlyfLocaStoredVersion = 3, HmtxTransformedVersion = 1;

    private static readonly uint[] KnownTags = Array.ConvertAll(new[]
    {
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
        "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    }, FontProgram.Tag);

    /// <summary>One table of the directory and where its data lies in the decompressed stream.</summary>
    private readonly record struct TableEntry(uint Tag, bool Transformed, int OrigLength, int StreamOffset, int StreamLength);

    /// <summary>Returns the sfnt of every font in the file: one for a plain WOFF2, several for a collection.</summary>
    public static List<byte[]> Decode(byte[] file)
    {
        var reader = new Woff2Reader(file);
        if (reader.U32() != Signature) throw new FormatException("Not a WOFF2 file.");
        uint flavor = reader.U32();
        reader.U32(); // length
        int numTables = reader.U16();
        reader.U16(); // reserved
        long totalSfntSize = reader.U32();
        long compressedSize = reader.U32();
        reader.Skip(UnusedHeaderFieldsLength);
        if (numTables == 0) throw new FormatException("The WOFF2 file has no tables.");
        if (totalSfntSize > MaxSize) throw new FormatException("The WOFF2 font is too large.");

        var entries = ReadTableDirectory(ref reader, numTables, out long streamSize);
        var fonts = ReadCollectionDirectory(ref reader, flavor, numTables);
        var stream = DecompressTables(file, reader.Position, compressedSize, streamSize);

        var tables = new byte[]?[numTables];
        var sfnts = new List<byte[]>(fonts.Count);
        foreach (var (fontFlavor, indices) in fonts) sfnts.Add(BuildFont(fontFlavor, indices, entries, tables, stream));
        return sfnts;
    }

    private static TableEntry[] ReadTableDirectory(ref Woff2Reader reader, int numTables, out long streamSize)
    {
        var entries = new TableEntry[numTables];
        streamSize = 0;
        for (int i = 0; i < numTables; i++)
        {
            int flags = reader.U8();
            uint tag = (flags & KnownTagMask) == TagFollowsCode ? reader.U32() : KnownTags[flags & KnownTagMask];
            int version = flags >> TransformVersionShift;
            int origLength = reader.Base128();
            bool glyfOrLoca = tag is TagGlyf or TagLoca;
            bool transformed = glyfOrLoca ? version == 0 : version != 0;
            if (glyfOrLoca ? version is not (0 or GlyfLocaStoredVersion) : version > (tag == TagHmtx ? HmtxTransformedVersion : 0))
                throw new FormatException("Unknown WOFF2 table transform.");
            int streamLength = transformed ? reader.Base128() : origLength;
            // A transformed loca has no data of its own: it is rebuilt with glyf.
            if (tag == TagLoca && transformed && streamLength != 0) throw new FormatException("Damaged WOFF2 loca table.");
            entries[i] = new TableEntry(tag, transformed, origLength, (int)streamSize, streamLength);
            streamSize += streamLength;
            if (streamSize > MaxSize) throw new FormatException("The WOFF2 font is too large.");
        }
        return entries;
    }

    // The fonts of a collection, each as its flavor and the indexes of its tables; a plain file is one font of every table.
    private static List<(uint Flavor, int[] Tables)> ReadCollectionDirectory(ref Woff2Reader reader, uint flavor, int numTables)
    {
        var fonts = new List<(uint Flavor, int[] Tables)>();
        if (flavor != CollectionFlavor)
        {
            fonts.Add((flavor, Enumerable.Range(0, numTables).ToArray()));
            return fonts;
        }
        reader.U32(); // version
        int numFonts = reader.U255();
        if (numFonts == 0) throw new FormatException("The WOFF2 collection has no fonts.");
        for (int f = 0; f < numFonts; f++)
        {
            int count = reader.U255();
            uint fontFlavor = reader.U32();
            var indices = new int[count];
            for (int i = 0; i < count; i++)
                if ((indices[i] = reader.U255()) >= numTables) throw new FormatException("Damaged WOFF2 collection directory.");
            fonts.Add((fontFlavor, indices));
        }
        return fonts;
    }

    // The tables are one Brotli stream. The declared size is allocated up front: keep it to what the compressed
    // data can plausibly produce, as for WOFF 1.0, so a small file cannot claim a large buffer.
    private static byte[] DecompressTables(byte[] file, int start, long compressedSize, long streamSize)
    {
        if (compressedSize > file.Length - start) throw new FormatException("The WOFF2 file is truncated.");
        if (streamSize > compressedSize * MaxBrotliExpansion + BrotliExpansionSlack) throw new FormatException("Damaged WOFF2 data.");
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
        return stream;
    }

    // Tables are rebuilt once and shared by the fonts of a collection that list them.
    private static byte[] BuildFont(uint flavor, int[] indices, TableEntry[] entries, byte[]?[] tables, byte[] stream)
    {
        int glyf = Find(indices, entries, TagGlyf), loca = Find(indices, entries, TagLoca), hmtx = Find(indices, entries, TagHmtx);
        if (glyf >= 0 && entries[glyf].Transformed && tables[glyf] == null)
        {
            if (loca < 0 || !entries[loca].Transformed) throw new FormatException("Damaged WOFF2 glyf table.");
            var entry = entries[glyf];
            (tables[glyf], tables[loca]) = ReconstructGlyf(new Woff2Reader(stream.AsSpan(entry.StreamOffset, entry.StreamLength)), entry.OrigLength);
            if (tables[loca]!.Length != entries[loca].OrigLength) throw new FormatException("Damaged WOFF2 loca table.");
        }
        else if (loca >= 0 && entries[loca].Transformed && tables[loca] == null) throw new FormatException("Damaged WOFF2 loca table.");
        foreach (int i in indices)
            if (tables[i] == null && !entries[i].Transformed)
                tables[i] = stream.AsSpan(entries[i].StreamOffset, entries[i].StreamLength).ToArray();
        if (hmtx >= 0 && entries[hmtx].Transformed && tables[hmtx] == null)
        {
            int hhea = Find(indices, entries, TagHhea), maxp = Find(indices, entries, TagMaxp), head = Find(indices, entries, TagHead);
            if (glyf < 0 || loca < 0 || hhea < 0 || maxp < 0) throw new FormatException("Damaged WOFF2 hmtx table.");
            var entry = entries[hmtx];
            tables[hmtx] = ReconstructHmtx(new Woff2Reader(stream.AsSpan(entry.StreamOffset, entry.StreamLength)), tables[glyf]!, tables[loca]!,
                tables[hhea]!, tables[maxp]!, head >= 0 ? tables[head] : null);
        }
        // A table listed twice is embedded once.
        var list = new List<(uint Tag, byte[] Data)>(indices.Length);
        foreach (int i in indices)
            if (!list.Exists(t => t.Tag == entries[i].Tag)) list.Add((entries[i].Tag, tables[i]!));
        return SfntBuilder.Build(flavor, list);
    }

    // The index of the font's table with the tag, or -1.
    private static int Find(int[] indices, TableEntry[] entries, uint tag)
    {
        foreach (int i in indices) if (entries[i].Tag == tag) return i;
        return -1;
    }

    // ------------------------------------------------------------------ glyf and loca

    // The transformed glyf table starts with a header, then seven streams (contour counts, point counts, point flags,
    // glyph data, composite data, bounding boxes and instructions), then with option bit 0 an overlap bitmap.
    private const int GlyfStreamCount = 7;
    private const int OverlapBitmapOption = 0x1;
    private const int ShortLocaFormat = 0;
    // Short loca offsets are halved into 16 bits, so a glyph may start at most here.
    private const int MaxShortLocaOffset = 0x1FFFE;

    // A glyph header: the contour count and the bounding box.
    private const int GlyphHeaderLength = 10, GlyphXMinField = 2;
    private const int CompositeContourCount = -1;
    private const int MaxPointsPerGlyph = 0xFFFF;

    // Composite glyph flags.
    private const int ArgsAreWords = 0x0001, HaveScale = 0x0008, MoreComponents = 0x0020, HaveXAndYScale = 0x0040, HaveTwoByTwo = 0x0080,
        HaveInstructions = 0x0100;

    // Simple glyph point flags.
    private const byte OnCurve = 0x01, XShort = 0x02, YShort = 0x04, RepeatFlag = 0x08, XSameOrPositive = 0x10, YSameOrPositive = 0x20,
        OverlapSimple = 0x40;
    private const int MaxFlagRepeat = 256;
    // Flags, then x and y coordinates of up to two bytes each: the most the points of a glyph can take.
    private const int MaxBytesPerPoint = 5;

    /// <summary>The seven streams of the transformed glyf table and the optional overlap bitmap.</summary>
    private ref struct GlyfStreams
    {
        public Woff2Reader Contours, Points, Flags, Glyphs, Composites, BoundingBoxes, Instructions, Overlap;
        public Woff2Reader BoundingBoxBitmap;
        public bool HasOverlapBitmap;

        public readonly bool HasBoundingBox(int glyph) => (BoundingBoxBitmap.ByteAt(glyph >> 3) & (0x80 >> (glyph & 7))) != 0;

        public readonly bool Overlaps(int glyph) => HasOverlapBitmap && (Overlap.ByteAt(glyph >> 3) & (0x80 >> (glyph & 7))) != 0;

        public (int XMin, int YMin, int XMax, int YMax) ReadBoundingBox() => (BoundingBoxes.S16(), BoundingBoxes.S16(), BoundingBoxes.S16(), BoundingBoxes.S16());
    }

    /// <summary>The decoded points of one simple glyph, in buffers that grow to the largest glyph seen.</summary>
    private sealed class PointBuffers
    {
        public int[] EndPoints = new int[16], Dx = new int[64], Dy = new int[64];
        public byte[] Flags = new byte[64];

        public void EnsureContours(int contours)
        {
            if (EndPoints.Length < contours) EndPoints = new int[Math.Max(contours, EndPoints.Length * 2)];
        }

        public void EnsurePoints(int points)
        {
            if (Dx.Length < points) (Dx, Dy, Flags) = (new int[points * 2], new int[points * 2], new byte[points * 2]);
        }
    }

    /// <summary>Rebuilds the glyf and loca tables from the transformed glyf data (WOFF2 section 5.1).</summary>
    private static (byte[] Glyf, byte[] Loca) ReconstructGlyf(Woff2Reader reader, int origLength)
    {
        int transformedSize = reader.Remaining;
        reader.U16(); // reserved
        int options = reader.U16();
        int numGlyphs = reader.U16();
        int indexFormat = reader.U16();
        var sizes = new long[GlyfStreamCount];
        for (int i = 0; i < sizes.Length; i++) sizes[i] = reader.U32();
        var streams = new GlyfStreams
        {
            Contours = reader.Sub(sizes[0]),
            Points = reader.Sub(sizes[1]),
            Flags = reader.Sub(sizes[2]),
            Glyphs = reader.Sub(sizes[3]),
            Composites = reader.Sub(sizes[4]),
            BoundingBoxes = reader.Sub(sizes[5]),
            Instructions = reader.Sub(sizes[6]),
            HasOverlapBitmap = (options & OverlapBitmapOption) != 0,
        };
        if (streams.HasOverlapBitmap) streams.Overlap = reader.Sub((numGlyphs + 7) >> 3);
        streams.BoundingBoxBitmap = streams.BoundingBoxes.Sub(((numGlyphs + 31) >> 5) << 2);

        // Start at the declared size, but no more than the data could plausibly expand to.
        var glyf = new TableWriter((int)Math.Clamp(origLength + 4L * numGlyphs, 1024, transformedSize * 4L + 1024));
        var loca = new int[numGlyphs + 1];
        var points = new PointBuffers();
        for (int g = 0; g < numGlyphs; g++)
        {
            loca[g] = glyf.Length;
            int contours = streams.Contours.S16();
            bool hasBoundingBox = streams.HasBoundingBox(g);
            if (contours == 0)
            {
                if (hasBoundingBox) throw new FormatException("Damaged WOFF2 glyph.");
                continue;
            }
            int glyphStart = glyf.Length;
            glyf.Skip(GlyphHeaderLength); // filled in below
            var box = contours < 0
                ? WriteCompositeGlyph(ref streams, glyf, contours, hasBoundingBox)
                : WriteSimpleGlyph(ref streams, glyf, contours, hasBoundingBox, g, points);
            glyf.Patch16(glyphStart, contours);
            glyf.Patch16(glyphStart + 2, Short(box.XMin));
            glyf.Patch16(glyphStart + 4, Short(box.YMin));
            glyf.Patch16(glyphStart + 6, Short(box.XMax));
            glyf.Patch16(glyphStart + 8, Short(box.YMax));
            glyf.Align4();
            if (glyf.Length > MaxSize) throw new FormatException("The WOFF2 font is too large.");
        }
        loca[numGlyphs] = glyf.Length;
        return (glyf.ToArray(), WriteLoca(loca, indexFormat));
    }

    // Composite: the component records are copied as they are; the bounding box is always stored.
    private static (int XMin, int YMin, int XMax, int YMax) WriteCompositeGlyph(ref GlyfStreams streams, TableWriter glyf, int contours, bool hasBoundingBox)
    {
        if (contours != CompositeContourCount || !hasBoundingBox) throw new FormatException("Damaged WOFF2 composite glyph.");
        ref var composites = ref streams.Composites;
        int start = composites.Position;
        bool haveInstructions = false;
        int flags;
        do
        {
            flags = composites.U16();
            composites.U16(); // glyph index
            int argumentsLength = (flags & ArgsAreWords) != 0 ? 4 : 2;
            if ((flags & HaveScale) != 0) argumentsLength += 2;
            else if ((flags & HaveXAndYScale) != 0) argumentsLength += 4;
            else if ((flags & HaveTwoByTwo) != 0) argumentsLength += 8;
            composites.Skip(argumentsLength);
            haveInstructions |= (flags & HaveInstructions) != 0;
        } while ((flags & MoreComponents) != 0);
        glyf.Write(composites.Slice(start, composites.Position - start));
        if (haveInstructions)
        {
            int length = streams.Glyphs.U255();
            glyf.U16(length);
            glyf.Write(streams.Instructions.Bytes(length));
        }
        return streams.ReadBoundingBox();
    }

    // Simple: the point counts, flags and coordinate triplets are decoded, then written in the usual compact form.
    private static (int XMin, int YMin, int XMax, int YMax) WriteSimpleGlyph(ref GlyfStreams streams, TableWriter glyf, int contours, bool hasBoundingBox,
        int glyph, PointBuffers points)
    {
        int total = 0;
        points.EnsureContours(contours);
        for (int c = 0; c < contours; c++)
        {
            total += streams.Points.U255();
            if (total > MaxPointsPerGlyph) throw new FormatException("Damaged WOFF2 glyph.");
            points.EndPoints[c] = total - 1;
        }
        points.EnsurePoints(total);
        var pointFlags = streams.Flags.Bytes(total);
        var box = DecodeTriplets(pointFlags, streams.Glyphs.Rest, points, total, out int tripletBytes);
        streams.Glyphs.Skip(tripletBytes);
        int instructionLength = streams.Glyphs.U255();
        if (hasBoundingBox) box = streams.ReadBoundingBox();
        else if (total == 0) box = (0, 0, 0, 0);
        for (int c = 0; c < contours; c++) glyf.U16(points.EndPoints[c]);
        glyf.U16(instructionLength);
        glyf.Write(streams.Instructions.Bytes(instructionLength));
        WritePoints(glyf, points, total, streams.Overlaps(glyph));
        return box;
    }

    // The triplet encoding (WOFF2 section 5.2): each point's flag byte says how many bytes of the glyph stream its
    // coordinates take and how they split; the bounding box is computed as a fallback for glyphs that store none.
    private static (int XMin, int YMin, int XMax, int YMax) DecodeTriplets(ReadOnlySpan<byte> pointFlags, ReadOnlySpan<byte> glyphStream, PointBuffers points,
        int total, out int bytesUsed)
    {
        int at = 0, x = 0, y = 0;
        var box = (XMin: int.MaxValue, YMin: int.MaxValue, XMax: int.MinValue, YMax: int.MinValue);
        for (int p = 0; p < total; p++)
        {
            int flag = pointFlags[p];
            var (dx, dy) = DecodeTriplet(flag & 0x7F, glyphStream, ref at);
            x += dx;
            y += dy;
            if (x < box.XMin) box.XMin = x;
            if (x > box.XMax) box.XMax = x;
            if (y < box.YMin) box.YMin = y;
            if (y > box.YMax) box.YMax = y;
            points.Dx[p] = dx;
            points.Dy[p] = dy;
            points.Flags[p] = (byte)(flag >> 7 ^ OnCurve); // the high bit of the flag means off-curve
        }
        bytesUsed = at;
        return box;
    }

    // Flags 0..9 are a y delta only, 10..19 an x delta only, 20..83 one byte for both, 84..119 one byte each,
    // 120..123 twelve bits each, and 124..127 sixteen bits each. The low bit (or two) of the flag gives the signs.
    private static (int Dx, int Dy) DecodeTriplet(int flag, ReadOnlySpan<byte> stream, ref int at)
    {
        int dx, dy;
        if (flag < 10)
        {
            dx = 0;
            dy = ((flag & 14) << 7) + stream[at++];
            if ((flag & 1) == 0) dy = -dy;
            return (dx, dy);
        }
        if (flag < 20)
        {
            dx = (((flag - 10) & 14) << 7) + stream[at++];
            dy = 0;
            if ((flag & 1) == 0) dx = -dx;
            return (dx, dy);
        }
        if (flag < 84)
        {
            int b0 = flag - 20, b1 = stream[at++];
            dx = 1 + (b0 & 0x30) + (b1 >> 4);
            dy = 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F);
        }
        else if (flag < 120)
        {
            int b0 = flag - 84;
            dx = 1 + ((b0 / 12) << 8) + stream[at];
            dy = 1 + (((b0 % 12) >> 2) << 8) + stream[at + 1];
            at += 2;
        }
        else if (flag < 124)
        {
            int b2 = stream[at + 1];
            dx = (stream[at] << 4) + (b2 >> 4);
            dy = ((b2 & 0x0F) << 8) + stream[at + 2];
            at += 3;
        }
        else
        {
            dx = stream[at] << 8 | stream[at + 1];
            dy = stream[at + 2] << 8 | stream[at + 3];
            at += 4;
        }
        if ((flag & 1) == 0) dx = -dx;
        if ((flag & 2) == 0) dy = -dy;
        return (dx, dy);
    }

    /// <summary>
    /// Writes a simple glyph's flags and coordinates in the usual compact TrueType form. On entry the point flags
    /// hold each point's on-curve bit; the rest of each flag is filled in here.
    /// </summary>
    private static void WritePoints(TableWriter glyf, PointBuffers points, int count, bool overlaps)
    {
        var flags = points.Flags;
        for (int i = 0; i < count; i++)
        {
            flags[i] |= CoordinateFlags(points.Dx[i], XShort, XSameOrPositive);
            flags[i] |= CoordinateFlags(points.Dy[i], YShort, YSameOrPositive);
        }
        if (overlaps && count > 0) flags[0] |= OverlapSimple;
        var span = glyf.Reserve(count * MaxBytesPerPoint);
        int at = 0;
        for (int i = 0; i < count;)
        {
            int run = 1;
            while (i + run < count && flags[i + run] == flags[i] && run < MaxFlagRepeat) run++;
            if (run > 1)
            {
                span[at++] = (byte)(flags[i] | RepeatFlag);
                span[at++] = (byte)(run - 1);
            }
            else span[at++] = flags[i];
            i += run;
        }
        at = WriteCoordinates(span, at, points.Dx, flags, count, XShort, XSameOrPositive);
        at = WriteCoordinates(span, at, points.Dy, flags, count, YShort, YSameOrPositive);
        glyf.Commit(at);
    }

    // A zero delta is left out (the "same" bit), a delta under 256 takes one byte with its sign in the "positive" bit,
    // and any other takes two bytes.
    private static byte CoordinateFlags(int delta, byte shortBit, byte sameOrPositiveBit)
        => delta == 0 ? sameOrPositiveBit : delta is > -256 and < 256 ? (byte)(shortBit | (delta > 0 ? sameOrPositiveBit : 0)) : (byte)0;

    private static int WriteCoordinates(Span<byte> span, int at, int[] deltas, byte[] flags, int count, byte shortBit, byte sameOrPositiveBit)
    {
        for (int i = 0; i < count; i++)
        {
            int delta = deltas[i];
            if ((flags[i] & shortBit) != 0) span[at++] = (byte)Math.Abs(delta);
            else if ((flags[i] & sameOrPositiveBit) == 0)
            {
                BinaryPrimitives.WriteInt16BigEndian(span[at..], (short)Short(delta));
                at += 2;
            }
        }
        return at;
    }

    private static byte[] WriteLoca(int[] offsets, int indexFormat)
    {
        bool shortFormat = indexFormat == ShortLocaFormat;
        var loca = new byte[offsets.Length * (shortFormat ? 2 : 4)];
        for (int g = 0; g < offsets.Length; g++)
        {
            if (shortFormat)
            {
                if (offsets[g] > MaxShortLocaOffset) throw new FormatException("Damaged WOFF2 loca table.");
                BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(g * 2), (ushort)(offsets[g] >> 1));
            }
            else BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)offsets[g]);
        }
        return loca;
    }

    private static int Short(int value) => value is >= short.MinValue and <= short.MaxValue ? value : throw new FormatException("Damaged WOFF2 glyph.");

    // ------------------------------------------------------------------ hmtx

    // The transformed hmtx: a flags byte (bit 0: proportional bearings left out, bit 1: monospace bearings left out),
    // then the advances and whatever bearings are stored. Left-out bearings equal the glyphs' xMin.
    private const int ProportionalBearingsOmitted = 0x1, MonospaceBearingsOmitted = 0x2;
    private const int MaxpNumGlyphsField = 4, HheaNumberOfHMetricsField = 34;
    private const int HeadIndexToLocFormatField = 50, HeadMinimumLengthForLocFormat = 52;

    /// <summary>Rebuilds hmtx, taking left side bearings that were left out from the glyphs' xMin (WOFF2 section 5.4).</summary>
    private static byte[] ReconstructHmtx(Woff2Reader reader, byte[] glyf, byte[] loca, byte[] hhea, byte[] maxp, byte[]? head)
    {
        int flags = reader.U8();
        int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(MaxpNumGlyphsField));
        int numHMetrics = BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(HheaNumberOfHMetricsField));
        if (numHMetrics < 1 || numHMetrics > numGlyphs) throw new FormatException("Damaged WOFF2 hmtx table.");
        // Without a head table the loca format is judged from the table's size.
        bool longLoca = head != null && head.Length >= HeadMinimumLengthForLocFormat
            ? BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(HeadIndexToLocFormatField)) != 0
            : loca.Length >= (numGlyphs + 1) * 4;
        var advances = new int[numHMetrics];
        for (int i = 0; i < numHMetrics; i++) advances[i] = reader.U16();
        var hmtx = new byte[numHMetrics * 4 + (numGlyphs - numHMetrics) * 2];
        for (int i = 0; i < numHMetrics; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(i * 4), (ushort)advances[i]);
            short bearing = (short)((flags & ProportionalBearingsOmitted) != 0 ? GlyphXMin(glyf, loca, longLoca, i) : reader.S16());
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(i * 4 + 2), bearing);
        }
        for (int g = numHMetrics; g < numGlyphs; g++)
        {
            short bearing = (short)((flags & MonospaceBearingsOmitted) != 0 ? GlyphXMin(glyf, loca, longLoca, g) : reader.S16());
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(numHMetrics * 4 + (g - numHMetrics) * 2), bearing);
        }
        return hmtx;
    }

    // The xMin of a glyph's header, or 0 for an empty glyph.
    private static int GlyphXMin(byte[] glyf, byte[] loca, bool longLoca, int glyph)
    {
        int start = longLoca
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(glyph * 4))
            : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(glyph * 2)) * 2;
        int end = longLoca
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(glyph * 4 + 4))
            : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(glyph * 2 + 2)) * 2;
        return end - start >= GlyphHeaderLength ? BinaryPrimitives.ReadInt16BigEndian(glyf.AsSpan(start + GlyphXMinField)) : 0;
    }

    // ------------------------------------------------------------------ binary helpers

    /// <summary>
    /// A forward read position over part of the WOFF2 data, in big-endian. Reading past the end is a truncated file,
    /// reported as <see cref="FormatException"/>.
    /// </summary>
    private ref struct Woff2Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; }

        public readonly int Remaining => _data.Length - Position;

        /// <summary>The bytes from the cursor to the end.</summary>
        public readonly ReadOnlySpan<byte> Rest => _data.Slice(Position);

        private int Take(int count)
        {
            if (count < 0 || count > _data.Length - Position) throw new FormatException("The WOFF2 data is truncated.");
            int at = Position;
            Position += count;
            return at;
        }

        public void Skip(int count) => Take(count);
        public int U8() => _data[Take(1)];
        public int U16() => BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(Take(2)));
        public short S16() => BinaryPrimitives.ReadInt16BigEndian(_data.Slice(Take(2)));
        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(Take(4)));
        public ReadOnlySpan<byte> Bytes(int count) => _data.Slice(Take(count), count);

        /// <summary>Bytes already read, by their position.</summary>
        public readonly ReadOnlySpan<byte> Slice(int at, int count) => _data.Slice(at, count);

        /// <summary>A byte by its position, for the bitmaps.</summary>
        public readonly int ByteAt(int index) => index < _data.Length ? _data[index] : throw new FormatException("The WOFF2 data is truncated.");

        /// <summary>A reader over the next <paramref name="count"/> bytes, which this reader moves past.</summary>
        public Woff2Reader Sub(long count)
        {
            if (count > int.MaxValue) throw new FormatException("The WOFF2 data is truncated.");
            return new Woff2Reader(_data.Slice(Take((int)count), (int)count));
        }

        /// <summary>UIntBase128: 1 to 5 bytes, 7 bits each, no leading zeros, no overflow.</summary>
        public int Base128()
        {
            uint value = 0;
            for (int i = 0; i < 5; i++)
            {
                int b = U8();
                if (i == 0 && b == 0x80 || (value & 0xFE000000) != 0) throw new FormatException("Damaged WOFF2 number.");
                value = value << 7 | (uint)(b & 0x7F);
                if ((b & 0x80) == 0) return value <= int.MaxValue ? (int)value : throw new FormatException("Damaged WOFF2 number.");
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

    /// <summary>A growable big-endian byte buffer for a table being rebuilt.</summary>
    private sealed class TableWriter(int capacity)
    {
        private byte[] _buffer = new byte[capacity];

        public int Length { get; private set; }

        private Span<byte> Grow(int count)
        {
            var span = Reserve(count);
            Length += count;
            return span;
        }

        /// <summary>Space for up to <paramref name="count"/> bytes; <see cref="Commit"/> says how many were written.</summary>
        public Span<byte> Reserve(int count)
        {
            if (Length + count > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));
            return _buffer.AsSpan(Length, count);
        }

        public void Commit(int count) => Length += count;
        public void U16(int value) => BinaryPrimitives.WriteUInt16BigEndian(Grow(2), (ushort)value);
        public void Write(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Grow(bytes.Length));
        public void Skip(int count) => Grow(count).Clear();
        public void Align4() => Skip((4 - (Length & 3)) & 3);
        public void Patch16(int at, int value) => BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(at), (ushort)value);
        public byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();
    }
}
