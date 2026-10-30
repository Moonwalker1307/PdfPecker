namespace PdfPecker.Rendering.Images;

/// <summary>
/// Baseline and progressive JPEG decoder (Huffman coding, any sampling factors, restart intervals, 1/3/4 components,
/// Adobe color transforms). Output is interleaved 8-bit components as stored: gray, RGB, or CMYK (Adobe CMYK JPEGs
/// keep their inverted values; PDF files correct those with a Decode array).
/// </summary>
/// <remarks>
/// Damaged data is read as far as it goes: a read past the end of the data throws, and <see cref="Decode(byte[], bool?)"/>
/// then returns what was decoded until that point. Segments are therefore read field by field in file order, so that
/// where the data ends decides what is kept.
/// </remarks>
internal sealed partial class JpegDecoder
{
    // The second byte of the markers this decoder acts on (ITU-T T.81, table B.1). Every marker starts with 0xFF.
    private const byte MarkerPrefix = 0xFF;
    private const byte StuffedZero = 0x00;
    private const byte Temporary = 0x01;
    private const byte BaselineFrame = 0xC0;
    private const byte ExtendedFrame = 0xC1;
    private const byte ProgressiveFrame = 0xC2;
    private const byte DefineHuffmanTables = 0xC4;
    private const byte Restart0 = 0xD0;
    private const byte Restart7 = 0xD7;
    private const byte StartOfImage = 0xD8;
    private const byte EndOfImage = 0xD9;
    private const byte StartOfScan = 0xDA;
    private const byte DefineQuantizationTables = 0xDB;
    private const byte DefineRestartInterval = 0xDD;
    private const byte App0 = 0xE0;
    private const byte App14 = 0xEE;

    private const int SupportedPrecision = 8;
    private const int MaxComponents = 4;
    private const int TableSlots = 4; // table selectors are two bits wide
    private const int BlockSize = 64;

    // An APP14 segment that starts with "Adobe" names the colour transform in its twelfth byte: 0 none (RGB or CMYK),
    // 1 YCbCr, 2 YCCK. An APP0 segment that starts with "JFIF" implies YCbCr.
    private const int AdobeMinLength = 12;
    private const int AdobeTransformOffset = 11;
    private const int JfifMinLength = 7;
    private const int NoAdobeSegment = -1;

    private readonly byte[] _data;
    private int _pos;
    private readonly ushort[]?[] _quantization = new ushort[TableSlots][];
    private readonly HuffmanTable?[] _dcTables = new HuffmanTable?[TableSlots], _acTables = new HuffmanTable?[TableSlots];
    private Component[] _components = Array.Empty<Component>();
    private int _width, _height, _mcusX, _mcusY, _hMax, _vMax;
    private bool _progressive;
    private int _restartInterval;
    private int _adobeTransform = NoAdobeSegment;
    private bool _jfif;
    private bool? _colorTransform; // set by the container (TIFF says whether its JPEG data is YCbCr), else from markers

    private JpegDecoder(byte[] data) => _data = data;

    /// <summary>Decodes a JPEG. Returns null if the data is not a supported JPEG.</summary>
    public static (byte[] Data, int Width, int Height, int Components)? Decode(byte[] data) => Decode(data, null);

    /// <summary>
    /// Decodes a JPEG whose three components are YCbCr (<paramref name="colorTransform"/> true) or RGB (false), or as
    /// its markers say (null).
    /// </summary>
    public static (byte[] Data, int Width, int Height, int Components)? Decode(byte[] data, bool? colorTransform)
    {
        var decoder = new JpegDecoder(data) { _colorTransform = colorTransform };
        try
        {
            var samples = decoder.ReadImage();
            return samples == null ? null : (samples, decoder._width, decoder._height, decoder._components.Length);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            // Truncated or corrupt: return what was decoded if the frame is known.
            SwallowedErrors.Record(ex);
            if (!decoder.HasSupportedFrame) return null;
            try { return (decoder.Output(), decoder._width, decoder._height, decoder._components.Length); }
            catch (Exception ex2) when (ex2 is IndexOutOfRangeException or ArgumentException) { SwallowedErrors.Record(ex2); return null; }
        }
    }

