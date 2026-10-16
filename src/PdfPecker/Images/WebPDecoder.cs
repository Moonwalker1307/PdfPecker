using System.Buffers.Binary;

namespace PdfPecker.Images;

/// <summary>
/// Reads WebP files: lossy (VP8) and lossless (VP8L) images, alpha planes, and the first frame of animations, as
/// browsers show a WebP that cannot animate.
/// </summary>
internal static class WebPDecoder
{
    // The RIFF header is "RIFF", the size of everything after it, and "WEBP"; then come the chunks.
    private const int RiffHeaderLength = 12;
    private const int RiffSizeField = 4;
    private const int MinLength = 16;

    // VP8X holds flags, then the canvas width and height minus one in 24 bits each.
    private const int CanvasHeaderLength = 10;
    private const int CanvasWidthField = 4;
    private const int CanvasHeightField = 7;

    // ANMF starts with the frame's offset halved, its size, duration and flags, then holds the frame's own chunks.
    private const int FrameHeaderLength = 16;
    private const int FrameXField = 0;
    private const int FrameYField = 3;

    // The ALPH header byte: compression in bits 0-1, prediction filter in bits 2-3, reserved bits 6-7.
    private const int AlphaCompressionMask = 0x03;
    private const int AlphaFilterShift = 2;
    private const int AlphaFilterMask = 0x03;
    private const int AlphaReservedShift = 6;
    private const int RawAlpha = 0;
    private const int LosslessAlpha = 1;

    private const uint Vp8Tag = 'V' | 'P' << 8 | '8' << 16 | ' ' << 24;
    private const uint Vp8LTag = 'V' | 'P' << 8 | '8' << 16 | 'L' << 24;
    private const uint Vp8XTag = 'V' | 'P' << 8 | '8' << 16 | 'X' << 24;
    private const uint AlphTag = 'A' | 'L' << 8 | 'P' << 16 | 'H' << 24;
    private const uint AnmfTag = 'A' | 'N' << 8 | 'M' << 16 | 'F' << 24;

    private enum AlphaFilter
    {
        None = 0,
        Horizontal = 1,
        Vertical = 2,
        Gradient = 3,
    }

    public static bool IsWebP(ReadOnlySpan<byte> data)
        => data.Length >= MinLength && data.StartsWith("RIFF"u8) && data[8..RiffHeaderLength].SequenceEqual("WEBP"u8);

