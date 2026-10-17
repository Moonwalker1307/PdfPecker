using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Images;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Images;

/// <summary>
/// Decodes the first image of a TIFF file: bilevel, grey, palette, RGB and CMYK pixels of 1 to 16 bits, in strips or
/// tiles, chunky or planar, uncompressed or compressed with LZW, Deflate, PackBits, CCITT fax (Group 3 and 4) or JPEG,
/// with the horizontal predictor and an alpha channel. Bilevel images stay 1 bit per pixel.
/// </summary>
internal static class TiffDecoder
{
    private const int CompressionNone = 1;
    private const int CompressionCcittRle = 2;
    private const int CompressionCcittGroup3 = 3;
    private const int CompressionCcittGroup4 = 4;
    private const int CompressionLzw = 5;
    private const int CompressionJpeg = 7;
    private const int CompressionDeflate = 8;
    private const int CompressionPackBits = 32773;
    private const int CompressionLegacyDeflate = 32946;

    private const int WhiteIsZero = 0;
    private const int BlackIsZero = 1;
    private const int Rgb = 2;
    private const int Palette = 3;
    private const int Cmyk = 5;
    private const int YCbCr = 6;

    private const int PlanarSeparate = 2;
    private const int FillOrderLeastSignificantBitFirst = 2;
    private const int PredictorHorizontal = 2;
    private const int PredictorFloatingPoint = 3;
    private const int AssociatedAlpha = 1;
    private const int UnassociatedAlpha = 2;
    private const int T4TwoDimensional = 1;

    // Tiles are multiples of 16 pixels wide and tall.
    private const int TileAlignment = 16;

    // No compression here makes more than about 2000 bytes from one.
    private const long MaxExpansion = 2000;
    private const long LargeImagePixels = 1 << 22;

    public static bool IsTiff(ReadOnlySpan<byte> data)
        => data.Length >= 8 && (data[..4].SequenceEqual("II*\0"u8) || data[..4].SequenceEqual("MM\0*"u8));

    /// <summary>The size and sample format of the image, as read and checked from its directory.</summary>
    private readonly record struct ImageFormat(
        int Width, int Height, int SamplesPerPixel, int BitsPerSample, int Compression, int Photometric, bool Planar,
        int Predictor, bool ReverseBits, int Colors, bool Alpha, bool Premultiplied);

    /// <summary>
    /// How the image is cut into chunks, strips of whole rows or tiles, each compressed on its own. Planar images have
    /// one set of chunks per sample, each in its own plane.
    /// </summary>
    private readonly record struct ChunkLayout(
        int Width, int Height, int Across, int Down, int Planes, int SamplesPerChunk, int RowBytes, long PlaneRowBytes,
        long[] Offsets, long[] ByteCounts, long ReadBudget);

    /// <summary>Decodes one image of the file; <paramref name="page"/> counts the directories from 0.</summary>
    public static PdfImage? Decode(byte[] data, long maxPixels, int page = 0)
    {
        if (!IsTiff(data)) return null;
        var directory = TiffDirectory.Read(data, page);
        if (directory == null
            || !TryReadFormat(directory, maxPixels, out var format)
            || !TryReadChunkLayout(directory, format, data.Length, maxPixels, out var layout))
            return null;

        var planes = DecodePlanes(data, directory, format, layout, out bool decodedYCbCr);
        if (planes == null) return null;
        // The JPEG decoder has already turned YCbCr chunks into RGB.
        int photometric = decodedYCbCr ? Rgb : format.Photometric;
        int width = format.Width, height = format.Height;

        if (format.BitsPerSample == 1 && format.SamplesPerPixel == 1 && photometric is WhiteIsZero or BlackIsZero)
        {
            // Bilevel images stay at 1 bit; the decode array says whether 0 is white or black.
            return new PdfImage
            {
                Width = width, Height = height, ColorSpace = "/DeviceGray", BitsPerComponent = 1, Data = PdfImage.Deflate(planes[0]),
                Decode = photometric == WhiteIsZero ? "[1 0]" : null,
            };
        }

        byte[]? palette = null;
        if (photometric == Palette && (palette = ReadPalette(directory, format.BitsPerSample)) == null) return null;

        var samples = new SampleReader(planes, format.Planar, format.SamplesPerPixel, format.BitsPerSample,
            layout.PlaneRowBytes, directory.LittleEndian);
        var alpha = format.Alpha ? new byte[width * height] : null;
        return photometric is WhiteIsZero or BlackIsZero
            ? ToGray(samples, width, height, photometric == WhiteIsZero, alpha, format.Premultiplied)
            : ToRgb(samples, width, height, photometric, palette, format.Colors, alpha, format.Premultiplied);
    }

