namespace PdfPecker.Rendering.Images;

// Symbol dictionaries (6.5, 7.4.2), and the arithmetic integer decoding (Annex A) they share with text regions.
internal sealed partial class Jbig2Decoder
{
    private const int MaxSymbolSize = 1 << 16; // the largest symbol width or height a height class may reach

    // ------------------------------------------------------------------ arithmetic integer decoding (Annex A)

    /// <summary>The contexts of one arithmetic integer decoding procedure (A.2), such as IADH or IADW.</summary>
    private sealed class IntContext
    {
        // After the sign, a unary prefix of up to five bits picks a range: its value bits and the smallest magnitude it
        // codes (Table A.1).
        private static readonly (int Bits, int Offset)[] Ranges = { (2, 0), (4, 4), (6, 20), (8, 84), (12, 340), (32, 4436) };

        private readonly byte[] _cx = new byte[512];

        /// <summary>Decodes an integer; returns false for OOB.</summary>
        public bool TryDecode(MqDecoder mq, out int value)
        {
            int prev = 1;
            int sign = DecodeBit(mq, ref prev);
            int range = 0;
            while (range < Ranges.Length - 1 && DecodeBit(mq, ref prev) != 0) range++;
            long magnitude = DecodeBits(mq, ref prev, Ranges[range].Bits) + Ranges[range].Offset;
            if (sign == 1 && magnitude == 0)
            {
                value = 0;
                return false;
            }
            value = (int)Math.Clamp(sign == 1 ? -magnitude : magnitude, int.MinValue, int.MaxValue);
            return true;
        }

        private int DecodeBit(MqDecoder mq, ref int prev)
        {
            int bit = mq.Decode(_cx, prev);
            prev = prev < 256 ? prev << 1 | bit : ((prev << 1 | bit) & 511) | 256;
            return bit;
        }

        private long DecodeBits(MqDecoder mq, ref int prev, int count)
        {
            long r = 0;
            for (int i = 0; i < count; i++) r = r << 1 | (long)DecodeBit(mq, ref prev);
            return r;
        }
    }

    /// <summary>Decodes a symbol ID of <paramref name="codeLen"/> bits with the IAID procedure (A.3).</summary>
    private static int DecodeIaid(MqDecoder mq, byte[] cx, int codeLen)
    {
        int prev = 1;
        for (int i = 0; i < codeLen; i++) prev = prev << 1 | mq.Decode(cx, prev);
        return prev - (1 << codeLen);
    }

    /// <summary>
    /// The arithmetic decoding contexts of a symbol dictionary or text region, by their names in the standard. A symbol
    /// dictionary shares them with the text regions of its aggregated symbols.
    /// </summary>
    private sealed class ArithmeticContexts
    {
        public readonly IntContext Iadh = new(), Iadw = new(), Iaex = new(), Iaai = new(), Iadt = new(), Iafs = new(),
            Iads = new(), Iait = new(), Iari = new(), Iardw = new(), Iardh = new(), Iardx = new(), Iardy = new();
        public byte[] Iaid = Array.Empty<byte>();
        public byte[] Gb = Array.Empty<byte>(), Gr = Array.Empty<byte>();
    }

    /// <summary>
    /// The coded data of a symbol dictionary or text region: an MQ decoder with its contexts, or a bit reader when the
    /// segment is Huffman-coded.
    /// </summary>
    private sealed class SymbolCoder
    {
        public SymbolCoder(MqDecoder mq) => Mq = mq;

        public SymbolCoder(BitReader bits) => Bits = bits;

        public MqDecoder? Mq { get; }

        public BitReader? Bits { get; }

        public bool Huffman => Bits != null;

        public ArithmeticContexts Contexts { get; } = new();

        /// <summary>Decodes an integer with <paramref name="table"/> when Huffman-coded, else with <paramref name="context"/>; false for OOB.</summary>
        public bool TryDecodeInt(IntContext context, HuffTable? table, out int value) =>
            Huffman ? table!.Decode(Bits!, out value) : context.TryDecode(Mq!, out value);

        /// <summary>Decodes an integer where OOB has no meaning, which then reads as zero.</summary>
        public int DecodeInt(IntContext context, HuffTable? table)
        {
            TryDecodeInt(context, table, out int value);
            return value;
        }

