using System.Buffers;
using System.Buffers.Binary;

namespace PdfPecker.Fonts;

/// <summary>TrueType outlines at an instance: every glyph of glyf with its gvar deltas, written again without hints.</summary>
internal static partial class FontVariations
{
    private const int ShortLocaEntryLength = 2, LongLocaEntryLength = 4, ShortLocaScale = 2;

    // A glyph starts with its contour count (negative for a composite) and bounding box; anything shorter is empty.
    private const int GlyphHeaderLength = 10;

    // Simple glyph point flags. A short coordinate is one unsigned byte whose sign is the "same or positive" bit; a
    // coordinate with neither bit set is a signed 16-bit delta, and one with only the "same" bit repeats the last.
    private const byte OnCurvePoint = 0x01, XShortVector = 0x02, YShortVector = 0x04, RepeatFlag = 0x08;
    private const byte XIsSameOrPositive = 0x10, YIsSameOrPositive = 0x20, OverlapSimple = 0x40;
    private const int MaxShortDelta = 255;

    // Composite glyph component flags, and the sizes of the three kinds of transform that may follow the arguments.
    private const int ArgsAreWords = 0x0001, ArgsAreXYValues = 0x0002, WeHaveAScale = 0x0008, MoreComponents = 0x0020;
    private const int WeHaveAnXAndYScale = 0x0040, WeHaveATwoByTwo = 0x0080, WeHaveInstructions = 0x0100;
    private const int ScaleLength = 2, XAndYScaleLength = 4, TwoByTwoLength = 8;
    private const int ComponentHeaderLength = 4, WordArgumentsLength = 4;

    // gvar moves four phantom points after a glyph's own: the origin and the advance on the x axis, then two for the
    // vertical metrics, which an instance does not use. Points are stored as x, y pairs.
    private const int PhantomPointCount = 4;
    private const int OriginPhantom = 0, AdvancePhantom = 1;

    private static InstanceOutlines InstanceGlyf(FontProgram p, float[] coords)
    {
        int n = p.NumGlyphs;
        var head = p.Table("head")!.Value;
        bool longLoca = p.S16(head.Offset + HeadIndexToLocFormatField) != 0;
        var loca = p.Table("loca")!.Value;
        var glyf = p.Table("glyf")!.Value;
        if (loca.Length < (n + 1) * (longLoca ? LongLocaEntryLength : ShortLocaEntryLength)) throw new FormatException("The font's loca table is too short.");
        var reader = new TableReader(p.Data);
        int GlyphStart(int g) => longLoca ? (int)reader.U32(loca.Offset + g * LongLocaEntryLength) : reader.U16(loca.Offset + g * ShortLocaEntryLength) * ShortLocaScale;

        var outlines = new InstanceOutlines(new int[n], new short[n], new (short XMin, short YMin, short XMax, short YMax)?[n]);
        var instancer = new GlyphInstancer(p.Data, new Gvar(p, coords));
        var glyfOut = new ArrayBufferWriter<byte>(Math.Max(1, glyf.Length + n * LongLocaEntryLength));
        var locaOut = new byte[(n + 1) * LongLocaEntryLength];
        for (int g = 0; g < n; g++)
        {
            // A glyph outside glyf is empty.
            int start = GlyphStart(g), end = GlyphStart(g + 1);
            if (start < 0 || start > glyf.Length) start = 0;
            if (end < start || end > glyf.Length) end = start;
            BinaryPrimitives.WriteUInt32BigEndian(locaOut.AsSpan(g * LongLocaEntryLength), (uint)glyfOut.WrittenCount);
            int advance = p.Advance(g);
            short lsb = p.LeftSideBearing(g);
            if (end - start < GlyphHeaderLength)
            {
                // An empty glyph (a space): only its advance can vary.
                outlines.Advances[g] = instancer.EmptyGlyphAdvance(g, advance, lsb);
                continue;
            }
            var glyph = instancer.Instance(glyf.Offset + start, g, advance, lsb, glyfOut);
            outlines.Advances[g] = glyph.Advance;
            outlines.Lsbs[g] = glyph.Lsb;
            outlines.Boxes[g] = glyph.Box;
            PadToFourBytes(glyfOut);
        }
        BinaryPrimitives.WriteUInt32BigEndian(locaOut.AsSpan(n * LongLocaEntryLength), (uint)glyfOut.WrittenCount);
        outlines.Tables[TagGlyf] = glyfOut.WrittenSpan.ToArray();
        outlines.Tables[TagLoca] = locaOut;
        return outlines;
    }

