using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

/// <summary>
/// JBIG2 (ITU-T T.88) decoder for JBIG2Decode image streams in the embedded organization PDF uses: generic regions
/// (arithmetic and MMR, typical prediction, adaptive templates), refinement regions, symbol dictionaries and text regions
/// (arithmetic and Huffman coding, refinement and aggregation), pattern dictionaries and halftone regions, standard and
/// custom Huffman tables, and striped pages of unknown height.
/// </summary>
/// <remarks>
/// The stream is a sequence of segments (<c>Jbig2Decoder.Segments.cs</c>); the region decoders are split by kind into
/// generic and refinement regions, symbol dictionaries and text regions, Huffman tables, and patterns and halftones.
/// The input is untrusted, so every allocation, decoded pixel and drawn pixel is charged to one work budget, and data
/// that runs far past its end stops the decoding.
/// </remarks>
internal sealed partial class Jbig2Decoder
{
    // ------------------------------------------------------------------ limits

    private const int MaxSymbols = 1 << 20;
    private const int MaxInputSymbols = 4 * MaxSymbols; // the symbols of all dictionaries a segment refers to
    private const int SymbolReferenceSize = 8; // the bytes a symbol takes in a list of input symbols, for the work budget
    private const long MaxWork = 1L << 28; // pixels allocated, decoded and drawn, against hostile input
    private const int MaxFill = 256; // arithmetic decoding far past the end of the data means the data is damaged

    private long _work;
    private long _imagePixels = PdfImageDecoder.MaxPixels; // the size of the image in the PDF, which bounds a region

    // ------------------------------------------------------------------ decoder state

    private readonly Dictionary<int, Bitmap[]> _symbols = new();
    private readonly Dictionary<int, Bitmap[]> _patterns = new();
    private readonly Dictionary<int, HuffTable> _tables = new();
    private readonly Dictionary<int, Bitmap> _regions = new(); // intermediate regions, kept for a later refinement
    private Bitmap? _page;
    private int _pageDefault;