        public void CheckData() => Jbig2Decoder.CheckData(Mq, Bits);
    }

    /// <summary>The symbol dictionaries and table segments that <paramref name="seg"/> refers to, in order.</summary>
    private (List<Bitmap> Symbols, CustomTables Tables) ReferredSymbolsAndTables(Segment seg)
    {
        var symbols = new List<Bitmap>();
        var tables = new List<HuffTable>();
        foreach (int r in seg.Refs)
        {
            if (_symbols.TryGetValue(r, out var s)) symbols.AddRange(s);
            else if (_tables.TryGetValue(r, out var t)) tables.Add(t);
        }
        return (symbols, new CustomTables(tables));
    }

    private static int CeilLog2(int v)
    {
        int n = 0;
        while ((1L << n) < v) n++;
        return n;
    }

    // ------------------------------------------------------------------ symbol dictionaries (6.5)

    /// <summary>The flags of a symbol dictionary segment (7.4.2.1.1).</summary>
    private readonly record struct SymbolDictionaryFlags(int Bits)
    {
        public bool Huffman => (Bits & 1) != 0;
        public bool RefinementAggregation => (Bits & 2) != 0;
        public int HeightTable => (Bits >> 2) & 3;
        public int WidthTable => (Bits >> 4) & 3;
        public int BitmapSizeTable => (Bits >> 6) & 1;
        public int AggregateTable => (Bits >> 7) & 1;
        public int Template => (Bits >> 10) & 3;
        public int RefinementTemplate => (Bits >> 12) & 1;

        /// <summary>Huffman-coded symbols without refinement are stored side by side in one bitmap per height class.</summary>
        public bool CollectiveBitmaps => Huffman && !RefinementAggregation;
    }

    /// <summary>The Huffman tables of a Huffman-coded symbol dictionary.</summary>
    private sealed record SymbolDictionaryTables(HuffTable Height, HuffTable Width, HuffTable BitmapSize, HuffTable Aggregate);

    /// <summary>A symbol dictionary's parameters, from its header and the segments it refers to.</summary>
    private sealed record SymbolDictionary(
        Segment Segment,
        SymbolDictionaryFlags Flags,
        sbyte[] At,
        sbyte[] RefinementAt,
        int NewCount,
        List<Bitmap> Input,
        SymbolDictionaryTables? Tables,
        int CodeLength);

    private Bitmap[] DecodeSymbolDictionary(Segment seg)
    {
        if (seg.Length < 2) throw new InvalidDataException("Short JBIG2 symbol dictionary.");
        var reader = seg.Reader();
        var flags = new SymbolDictionaryFlags(reader.U16());
        var at = ReadAdaptivePixels(ref reader, 8, flags.Huffman ? 0 : flags.Template == 0 ? 8 : 2);
        var refinementAt = ReadAdaptivePixels(ref reader, 4, flags.RefinementAggregation && flags.RefinementTemplate == 0 ? 4 : 0);
        uint exportedCount = reader.U32(), newCount = reader.U32();
        if (exportedCount > MaxSymbols || newCount > MaxSymbols) throw new InvalidDataException("Too many JBIG2 symbols.");

        var (input, custom) = ReferredSymbolsAndTables(seg);
        var tables = flags.Huffman
            ? new SymbolDictionaryTables(
                SelectTable(flags.HeightTable, custom, 4, 5),
                SelectTable(flags.WidthTable, custom, 2, 3),
                SelectTable(flags.BitmapSizeTable, custom, 1),
                SelectTable(flags.AggregateTable, custom, 1))
            : null;

        var coder = flags.Huffman
            ? new SymbolCoder(new BitReader(seg.Data, reader.Position, seg.End))
            : new SymbolCoder(new MqDecoder(seg.Data, reader.Position, seg.End));
        int codeLength = CeilLog2(input.Count + (int)newCount);
        if (!flags.Huffman)
        {
            coder.Contexts.Gb = new byte[GenericContextSize(flags.Template)];
            coder.Contexts.Iaid = new byte[1 << (codeLength + 1)];
        }
        if (!flags.Huffman || flags.RefinementAggregation) coder.Contexts.Gr = new byte[RefinementContextSize(flags.RefinementTemplate)];

        var dict = new SymbolDictionary(seg, flags, at, refinementAt, (int)newCount, input, tables, codeLength);
        var newSymbols = DecodeNewSymbols(dict, coder);
        var all = new List<Bitmap>(input);
        all.AddRange(newSymbols);
        return ReadExportedSymbols(coder, all);
    }