    /// <summary>The decoded picture, or null when the data is not a readable WebP image.</summary>
    public static DecodedImage? Decode(byte[] data, long maxPixels)
    {
        if (!IsWebP(data)) return null;
        // The RIFF size may claim more than the file holds, or less, when something is appended after the image.
        long riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(RiffSizeField));
        var chunks = new ChunkReader(data, RiffHeaderLength, (int)Math.Min(data.Length, riffEnd));
        int canvasWidth = 0, canvasHeight = 0;
        Chunk? alpha = null;
        while (chunks.TryRead(out var chunk))
        {
            switch (chunk.Tag)
            {
                case Vp8XTag:
                    if (chunk.Length >= CanvasHeaderLength)
                    {
                        var canvas = data.AsSpan(chunk.Offset, CanvasHeaderLength);
                        canvasWidth = ReadUInt24(canvas[CanvasWidthField..]) + 1;
                        canvasHeight = ReadUInt24(canvas[CanvasHeightField..]) + 1;
                        if ((long)canvasWidth * canvasHeight > maxPixels) return null;
                    }
                    break;
                case AlphTag:
                    alpha = chunk;
                    break;
                case Vp8Tag:
                case Vp8LTag:
                    return DecodeFrame(data, chunk, alpha, maxPixels);
                case AnmfTag:
                    // A frame needs the canvas that the VP8X chunk declares before it.
                    return chunk.Length >= FrameHeaderLength && canvasWidth > 0
                        ? FirstFrame(data, chunk, canvasWidth, canvasHeight, maxPixels)
                        : null;
            }
        }
        return null;
    }

    /// <summary>The first frame of an animation, placed on the transparent canvas.</summary>
    private static DecodedImage? FirstFrame(byte[] data, Chunk frameChunk, int canvasWidth, int canvasHeight, long maxPixels)
    {
        var header = data.AsSpan(frameChunk.Offset, FrameHeaderLength);
        int offsetX = 2 * ReadUInt24(header[FrameXField..]);
        int offsetY = 2 * ReadUInt24(header[FrameYField..]);
        var chunks = new ChunkReader(data, frameChunk.Offset + FrameHeaderLength, frameChunk.Offset + frameChunk.Length);
        Chunk? alpha = null;
        DecodedImage? decoded = null;
        while (chunks.TryRead(out var chunk))
        {
            if (chunk.Tag == AlphTag)
            {
                alpha = chunk;
            }
            else if (chunk.Tag is Vp8Tag or Vp8LTag)
            {
                decoded = DecodeFrame(data, chunk, alpha, maxPixels);
                break;
            }
        }
        if (decoded is not { } frame) return null;
        if (offsetX == 0 && offsetY == 0 && frame.Width == canvasWidth && frame.Height == canvasHeight) return frame;
        // Around the frame the canvas is only transparent pixels, which the header asks for with no data behind them: a
        // huge canvas from a tiny file and a small frame is not allocated, as with GIF screens.
        long canvasPixels = (long)canvasWidth * canvasHeight;
        if (canvasPixels > 1 << 22 && canvasPixels > 4L * frame.Width * frame.Height && canvasPixels > data.Length * 1000L) return null;

        var rgb = new byte[canvasWidth * canvasHeight * 3];
        var alphaPlane = new byte[canvasWidth * canvasHeight];
        // The frame is clipped to the canvas; a frame that starts past its edge leaves the canvas empty.
        int visibleWidth = Math.Min(frame.Width, canvasWidth - offsetX);
        int visibleHeight = Math.Min(frame.Height, canvasHeight - offsetY);
        if (visibleWidth <= 0) visibleHeight = 0;
        for (int y = 0; y < visibleHeight; y++)
        {
            int source = y * frame.Width, target = (offsetY + y) * canvasWidth + offsetX;
            frame.Rgb.AsSpan(source * 3, visibleWidth * 3).CopyTo(rgb.AsSpan(target * 3));
            var alphaRow = alphaPlane.AsSpan(target, visibleWidth);
            if (frame.Alpha == null) alphaRow.Fill(255);
            else frame.Alpha.AsSpan(source, visibleWidth).CopyTo(alphaRow);
        }
        return new DecodedImage(canvasWidth, canvasHeight, rgb, alphaPlane);
    }

    /// <summary>Decodes a VP8L image, or a VP8 image with the alpha plane of an ALPH chunk that came before it.</summary>
    private static DecodedImage? DecodeFrame(byte[] data, Chunk image, Chunk? alpha, long maxPixels)
    {
        if (image.Tag == Vp8LTag)
        {
            var argb = Vp8lDecoder.Decode(data, image.Offset, image.Length, maxPixels, out int width, out int height, out _);
            if (argb == null) return null;
            var rgb = new byte[width * height * 3];
            var alphaPlane = new byte[width * height];
            for (int i = 0; i < argb.Length; i++)
            {
                uint pixel = argb[i];
                rgb[i * 3] = (byte)(pixel >> 16);
                rgb[i * 3 + 1] = (byte)(pixel >> 8);
                rgb[i * 3 + 2] = (byte)pixel;
                alphaPlane[i] = (byte)(pixel >> 24);
            }
            return new DecodedImage(width, height, rgb, alphaPlane);
        }

        var lossy = Vp8Decoder.Decode(data, image.Offset, image.Length, maxPixels);
        if (lossy is not { } picture || alpha is not { } alphaChunk) return lossy;
        // A missing or damaged alpha plane leaves the picture opaque.
        var plane = DecodeAlpha(data, alphaChunk, picture.Width, picture.Height, maxPixels);
        return plane == null ? picture : picture with { Alpha = plane };
    }

    /// <summary>The alpha plane of a lossy image (the ALPH chunk): raw or lossless-coded, then unfiltered.</summary>
    private static byte[]? DecodeAlpha(byte[] data, Chunk chunk, int width, int height, long maxPixels)
    {
        if (chunk.Length <= 1) return null;
        int header = data[chunk.Offset];
        int compression = header & AlphaCompressionMask;
        var filter = (AlphaFilter)((header >> AlphaFilterShift) & AlphaFilterMask);
        if (compression is not (RawAlpha or LosslessAlpha) || header >> AlphaReservedShift != 0) return null;

        int count = width * height;
        var plane = new byte[count];
        int streamOffset = chunk.Offset + 1, streamLength = chunk.Length - 1;
        if (compression == RawAlpha)
        {
            if (streamLength < count) return null;
            data.AsSpan(streamOffset, count).CopyTo(plane);
        }
        else
        {
            // The plane is coded as a headerless lossless image whose green channel holds the alpha values.
            var argb = Vp8lDecoder.DecodeStream(data, streamOffset, streamLength, width, height, maxPixels);
            if (argb == null) return null;
            for (int i = 0; i < count; i++) plane[i] = (byte)(argb[i] >> 8);
        }
        if (filter != AlphaFilter.None) Unfilter(plane, width, height, filter);
        return plane;
    }

    /// <summary>Undoes the horizontal, vertical or gradient prediction of an alpha plane, in place.</summary>
    private static void Unfilter(Span<byte> plane, int width, int height, AlphaFilter filter)
    {
        for (int y = 0; y < height; y++)
        {
            var row = plane.Slice(y * width, width);
            if (y == 0)
            {
                // Every filter predicts the first row from the left, starting from zero.
                AddLeftPrediction(row, 0);
                continue;
            }
            var above = plane.Slice((y - 1) * width, width);
            switch (filter)
            {
                case AlphaFilter.Horizontal:
                    // Later rows start from the pixel above, then predict from the left.
                    AddLeftPrediction(row, above[0]);
                    break;
                case AlphaFilter.Vertical:
                    for (int x = 0; x < width; x++) row[x] += above[x];
                    break;
                default:
                {
                    // The first pixel takes the one above, as left and top-left both start there.
                    int left = above[0], topLeft = above[0];
                    for (int x = 0; x < width; x++)
                    {
                        int top = above[x];
                        int gradient = Math.Clamp(left + top - topLeft, 0, 255);
                        left = row[x] = (byte)(row[x] + gradient);
                        topLeft = top;
                    }
                    break;
                }
            }
        }
    }

    private static void AddLeftPrediction(Span<byte> row, byte start)
    {
        byte predicted = start;
        for (int x = 0; x < row.Length; x++) predicted = row[x] = (byte)(predicted + row[x]);
    }

    private static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;

    /// <summary>A chunk's FourCC, and where its body starts in the file and how much of it the file holds.</summary>
    private readonly record struct Chunk(uint Tag, int Offset, int Length);

    /// <summary>Walks the RIFF chunks between two offsets, each a FourCC, a 32-bit size, the body and a pad byte.</summary>
    private ref struct ChunkReader
    {
        private const int ChunkHeaderLength = 8;

        private readonly ReadOnlySpan<byte> data;
        private readonly int end;
        private int position;

        public ChunkReader(ReadOnlySpan<byte> data, int start, int end)
        {
            this.data = data;
            this.end = end;
            position = start;
        }

        /// <summary>
        /// Reads the next chunk header; the body is cut at the end of the range. A size that reaches past int.MaxValue
        /// leaves the position there, so the next header read throws ArgumentOutOfRangeException, which the caller
        /// treats as a damaged image.
        /// </summary>
        public bool TryRead(out Chunk chunk)
        {
            if (position + ChunkHeaderLength > end)
            {
                chunk = default;
                return false;
            }
            var header = data.Slice(position, ChunkHeaderLength);
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(header);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            int body = position + ChunkHeaderLength;
            chunk = new Chunk(tag, body, (int)Math.Min(size, end - body));
            // Chunk bodies are padded to an even length.
            position = body + (int)Math.Min(size + (size & 1), int.MaxValue - body);
            return true;
        }
    }
}
