namespace PdfPecker.Rendering.Images;

// Text regions (6.4, 7.4.3): symbols placed along strips, optionally refined.
internal sealed partial class Jbig2Decoder
{
    /// <summary>The corner of a symbol instance that its coordinates name (REFCORNER).</summary>
    private enum ReferenceCorner
    {
        BottomLeft = 0,
        TopLeft = 1,
        BottomRight = 2,
        TopRight = 3,
    }

    /// <summary>The flags of a text region segment (7.4.3.1.1).</summary>
    private readonly record struct TextRegionFlags(int Bits)
    {
        public bool Huffman => (Bits & 1) != 0;
        public bool Refine => (Bits & 2) != 0;
        public int LogStrips => (Bits >> 2) & 3;
        public ReferenceCorner Corner => (ReferenceCorner)((Bits >> 4) & 3);
        public bool Transposed => (Bits & 0x40) != 0;
        public CombinationOperator Op => (CombinationOperator)((Bits >> 7) & 3);
        public int DefaultPixel => (Bits >> 9) & 1;

        /// <summary>A five-bit signed offset added to every S step after the first in a strip.</summary>
        public int DsOffset => (Bits >> 10 & 0x1F) >= 16 ? (Bits >> 10 & 0x1F) - 32 : Bits >> 10 & 0x1F;

        public int RefinementTemplate => (Bits >> 15) & 1;
    }

    /// <summary>The Huffman table selectors of a Huffman-coded text region (7.4.3.1.2).</summary>
    private readonly record struct TextHuffmanFlags(int Bits)
    {
        public int FirstS => Bits & 3;
        public int DeltaS => (Bits >> 2) & 3;
        public int DeltaT => (Bits >> 4) & 3;
        public int RefinementWidth => (Bits >> 6) & 3;
        public int RefinementHeight => (Bits >> 8) & 3;
        public int RefinementX => (Bits >> 10) & 3;
        public int RefinementY => (Bits >> 12) & 3;
        public int RefinementSize => (Bits >> 14) & 1;
    }

    /// <summary>The Huffman tables of a Huffman-coded text region.</summary>
    private sealed record TextRegionTables(
        HuffTable FirstS,
        HuffTable DeltaS,
        HuffTable DeltaT,
        HuffTable RefinementWidth,
        HuffTable RefinementHeight,
        HuffTable RefinementX,
        HuffTable RefinementY,
        HuffTable RefinementSize);

    /// <summary>The parameters of a text region: from a text region segment, or for an aggregated symbol from its dictionary.</summary>
    private sealed record TextRegion
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Instances { get; init; }
        public required List<Bitmap> Symbols { get; init; }
        public required SymbolCoder Coder { get; init; }
        public bool Refine { get; init; }
        public int LogStrips { get; init; }
        public ReferenceCorner Corner { get; init; }
        public bool Transposed { get; init; }
        public CombinationOperator Op { get; init; }
        public int DefaultPixel { get; init; }
        public int DsOffset { get; init; }
        public int RefinementTemplate { get; init; }
        public sbyte[] RefinementAt { get; init; } = new sbyte[4];

        /// <summary>The bits of a symbol ID that is not Huffman-coded.</summary>
        public int CodeLength { get; init; }

        public TextRegionTables? Tables { get; init; }