    /// <summary>Decodes the new symbols, height class by height class (6.5.5).</summary>
    private List<Bitmap> DecodeNewSymbols(SymbolDictionary dict, SymbolCoder coder)
    {
        var newSymbols = new List<Bitmap>();
        int height = 0;
        while (newSymbols.Count < dict.NewCount)
        {
            if (!coder.TryDecodeInt(coder.Contexts.Iadh, dict.Tables?.Height, out int deltaHeight))
                throw new InvalidDataException("Bad JBIG2 height class.");
            height += deltaHeight;
            if (height < 0 || height > MaxSymbolSize) throw new InvalidDataException("Bad JBIG2 symbol height.");
            DecodeHeightClass(dict, coder, newSymbols, height);
        }
        return newSymbols;
    }

    /// <summary>Decodes the symbols of one height, each width coded as a difference from the previous one, until OOB.</summary>
    private void DecodeHeightClass(SymbolDictionary dict, SymbolCoder coder, List<Bitmap> newSymbols, int height)
    {
        int width = 0, totalWidth = 0, first = newSymbols.Count;
        var collectiveWidths = new List<int>();
        while (coder.TryDecodeInt(coder.Contexts.Iadw, dict.Tables?.Width, out int deltaWidth))
        {
            if (newSymbols.Count >= dict.NewCount) throw new InvalidDataException("Too many JBIG2 symbols in a height class.");
            coder.CheckData();
            width += deltaWidth;
            if (width < 0 || width > MaxSymbolSize) throw new InvalidDataException("Bad JBIG2 symbol width.");
            totalWidth += width;
            if (dict.Flags.CollectiveBitmaps)
            {
                collectiveWidths.Add(width);
                newSymbols.Add(null!); // filled from the collective bitmap below
            }
            else newSymbols.Add(DecodeSymbolBitmap(dict, coder, newSymbols, width, height));
        }
        if (!dict.Flags.CollectiveBitmaps) return;
        var collective = DecodeCollectiveBitmap(dict, coder.Bits!, totalWidth, height);
        int x0 = 0;
        for (int i = 0; i < collectiveWidths.Count; i++)
        {
            newSymbols[first + i] = Crop(collective, x0, 0, collectiveWidths[i], height);
            x0 += collectiveWidths[i];
        }
    }

    /// <summary>Decodes one symbol's bitmap: a generic region, or with refinement and aggregation a refined symbol or a text region.</summary>
    private Bitmap DecodeSymbolBitmap(SymbolDictionary dict, SymbolCoder coder, List<Bitmap> newSymbols, int width, int height)
    {
        if (!dict.Flags.RefinementAggregation)
            return DecodeGenericArith(coder.Mq!, coder.Contexts.Gb, width, height, dict.Flags.Template, false, dict.At);
        if (!coder.TryDecodeInt(coder.Contexts.Iaai, dict.Tables?.Aggregate, out int instances))
            throw new InvalidDataException("Bad JBIG2 aggregate.");
        // The symbols so far, from the referred dictionaries and this one, are what the new symbol is built from.
        var available = new List<Bitmap>(dict.Input);
        available.AddRange(newSymbols);
        return instances > 1
            ? DecodeAggregateSymbol(dict, coder, available, instances, width, height)
            : DecodeRefinedSymbol(dict, coder, available, width, height);
    }

    /// <summary>A symbol made of several others, decoded as a text region of the symbol's size (6.5.8.2.1).</summary>
    private Bitmap DecodeAggregateSymbol(SymbolDictionary dict, SymbolCoder coder, List<Bitmap> available, int instances, int width,
        int height) =>
        DecodeText(new TextRegion
        {
            Width = width,
            Height = height,
            Instances = instances,
            Symbols = available,
            Coder = coder,
            Refine = true,
            Corner = ReferenceCorner.TopLeft,
            Op = CombinationOperator.Or,
            RefinementTemplate = dict.Flags.RefinementTemplate,
            RefinementAt = dict.RefinementAt,
            CodeLength = coder.Huffman ? Math.Max(1, dict.CodeLength) : dict.CodeLength,
            Tables = coder.Huffman
                ? new TextRegionTables(Std(6), Std(8), Std(11), Std(15), Std(15), Std(15), Std(15), Std(1))
                : null,
        });