    /// <summary>
    /// Whether a frame header with a component count that can be output has been read. Two components can be declared
    /// but have no colour space to be output in.
    /// </summary>
    private bool HasSupportedFrame => _components.Length is 1 or 3 or 4 && _width != 0;

    /// <summary>Reads the segments up to the end of the image and returns the decoded samples, or null.</summary>
    private byte[]? ReadImage()
    {
        var d = _data;
        if (d.Length < 4 || d[0] != MarkerPrefix || d[1] != StartOfImage) return null;
        _pos = 2;
        bool frame = false;
        while (_pos < d.Length)
        {
            if (d[_pos] != MarkerPrefix) { _pos++; continue; }
            byte marker = d[_pos + 1];
            if (marker == MarkerPrefix) { _pos++; continue; } // a fill byte before the marker
            _pos += 2;
            if (marker == EndOfImage) break;
            if (IsStandalone(marker)) continue;
            int length = ReadUInt16(_pos);
            int segmentEnd = _pos + length;
            int body = _pos + 2;
            switch (marker)
            {
                case DefineQuantizationTables:
                    ReadQuantizationTables(body, segmentEnd);
                    break;
                case DefineHuffmanTables:
                    ReadHuffmanTables(body, segmentEnd);
                    break;
                case BaselineFrame or ExtendedFrame or ProgressiveFrame:
                    _progressive = marker == ProgressiveFrame;
                    ReadFrameHeader(body);
                    frame = true;
                    break;
                case DefineRestartInterval:
                    _restartInterval = ReadUInt16(body);
                    break;
                case App14:
                    if (length >= AdobeMinLength && StartsWith(body, "Adobe"u8)) _adobeTransform = d[body + AdobeTransformOffset];
                    break;
                case App0:
                    if (length >= JfifMinLength && StartsWith(body, "JFIF"u8)) _jfif = true;
                    break;
                case StartOfScan:
                    if (!frame) return null;
                    _pos = ReadScan(body);
                    continue;
                default:
                    if (IsUnsupportedFrame(marker)) return null;
                    break;
            }
            _pos = segmentEnd;
        }
        return frame && HasSupportedFrame ? Output() : null;
    }

    /// <summary>Markers without a length and a segment: start of image, the restart markers, TEM, and a stuffed zero.</summary>
    private static bool IsStandalone(byte marker)
        => marker is StartOfImage or (>= Restart0 and <= Restart7) or Temporary or StuffedZero;