    private static void PadToFourBytes(ArrayBufferWriter<byte> output)
    {
        int padding = -output.WrittenCount & 3;
        output.GetSpan(padding)[..padding].Clear();
        output.Advance(padding);
    }

    private readonly record struct InstancedGlyph(int Advance, short Lsb, (short XMin, short YMin, short XMax, short YMax) Box);

    /// <summary>One component of a composite glyph; its transform is kept as the bytes' place in the font.</summary>
    private readonly record struct Component(int Flags, int Glyph, int Arg1, int Arg2, int TransformStart, int TransformLength);

    /// <summary>
    /// Instances glyphs one at a time, reusing its buffers from glyph to glyph. Glyph data is read from the whole font:
    /// a glyph that claims more points or components than its loca length allows reads on into the bytes that follow,
    /// and only the end of the file stops it, as an <see cref="IndexOutOfRangeException"/>.
    /// </summary>
    private sealed class GlyphInstancer(byte[] data, Gvar gvar)
    {
        private int[] _endPoints = Array.Empty<int>();
        private byte[] _flags = Array.Empty<byte>();
        private float[] _points = new float[PhantomPointCount * 2];
        private int[] _xs = Array.Empty<int>(), _ys = Array.Empty<int>();
        private byte[] _xBytes = Array.Empty<byte>(), _yBytes = Array.Empty<byte>();
        private readonly List<Component> _components = new();

        /// <summary>The advance of a glyph without outline data, from its phantom points.</summary>
        public int EmptyGlyphAdvance(int gid, int advance, short lsb)
        {
            var points = Points(0);
            points[OriginPhantom * 2] = -lsb;
            points[AdvancePhantom * 2] = advance - lsb;
            gvar.Apply(gid, default, points, 0);
            return Math.Max(0, (int)MathF.Round(points[AdvancePhantom * 2] - points[OriginPhantom * 2]));
        }

        /// <summary>Writes the glyph at the instance and returns its advance, left side bearing and bounding box.</summary>
        public InstancedGlyph Instance(int offset, int gid, int advance, short lsb, ArrayBufferWriter<byte> output)
        {
            var cursor = new GlyphCursor(data, offset);
            int contours = cursor.ReadInt16();
            short xMin = cursor.ReadInt16(), yMin = cursor.ReadInt16(), xMax = cursor.ReadInt16(), yMax = cursor.ReadInt16();
            // The origin is where the left side bearing puts it from the glyph's left edge.
            float originX = xMin - lsb;
            return contours >= 0
                ? InstanceSimple(ref cursor, contours, gid, originX, advance, output)
                : InstanceComposite(ref cursor, contours, (xMin, yMin, xMax, yMax), gid, originX, advance, output);
        }

        // ---- Simple glyphs: decode the points, add the deltas, encode them again without instructions ----