    private static bool TryReadFormat(TiffDirectory directory, long maxPixels, out ImageFormat format)
    {
        format = default;
        long width = directory.Single(TiffTag.ImageWidth, 0), height = directory.Single(TiffTag.ImageLength, 0);
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue || width * height > maxPixels) return false;

        int samplesPerPixel = (int)directory.Single(TiffTag.SamplesPerPixel, 1);
        var bitsPerSampleValues = directory.Values(TiffTag.BitsPerSample);
        int bitsPerSample = bitsPerSampleValues.Length > 0 ? (int)bitsPerSampleValues[0] : 1;
        int compression = (int)directory.Single(TiffTag.Compression, CompressionNone);
        int photometric = (int)directory.Single(TiffTag.PhotometricInterpretation, samplesPerPixel >= 3 ? Rgb : BlackIsZero);
        bool planar = directory.Single(TiffTag.PlanarConfiguration, 1) == PlanarSeparate;
        int predictor = (int)directory.Single(TiffTag.Predictor, 1);
        int sampleFormat = (int)directory.Single(TiffTag.SampleFormat, 1);
        bool reverseBits = directory.Single(TiffTag.FillOrder, 1) == FillOrderLeastSignificantBitFirst;

        // Unsigned, signed and undefined integer samples are read alike; floating point samples (3) are not supported.
        if (samplesPerPixel is < 1 or > 8 || bitsPerSample is not (1 or 2 or 4 or 8 or 16) || sampleFormat is not (1 or 2 or 4))
            return false;
        // Every sample must have the same depth, so one sample reader serves them all.
        foreach (long bits in bitsPerSampleValues)
            if (bits != bitsPerSample) return false;

        int colors = photometric switch
        {
            WhiteIsZero or BlackIsZero or Palette => 1,
            Rgb or YCbCr => 3,
            Cmyk => 4,
            _ => -1,
        };
        if (colors < 0 || samplesPerPixel < colors) return false;
        // YCbCr outside JPEG is usually subsampled, which is rare and not supported.
        if (photometric == YCbCr && compression != CompressionJpeg) return false;

        // The first sample after the colours is alpha only when the file says so; other extra samples are ignored.
        var extraSamples = directory.Values(TiffTag.ExtraSamples);
        bool alpha = samplesPerPixel > colors && extraSamples.Length > 0 && extraSamples[0] is AssociatedAlpha or UnassociatedAlpha;
        bool premultiplied = alpha && extraSamples[0] == AssociatedAlpha;

