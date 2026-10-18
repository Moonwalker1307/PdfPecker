namespace PdfPecker.Rendering.Images;

// Pattern dictionaries (6.7, 7.4.4) and halftone regions (6.6, 7.4.5).
internal sealed partial class Jbig2Decoder
{
    private const int PatternDictionaryHeaderLength = 7;
    private const int HalftoneHeaderLength = 21;

    /// <summary>The flags byte of a pattern dictionary segment (7.4.4.1.1).</summary>
    private readonly record struct PatternDictionaryFlags(int Bits)
    {
        public bool Mmr => (Bits & 1) != 0;
        public int Template => (Bits >> 1) & 3;
    }

    /// <summary>The flags byte of a halftone region segment (7.4.5.1.1).</summary>
    private readonly record struct HalftoneRegionFlags(int Bits)
    {
        public bool Mmr => (Bits & 1) != 0;
        public int Template => (Bits >> 1) & 3;
        public bool EnableSkip => (Bits & 8) != 0;
        public CombinationOperator Op => (CombinationOperator)((Bits >> 4) & 7);
        public int DefaultPixel => (Bits >> 7) & 1;
    }

    /// <summary>
    /// The grid of a halftone region (7.4.5.1.2, 7.4.5.1.3): its size in cells, the origin of its first cell and the
    /// vector between neighbouring cells, in 1/256 pixel.
    /// </summary>
    private readonly record struct HalftoneGrid(int Width, int Height, int X, int Y, int VectorX, int VectorY)
    {
        /// <summary>The page position of the cell in row <paramref name="mg"/> and column <paramref name="ng"/> (6.6.5.2).</summary>
        public (long X, long Y) CellPosition(int mg, int ng) =>
            ((X + (long)mg * VectorY + (long)ng * VectorX) >> 8, (Y + (long)mg * VectorX - (long)ng * VectorY) >> 8);
    }

    private Bitmap[] DecodePatternDictionary(Segment seg)
    {
        if (seg.Length < PatternDictionaryHeaderLength) throw new InvalidDataException("Short JBIG2 pattern dictionary.");
        var reader = seg.Reader();
        var flags = new PatternDictionaryFlags(reader.U8());
        int pw = reader.U8(), ph = reader.U8();
        uint grayMax = reader.U32();
        if (pw == 0 || ph == 0 || grayMax >= MaxSymbols) throw new InvalidDataException("Bad JBIG2 pattern dictionary.");
        int n = (int)grayMax + 1;
        long cw = (long)n * pw;
        if (cw > int.MaxValue) throw new InvalidDataException("Bad JBIG2 pattern dictionary.");
        // The patterns are stored side by side in one collective bitmap (6.7.5).
        Bitmap collective;
        if (flags.Mmr) collective = DecodeGenericMmr(seg.Data[..seg.End], reader.Position, (int)cw, ph, out _);
        else
        {
            // The first adaptive pixel sits one pattern to the left, as far as the signed byte of its offset reaches.
            var at = new sbyte[] { (sbyte)-Math.Min(pw, 128), 0, -3, -1, 2, -2, -2, -2 };
            var mq = new MqDecoder(seg.Data, reader.Position, seg.End);
            collective = DecodeGenericArith(mq, new byte[GenericContextSize(flags.Template)], (int)cw, ph, flags.Template, false, at);
        }
        var pats = new Bitmap[n];
        for (int i = 0; i < n; i++) pats[i] = Crop(collective, i * pw, 0, pw, ph);
        return pats;
    }

