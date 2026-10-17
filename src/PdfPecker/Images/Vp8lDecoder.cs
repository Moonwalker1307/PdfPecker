namespace PdfPecker.Images;

/// <summary>
/// Decoder for lossless WebP (the VP8L bitstream): prefix-coded ARGB pixels with LZ77 back-references and a colour
/// cache, undone through up to four image transforms. Also decodes the lossless alpha planes of lossy WebP images.
/// </summary>
internal ref partial struct Vp8lDecoder
{
    // The chunk starts with a signature byte, then the width and height minus one in 14 bits each, a hint that alpha
    // is used, and a version number that must be zero.
    private const byte Signature = 0x2F;
    private const int MinChunkLength = 5;
    private const int SizeBits = 14;
    private const int VersionBits = 3;

    // The alphabets of a prefix code group: the first code holds the green values, then the back-reference lengths,
    // then the colour cache indexes; the last code holds the back-reference distances.
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int NumDistanceCodes = 40;
    private const int MaxCacheBits = 11;

    // A transform is a 2-bit type; predictor and cross-colour tiles are 2^(3-bit value + 2) pixels wide; a palette has
    // up to 256 colours, and up to 16 of them let several pixels share one coded pixel.
    private const int TransformTypeBits = 2;
    private const int TileSizeBits = 3;
    private const int MinTileBits = 2;
    private const int PaletteSizeBits = 8;
    private const int EntropyImageBits = 3;

    // Distance codes 1..120 map to nearby pixels: high nibble is the row above, low nibble is 8 minus the column offset.
    private const int NumPlaneCodes = 120;
    private static readonly byte[] CodeToPlane =
    {
        0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a,
        0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03,
        0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d, 0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
        0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
        0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e, 0x00, 0x74, 0x7c, 0x41,
        0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
    };

    // A stream only needs more prefix code lookup entries than this when its header is damaged or hostile.
    private const long MaxTableEntries = 1 << 24;

    private BitReader reader;
    private readonly long maxPixels;
    private readonly List<Transform> transforms = new();
    private int seenTransforms; // one bit per transform type, as each may appear only once
    private long work; // pixels decoded in all images of the stream, against the pixel budget
    private long tableEntries; // prefix code lookup entries kept, against MaxTableEntries
    private int[]? codeLengths; // the code lengths of the prefix code being read, a buffer shared by all of them

    private Vp8lDecoder(byte[] data, int offset, int length, long maxPixels)
    {
        reader = new BitReader(Range(data, offset, length));
        this.maxPixels = maxPixels;
    }

    /// <summary>Decodes a VP8L chunk (with its signature and size header) to ARGB pixels, or returns null.</summary>
    public static uint[]? Decode(byte[] data, int offset, int length, long maxPixels, out int width, out int height, out bool hasAlpha)
    {
        width = height = 0;
        hasAlpha = false;
        if (length < MinChunkLength || data[offset] != Signature) return null;
        var decoder = new Vp8lDecoder(data, offset + 1, length - 1, maxPixels);
        width = (int)decoder.reader.Read(SizeBits) + 1;
        height = (int)decoder.reader.Read(SizeBits) + 1;
        hasAlpha = decoder.reader.ReadBit();
        if (decoder.reader.Read(VersionBits) != 0) return null;
        if ((long)width * height > maxPixels) return null;
        return decoder.DecodeImage(width, height);
    }

    /// <summary>Decodes a headerless VP8L stream of a known size (the lossless alpha plane of a lossy image).</summary>
    public static uint[]? DecodeStream(byte[] data, int offset, int length, int width, int height, long maxPixels)
    {
        if ((long)width * height > maxPixels) return null;
        return new Vp8lDecoder(data, offset, length, maxPixels).DecodeImage(width, height);
    }

    /// <summary>The bytes from <paramref name="offset"/> on, at most <paramref name="length"/> of them and none past the end.</summary>
    private static ReadOnlySpan<byte> Range(byte[] data, int offset, int length)
    {
        int end = Math.Min(data.Length, offset + Math.Max(0, length));
        return end > offset ? data.AsSpan(offset, end - offset) : default;
    }

    /// <summary>Reads the transforms and the coded image, then undoes the transforms, the last one read first.</summary>
    private uint[]? DecodeImage(int width, int height)
    {
        try
        {
            // Colour indexing packs several pixels into one, so the coded image can be narrower than the picture.
            int codedWidth = width;
            while (reader.ReadBit())
                if (!ReadTransform(ref codedWidth, height)) return null;
            var pixels = DecodeImageStream(codedWidth, height, isMainImage: true);
            if (pixels == null) return null;
            for (int i = transforms.Count - 1; i >= 0; i--) pixels = transforms[i].Undo(pixels, height);
            return pixels;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static int SubSampleSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    private bool ReadTransform(ref int codedWidth, int height)
    {
        var type = (TransformType)reader.Read(TransformTypeBits);
        int typeBit = 1 << (int)type;
        if ((seenTransforms & typeBit) != 0) return false;
        seenTransforms |= typeBit;
        switch (type)
        {
            case TransformType.Predictor:
            case TransformType.CrossColor:
            {
                int tileBits = (int)reader.Read(TileSizeBits) + MinTileBits;
                var tiles = DecodeImageStream(SubSampleSize(codedWidth, tileBits), SubSampleSize(height, tileBits), isMainImage: false);
                if (tiles == null) return false;
                transforms.Add(new Transform(type, tileBits, codedWidth, tiles));
                break;
            }
            case TransformType.SubtractGreen:
                transforms.Add(new Transform(type, 0, codedWidth, null));
                break;
            case TransformType.ColorIndexing:
            {
                int colors = (int)reader.Read(PaletteSizeBits) + 1;
                int packingBits = PackingBits(colors);
                var palette = DecodeImageStream(colors, 1, isMainImage: false);
                if (palette == null) return false;
                transforms.Add(new Transform(type, packingBits, codedWidth, ExpandPalette(palette, colors, packingBits)));
                codedWidth = SubSampleSize(codedWidth, packingBits);
                break;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ entropy-coded image

    /// <summary>
    /// One prefix-coded image: the main image, or a transform's tiles, a palette or an entropy image. Only the main
    /// image may have an entropy image of its own, which chooses one of several groups of prefix codes per tile.
    /// </summary>
    private uint[]? DecodeImageStream(int width, int height, bool isMainImage)
    {
        long pixelCount = (long)width * height;
        work += pixelCount;
        if (pixelCount > maxPixels || work > maxPixels * 2) return null;

        if (!TryReadColorCacheBits(out int cacheBits)) return null;
        var entropyImage = default(EntropyImage);
        if (isMainImage && reader.ReadBit() && !TryReadEntropyImage(width, height, out entropyImage)) return null;
        var groups = ReadPrefixCodeGroups(entropyImage, cacheBits);
        return groups == null ? null : DecodePixels(width, (int)pixelCount, groups, entropyImage, cacheBits);
    }

    private bool TryReadColorCacheBits(out int cacheBits)
    {
        cacheBits = 0;
        if (!reader.ReadBit()) return true;
        cacheBits = (int)reader.Read(4);
        return cacheBits is >= 1 and <= MaxCacheBits;
    }

    /// <summary>Reads the entropy image and turns its pixels into the indexes of their prefix code groups.</summary>
    private bool TryReadEntropyImage(int width, int height, out EntropyImage entropyImage)
    {
        int tileBits = (int)reader.Read(EntropyImageBits) + MinTileBits;
        int tilesPerRow = SubSampleSize(width, tileBits);
        var groups = DecodeImageStream(tilesPerRow, SubSampleSize(height, tileBits), isMainImage: false);
        if (groups == null)
        {
            entropyImage = default;
            return false;
        }
        // The group index is in the red and green channels.
        uint largest = 0;
        for (int i = 0; i < groups.Length; i++)
        {
            uint group = (groups[i] >> 8) & 0xFFFF;
            groups[i] = group;
            largest = Math.Max(largest, group);
        }
        entropyImage = new EntropyImage(tileBits, tilesPerRow, groups, (int)largest);
        return true;
    }

    /// <summary>
    /// Reads every prefix code group of the image. All of them are in the stream, but only the ones the entropy image
    /// refers to are kept, and only those count against the lookup table budget.
    /// </summary>
    private PrefixCodeGroup[]? ReadPrefixCodeGroups(in EntropyImage entropyImage, int cacheBits)
    {
        var used = new bool[entropyImage.GroupCount];
        if (entropyImage.Groups is { } indexes)
            foreach (uint index in indexes) used[index] = true;
        else
            used[0] = true;

        int greenAlphabet = NumLiteralCodes + NumLengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0);
        var groups = new PrefixCodeGroup[used.Length];
        for (int i = 0; i < groups.Length; i++)
        {
            bool kept = used[i];
            if (!TryReadKeptCode(greenAlphabet, kept, out var green)
                || !TryReadKeptCode(NumLiteralCodes, kept, out var red)
                || !TryReadKeptCode(NumLiteralCodes, kept, out var blue)
                || !TryReadKeptCode(NumLiteralCodes, kept, out var alpha)
                || !TryReadKeptCode(NumDistanceCodes, kept, out var distance))
                return null;
            if (kept) groups[i] = new PrefixCodeGroup(green, red, blue, alpha, distance);
        }
        return groups;
    }

    /// <summary>Reads one prefix code of a group; false when a kept code takes the lookup tables over their budget.</summary>
    private bool TryReadKeptCode(int alphabetSize, bool kept, out PrefixCode code)
    {
        code = ReadPrefixCode(alphabetSize) ?? throw new InvalidDataException("Bad prefix code.");
        return !kept || (tableEntries += code.TableSize) <= MaxTableEntries;
    }

    /// <summary>Decodes the pixels of an image: literal colours, back-references to earlier pixels and colour cache hits.</summary>
    private uint[] DecodePixels(int width, int pixelCount, PrefixCodeGroup[] groups, in EntropyImage entropyImage, int cacheBits)
    {
        var pixels = new uint[pixelCount];
        var cache = new ColorCache(cacheBits);
        var groupIndexes = entropyImage.Groups;
        int tileBits = entropyImage.TileBits, tilesPerRow = entropyImage.TilesPerRow;
        int position = 0, x = 0, y = 0;
        var group = groups[0];
        while (position < pixels.Length)
        {
            if (groupIndexes != null) group = groups[groupIndexes[(y >> tileBits) * tilesPerRow + (x >> tileBits)]];
            int code = group.Green.Read(ref reader);
            if (code < NumLiteralCodes)
            {
                int red = group.Red.Read(ref reader), blue = group.Blue.Read(ref reader), alpha = group.Alpha.Read(ref reader);
                uint argb = (uint)(alpha << 24 | red << 16 | code << 8 | blue);
                pixels[position++] = argb;
                cache.Insert(argb);
                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }
            else if (code < NumLiteralCodes + NumLengthCodes)
            {
                int length = ReadLengthOrDistance(code - NumLiteralCodes);
                int distance = PlaneCodeToDistance(width, ReadLengthOrDistance(group.Distance.Read(ref reader)));
                if (distance > position || length > pixels.Length - position) throw new InvalidDataException("Bad back-reference.");
                // The copy may overlap what it writes, which repeats the last pixels, so it goes one pixel at a time.
                for (int end = position + length; position < end; position++)
                {
                    uint argb = pixels[position - distance];
                    pixels[position] = argb;
                    cache.Insert(argb);
                }
                x += length;
                while (x >= width)
                {
                    x -= width;
                    y++;
                }
            }
            else
            {
                if (!cache.TryLookup(code - NumLiteralCodes - NumLengthCodes, out uint argb)) throw new InvalidDataException("Bad colour cache index.");
                pixels[position++] = argb;
                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }
            if (reader.Overrun) throw new InvalidDataException("Truncated lossless WebP data.");
        }
        return pixels;
    }

    /// <summary>A back-reference length or distance: the first four symbols are the value itself, later ones take extra bits.</summary>
    private int ReadLengthOrDistance(int symbol)
    {
        if (symbol < 4) return symbol + 1;
        int extraBits = (symbol - 2) >> 1;
        int offset = (2 + (symbol & 1)) << extraBits;
        return offset + (int)reader.Read(extraBits) + 1;
    }

    /// <summary>Distance codes up to 120 name a pixel near the current one; larger codes are a plain distance plus 120.</summary>
    private static int PlaneCodeToDistance(int width, int planeCode)
    {
        if (planeCode > NumPlaneCodes) return planeCode - NumPlaneCodes;
        int plane = CodeToPlane[planeCode - 1];
        int rows = plane >> 4, columns = 8 - (plane & 0xF);
        int distance = rows * width + columns;
        return distance >= 1 ? distance : 1;
    }

    /// <summary>
    /// The tiles of the main image and the prefix code group each one uses; <see cref="Groups"/> is null when the
    /// whole image uses one group.
    /// </summary>
    private readonly record struct EntropyImage(int TileBits, int TilesPerRow, uint[]? Groups, int LargestGroup)
    {
        public int GroupCount => Groups == null ? 1 : LargestGroup + 1;
    }

    /// <summary>Recently seen colours, at a hash of their value, that later pixels can refer to by that hash.</summary>
    private readonly struct ColorCache
    {
        private const uint HashMultiplier = 0x1e35a7bd;

        private readonly uint[]? entries;
        private readonly int shift;

        public ColorCache(int bits)
        {
            entries = bits > 0 ? new uint[1 << bits] : null;
            shift = 32 - bits;
        }

        public void Insert(uint argb)
        {
            if (entries != null) entries[(HashMultiplier * argb) >> shift] = argb;
        }

        public bool TryLookup(int key, out uint argb)
        {
            if (entries == null || key >= entries.Length)
            {
                argb = 0;
                return false;
            }
            argb = entries[key];
            return true;
        }
    }
}