        private InstancedGlyph InstanceSimple(ref GlyphCursor cursor, int contours, int gid, float originX, int advance, ArrayBufferWriter<byte> output)
        {
            var endPoints = Buffer(ref _endPoints, contours);
            for (int c = 0; c < contours; c++) endPoints[c] = cursor.ReadUInt16();
            int pointCount = contours == 0 ? 0 : endPoints[^1] + 1;
            cursor.Skip(cursor.ReadUInt16()); // instructions
            var flags = ReadFlags(ref cursor, pointCount);
            var points = Points(pointCount);
            ReadCoordinates(ref cursor, flags, points, 0, XShortVector, XIsSameOrPositive);
            ReadCoordinates(ref cursor, flags, points, 1, YShortVector, YIsSameOrPositive);
            SetPhantomPoints(points, pointCount, originX, advance);
            gvar.Apply(gid, endPoints, points, pointCount);

            var box = RoundPoints(points, pointCount);
            var (newOrigin, newAdvance) = RoundedMetrics(points, pointCount);
            WriteSimple(output, contours, box, endPoints, flags, _xs.AsSpan(0, pointCount), _ys.AsSpan(0, pointCount));
            return new InstancedGlyph(newAdvance, (short)Math.Clamp(box.XMin - newOrigin, short.MinValue, short.MaxValue),
                ((short)box.XMin, (short)box.YMin, (short)box.XMax, (short)box.YMax));
        }

        private ReadOnlySpan<byte> ReadFlags(scoped ref GlyphCursor cursor, int pointCount)
        {
            var flags = Buffer(ref _flags, pointCount);
            for (int i = 0; i < pointCount;)
            {
                byte flag = cursor.ReadByte();
                flags[i++] = flag;
                if ((flag & RepeatFlag) == 0) continue;
                int repeat = cursor.ReadByte();
                for (int r = 0; r < repeat && i < pointCount; r++) flags[i++] = flag;
            }
            return flags;
        }

        // One axis of the points: each coordinate is a delta from the one before.
        private static void ReadCoordinates(scoped ref GlyphCursor cursor, ReadOnlySpan<byte> flags, Span<float> points, int axis, byte shortBit, byte sameOrPositiveBit)
        {
            int value = 0;
            for (int i = 0; i < flags.Length; i++)
            {
                byte flag = flags[i];
                if ((flag & shortBit) != 0)
                {
                    int delta = cursor.ReadByte();
                    value += (flag & sameOrPositiveBit) != 0 ? delta : -delta;
                }
                else if ((flag & sameOrPositiveBit) == 0) value += cursor.ReadInt16();
                points[i * 2 + axis] = value;
            }
        }

        // Rounds the points into the coordinate buffers and returns their bounding box (all zero without points).
        private (int XMin, int YMin, int XMax, int YMax) RoundPoints(ReadOnlySpan<float> points, int pointCount)
        {
            var xs = Buffer(ref _xs, pointCount);
            var ys = Buffer(ref _ys, pointCount);
            int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue;
            for (int i = 0; i < pointCount; i++)
            {
                xs[i] = (int)MathF.Round(points[i * 2]);
                ys[i] = (int)MathF.Round(points[i * 2 + 1]);
                xMin = Math.Min(xMin, xs[i]); xMax = Math.Max(xMax, xs[i]);
                yMin = Math.Min(yMin, ys[i]); yMax = Math.Max(yMax, ys[i]);
            }
            if (pointCount == 0) xMin = yMin = xMax = yMax = 0;
            return (xMin, yMin, xMax, yMax);
        }

        private void WriteSimple(ArrayBufferWriter<byte> output, int contours, (int XMin, int YMin, int XMax, int YMax) box,
            ReadOnlySpan<int> endPoints, ReadOnlySpan<byte> sourceFlags, ReadOnlySpan<int> xs, ReadOnlySpan<int> ys)
        {
            int pointCount = xs.Length;
            var xBytes = Buffer(ref _xBytes, pointCount * 2);
            var yBytes = Buffer(ref _yBytes, pointCount * 2);
            int headerLength = GlyphHeaderLength + contours * 2 + 2;
            var writer = new GlyphWriter(output.GetSpan(headerLength + pointCount + pointCount * 4));
            writer.WriteUInt16(contours);
            writer.WriteUInt16(box.XMin); writer.WriteUInt16(box.YMin); writer.WriteUInt16(box.XMax); writer.WriteUInt16(box.YMax);
            foreach (int end in endPoints) writer.WriteUInt16(end);
            writer.WriteUInt16(0); // no instructions
            int xLength = 0, yLength = 0, previousX = 0, previousY = 0;
            for (int i = 0; i < pointCount; i++)
            {
                // Only the on-curve bit and, on the first point, the overlap bit carry over; there are no repeats.
                byte flag = (byte)(sourceFlags[i] & OnCurvePoint | (i == 0 ? sourceFlags[0] & OverlapSimple : 0));
                flag |= EncodeCoordinate(xs[i] - previousX, xBytes, ref xLength, XShortVector, XIsSameOrPositive);
                flag |= EncodeCoordinate(ys[i] - previousY, yBytes, ref yLength, YShortVector, YIsSameOrPositive);
                previousX = xs[i];
                previousY = ys[i];
                writer.WriteByte(flag);
            }
            writer.Write(xBytes[..xLength]);
            writer.Write(yBytes[..yLength]);
            output.Advance(writer.Position);
        }

