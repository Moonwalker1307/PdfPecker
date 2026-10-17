namespace PdfPecker.Rendering;

/// <summary>
/// Writes baseline JPEG (JFIF) files: YCbCr with 4:2:0 chroma subsampling below quality 90 and 4:4:4 from 90 up, the
/// IJG quantisation tables scaled by quality, and Huffman tables optimised for each image. Alpha is flattened onto a
/// matte colour because JPEG has no transparency.
/// </summary>
internal static partial class JpegEncoder
{
    /// <summary>The largest width or height a baseline JPEG can describe.</summary>
    public const int MaxDimension = 65535;

    // From this quality up, chroma keeps full resolution; below it, Cb and Cr are halved in both directions.
    private const int FullChromaQuality = 90;

    private const int BlockSize = 8;
    private const int BlockLength = BlockSize * BlockSize;

    // The natural position of each coefficient in zigzag order.
    private static readonly byte[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21,
        28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61,
        54, 47, 55, 62, 63,
    };

    // ITU-T T.81 Annex K tables (natural order), as scaled by the IJG library.
    private static readonly byte[] StdLuminance =
    {
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29,
        51, 87, 80, 62, 18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121,
        120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    };

    private static readonly byte[] StdChrominance =
    {
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    };

    // The second byte of each marker this encoder writes.
    private enum Marker : byte
    {
        StartOfFrameBaseline = 0xC0,
        DefineHuffmanTable = 0xC4,
        StartOfImage = 0xD8,
        EndOfImage = 0xD9,
        StartOfScan = 0xDA,
        DefineQuantizationTable = 0xDB,
        App0 = 0xE0,
    }

    // Component 1 is Y and uses quantisation and Huffman tables 0; components 2 and 3 are Cb and Cr and share tables 1.
    private const int LumaTable = 0;
    private const int ChromaTable = 1;
    private const byte FullSampling = 0x11; // one block across and down per MCU
    private const byte HalfChromaSampling = 0x22; // Y has two blocks across and down for each chroma block

    // A DHT segment names its table by class (0 DC, 1 AC) in the high nibble and destination in the low one.
    private const byte DcClass = 0x00;
    private const byte AcClass = 0x10;

    /// <summary>Encodes premultiplied 0xAABBGGRR pixels, composited onto <paramref name="matte"/> (0xRRGGBB).</summary>
    public static byte[] Encode(uint[] pixels, int width, int height, int quality, uint matte)
    {
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be between 1 and 100.");
        if (width > MaxDimension || height > MaxDimension)
            throw new NotSupportedException($"JPEG images are limited to {MaxDimension} pixels in each dimension; lower the Dpi or MaxDimension.");

        var frame = new Frame(width, height, subsample: quality < FullChromaQuality);
        var lumaQuantization = ScaleTable(StdLuminance, quality);
        var chromaQuantization = ScaleTable(StdChrominance, quality);

        var planes = ToYCbCr(pixels, frame, matte);
        if (frame.Subsample)
        {
            planes.Cb = Downsample(planes.Cb, frame.LumaWidth, frame.LumaHeight);
            planes.Cr = Downsample(planes.Cr, frame.LumaWidth, frame.LumaHeight);
        }

        // Transform and quantise every block once; the coefficients feed both the statistics and the output pass.
        var scan = new Scan(
            Transform(planes.Y, frame.LumaWidth, frame.LumaHeight, lumaQuantization),
            Transform(planes.Cb, frame.ChromaWidth, frame.ChromaHeight, chromaQuantization),
            Transform(planes.Cr, frame.ChromaWidth, frame.ChromaHeight, chromaQuantization),
            frame);
        var tables = scan.OptimalTables();

        var output = new JpegWriter(width * height / 4 + 1024);
        output.WriteMarker(Marker.StartOfImage);
        WriteJfifHeader(output);
        WriteQuantizationTables(output, lumaQuantization, chromaQuantization);
        WriteFrameHeader(output, width, height, frame.Subsample);
        WriteHuffmanTable(output, DcClass | LumaTable, tables.LumaDc);
        WriteHuffmanTable(output, AcClass | LumaTable, tables.LumaAc);
        WriteHuffmanTable(output, DcClass | ChromaTable, tables.ChromaDc);
        WriteHuffmanTable(output, AcClass | ChromaTable, tables.ChromaAc);
        WriteScanHeader(output);
        scan.Write(output, tables);
        output.WriteMarker(Marker.EndOfImage);
        return output.ToArray();
    }