    private Region? DecodeHalftoneRegion(Segment seg)
    {
        var reader = seg.Reader();
        var info = RegionInfo.Read(ref reader);
        if (seg.End - reader.Position < HalftoneHeaderLength) return null;
        var flags = new HalftoneRegionFlags(reader.U8());
        uint gw = reader.U32(), gh = reader.U32();
        int gx = reader.S32(), gy = reader.S32();
        int rx = reader.U16(), ry = reader.U16();
        var pats = FirstReferredPatterns(seg);
        if (pats == null || pats.Length == 0 || info.W <= 0 || info.H <= 0) return null;
        // The region is drawn on a page no larger than the image, and every grid cell covers at least a pixel of the
        // region in any real file. Both sizes come from the segment header, so without these bounds a few bytes could
        // ask for gigabytes: the region, the skip bitmap, the bit-planes and the gray-scale values all scale with them.
        // Each side is checked before the product, which two 32-bit sides could overflow even as a long.
        long regionPixels = (long)info.W * info.H;
        if (regionPixels > _imagePixels || gw == 0 || gh == 0 || gw > regionPixels || gh > regionPixels) return null;
        long cells = (long)gw * gh;
        if (cells > regionPixels) return null;
        var grid = new HalftoneGrid((int)gw, (int)gh, gx, gy, rx, ry);
        Work(cells * sizeof(int)); // the gray-scale values below
        var region = NewBitmap(info.W, info.H);
        region.Fill(flags.DefaultPixel);
        var skip = flags.EnableSkip ? SkippedCells(grid, pats[0], info) : null;
        var gray = DecodeGrayScaleImage(seg, reader.Position, flags, grid, CeilLog2(pats.Length), skip);
        DrawPatterns(region, grid, gray, pats, skip, flags.Op);
        return new Region(region, info);
    }

    private Bitmap[]? FirstReferredPatterns(Segment seg)
    {
        foreach (int r in seg.Refs)
            if (_patterns.TryGetValue(r, out var pats)) return pats;
        return null;
    }

    /// <summary>Marks the grid cells whose pattern would fall wholly outside the region (6.6.5.1).</summary>
    private Bitmap SkippedCells(HalftoneGrid grid, Bitmap pattern, RegionInfo info)
    {
        int hpw = pattern.W, hph = pattern.H;
        var skip = NewBitmap(grid.Width, grid.Height);
        for (int mg = 0; mg < grid.Height; mg++)
            for (int ng = 0; ng < grid.Width; ng++)
            {
                var (x, y) = grid.CellPosition(mg, ng);
                if (x + hpw <= 0 || x >= info.W || y + hph <= 0 || y >= info.H) skip.P[mg * grid.Width + ng] = 1;
            }
        return skip;
    }

    /// <summary>The gray-scale image of the grid: Gray-coded bit-planes, most significant first (C.5).</summary>
    private int[] DecodeGrayScaleImage(Segment seg, int start, HalftoneRegionFlags flags, HalftoneGrid grid, int bpp, Bitmap? skip)
    {
        int ngw = grid.Width, ngh = grid.Height;
        var gray = new int[ngw * ngh];
        if (bpp == 0) return gray;
        int template = flags.Template;
        var at = template switch
        {
            0 => new sbyte[] { 3, -1, -3, -1, 2, -2, -2, -2 },
            1 => new sbyte[] { 3, -1, 0, 0, 0, 0, 0, 0 },
            _ => new sbyte[] { 2, -1, 0, 0, 0, 0, 0, 0 },
        };
        var mq = flags.Mmr ? null : new MqDecoder(seg.Data, start, seg.End);
        var cx = new byte[GenericContextSize(template)];
        int pos = start;
        Bitmap? prev = null;
        for (int j = bpp - 1; j >= 0; j--)
        {
            Bitmap plane;
            if (flags.Mmr)
            {
                plane = DecodeGenericMmr(seg.Data[..seg.End], pos, ngw, ngh, out int end);
                pos = end;
            }
            else plane = DecodeGenericArith(mq!, cx, ngw, ngh, template, false, at, skip);
            if (prev != null)
                for (int i = 0; i < plane.P.Length; i++) plane.P[i] ^= prev.P[i];
            for (int i = 0; i < gray.Length; i++) gray[i] |= plane.P[i] << j;
            prev = plane;
        }
        return gray;
    }

    /// <summary>Draws each cell's pattern, chosen by its gray-scale value, at the cell's place in the region (6.6.5.2).</summary>
    private void DrawPatterns(Bitmap region, HalftoneGrid grid, int[] gray, Bitmap[] pats, Bitmap? skip, CombinationOperator op)
    {
        int hpw = pats[0].W, hph = pats[0].H;
        for (int mg = 0; mg < grid.Height; mg++)
            for (int ng = 0; ng < grid.Width; ng++)
            {
                int cell = mg * grid.Width + ng;
                if (skip != null && skip.P[cell] != 0) continue;
                var (x, y) = grid.CellPosition(mg, ng);
                int g = Math.Min(gray[cell], pats.Length - 1);
                if (x <= -hpw || y <= -hph || x >= region.W || y >= region.H) continue;
                Work((long)hpw * hph + 4);
                region.Compose(pats[g], (int)x, (int)y, op);
            }
    }
}
