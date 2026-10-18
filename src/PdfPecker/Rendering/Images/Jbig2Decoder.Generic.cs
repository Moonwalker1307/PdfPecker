namespace PdfPecker.Rendering.Images;

// Generic region decoding (6.2) and generic refinement region decoding (6.3), with their segments (7.4.6, 7.4.7).
internal sealed partial class Jbig2Decoder
{
    /// <summary>The flags byte of a generic region segment (7.4.6.2).</summary>
    private readonly record struct GenericRegionFlags(int Bits)
    {
        public bool Mmr => (Bits & 1) != 0;
        public int Template => (Bits >> 1) & 3;
        public bool TypicalPrediction => (Bits & 8) != 0;

        /// <summary>The adaptive template bytes that follow: four pixels for template 0, one for the others, none for MMR.</summary>
        public int AdaptivePixelBytes => Mmr ? 0 : Template == 0 ? 8 : 2;
    }

    /// <summary>The flags byte of a generic refinement region segment (7.4.7.2).</summary>
    private readonly record struct RefinementRegionFlags(int Bits)
    {
        public int Template => Bits & 1;
        public bool TypicalPrediction => (Bits & 2) != 0;
    }

    /// <summary>
    /// Reads <paramref name="count"/> adaptive template offsets, as (x, y) byte pairs, into an array of
    /// <paramref name="length"/>. Bytes at or past <paramref name="end"/> read as zero.
    /// </summary>
    private static sbyte[] ReadAdaptivePixels(ref SegmentReader reader, int length, int count, int end)
    {
        var at = new sbyte[length];
        for (int i = 0; i < count; i++) at[i] = reader.S8Before(end);
        return at;
    }

    /// <summary>As the other overload, but the offsets are bounded only by the stream, past whose end a read throws.</summary>
    private static sbyte[] ReadAdaptivePixels(ref SegmentReader reader, int length, int count)
    {
        var at = new sbyte[length];
        for (int i = 0; i < count; i++) at[i] = reader.S8();
        return at;
    }

    private Region? DecodeGenericRegionSegment(Segment seg)
    {
        var reader = seg.Reader();
        var info = RegionInfo.Read(ref reader);
        if (reader.Position >= seg.End) return null;
        var flags = new GenericRegionFlags(reader.U8());
        var at = ReadAdaptivePixels(ref reader, 8, flags.AdaptivePixelBytes, seg.End);
        if (info.H == int.MaxValue)
        {
            // Unknown height: the row count follows the data.
            int rows = int.MaxValue;
            if (seg.End - 4 >= reader.Position) rows = (int)Math.Min(reader.U32At(seg.End - 4), int.MaxValue);
            info = info with { H = rows };
        }
        if (info.W <= 0 || info.H <= 0) return null;
        var bitmap = flags.Mmr
            ? DecodeGenericMmr(seg.Data[..seg.End], reader.Position, info.W, info.H, out _)
            : DecodeGenericArith(new MqDecoder(seg.Data, reader.Position, seg.End), new byte[GenericContextSize(flags.Template)],
                info.W, info.H, flags.Template, flags.TypicalPrediction, at);
        return new Region(bitmap, info);
    }

    private Region? DecodeRefinementSegment(Segment seg)
    {
        var reader = seg.Reader();
        var info = RegionInfo.Read(ref reader);
        if (reader.Position >= seg.End) return null;
        var flags = new RefinementRegionFlags(reader.U8());
        var at = ReadAdaptivePixels(ref reader, 4, flags.Template == 0 ? 4 : 0, seg.End);
        // The reference is the intermediate region the segment refers to, or else the page under the region.
        var reference = TakeReferredRegion(seg);
        if (reference == null)
        {
            if (_page == null) return null;
            reference = Crop(_page, info.X, info.Y, info.W, info.H);
        }
        if (info.W <= 0 || info.H <= 0) return null;
        var mq = new MqDecoder(seg.Data, reader.Position, seg.End);
        var bitmap = DecodeRefinement(mq, new byte[RefinementContextSize(flags.Template)], info.W, info.H, flags.Template, reference,
            0, 0, flags.TypicalPrediction, at);
        return new Region(bitmap, info);
    }

    /// <summary>Removes and returns the first intermediate region the segment refers to, which a refinement uses up.</summary>
    private Bitmap? TakeReferredRegion(Segment seg)
    {
        foreach (int r in seg.Refs)
            if (_regions.Remove(r, out var region)) return region;
        return null;
    }

    // ------------------------------------------------------------------ generic region decoding (6.2)

    private static readonly int[] TpgdContexts = { 0x9B25, 0x0795, 0x00E5, 0x0195 };