    /// <summary>
    /// The size of the coded image: whole MCUs of 8x8 pixels, or of 16x16 with chroma subsampling, and the planes
    /// padded to them.
    /// </summary>
    private readonly struct Frame
    {
        public Frame(int width, int height, bool subsample)
        {
            Width = width;
            Height = height;
            Subsample = subsample;
            int mcuSize = subsample ? 2 * BlockSize : BlockSize;
            McusX = (width + mcuSize - 1) / mcuSize;
            McusY = (height + mcuSize - 1) / mcuSize;
            LumaWidth = McusX * mcuSize;
            LumaHeight = McusY * mcuSize;
            ChromaWidth = subsample ? LumaWidth / 2 : LumaWidth;
            ChromaHeight = subsample ? LumaHeight / 2 : LumaHeight;
        }

        public int Width { get; }
        public int Height { get; }
        public bool Subsample { get; }
        public int McusX { get; }
        public int McusY { get; }
        public int LumaWidth { get; }
        public int LumaHeight { get; }
        public int ChromaWidth { get; }
        public int ChromaHeight { get; }

        /// <summary>The luma blocks across and down in each MCU.</summary>
        public int LumaBlocksPerMcuSide => Subsample ? 2 : 1;
    }

    /// <summary>A standard table scaled for the quality as libjpeg does: 50 keeps it, lower values coarsen it.</summary>
    private static byte[] ScaleTable(byte[] standard, int quality)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var table = new byte[BlockLength];
        for (int i = 0; i < BlockLength; i++) table[i] = (byte)Math.Clamp((standard[i] * scale + 50) / 100, 1, 255);
        return table;
    }

    /// <summary>APP0: JFIF 1.01, no density units, 1:1 aspect ratio, no thumbnail.</summary>
    private static void WriteJfifHeader(JpegWriter output)
    {
        output.WriteMarker(Marker.App0);
        output.WriteUInt16(16);
        output.Write("JFIF\0"u8);
        output.WriteByte(1); // major version
        output.WriteByte(1); // minor version
        output.WriteByte(0); // density units: none, so the densities are an aspect ratio
        output.WriteUInt16(1);
        output.WriteUInt16(1);
        output.WriteByte(0); // thumbnail width
        output.WriteByte(0); // thumbnail height
    }

    /// <summary>DQT: both tables, 8-bit, in zigzag order.</summary>
    private static void WriteQuantizationTables(JpegWriter output, byte[] luma, byte[] chroma)
    {
        output.WriteMarker(Marker.DefineQuantizationTable);
        output.WriteUInt16(2 + 2 * (1 + BlockLength));
        WriteQuantizationTable(output, LumaTable, luma);
        WriteQuantizationTable(output, ChromaTable, chroma);
    }

    private static void WriteQuantizationTable(JpegWriter output, int destination, byte[] table)
    {
        output.WriteByte((byte)destination); // precision 0 (8-bit values) in the high nibble
        for (int k = 0; k < BlockLength; k++) output.WriteByte(table[ZigZag[k]]);
    }

    /// <summary>SOF0: 8-bit samples, three components, Y subsampled against chroma or not.</summary>
    private static void WriteFrameHeader(JpegWriter output, int width, int height, bool subsample)
    {
        output.WriteMarker(Marker.StartOfFrameBaseline);
        output.WriteUInt16(8 + 3 * 3);
        output.WriteByte(8); // sample precision
        output.WriteUInt16(height);
        output.WriteUInt16(width);
        output.WriteByte(3);
        WriteFrameComponent(output, 1, subsample ? HalfChromaSampling : FullSampling, LumaTable);
        WriteFrameComponent(output, 2, FullSampling, ChromaTable);
        WriteFrameComponent(output, 3, FullSampling, ChromaTable);
    }

    private static void WriteFrameComponent(JpegWriter output, byte id, byte sampling, int quantizationTable)
    {
        output.WriteByte(id);
        output.WriteByte(sampling);
        output.WriteByte((byte)quantizationTable);
    }

    private static void WriteHuffmanTable(JpegWriter output, int classAndDestination, HuffmanTable table)
    {
        output.WriteMarker(Marker.DefineHuffmanTable);
        output.WriteUInt16(2 + 1 + HuffmanTable.MaxCodeLength + table.Values.Length);
        output.WriteByte((byte)classAndDestination);
        for (int length = 1; length <= HuffmanTable.MaxCodeLength; length++) output.WriteByte(table.Counts[length]);
        output.Write(table.Values);
    }

    /// <summary>SOS: all three components in one sequential scan of the full spectrum.</summary>
    private static void WriteScanHeader(JpegWriter output)
    {
        output.WriteMarker(Marker.StartOfScan);
        output.WriteUInt16(6 + 2 * 3);
        output.WriteByte(3);
        WriteScanComponent(output, 1, LumaTable);
        WriteScanComponent(output, 2, ChromaTable);
        WriteScanComponent(output, 3, ChromaTable);
        output.WriteByte(0); // spectral selection start
        output.WriteByte(BlockLength - 1); // spectral selection end
        output.WriteByte(0); // successive approximation
    }

    private static void WriteScanComponent(JpegWriter output, byte id, int table)
    {
        output.WriteByte(id);
        output.WriteByte((byte)(table << 4 | table)); // DC table in the high nibble, AC table in the low one
    }
}