    /// <summary>A symbol that refines one other symbol (6.5.8.2.2).</summary>
    private Bitmap DecodeRefinedSymbol(SymbolDictionary dict, SymbolCoder coder, List<Bitmap> available, int width, int height)
    {
        int rtemplate = dict.Flags.RefinementTemplate;
        if (coder.Bits is { } br)
        {
            int id = (int)br.ReadBits(Math.Max(1, dict.CodeLength));
            Std(15).Decode(br, out int dx);
            Std(15).Decode(br, out int dy);
            Std(1).Decode(br, out int bmSize);
            br.Align();
            if ((uint)id >= (uint)available.Count) throw new InvalidDataException("Bad JBIG2 symbol id.");
            // The refinement is an arithmetic-coded bitmap of the given byte size inside the Huffman-coded data.
            var seg = dict.Segment;
            var rmq = new MqDecoder(seg.Data, br.Position, Math.Min(seg.End, br.Position + Math.Max(0, bmSize)));
            var bmp = DecodeRefinement(rmq, coder.Contexts.Gr, width, height, rtemplate, available[id], dx, dy, false, dict.RefinementAt);
            br.Position += Math.Max(0, bmSize);
            return bmp;
        }
        else
        {
            var mq = coder.Mq!;
            int id = DecodeIaid(mq, coder.Contexts.Iaid, dict.CodeLength);
            coder.Contexts.Iardx.TryDecode(mq, out int dx);
            coder.Contexts.Iardy.TryDecode(mq, out int dy);
            if ((uint)id >= (uint)available.Count) throw new InvalidDataException("Bad JBIG2 symbol id.");
            return DecodeRefinement(mq, coder.Contexts.Gr, width, height, rtemplate, available[id], dx, dy, false, dict.RefinementAt);
        }
    }

    /// <summary>The collective bitmap of a height class (6.5.9): uncompressed when its coded size is zero, else MMR.</summary>
    private Bitmap DecodeCollectiveBitmap(SymbolDictionary dict, BitReader br, int totalWidth, int height)
    {
        var seg = dict.Segment;
        var d = seg.Data;
        dict.Tables!.BitmapSize.Decode(br, out int bmSize);
        br.Align();
        Bitmap collective;
        if (bmSize == 0)
        {
            int rowBytes = (totalWidth + 7) / 8;
            if ((long)rowBytes * height > seg.End - br.Position) throw new InvalidDataException("Short JBIG2 collective bitmap.");
            collective = NewBitmap(totalWidth, height);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < totalWidth; x++)
                {
                    int bi = br.Position + y * rowBytes + (x >> 3);
                    collective.P[y * totalWidth + x] = (byte)(bi < seg.End ? d[bi] >> (7 - (x & 7)) & 1 : 0);
                }
            br.Position += rowBytes * height;
        }
        else
        {
            collective = DecodeGenericMmr(d[..Math.Min(seg.End, br.Position + bmSize)], br.Position, totalWidth, height, out _);
            br.Position += bmSize;
        }
        return collective;
    }

    /// <summary>The exported symbols (6.5.10): alternating runs of symbols that are not exported and that are.</summary>
    private static Bitmap[] ReadExportedSymbols(SymbolCoder coder, List<Bitmap> all)
    {
        var exported = new List<Bitmap>();
        int index = 0;
        bool exportFlag = false;
        for (int guard = 0; index < all.Count && guard <= 2 * all.Count + 2; guard++)
        {
            if (!coder.TryDecodeInt(coder.Contexts.Iaex, Std(1), out int run)) break;
            if (run < 0 || run > all.Count - index) throw new InvalidDataException("Bad JBIG2 export run.");
            if (exportFlag) for (int i = 0; i < run; i++) exported.Add(all[index + i]);
            index += run;
            exportFlag = !exportFlag;
        }
        return exported.ToArray();
    }
}