    /// <summary>
    /// Decodes a JBIG2 image to packed 1-bit rows of <paramref name="width"/> x <paramref name="height"/> pixels with 1 =
    /// white, as the JBIG2Decode filter delivers them. Returns null when nothing can be decoded.
    /// </summary>
    public static byte[]? Decode(byte[] data, byte[]? globals, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > PdfImageDecoder.MaxPixels) return null;
        var dec = new Jbig2Decoder { _imagePixels = (long)width * height };
        try
        {
            if (globals != null) dec.Run(globals, height);
            dec.Run(data, height);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException
                                       || (ex is InvalidDataException && !ex.Message.StartsWith("The page", StringComparison.Ordinal)))
        {
            // Damaged data: show what was decoded so far. Only the page's decode budget, whose message starts with
            // "The page", is passed on, so the renderer can stop the page.
        }
        return dec._page == null ? null : PackRows(dec._page, width, height);
    }

    /// <summary>Packs the page into 1-bit rows of the image size, white where the page is white or missing.</summary>
    private static byte[] PackRows(Bitmap page, int width, int height)
    {
        int rowBytes = (width + 7) / 8;
        var o = new byte[rowBytes * height];
        Array.Fill(o, (byte)0xFF);
        int h = Math.Min(height, page.H), w = Math.Min(width, page.W);
        for (int y = 0; y < h; y++)
        {
            int s = y * page.W, d = y * rowBytes;
            for (int x = 0; x < w; x++)
                if (page.P[s + x] != 0) o[d + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
        }
        return o;
    }

    private void Run(byte[] data, int pdfHeight)
    {
        int position = 0;
        while (TryReadSegment(data, ref position, out var segment) && segment.Type != SegmentType.EndOfFile)
            ProcessSegment(segment, pdfHeight);
    }

    private void ProcessSegment(Segment seg, int pdfHeight)
    {
        PdfFile.CheckCancel();
        switch (seg.Type)
        {
            case SegmentType.SymbolDictionary:
                _symbols[seg.Number] = DecodeSymbolDictionary(seg);
                break;
            case SegmentType.IntermediateTextRegion or SegmentType.ImmediateTextRegion or SegmentType.ImmediateLosslessTextRegion:
                PlaceRegion(seg, DecodeTextRegionSegment(seg));
                break;
            case SegmentType.PatternDictionary:
                _patterns[seg.Number] = DecodePatternDictionary(seg);
                break;
            case SegmentType.IntermediateHalftoneRegion or SegmentType.ImmediateHalftoneRegion
                or SegmentType.ImmediateLosslessHalftoneRegion:
                PlaceRegion(seg, DecodeHalftoneRegion(seg));
                break;
            case SegmentType.IntermediateGenericRegion or SegmentType.ImmediateGenericRegion
                or SegmentType.ImmediateLosslessGenericRegion:
                PlaceRegion(seg, DecodeGenericRegionSegment(seg));
                break;
            case SegmentType.IntermediateRefinementRegion or SegmentType.ImmediateRefinementRegion
                or SegmentType.ImmediateLosslessRefinementRegion:
                // A refinement of the page itself is drawn back with the region's own operator, as other readers do.
                PlaceRegion(seg, DecodeRefinementSegment(seg));
                break;
            case SegmentType.PageInformation:
                PageInfo(seg, pdfHeight);
                break;
            case SegmentType.Tables:
                _tables[seg.Number] = ReadTableSegment(seg);
                break;
            // End of page, end of stripe (pages of unknown height take the PDF image height), profiles and extensions:
            // nothing to do.
        }
    }

    /// <summary>Keeps an intermediate region for a later refinement, or draws an immediate one on the page.</summary>
    private void PlaceRegion(Segment seg, Region? decoded)
    {
        if (decoded is not { } region) return;
        var (bmp, info) = region;
        if (seg.IsIntermediateRegion)
        {
            _regions[seg.Number] = bmp;
            return;
        }
        if (_page == null) return;
        Work((long)bmp.W * bmp.H);
        _page.Compose(bmp, info.X, info.Y, info.Op);
    }

    // ------------------------------------------------------------------ budget

    private void Work(long n)
    {
        _work += n;
        if (_work > MaxWork) throw new InvalidDataException("JBIG2 image takes too much work to decode.");
        if ((_work & 0xFFFFF) < n) PdfFile.CheckCancel();
    }

    private static void CheckData(MqDecoder? mq, BitReader? br = null)
    {
        if (mq != null && mq.FillBytes > MaxFill || br != null && br.Position > br.End + MaxFill)
            throw new InvalidDataException("JBIG2 data ended early.");
    }

    private Bitmap NewBitmap(int w, int h)
    {
        Work((long)w * h + 16);
        return new Bitmap(w, h);
    }

    private Bitmap Crop(Bitmap b, int x, int y, int w, int h)
    {
        Work((long)w * h + 16);
        return b.Crop(x, y, w, h);
    }

    // ------------------------------------------------------------------ bitmaps

    /// <summary>How a region is combined with the bitmap below it (7.4.1.5); the values past XNOR replace.</summary>
    internal enum CombinationOperator
    {
        Or = 0,
        And = 1,
        Xor = 2,
        Xnor = 3,
        Replace = 4,
    }

    internal sealed class Bitmap
    {
        public readonly int W, H;
        public readonly byte[] P; // one byte per pixel, 1 = black

        public Bitmap(int w, int h)
        {
            if (w < 0 || h < 0 || (long)w * h > PdfImageDecoder.MaxPixels) throw new InvalidDataException("JBIG2 bitmap too large.");
            W = w;
            H = h;
            PdfFile.ChargeDecoded((long)w * h + 32);
            P = new byte[w * h];
        }

        public int Get(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H ? P[y * W + x] : 0;

        public void Fill(int v)
        {
            if (v != 0) Array.Fill(P, (byte)1);
        }

        /// <summary>Combines <paramref name="src"/> into this bitmap at (x, y) with <paramref name="op"/>.</summary>
        public void Compose(Bitmap src, int x, int y, CombinationOperator op)
        {
            int sx0 = Math.Max(0, -x), sy0 = Math.Max(0, -y);
            int sx1 = (int)Math.Min(src.W, (long)W - x), sy1 = (int)Math.Min(src.H, (long)H - y);
            for (int sy = sy0; sy < sy1; sy++)
            {
                int s = sy * src.W, d = (sy + y) * W + x;
                for (int sx = sx0; sx < sx1; sx++)
                {
                    int a = P[d + sx], b = src.P[s + sx];
                    P[d + sx] = op switch
                    {
                        CombinationOperator.Or => (byte)(a | b),
                        CombinationOperator.And => (byte)(a & b),
                        CombinationOperator.Xor => (byte)(a ^ b),
                        CombinationOperator.Xnor => (byte)(1 - (a ^ b)),
                        _ => (byte)b,
                    };
                }
            }
        }

        public Bitmap Crop(int x, int y, int w, int h)
        {
            var r = new Bitmap(w, h);
            for (int yy = 0; yy < h; yy++)
                for (int xx = 0; xx < w; xx++)
                    r.P[yy * w + xx] = (byte)Get(x + xx, y + yy);
            return r;
        }
    }

    /// <summary>A decoded region and where it goes on the page.</summary>
    private readonly record struct Region(Bitmap Bitmap, RegionInfo Info);
}
