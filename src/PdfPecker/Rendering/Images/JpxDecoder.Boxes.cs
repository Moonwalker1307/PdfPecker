namespace PdfPecker.Rendering.Images;

// The JP2 file format (ISO/IEC 15444-1, annex I): the boxes around the codestream that say how its components make
// up the image's colors.
internal sealed partial class JpxDecoder
{
    // Box types, which are four characters read as a big-endian number.
    private const uint HeaderBox = 0x6A703268;              // 'jp2h', a superbox
    private const uint CodestreamBox = 0x6A703263;          // 'jp2c'
    private const uint ColourSpecificationBox = 0x636F6C72; // 'colr'
    private const uint PaletteBox = 0x70636C72;             // 'pclr'
    private const uint ComponentMappingBox = 0x636D6170;    // 'cmap'
    private const uint ChannelDefinitionBox = 0x63646566;   // 'cdef'

    // A box starts with its length and type; a length of 1 means a 64-bit length follows, and 0 that the box runs to the
    // end of its parent.
    private const int BoxHeaderLength = 8, ExtendedBoxHeaderLength = 16;
    private const long ExtendedLength = 1, LengthToEnd = 0;

    // The boxes of one parent are counted, and header boxes nested in header boxes are only followed a few levels
    // deep: each level is a call, and a file of nothing but nested header boxes would otherwise overflow the stack.
    private const int MaxBoxes = 10000, MaxHeaderDepth = 8;

    // colr: method, precedence and approximation, then an enumerated colour space or an ICC profile, whose header has
    // the profile's colour space at offset 16.
    private const int ColourMethodEnumerated = 1, ColourMethodIcc = 2, ColourMethodAnyIcc = 3;
    private const int ColourFieldsLength = 3, EnumeratedLength = 7, IccSpaceOffset = 16, IccHeaderLength = 20;
    private const uint IccGray = 0x47524159, IccRgb = 0x52474220, IccCmyk = 0x434D594B; // 'GRAY', 'RGB ', 'CMYK'

    // pclr: the number of entries and of columns, then each column's bit depth (as in SIZ), which is at most 38 bits.
    private const int PaletteFieldsLength = 3, MaxPaletteEntries = 1024, MaxPaletteDepth = 38;
    private const int ComponentMappingLength = 4, ChannelDefinitionLength = 6;

    /// <summary>What the boxes of a JP2 file say: where the codestream is and how its components map to colors.</summary>
    private sealed class Jp2Header
    {
        public int CodestreamStart = -1, CodestreamEnd;

        // From the first colour specification box: an enumerated colour space, or the number of components of an ICC
        // profile's colour space. A negative colour space is the same as none.
        public int EnumeratedColourSpace = -1;
        public int IccComponents;

        public Jp2Palette? Palette;
        public ComponentMapping[]? ComponentMap;
        public ChannelDefinition[]? ChannelDefinitions;
    }

    /// <summary>A palette: each entry has one value per column, and each column its own bit depth.</summary>
    private sealed record Jp2Palette(long[][] Entries, int[] ColumnBits);

    /// <summary>A channel of the image: a component as it is (<see cref="Type"/> 0), or through a palette column (1).</summary>
    private readonly record struct ComponentMapping(int Component, int Type, int PaletteColumn);

    /// <summary>What a channel is: a colour (type 0) or opacity (1 or 2), and the colour it belongs to (0 for the whole image).</summary>
    private readonly record struct ChannelDefinition(int Channel, int Type, int Association);

    /// <summary>
    /// Reads the boxes from <paramref name="pos"/> to <paramref name="end"/> into <paramref name="header"/>, going into the
    /// JP2 header box. Returns false when a box header is cut off.
    /// </summary>
    private static bool ReadBoxes(byte[] data, int pos, int end, Jp2Header header, int depth = 0)
    {
        for (int boxes = 0; pos + BoxHeaderLength <= end && boxes < MaxBoxes; boxes++)
        {
            var reader = new JpxReader(data, pos);
            long length = reader.I32();
            uint type = reader.U32();
            int headerLength = BoxHeaderLength;
            if (length == ExtendedLength)
            {
                if (pos + ExtendedBoxHeaderLength > end) return false;
                length = (long)((ulong)reader.U32() << 32 | reader.U32());
                headerLength = ExtendedBoxHeaderLength;
            }
            else if (length == LengthToEnd) length = end - pos;
            // A truncated last box keeps what is there. The length is compared with what is left, because pos + length
            // wraps around for a 64-bit length near the maximum and would send the reader back to earlier boxes.
            if (length < headerLength || length > end - pos) length = end - pos;
            int bodyStart = pos + headerLength, bodyEnd = (int)(pos + length);
            ReadBox(data, type, bodyStart, bodyEnd, header, depth);
            pos = bodyEnd;
        }
        return true;
    }