        // A delta of zero needs no bytes, a small one a byte with its sign in the flag, any other two (only the low 16
        // bits of a delta that does not fit are kept).
        private static byte EncodeCoordinate(int delta, Span<byte> bytes, ref int length, byte shortBit, byte sameOrPositiveBit)
        {
            if (delta == 0) return sameOrPositiveBit;
            if (delta >= -MaxShortDelta && delta <= MaxShortDelta)
            {
                bytes[length++] = (byte)Math.Abs(delta);
                return (byte)(shortBit | (delta > 0 ? sameOrPositiveBit : 0));
            }
            bytes[length++] = (byte)(delta >> 8);
            bytes[length++] = (byte)delta;
            return 0;
        }

        // ---- Composite glyphs: the component offsets are the points that the deltas move ----

        private InstancedGlyph InstanceComposite(ref GlyphCursor cursor, int contours, (short XMin, short YMin, short XMax, short YMax) box,
            int gid, float originX, int advance, ArrayBufferWriter<byte> output)
        {
            var components = _components;
            components.Clear();
            while (true)
            {
                var component = ReadComponent(ref cursor);
                components.Add(component);
                if ((component.Flags & MoreComponents) == 0) break;
            }
            int pointCount = components.Count;
            var points = Points(pointCount);
            for (int i = 0; i < pointCount; i++)
            {
                points[i * 2] = components[i].Arg1;
                points[i * 2 + 1] = components[i].Arg2;
            }
            SetPhantomPoints(points, pointCount, originX, advance);
            gvar.Apply(gid, default, points, pointCount);
            var (newOrigin, newAdvance) = RoundedMetrics(points, pointCount);
            WriteComposite(output, contours, box, points);
            return new InstancedGlyph(newAdvance, (short)Math.Clamp(box.XMin - newOrigin, short.MinValue, short.MaxValue), box);
        }

        private static Component ReadComponent(ref GlyphCursor cursor)
        {
            int flags = cursor.ReadUInt16(), glyph = cursor.ReadUInt16();
            bool xyValues = (flags & ArgsAreXYValues) != 0;
            int arg1, arg2;
            if ((flags & ArgsAreWords) != 0)
            {
                arg1 = xyValues ? cursor.ReadInt16() : cursor.ReadUInt16();
                arg2 = xyValues ? cursor.ReadInt16() : cursor.ReadUInt16();
            }
            else
            {
                arg1 = xyValues ? (sbyte)cursor.ReadByte() : cursor.ReadByte();
                arg2 = xyValues ? (sbyte)cursor.ReadByte() : cursor.ReadByte();
            }
            int transformLength = (flags & WeHaveAScale) != 0 ? ScaleLength
                : (flags & WeHaveAnXAndYScale) != 0 ? XAndYScaleLength
                : (flags & WeHaveATwoByTwo) != 0 ? TwoByTwoLength : 0;
            int transformStart = cursor.Position;
            cursor.ReadBytes(transformLength);
            return new Component(flags, glyph, arg1, arg2, transformStart, transformLength);
        }