    // The default adaptive pixel positions of template 0, which allow a faster row-buffered context.
    private static readonly sbyte[] NominalTemplate0Pixels = { 3, -1, -3, -1, 2, -2, -2, -2 };

    private static int GenericContextSize(int template) => template == 0 ? 1 << 16 : template == 1 ? 1 << 13 : 1 << 10;

    private Bitmap DecodeGenericArith(MqDecoder mq, byte[] cx, int w, int h, int template, bool tpgdon, sbyte[] at, Bitmap? skip = null)
    {
        var b = NewBitmap(w, h);
        Work((long)w * h);
        bool ltp = false;
        bool nominal0 = template == 0 && at.Length >= 8 && at.AsSpan(0, 8).SequenceEqual(NominalTemplate0Pixels);
        for (int y = 0; y < h; y++)
        {
            CheckData(mq);
            if (tpgdon)
            {
                ltp ^= mq.Decode(cx, TpgdContexts[template]) != 0;
                if (ltp)
                {
                    if (y > 0) Buffer.BlockCopy(b.P, (y - 1) * w, b.P, y * w, w);
                    continue;
                }
            }
            if (nominal0 && skip == null)
            {
                DecodeRowTemplate0(mq, cx, b, y);
                continue;
            }
            for (int x = 0; x < w; x++)
            {
                if (skip != null && skip.P[y * w + x] != 0) continue;
                int c = template switch
                {
                    0 => Context0(b, x, y, at),
                    1 => Context1(b, x, y, at),
                    2 => Context2(b, x, y, at),
                    _ => Context3(b, x, y, at),
                };
                if (mq.Decode(cx, c) != 0) b.P[y * w + x] = 1;
            }
        }
        return b;
    }

    private static void DecodeRowTemplate0(MqDecoder mq, byte[] cx, Bitmap b, int y)
    {
        int w = b.W;
        var p = b.P;
        int r1 = (y - 1) * w, r2 = (y - 2) * w, r0 = y * w;
        bool has1 = y >= 1, has2 = y >= 2;
        int G1(int x) => has1 && (uint)x < (uint)w ? p[r1 + x] : 0;
        int G2(int x) => has2 && (uint)x < (uint)w ? p[r2 + x] : 0;
        // Windows over rows y-2 (x-2..x+2), y-1 (x-3..x+3) and y (x-4..x-1), shifted one pixel per step.
        int line2 = G2(-2) << 4 | G2(-1) << 3 | G2(0) << 2 | G2(1) << 1 | G2(2);
        int line1 = G1(-3) << 6 | G1(-2) << 5 | G1(-1) << 4 | G1(0) << 3 | G1(1) << 2 | G1(2) << 1 | G1(3);
        int line0 = 0;
        for (int x = 0; x < w; x++)
        {
            // Spec bit order: y-2 row: A4(-2) b15, -1 b14, 0 b13, +1 b12, A3(+2) b11;
            // y-1 row: A2(-3) b10, -2 b9, -1 b8, 0 b7, +1 b6, +2 b5, A1(+3) b4; y row: -4 b3 .. -1 b0.
            int c = line2 << 11 | line1 << 4 | line0;
            int bit = mq.Decode(cx, c);
            if (bit != 0) p[r0 + x] = 1;
            line2 = (line2 << 1 & 0x1F) | G2(x + 3);
            line1 = (line1 << 1 & 0x7F) | G1(x + 4);
            line0 = (line0 << 1 & 0xF) | bit;
        }
    }

    private static int Context0(Bitmap b, int x, int y, sbyte[] at) =>
        b.Get(x - 1, y) | b.Get(x - 2, y) << 1 | b.Get(x - 3, y) << 2 | b.Get(x - 4, y) << 3
        | b.Get(x + at[0], y + at[1]) << 4
        | b.Get(x + 2, y - 1) << 5 | b.Get(x + 1, y - 1) << 6 | b.Get(x, y - 1) << 7 | b.Get(x - 1, y - 1) << 8 | b.Get(x - 2, y - 1) << 9
        | b.Get(x + at[2], y + at[3]) << 10 | b.Get(x + at[4], y + at[5]) << 11
        | b.Get(x + 1, y - 2) << 12 | b.Get(x, y - 2) << 13 | b.Get(x - 1, y - 2) << 14
        | b.Get(x + at[6], y + at[7]) << 15;