    private static void ReadBox(byte[] data, uint type, int start, int end, Jp2Header header, int depth)
    {
        switch (type)
        {
            case HeaderBox:
                if (depth < MaxHeaderDepth) ReadBoxes(data, start, end, header, depth + 1);
                break;
            case CodestreamBox:
                if (header.CodestreamStart < 0) (header.CodestreamStart, header.CodestreamEnd) = (start, end);
                break;
            case ColourSpecificationBox:
                // Only the first colour specification counts.
                if (header.EnumeratedColourSpace < 0 && header.IccComponents == 0) ReadColourSpecification(data, start, end, header);
                break;
            case PaletteBox:
                if (end - start >= PaletteFieldsLength) header.Palette = ReadPalette(data, start, end) ?? header.Palette;
                break;
            case ComponentMappingBox:
                header.ComponentMap = ReadComponentMapping(data, start, end);
                break;
            case ChannelDefinitionBox:
                if (end - start >= 2) header.ChannelDefinitions = ReadChannelDefinitions(data, start, end);
                break;
        }
    }

    private static void ReadColourSpecification(byte[] data, int start, int end, Jp2Header header)
    {
        int length = end - start;
        if (length < ColourFieldsLength) return;
        int method = data[start];
        var reader = new JpxReader(data, start + ColourFieldsLength);
        if (method == ColourMethodEnumerated && length >= EnumeratedLength)
            header.EnumeratedColourSpace = reader.I32();
        else if (method is ColourMethodIcc or ColourMethodAnyIcc && length >= ColourFieldsLength + IccHeaderLength)
        {
            reader.Skip(IccSpaceOffset);
            header.IccComponents = reader.U32() switch
            {
                IccGray => 1,
                IccRgb => 3,
                IccCmyk => 4,
                _ => 0,
            };
        }
    }

    /// <summary>Reads a palette, or returns null when its entry count, column count or a column depth is out of range.</summary>
    private static Jp2Palette? ReadPalette(byte[] data, int start, int end)
    {
        var reader = new JpxReader(data, start);
        int entryCount = reader.U16(), columns = reader.U8();
        if (entryCount is < 1 or > MaxPaletteEntries || columns < 1 || start + PaletteFieldsLength + columns > end) return null;
        var bits = new int[columns];
        for (int c = 0; c < columns; c++)
        {
            bits[c] = (reader.U8() & DepthMask) + 1;
            if (bits[c] > MaxPaletteDepth) return null;
        }
        // Each value takes whole bytes, up to five for 38 bits; a palette cut short is padded with zeros.
        var entries = new long[entryCount][];
        int pos = reader.Position;
        for (int e = 0; e < entryCount; e++)
        {
            var entry = entries[e] = new long[columns];
            for (int c = 0; c < columns; c++)
            {
                int bytes = (bits[c] + 7) / 8;
                long value = 0;
                for (int k = 0; k < bytes; k++) value = value << 8 | (pos < end ? data[pos++] : (byte)0);
                entry[c] = value;
            }
        }
        return new Jp2Palette(entries, bits);
    }

    private static ComponentMapping[] ReadComponentMapping(byte[] data, int start, int end)
    {
        var map = new ComponentMapping[(end - start) / ComponentMappingLength];
        var reader = new JpxReader(data, start);
        for (int i = 0; i < map.Length; i++) map[i] = new ComponentMapping(reader.U16(), reader.U8(), reader.U8());
        return map;
    }

    private static ChannelDefinition[] ReadChannelDefinitions(byte[] data, int start, int end)
    {
        var reader = new JpxReader(data, start);
        var definitions = new ChannelDefinition[Math.Min(reader.U16(), (end - start - 2) / ChannelDefinitionLength)];
        for (int i = 0; i < definitions.Length; i++) definitions[i] = new ChannelDefinition(reader.U16(), reader.U16(), reader.U16());
        return definitions;
    }
}