        // Each component is written with word arguments (moved when they are offsets, kept when they are point numbers),
        // without instructions; the bounding box stays the default instance's, which matters little in a PDF.
        private void WriteComposite(ArrayBufferWriter<byte> output, int contours, (short XMin, short YMin, short XMax, short YMax) box, ReadOnlySpan<float> points)
        {
            var components = _components;
            int length = GlyphHeaderLength;
            foreach (var component in components) length += ComponentHeaderLength + WordArgumentsLength + component.TransformLength;
            var writer = new GlyphWriter(output.GetSpan(length));
            writer.WriteUInt16(contours);
            writer.WriteUInt16(box.XMin); writer.WriteUInt16(box.YMin); writer.WriteUInt16(box.XMax); writer.WriteUInt16(box.YMax);
            for (int i = 0; i < components.Count; i++)
            {
                var component = components[i];
                bool xyValues = (component.Flags & ArgsAreXYValues) != 0;
                int flags = (component.Flags | ArgsAreWords) & ~WeHaveInstructions;
                flags = i < components.Count - 1 ? flags | MoreComponents : flags & ~MoreComponents;
                writer.WriteUInt16(flags);
                writer.WriteUInt16(component.Glyph);
                writer.WriteUInt16(xyValues ? (int)MathF.Round(points[i * 2]) : component.Arg1);
                writer.WriteUInt16(xyValues ? (int)MathF.Round(points[i * 2 + 1]) : component.Arg2);
                writer.Write(data.AsSpan(component.TransformStart, component.TransformLength));
            }
            output.Advance(writer.Position);
        }

        // ---- Points ----

        // The point buffer for a glyph: its own points, then the four phantom points, all zero.
        private Span<float> Points(int pointCount)
        {
            var points = Buffer(ref _points, (pointCount + PhantomPointCount) * 2);
            points.Clear();
            return points;
        }

        private static void SetPhantomPoints(Span<float> points, int pointCount, float originX, int advance)
        {
            int phantoms = pointCount * 2;
            points[phantoms + OriginPhantom * 2] = originX;
            points[phantoms + AdvancePhantom * 2] = originX + advance;
        }

        // The rounded origin and the advance from it to the advance phantom point.
        private static (int Origin, int Advance) RoundedMetrics(ReadOnlySpan<float> points, int pointCount)
        {
            int phantoms = pointCount * 2;
            int origin = (int)MathF.Round(points[phantoms + OriginPhantom * 2]);
            return (origin, Math.Max(0, (int)MathF.Round(points[phantoms + AdvancePhantom * 2]) - origin));
        }

        private static Span<T> Buffer<T>(ref T[] buffer, int length)
        {
            if (buffer.Length < length) buffer = new T[Math.Max(length, buffer.Length * 2)];
            return buffer.AsSpan(0, length);
        }
    }

    /// <summary>Reads glyph data at a position in the font. A read past the end of the file throws.</summary>
    private ref struct GlyphCursor(ReadOnlySpan<byte> data, int position)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; } = position;

        public byte ReadByte() => _data[Position++];

        public int ReadUInt16()
        {
            int value = _data[Position] << 8 | _data[Position + 1];
            Position += 2;
            return value;
        }

        public short ReadInt16() => (short)ReadUInt16();

        public void Skip(int count) => Position += count;

        /// <summary>Moves past some bytes; bytes past the end of the file throw <see cref="ArgumentOutOfRangeException"/>.</summary>
        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            var bytes = _data.Slice(Position, count);
            Position += count;
            return bytes;
        }
    }

    /// <summary>Writes big-endian glyph data into a span sized for it; 16-bit fields keep the low bits of a larger value.</summary>
    private ref struct GlyphWriter(Span<byte> span)
    {
        private readonly Span<byte> _span = span;

        public int Position { get; private set; }

        public void WriteByte(byte value) => _span[Position++] = value;

        public void WriteUInt16(int value)
        {
            _span[Position++] = (byte)(value >> 8);
            _span[Position++] = (byte)value;
        }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_span[Position..]);
            Position += bytes.Length;
        }
    }
}