        /// <summary>The symbol ID codes of a Huffman-coded text region segment.</summary>
        public HuffTable? SymbolCodes { get; init; }
    }

    private Region? DecodeTextRegionSegment(Segment seg)
    {
        var reader = seg.Reader();
        var info = RegionInfo.Read(ref reader);
        var flags = new TextRegionFlags(reader.U16());
        var huffmanFlags = new TextHuffmanFlags(flags.Huffman ? reader.U16() : 0);
        var rat = ReadAdaptivePixels(ref reader, 4, flags.Refine && flags.RefinementTemplate == 0 ? 4 : 0);
        int instances = (int)Math.Min(reader.U32(), int.MaxValue);

        var (symbols, custom) = ReferredSymbolsAndTables(seg);
        if (info.W <= 0 || info.H <= 0) return null;
        SymbolCoder coder;
        TextRegionTables? tables = null;
        HuffTable? symbolCodes = null;
        int codeLength = 0;
        if (flags.Huffman)
        {
            tables = new TextRegionTables(
                SelectTable(huffmanFlags.FirstS, custom, 6, 7),
                SelectTable(huffmanFlags.DeltaS, custom, 8, 9, 10),
                SelectTable(huffmanFlags.DeltaT, custom, 11, 12, 13),
                SelectTable(huffmanFlags.RefinementWidth, custom, 14, 15),
                SelectTable(huffmanFlags.RefinementHeight, custom, 14, 15),
                SelectTable(huffmanFlags.RefinementX, custom, 14, 15),
                SelectTable(huffmanFlags.RefinementY, custom, 14, 15),
                SelectTable(huffmanFlags.RefinementSize, custom, 1));
            var br = new BitReader(seg.Data, reader.Position, seg.End);
            symbolCodes = ReadSymbolCodes(br, symbols.Count);
            br.Align();
            coder = new SymbolCoder(br);
        }
        else
        {
            codeLength = CeilLog2(symbols.Count);
            coder = new SymbolCoder(new MqDecoder(seg.Data, reader.Position, seg.End));
            coder.Contexts.Iaid = new byte[1 << (codeLength + 1)];
        }
        coder.Contexts.Gr = new byte[RefinementContextSize(flags.RefinementTemplate)];

        var bitmap = DecodeText(new TextRegion
        {
            Width = info.W,
            Height = info.H,
            Instances = instances,
            Symbols = symbols,
            Coder = coder,
            Refine = flags.Refine,
            LogStrips = flags.LogStrips,
            Corner = flags.Corner,
            Transposed = flags.Transposed,
            Op = flags.Op,
            DefaultPixel = flags.DefaultPixel,
            DsOffset = flags.DsOffset,
            RefinementTemplate = flags.RefinementTemplate,
            RefinementAt = rat,
            CodeLength = codeLength,
            Tables = tables,
            SymbolCodes = symbolCodes,
        });
        return new Region(bitmap, info);
    }

    /// <summary>Decodes a text region (6.4.5): strips of symbol instances, each strip starting with its T step.</summary>
    private Bitmap DecodeText(TextRegion t)
    {
        var region = NewBitmap(t.Width, t.Height);
        region.Fill(t.DefaultPixel);
        var coder = t.Coder;
        int strips = 1 << t.LogStrips;
        int stripT = -coder.DecodeInt(coder.Contexts.Iadt, t.Tables?.DeltaT) * strips;
        int firstS = 0;
        int instances = 0;
        while (instances < t.Instances)
        {
            if (!coder.TryDecodeInt(coder.Contexts.Iadt, t.Tables?.DeltaT, out int dt)) throw new InvalidDataException("Bad JBIG2 strip.");
            stripT += dt * strips;
            DecodeStrip(t, region, stripT, ref firstS, ref instances);
        }
        return region;
    }

    /// <summary>Decodes and draws the instances of one strip, each S step a difference from the last, until OOB.</summary>
    private void DecodeStrip(TextRegion t, Bitmap region, int stripT, ref int firstS, ref int instances)
    {
        var coder = t.Coder;
        firstS += coder.DecodeInt(coder.Contexts.Iafs, t.Tables?.FirstS);
        int s = firstS;
        while (instances < t.Instances)
        {
            coder.CheckData();
            int curT = t.LogStrips == 0 ? 0 : coder.Huffman ? (int)coder.Bits!.ReadBits(t.LogStrips) : coder.DecodeInt(coder.Contexts.Iait, null);
            var symbol = DecodeSymbolInstance(t);
            s = DrawSymbolInstance(t, region, symbol, s, stripT + curT);
            instances++;
            if (!coder.TryDecodeInt(coder.Contexts.Iads, t.Tables?.DeltaS, out int ds)) break;
            s += ds + t.DsOffset;
        }
    }

    /// <summary>Decodes a symbol instance's ID and, when it is refined, the refined bitmap.</summary>
    private Bitmap DecodeSymbolInstance(TextRegion t)
    {
        var coder = t.Coder;
        int id;
        if (coder.Bits is { } br)
        {
            if (t.SymbolCodes != null) t.SymbolCodes.Decode(br, out id);
            else id = (int)br.ReadBits(t.CodeLength);
        }
        else id = DecodeIaid(coder.Mq!, coder.Contexts.Iaid, t.CodeLength);
        if ((uint)id >= (uint)t.Symbols.Count) throw new InvalidDataException("Bad JBIG2 symbol id.");
        int ri = 0;
        if (t.Refine) ri = coder.Bits is { } rb ? rb.ReadBit() : coder.DecodeInt(coder.Contexts.Iari, null);
        return ri == 0 ? t.Symbols[id] : DecodeRefinedInstance(t, t.Symbols[id]);
    }

    /// <summary>Refines a symbol instance (6.4.11.3): its size and offset change, then the bitmap is refined.</summary>
    private Bitmap DecodeRefinedInstance(TextRegion t, Bitmap symbol)
    {
        var coder = t.Coder;
        var cx = coder.Contexts;
        int rdw = coder.DecodeInt(cx.Iardw, t.Tables?.RefinementWidth), rdh = coder.DecodeInt(cx.Iardh, t.Tables?.RefinementHeight);
        int rdx = coder.DecodeInt(cx.Iardx, t.Tables?.RefinementX), rdy = coder.DecodeInt(cx.Iardy, t.Tables?.RefinementY);
        int w = symbol.W + rdw, h = symbol.H + rdh;
        if (w < 0 || h < 0) throw new InvalidDataException("Bad JBIG2 refinement size.");
        int dx = (rdw >> 1) + rdx, dy = (rdh >> 1) + rdy;
        if (coder.Bits is not { } br) return DecodeRefinement(coder.Mq!, cx.Gr, w, h, t.RefinementTemplate, symbol, dx, dy, false, t.RefinementAt);
        // A Huffman-coded region, or the aggregated symbol of a Huffman-coded dictionary, carries each refinement as an
        // arithmetic-coded bitmap of a given byte size in the same segment data.
        t.Tables!.RefinementSize.Decode(br, out int bmSize);
        br.Align();
        return DecodeRefinement(br.TakeArithmeticData(bmSize), cx.Gr, w, h, t.RefinementTemplate, symbol, dx, dy, false, t.RefinementAt);
    }

    /// <summary>Draws a symbol instance with its reference corner at (<paramref name="s"/>, <paramref name="tt"/>); returns the S after it.</summary>
    private int DrawSymbolInstance(TextRegion t, Bitmap region, Bitmap ib, int s, int tt)
    {
        int wi = ib.W, hi = ib.H;
        bool right = t.Corner is ReferenceCorner.BottomRight or ReferenceCorner.TopRight;
        bool bottom = t.Corner is ReferenceCorner.BottomLeft or ReferenceCorner.BottomRight;
        if (!t.Transposed && right) s += wi - 1;
        else if (t.Transposed && bottom) s += hi - 1;
        int x, y;
        if (!t.Transposed)
        {
            x = right ? s - wi + 1 : s;
            y = bottom ? tt - hi + 1 : tt;
        }
        else
        {
            x = right ? tt - wi + 1 : tt;
            y = bottom ? s - hi + 1 : s;
        }
        Work((long)wi * hi + 16);
        region.Compose(ib, x, y, t.Op);
        if (!t.Transposed && !right) s += wi - 1;
        else if (t.Transposed && !bottom) s += hi - 1;
        return s;
    }
}