    /// <summary>Frame headers of lossless, hierarchical and arithmetic-coded JPEGs, which this decoder does not read.</summary>
    private static bool IsUnsupportedFrame(byte marker)
        => marker is 0xC3 or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF);

    private int ReadUInt16(int at) => _data[at] << 8 | _data[at + 1];

    /// <summary>Compares byte by byte, so that a segment cut off inside the text reads past the end like any other.</summary>
    private bool StartsWith(int at, ReadOnlySpan<byte> text)
    {
        for (int i = 0; i < text.Length; i++)
            if (_data[at + i] != text[i]) return false;
        return true;
    }

    // ------------------------------------------------------------------ table and frame segments

    private void ReadQuantizationTables(int p, int end)
    {
        var d = _data;
        while (p < end)
        {
            // Precision 0 holds 8-bit values, anything else 16-bit ones; the values come in zigzag order.
            bool sixteenBit = d[p] >> 4 != 0;
            int slot = d[p] & (TableSlots - 1);
            p++;
            var table = new ushort[BlockSize];
            for (int i = 0; i < BlockSize; i++)
                table[ZigZag[i]] = sixteenBit ? (ushort)(d[p + 2 * i] << 8 | d[p + 2 * i + 1]) : d[p + i];
            p += sixteenBit ? 2 * BlockSize : BlockSize;
            _quantization[slot] = table;
        }
    }

    private void ReadHuffmanTables(int p, int end)
    {
        var d = _data;
        Span<int> counts = stackalloc int[HuffmanTable.MaxCodeLength + 1]; // the number of codes of each length
        while (p + 1 + HuffmanTable.MaxCodeLength <= end)
        {
            bool ac = d[p] >> 4 != 0;
            int slot = d[p] & (TableSlots - 1);
            p++;
            int total = 0;
            for (int length = 1; length <= HuffmanTable.MaxCodeLength; length++)
            {
                counts[length] = d[p + length - 1];
                total += counts[length];
            }
            p += HuffmanTable.MaxCodeLength;
            // A damaged segment can count more values than it holds; the codes without one decode to zero.
            var values = d.AsSpan(p, Math.Min(total, end - p)).ToArray();
            p += total;
            var table = new HuffmanTable(counts, values);
            if (ac) _acTables[slot] = table;
            else _dcTables[slot] = table;
        }
    }

    private void ReadFrameHeader(int p)
    {
        var d = _data;
        if (d[p] != SupportedPrecision) throw new InvalidDataException("Only 8-bit JPEG is supported.");
        // The size is kept even when the rest of the header is bad: the partial output of a damaged file has the size
        // of the last frame header it read.
        // A size over the pixel cap is refused before it replaces the size of an earlier header, so the partial output
        // made of the earlier frame's components is never allocated at the refused size.
        int height = Math.Max(1, ReadUInt16(p + 1)); // a DNL-defined height of 0: decode what we can
        int width = ReadUInt16(p + 3);
        if ((long)width * height > PdfImageDecoder.MaxPixels) throw new InvalidDataException("JPEG too large.");
        _height = height;
        _width = width;
        int count = d[p + 5];
        if (_width <= 0 || count < 1 || count > MaxComponents) throw new InvalidDataException("Bad JPEG frame.");
        // The components are published only once the whole header has been read: a truncated header must not leave
        // half-built components behind for the partial output that Decode makes of a damaged file.
        var components = new Component[count];
        int hMax = 1, vMax = 1;
        for (int i = 0; i < count; i++)
        {
            int at = p + 6 + i * 3;
            var c = new Component(id: d[at], h: Math.Max(1, d[at + 1] >> 4), v: Math.Max(1, d[at + 1] & 15), quantizationTable: d[at + 2] & (TableSlots - 1));
            components[i] = c;
            hMax = Math.Max(hMax, c.H);
            vMax = Math.Max(vMax, c.V);
        }
        _hMax = hMax;
        _vMax = vMax;
        _mcusX = (_width + 8 * _hMax - 1) / (8 * _hMax);
        _mcusY = (_height + 8 * _vMax - 1) / (8 * _vMax);
        foreach (var c in components)
        {
            c.BlocksPerLine = _mcusX * c.H;
            c.BlocksPerColumn = _mcusY * c.V;
            c.Coefficients = new short[c.BlocksPerLine * c.BlocksPerColumn * BlockSize];
        }
        _components = components;
    }

    /// <summary>A component of the frame: its sampling factors, its coefficients while scans fill them, then its samples.</summary>
    private sealed class Component(int id, int h, int v, int quantizationTable)
    {
        public readonly int Id = id;
        public readonly int H = h, V = v; // sampling factors
        public readonly int QuantizationTable = quantizationTable;
        public int BlocksPerLine, BlocksPerColumn;
        public short[] Coefficients = Array.Empty<short>(); // 64 per block, in natural order
        public int DcPrediction;
        public int DcTable, AcTable; // chosen by each scan
        public byte[] Plane = Array.Empty<byte>();
        public int PlaneStride;
    }
}