    private static int Context1(Bitmap b, int x, int y, sbyte[] at) =>
        b.Get(x - 1, y) | b.Get(x - 2, y) << 1 | b.Get(x - 3, y) << 2
        | b.Get(x + at[0], y + at[1]) << 3
        | b.Get(x + 2, y - 1) << 4 | b.Get(x + 1, y - 1) << 5 | b.Get(x, y - 1) << 6 | b.Get(x - 1, y - 1) << 7 | b.Get(x - 2, y - 1) << 8
        | b.Get(x + 2, y - 2) << 9 | b.Get(x + 1, y - 2) << 10 | b.Get(x, y - 2) << 11 | b.Get(x - 1, y - 2) << 12;

    private static int Context2(Bitmap b, int x, int y, sbyte[] at) =>
        b.Get(x - 1, y) | b.Get(x - 2, y) << 1
        | b.Get(x + at[0], y + at[1]) << 2
        | b.Get(x + 1, y - 1) << 3 | b.Get(x, y - 1) << 4 | b.Get(x - 1, y - 1) << 5 | b.Get(x - 2, y - 1) << 6
        | b.Get(x + 1, y - 2) << 7 | b.Get(x, y - 2) << 8 | b.Get(x - 1, y - 2) << 9;

    private static int Context3(Bitmap b, int x, int y, sbyte[] at) =>
        b.Get(x - 1, y) | b.Get(x - 2, y) << 1 | b.Get(x - 3, y) << 2 | b.Get(x - 4, y) << 3
        | b.Get(x + at[0], y + at[1]) << 4
        | b.Get(x + 1, y - 1) << 5 | b.Get(x, y - 1) << 6 | b.Get(x - 1, y - 1) << 7 | b.Get(x - 2, y - 1) << 8 | b.Get(x - 3, y - 1) << 9;

    private Bitmap DecodeGenericMmr(byte[] d, int start, int w, int h, out int end)
    {
        var b = NewBitmap(w, h);
        Work((long)w * h);
        var packed = CcittFaxDecoder.DecodeMmr(d, start, w, h, out end);
        int rowBytes = (w + 7) / 8;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                b.P[y * w + x] = (byte)(packed[y * rowBytes + (x >> 3)] >> (7 - (x & 7)) & 1);
        return b;
    }

    // ------------------------------------------------------------------ generic refinement (6.3)

    private static int RefinementContextSize(int template) => template == 0 ? 1 << 13 : 1 << 10;

    private Bitmap DecodeRefinement(MqDecoder mq, byte[] cx, int w, int h, int template, Bitmap reference, int dx, int dy,
        bool tpgron, sbyte[] at)
    {
        var b = NewBitmap(w, h);
        Work((long)w * h * 2);
        bool ltp = false;
        for (int y = 0; y < h; y++)
        {
            CheckData(mq);
            if (tpgron) ltp ^= mq.Decode(cx, template == 0 ? 0x0100 : 0x0080) != 0;
            for (int x = 0; x < w; x++)
            {
                int rx = x - dx, ry = y - dy;
                if (ltp)
                {
                    // Typical prediction: a uniform 3x3 reference neighborhood predicts the pixel.
                    int v = reference.Get(rx, ry);
                    bool uniform = true;
                    for (int j = -1; j <= 1 && uniform; j++)
                        for (int i = -1; i <= 1; i++)
                            if (reference.Get(rx + i, ry + j) != v) { uniform = false; break; }
                    if (uniform)
                    {
                        b.P[y * w + x] = (byte)v;
                        continue;
                    }
                }
                int c;
                if (template == 0)
                {
                    c = b.Get(x - 1, y) | b.Get(x + 1, y - 1) << 1 | b.Get(x, y - 1) << 2 | b.Get(x + at[0], y + at[1]) << 3
                        | reference.Get(rx + 1, ry + 1) << 4 | reference.Get(rx, ry + 1) << 5 | reference.Get(rx - 1, ry + 1) << 6
                        | reference.Get(rx + 1, ry) << 7 | reference.Get(rx, ry) << 8 | reference.Get(rx - 1, ry) << 9
                        | reference.Get(rx + 1, ry - 1) << 10 | reference.Get(rx, ry - 1) << 11
                        | reference.Get(rx + at[2], ry + at[3]) << 12;
                }
                else
                {
                    c = b.Get(x - 1, y) | b.Get(x + 1, y - 1) << 1 | b.Get(x, y - 1) << 2 | b.Get(x - 1, y - 1) << 3
                        | reference.Get(rx + 1, ry + 1) << 4 | reference.Get(rx, ry + 1) << 5 | reference.Get(rx + 1, ry) << 6
                        | reference.Get(rx, ry) << 7 | reference.Get(rx - 1, ry) << 8 | reference.Get(rx, ry - 1) << 9;
                }
                if (mq.Decode(cx, c) != 0) b.P[y * w + x] = 1;
            }
        }
        return b;
    }
}