        format = new ImageFormat((int)width, (int)height, samplesPerPixel, bitsPerSample, compression, photometric, planar,
            predictor, reverseBits, colors, alpha, premultiplied);
        return true;
    }

    /// <summary>
    /// Reads where the strips or tiles are and checks that decoding them stays in proportion to the file and the image
    /// before anything is allocated for them.
    /// </summary>
    private static bool TryReadChunkLayout(TiffDirectory directory, in ImageFormat format, int fileLength, long maxPixels, out ChunkLayout layout)
    {
        layout = default;
        int width = format.Width, height = format.Height, bitsPerSample = format.BitsPerSample;
        bool tiled = directory.Contains(TiffTag.TileWidth);
        int chunkWidth = tiled ? (int)directory.Single(TiffTag.TileWidth, 0) : width;
        int chunkHeight = tiled ? (int)directory.Single(TiffTag.TileLength, 0) : (int)Math.Clamp(directory.Single(TiffTag.RowsPerStrip, height), 1, height);
        if (chunkWidth <= 0 || chunkHeight <= 0 || (long)chunkWidth * chunkHeight > maxPixels) return false;
        // Tiles are multiples of 16 pixels, so one never needs to be more than 15 pixels wider or taller than the
        // image; a bigger one would only be allocated and decoded to be cut away.
        if (tiled && (chunkWidth > AlignToTile(width) || chunkHeight > AlignToTile(height))) return false;

        var offsets = directory.Values(tiled ? TiffTag.TileOffsets : TiffTag.StripOffsets);
        var byteCounts = directory.Values(tiled ? TiffTag.TileByteCounts : TiffTag.StripByteCounts);
        int across = (width + chunkWidth - 1) / chunkWidth, down = (height + chunkHeight - 1) / chunkHeight;
        int planes = format.Planar ? format.SamplesPerPixel : 1;
        int samplesPerChunk = format.Planar ? 1 : format.SamplesPerPixel;
        if (offsets.Length < (long)across * down * planes) return false;

        long planeRowBytes = ((long)width * samplesPerChunk * bitsPerSample + 7) / 8;
        long rawBytes = planeRowBytes * height * planes;
        if (rawBytes > int.MaxValue) return false;
        // A few bytes cannot fill a huge image, so it is not allocated for them.
        if ((long)width * height > LargeImagePixels && rawBytes > fileLength * MaxExpansion) return false;

        // Every chunk decodes to at most its own size, so this is all the chunks together can make. Tiles that cross
        // the right and bottom edges pad the image, but never to more than twice its size, rounded up to whole tiles.
        long paddedBytes = ((AlignToTile(width) * samplesPerChunk * bitsPerSample + 7) / 8) * AlignToTile(height) * planes;
        long chunkRowBytes = ((long)chunkWidth * samplesPerChunk * bitsPerSample + 7) / 8;
        long chunkBytes = chunkRowBytes * chunkHeight * across * down * planes;
        if (chunkBytes > 4 * paddedBytes) return false;

        // Chunks may share data, as blank tiles often do, but a thousand pointing at one large blob would copy and decode
        // it a thousand times: what they read together stays in proportion to the file and the image.
        long readBudget = fileLength + 4 * chunkBytes;
        layout = new ChunkLayout(chunkWidth, chunkHeight, across, down, planes, samplesPerChunk, (int)chunkRowBytes,
            planeRowBytes, offsets, byteCounts, readBudget);
        return true;
    }

    private static long AlignToTile(int size) => (size + (TileAlignment - 1L)) / TileAlignment * TileAlignment;

    /// <summary>
    /// Decodes every chunk into its plane: one plane of interleaved samples, or one plane per sample for a planar image.
    /// Returns null when a chunk cannot be decoded.
    /// </summary>
    private static byte[][]? DecodePlanes(byte[] data, TiffDirectory directory, in ImageFormat format, in ChunkLayout layout, out bool decodedYCbCr)
    {
        decodedYCbCr = false;
        var planes = new byte[layout.Planes][];
        for (int p = 0; p < planes.Length; p++) planes[p] = new byte[layout.PlaneRowBytes * format.Height];
        var jpegTables = format.Compression == CompressionJpeg ? directory.Bytes(TiffTag.JpegTables) : null;
        long readBudget = layout.ReadBudget;

        for (int p = 0; p < layout.Planes; p++)
        for (int chunkY = 0; chunkY < layout.Down; chunkY++)
        for (int chunkX = 0; chunkX < layout.Across; chunkX++)
        {
            int index = (p * layout.Down + chunkY) * layout.Across + chunkX;
            long start = layout.Offsets[index];
            long length = index < layout.ByteCounts.Length ? layout.ByteCounts[index] : data.Length - start;
            if (start <= 0 || start >= data.Length) continue; // a missing chunk stays blank
            length = Math.Min(length, data.Length - start);
            if ((readBudget -= Math.Max(length, 0)) < 0) return null;

            var compressed = data.AsSpan((int)start, (int)length).ToArray();
            var chunk = DecompressChunk(compressed, directory, format, layout, jpegTables, ref decodedYCbCr);
            if (chunk == null || !TryUndoPredictor(chunk, format, layout, directory.LittleEndian)) return null;
            CopyIntoPlane(chunk, planes[p], format, layout, chunkX, chunkY);
        }
        return planes;
    }

    /// <summary>
    /// Decompresses one chunk to (at most) its full size of <see cref="ChunkLayout.RowBytes"/> by
    /// <see cref="ChunkLayout.Height"/>, or returns null when the compression or its parameters are not supported.
    /// </summary>
    private static byte[]? DecompressChunk(byte[] compressed, TiffDirectory directory, in ImageFormat format, in ChunkLayout layout,
        byte[]? jpegTables, ref bool decodedYCbCr)
    {
        // Strips may share one blob, so each stops at its own size.
        int expected = layout.RowBytes * layout.Height;
        switch (format.Compression)
        {
            case CompressionNone:
                return compressed;
            case CompressionLzw:
                // Old-style LZW, from before TIFF 6.0, packs its codes least significant bit first, so the clear code that
                // starts the data reads as a zero byte and then an odd one. The current style starts with 0x80.
                if (compressed.Length > 1 && compressed[0] == 0 && (compressed[1] & 1) != 0) return null;
                return Filters.Lzw(compressed, earlyChange: true, expected);
            case CompressionDeflate or CompressionLegacyDeflate:
                return TiffCodecs.Inflate(compressed, expected);
            case CompressionPackBits:
                return TiffCodecs.UnpackBits(compressed, expected);
            case CompressionCcittRle or CompressionCcittGroup3 or CompressionCcittGroup4:
                return DecodeFax(compressed, directory, format, layout);
            case CompressionJpeg:
                return DecodeJpeg(compressed, format, layout, jpegTables, expected, ref decodedYCbCr);
            default:
                return null;
        }
    }

    private static byte[]? DecodeFax(byte[] compressed, TiffDirectory directory, in ImageFormat format, in ChunkLayout layout)
    {
        if (format.BitsPerSample != 1 || layout.SamplesPerChunk != 1) return null;
        if (format.ReverseBits) TiffCodecs.ReverseBitOrder(compressed);
        // K selects the coding as in a PDF CCITTFaxDecode filter: negative for Group 4, positive for two-dimensional
        // Group 3, and 0 for one-dimensional Group 3 and the modified Huffman (RLE) variant.
        int k = format.Compression switch
        {
            CompressionCcittGroup4 => -1,
            CompressionCcittGroup3 when (directory.Single(TiffTag.T4Options, 0) & T4TwoDimensional) != 0 => 1,
            _ => 0,
        };
        // Fax data codes white runs as 0 bits and black runs as 1; the photometric interpretation then says what 0 and
        // 1 are, as for any other bilevel image.
        return CcittFaxDecoder.Decode(compressed, k, layout.Width, layout.Height,
            byteAlign: format.Compression == CompressionCcittRle, blackIs1: true);
    }

    private static byte[]? DecodeJpeg(byte[] compressed, in ImageFormat format, in ChunkLayout layout, byte[]? jpegTables,
        int expected, ref bool decodedYCbCr)
    {
        if (format.BitsPerSample != 8) return null;
        var jpeg = compressed;
        if (jpegTables is { Length: > 4 } && compressed.Length > 2)
        {
            // An abbreviated stream: the shared tables, without their own start and end markers, go in after its
            // start-of-image marker.
            jpeg = new byte[2 + jpegTables.Length - 4 + compressed.Length - 2];
            jpeg[0] = 0xFF;
            jpeg[1] = 0xD8;
            jpegTables.AsSpan(2, jpegTables.Length - 4).CopyTo(jpeg.AsSpan(2));
            compressed.AsSpan(2).CopyTo(jpeg.AsSpan(jpegTables.Length - 2));
        }
        bool ycbcr = format.Photometric == YCbCr;
        var decoded = JpegDecoder.Decode(jpeg, colorTransform: ycbcr);
        if (decoded is not { } image || image.Components != layout.SamplesPerChunk && !(ycbcr && image.Components == 3)) return null;
        decodedYCbCr |= ycbcr;

        var chunk = new byte[expected];
        int imageRowBytes = image.Width * image.Components, copy = Math.Min(imageRowBytes, layout.RowBytes);
        for (int y = 0; y < Math.Min(layout.Height, image.Height); y++)
            Array.Copy(image.Data, y * imageRowBytes, chunk, y * layout.RowBytes, copy);
        return chunk;
    }

    /// <summary>
    /// Undoes the predictor of a decompressed chunk, or returns false for one that is not supported. The fax and JPEG
    /// codings never use one, so the tag is ignored for them except for the unsupported floating point predictor.
    /// </summary>
    private static bool TryUndoPredictor(byte[] chunk, in ImageFormat format, in ChunkLayout layout, bool littleEndian)
    {
        bool predictable = format.Compression is not (CompressionJpeg or CompressionCcittRle or CompressionCcittGroup3 or CompressionCcittGroup4);
        if (format.Predictor == PredictorHorizontal && predictable)
        {
            if (format.BitsPerSample is not (8 or 16)) return false;
            TiffCodecs.UndoHorizontalPredictor(chunk, layout.RowBytes, layout.SamplesPerChunk, format.BitsPerSample, littleEndian);
            return true;
        }
        return format.Predictor != PredictorFloatingPoint;
    }

    /// <summary>Copies the chunk's rows into the plane, cut at the image's right and bottom edges.</summary>
    private static void CopyIntoPlane(byte[] chunk, byte[] plane, in ImageFormat format, in ChunkLayout layout, int chunkX, int chunkY)
    {
        int rows = Math.Min(layout.Height, format.Height - chunkY * layout.Height);
        long xBytes = (long)chunkX * layout.Width * layout.SamplesPerChunk * format.BitsPerSample / 8;
        int copy = (int)Math.Min(layout.RowBytes, layout.PlaneRowBytes - xBytes);
        for (int y = 0; y < rows; y++)
        {
            // A chunk that decoded short, such as a truncated uncompressed strip, fills only the rows it has.
            long from = (long)y * layout.RowBytes;
            if (from >= chunk.Length) break;
            long to = (chunkY * layout.Height + y) * layout.PlaneRowBytes + xBytes;
            Array.Copy(chunk, from, plane, to, (int)Math.Min(copy, chunk.Length - from));
        }
    }

    /// <summary>
    /// The colour map as 8-bit RGB triples, or null when it is too short. The file stores 16-bit values, all reds, then
    /// all greens, then all blues.
    /// </summary>
    private static byte[]? ReadPalette(TiffDirectory directory, int bitsPerSample)
    {
        var map = directory.Values(TiffTag.ColorMap);
        int entries = 1 << bitsPerSample;
        if (map.Length < entries * 3) return null;
        var palette = new byte[entries * 3];
        for (int i = 0; i < entries; i++)
            for (int c = 0; c < 3; c++) palette[i * 3 + c] = (byte)(map[c * entries + i] >> 8);
        return palette;
    }

    private static PdfImage ToGray(in SampleReader samples, int width, int height, bool whiteIsZero, byte[]? alpha, bool premultiplied)
    {
        var gray = new byte[width * height];
        for (int y = 0, o = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++, o++)
            {
                byte value = samples.ReadScaled(x, y, 0);
                if (alpha != null) value = Unpremultiply(value, alpha[o] = samples.ReadScaled(x, y, 1), premultiplied);
                gray[o] = whiteIsZero ? (byte)(255 - value) : value;
            }
        }
        var image = new PdfImage { Width = width, Height = height, ColorSpace = "/DeviceGray", Data = PdfImage.Deflate(gray) };
        if (alpha != null && alpha.AsSpan().ContainsAnyExcept((byte)255))
            image.SMask = new PdfImage { Width = width, Height = height, ColorSpace = "/DeviceGray", Data = PdfImage.Deflate(alpha) };
        return image;
    }

    private static PdfImage? ToRgb(in SampleReader samples, int width, int height, int photometric, byte[]? palette, int colors,
        byte[]? alpha, bool premultiplied)
    {
        var rgb = new byte[width * height * 3];
        for (int y = 0, o = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++, o++)
            {
                byte red, green, blue;
                if (palette != null)
                {
                    int entry = samples.Read(x, y, 0) * 3;
                    (red, green, blue) = (palette[entry], palette[entry + 1], palette[entry + 2]);
                }
                else if (photometric == Cmyk)
                {
                    var (r, g, b) = DeviceCmyk.Convert(samples.ReadScaled(x, y, 0) / 255f, samples.ReadScaled(x, y, 1) / 255f,
                        samples.ReadScaled(x, y, 2) / 255f, samples.ReadScaled(x, y, 3) / 255f);
                    (red, green, blue) = ((byte)r, (byte)g, (byte)b);
                }
                else (red, green, blue) = (samples.ReadScaled(x, y, 0), samples.ReadScaled(x, y, 1), samples.ReadScaled(x, y, 2));

                if (alpha != null)
                {
                    byte a = alpha[o] = samples.ReadScaled(x, y, colors);
                    red = Unpremultiply(red, a, premultiplied);
                    green = Unpremultiply(green, a, premultiplied);
                    blue = Unpremultiply(blue, a, premultiplied);
                }
                rgb[o * 3] = red;
                rgb[o * 3 + 1] = green;
                rgb[o * 3 + 2] = blue;
            }
        }
        return PdfImage.FromPixels(new DecodedImage(width, height, rgb, alpha));
    }

    private static byte Unpremultiply(byte color, byte alpha, bool premultiplied)
        => !premultiplied || alpha == 255 ? color : alpha == 0 ? (byte)0 : (byte)Math.Min(255, (color * 255 + alpha / 2) / alpha);

    /// <summary>Reads one sample of the decoded planes, whatever its depth and whether the samples are planar or interleaved.</summary>
    private readonly struct SampleReader
    {
        private readonly byte[][] planes;
        private readonly bool planar;
        private readonly int samplesPerPixel;
        private readonly int bitsPerSample;
        private readonly int maxValue;
        private readonly long rowBytes;
        private readonly bool littleEndian;

        public SampleReader(byte[][] planes, bool planar, int samplesPerPixel, int bitsPerSample, long rowBytes, bool littleEndian)
        {
            this.planes = planes;
            this.planar = planar;
            this.samplesPerPixel = samplesPerPixel;
            this.bitsPerSample = bitsPerSample;
            this.rowBytes = rowBytes;
            this.littleEndian = littleEndian;
            maxValue = (1 << bitsPerSample) - 1;
        }

        public int Read(int x, int y, int sample)
        {
            var plane = planar ? planes[sample] : planes[0];
            int index = planar ? x : x * samplesPerPixel + sample;
            long row = y * rowBytes;
            switch (bitsPerSample)
            {
                case 8:
                    return plane[row + index];
                case 16:
                    return TiffCodecs.ReadUInt16(plane, row + index * 2, littleEndian);
                default:
                {
                    // Samples of 1, 2 or 4 bits are packed from the most significant bit of each byte.
                    long bit = (long)index * bitsPerSample;
                    return (plane[row + bit / 8] >> (8 - bitsPerSample - (int)(bit % 8))) & maxValue;
                }
            }
        }

        /// <summary>The sample scaled to 8 bits: 16-bit samples keep their high byte, smaller ones stretch to 0 to 255.</summary>
        public byte ReadScaled(int x, int y, int sample)
        {
            int value = Read(x, y, sample);
            return bitsPerSample switch
            {
                16 => (byte)(value >> 8),
                8 => (byte)value,
                _ => (byte)(value * 255 / maxValue),
            };
        }
    }
}
