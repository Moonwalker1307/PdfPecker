using System.Runtime.ExceptionServices;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

/// <summary>How the components of a decoded JPEG 2000 image are to be read when the PDF gives no color space.</summary>
internal enum JpxColor { Unknown, Gray, Rgb, Cmyk }

/// <summary>A decoded JPEG 2000 image: interleaved 8-bit color components and an optional alpha channel.</summary>
internal sealed class JpxImage(int width, int height, int components, byte[] data, byte[]? alpha, JpxColor color)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Components { get; } = components;
    public byte[] Data { get; } = data;
    public byte[]? Alpha { get; } = alpha;
    public JpxColor Color { get; } = color;
}

/// <summary>
/// JPEG 2000 (ISO/IEC 15444-1) decoder for JPXDecode image streams: JP2 files and raw codestreams, all progression orders
/// and progression changes, tiles, precincts, packed packet headers, every code-block style, 5/3 and 9/7 wavelets,
/// scalar quantization, ROI shifts, multiple component transforms, subsampled components, palettes, channel definitions
/// and sYCC. Images decode to 8 bits per component.
/// </summary>
/// <remarks>
/// Each stage has its own file: the JP2 boxes (Boxes), the codestream's main and tile-part headers (Markers), the tile
/// structure (Tiles), the packets that fill the code-blocks (Packets), the code-block decoder (Tier1), the inverse
/// wavelet transform (Wavelet) and the composition of the components into an image (Output). A damaged tile leaves its
/// own area blank and the other tiles still decode.
/// </remarks>
internal sealed partial class JpxDecoder
{
    private const int MaxComponents = 256;
    private const int MaxLevels = 32;
    private const int MaxTiles = 65535;

    private readonly byte[] _data;

    // The reference grid from the SIZ segment: the image area runs from (_imageX0, _imageY0) to (_imageX1, _imageY1),
    // and the tiles start at (_tileX0, _tileY0).
    private int _imageX0, _imageY0, _imageX1, _imageY1;
    private int _tileX0, _tileY0, _tileWidth, _tileHeight;
    private ImageComponent[] _components = Array.Empty<ImageComponent>();

    // Each component decoded at its own resolution, 8 bits per sample.
    private ComponentPlane[] _planes = Array.Empty<ComponentPlane>();

    private JpxDecoder(byte[] data) => _data = data;

    /// <summary>Decodes a JP2 file or raw JPEG 2000 codestream, or returns null when it cannot be read.</summary>
    public static JpxImage? Decode(byte[] data)
    {
        if (data.Length < 4) return null;
        int start = 0, end = data.Length;
        Jp2Header? header = null;
        if ((data[0] << 8 | data[1]) != StartOfCodestream)
        {
            header = new Jp2Header();
            if (!ReadBoxes(data, 0, data.Length, header) || header.CodestreamStart < 0) return null;
            (start, end) = (header.CodestreamStart, header.CodestreamEnd);
        }
        var decoder = new JpxDecoder(data);
        return decoder.DecodeCodestream(start, end) ? decoder.Compose(header) : null;
    }

    /// <summary>A component as SIZ describes it: sample precision in bits, and its subsampling on the reference grid.</summary>
    private sealed record ImageComponent(int Precision, bool Signed, int SubsamplingX, int SubsamplingY);

    /// <summary>The decoded samples of a component, covering its own grid from (X0, Y0).</summary>
    private sealed class ComponentPlane(int x0, int y0, int width, int height)
    {
        public int X0 { get; } = x0;
        public int Y0 { get; } = y0;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[] Samples { get; set; } = Array.Empty<byte>();
    }

    private static int CeilDiv(long a, long b) => (int)((a + b - 1) / b);
    private static int CeilDivPow2(int a, int k) => (int)(((long)a + (1L << k) - 1) >> k);

    /// <summary>Runs <paramref name="body"/> for 0..count-1, in parallel when there are at least <paramref name="minParallel"/> items.</summary>
    private static void ParallelRange<T>(int count, int minParallel, Func<T> init, Action<int, T> body)
    {
        if (count < minParallel || Environment.ProcessorCount < 2)
        {
            var state = init();
            for (int i = 0; i < count; i++) body(i, state);
            return;
        }
        var cancel = PdfFile.CurrentCancellation;
        try
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancel },
                init, (i, _, state) => { body(i, state); return state; }, _ => { });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // Callers handle the errors of damaged data, so the first one is passed on as itself.
            ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
        }
    }

    /// <summary>Reads the big-endian fields of marker segments and boxes from a position in the data, and moves past them.</summary>
    /// <remarks>
    /// Callers check the length of a segment or box before they read its fields. A read past the end of the data fails
    /// with an <see cref="IndexOutOfRangeException"/>, as damaged data does elsewhere in the decoder.
    /// </remarks>
    private ref struct JpxReader(ReadOnlySpan<byte> data, int position)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; } = position;

        public byte U8() => _data[Position++];

        public int U16()
        {
            int value = _data[Position] << 8 | _data[Position + 1];
            Position += 2;
            return value;
        }

        public int I32()
        {
            int value = _data[Position] << 24 | _data[Position + 1] << 16 | _data[Position + 2] << 8 | _data[Position + 3];
            Position += 4;
            return value;
        }

        public uint U32() => (uint)I32();

        public void Skip(int count) => Position += count;
    }
}
