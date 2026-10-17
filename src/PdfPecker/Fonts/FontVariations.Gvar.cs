namespace PdfPecker.Fonts;

/// <summary>The gvar table: per glyph, the tuple variations whose deltas move its points.</summary>
internal static partial class FontVariations
{
    // gvar header: the axis count, the shared peak tuples, the glyph count, the offset size and the offsets of each
    // glyph's variation data, which count from the start of that data.
    private const int GvarAxisCountField = 4, SharedTupleCountField = 6, SharedTuplesOffsetField = 8;
    private const int GvarGlyphCountField = 12, GvarFlagsField = 14, GlyphVariationDataArrayField = 16, GlyphOffsetsField = 20;
    private const int LongOffsetsFlag = 1;
    private const int ShortGvarOffsetLength = 2, LongGvarOffsetLength = 4, ShortGvarOffsetScale = 2;
    // How far into the table the shared tuples must fit (an old, lenient bound kept as it is).
    private const int SharedTuplesCheckStart = 8;

    // A glyph's variation data: the tuple count with the shared-points flag, the offset of the serialized data, then
    // a header per tuple: the size of its data and its index with three flags.
    private const int TupleVariationCountField = 0, SerializedDataOffsetField = 2, TupleHeadersField = 4;
    private const int TupleCountMask = 0x0FFF, SharedPointNumbers = 0x8000;
    private const int TupleHeaderLength = 4, TupleIndexField = 2;
    private const int EmbeddedPeakTuple = 0x8000, IntermediateRegion = 0x4000, PrivatePointNumbers = 0x2000, TupleIndexMask = 0x0FFF;
    private const int CoordinateLength = 2;

    // Packed point numbers: a count (two bytes when its high bit is set; zero for all points), then runs of byte or
    // word deltas from the previous point number.
    private const int PointCountIsWord = 0x80, PointCountHighMask = 0x7F;
    private const int PointsAreWords = 0x80, PointRunCountMask = 0x7F;

    // Packed deltas: runs of zeros, bytes, words or 32-bit numbers.
    private const int DeltaKindMask = 0xC0, DeltaRunCountMask = 0x3F;
    private const int DeltasAreZero = 0x80, DeltasAreWords = 0x40, DeltasAreLongs = 0xC0;

    // A point list read as "all points" rather than as numbers.
    private const int AllPoints = -1;

    /// <summary>
    /// The glyph variations of a gvar table at one instance. Reads go through the whole font: data that claims more
    /// than the table holds reads on into the bytes after it, and only the end of the file stops it. The buffers are
    /// reused from glyph to glyph.
    /// </summary>
    private sealed class Gvar
    {
        private readonly byte[] _data;
        private readonly TableReader _reader;
        private readonly float[] _coords;
        private readonly int _start, _end, _axisCount, _sharedTuples, _sharedCount, _glyphCount, _dataStart;
        private readonly bool _longOffsets;

        private int[] _sharedPoints = Array.Empty<int>(), _privatePoints = Array.Empty<int>();
        private float[] _dx = Array.Empty<float>(), _dy = Array.Empty<float>(), _deltas = Array.Empty<float>(), _original = Array.Empty<float>();
        private bool[] _touched = Array.Empty<bool>();

        public Gvar(FontProgram p, float[] coords)
        {
            _data = p.Data;
            _reader = new TableReader(p.Data);
            _coords = coords;
            var table = p.Table("gvar")!.Value;
            _start = table.Offset;
            _end = table.Offset + table.Length;
            _axisCount = _reader.U16(_start + GvarAxisCountField);
            _sharedCount = _reader.U16(_start + SharedTupleCountField);
            _sharedTuples = _start + (int)_reader.U32(_start + SharedTuplesOffsetField);
            _glyphCount = _reader.U16(_start + GvarGlyphCountField);
            _longOffsets = (_reader.U16(_start + GvarFlagsField) & LongOffsetsFlag) != 0;
            long dataStart = _reader.U32(_start + GlyphVariationDataArrayField);
            if (_axisCount == 0 || dataStart > table.Length || SharedTuplesCheckStart + (long)_sharedCount * _axisCount * CoordinateLength > table.Length)
                throw new FormatException("Bad gvar header.");
            _dataStart = _start + (int)dataStart;
        }

        private int GlyphDataOffset(int g)
            => _longOffsets
                ? (int)_reader.U32(_start + GlyphOffsetsField + g * LongGvarOffsetLength)
                : _reader.U16(_start + GlyphOffsetsField + g * ShortGvarOffsetLength) * ShortGvarOffsetScale;

        /// <summary>
        /// Adds the glyph's deltas to its points (x, y pairs, then the four phantom points). Points a tuple does not
        /// list are inferred from their neighbours on the contour (IUP) for glyphs with contours.
        /// </summary>
        public void Apply(int gid, ReadOnlySpan<int> contourEnds, Span<float> points, int pointCount)
        {
            if (gid >= _glyphCount) return;
            int dataOffset = GlyphDataOffset(gid), dataEnd = GlyphDataOffset(gid + 1);
            if (dataEnd <= dataOffset) return;
            long glyphData = (long)_dataStart + dataOffset;
            if (dataOffset < 0 || glyphData + TupleHeadersField > _end) throw new FormatException("Glyph variation data outside gvar.");
            int at = (int)glyphData;
            int total = pointCount + PhantomPointCount;
            int tupleCount = _reader.U16(at + TupleVariationCountField) & TupleCountMask;
            bool hasSharedPoints = (_reader.U16(at + TupleVariationCountField) & SharedPointNumbers) != 0;
            int serial = at + _reader.U16(at + SerializedDataOffsetField);
            int header = at + TupleHeadersField;
            int sharedPoints = AllPoints;
            if (hasSharedPoints)
            {
                var packed = new PackedData(_data, serial, _end);
                sharedPoints = ReadPoints(ref packed, ref _sharedPoints);
                serial = packed.Position;
            }
            // Inferred deltas interpolate between the points as they were before any tuple moved them.
            Span<float> original = default;
            if (!contourEnds.IsEmpty)
            {
                original = Buffer(ref _original, points.Length);
                points.CopyTo(original);
            }

            for (int t = 0; t < tupleCount; t++)
            {
                int size = _reader.U16(header), index = _reader.U16(header + TupleIndexField);
                header += TupleHeaderLength;
                int peak;
                if ((index & EmbeddedPeakTuple) != 0)
                {
                    peak = header;
                    header += _axisCount * CoordinateLength;
                }
                else
                {
                    int shared = index & TupleIndexMask;
                    if (shared >= _sharedCount)
                    {
                        serial += size;
                        continue;
                    }
                    peak = _sharedTuples + shared * _axisCount * CoordinateLength;
                }
                int regionStart = -1, regionEnd = -1;
                if ((index & IntermediateRegion) != 0)
                {
                    regionStart = header;
                    regionEnd = header + _axisCount * CoordinateLength;
                    header += _axisCount * CoordinateLength * 2;
                }
                int next = serial + size;
                float scalar = Scalar(peak, regionStart, regionEnd);
                if (scalar != 0)
                {
                    var packed = new PackedData(_data, serial, _end);
                    int tuplePoints = (index & PrivatePointNumbers) != 0 ? ReadPoints(ref packed, ref _privatePoints) : sharedPoints;
                    var numbers = (index & PrivatePointNumbers) != 0 ? _privatePoints : _sharedPoints;
                    ApplyTuple(ref packed, tuplePoints == AllPoints ? default : numbers.AsSpan(0, tuplePoints), tuplePoints == AllPoints,
                        scalar, contourEnds, original, points, total);
                }
                serial = next;
            }
        }

        // One tuple's deltas, scaled: to every point, or to the listed points with the rest inferred.
        private void ApplyTuple(ref PackedData packed, ReadOnlySpan<int> numbers, bool allPoints, float scalar,
            ReadOnlySpan<int> contourEnds, ReadOnlySpan<float> original, Span<float> points, int total)
        {
            int count = allPoints ? total : numbers.Length;
            var dx = ReadDeltas(ref packed, ref _dx, count);
            var dy = ReadDeltas(ref packed, ref _dy, count);
            if (allPoints)
            {
                for (int i = 0; i < total && i < count; i++)
                {
                    points[i * 2] += dx[i] * scalar;
                    points[i * 2 + 1] += dy[i] * scalar;
                }
                return;
            }
            var deltas = Buffer(ref _deltas, total * 2);
            var touched = Buffer(ref _touched, total);
            deltas.Clear();
            touched.Clear();
            for (int i = 0; i < numbers.Length; i++)
            {
                int point = numbers[i];
                if ((uint)point >= (uint)total) continue;
                deltas[point * 2] += dx[i];
                deltas[point * 2 + 1] += dy[i];
                touched[point] = true;
            }
            if (!contourEnds.IsEmpty) Interpolate(contourEnds, original, deltas, touched);
            for (int i = 0; i < total; i++)
            {
                points[i * 2] += deltas[i * 2] * scalar;
                points[i * 2 + 1] += deltas[i * 2 + 1] * scalar;
            }
        }

        /// <summary>How much a tuple applies at the instance's coordinates: 0 to 1.</summary>
        private float Scalar(int peak, int regionStart, int regionEnd)
        {
            float scalar = 1;
            for (int a = 0; a < _axisCount; a++)
            {
                float peakCoord = CoordinateAt(peak, a);
                if (peakCoord == 0) continue;
                float coord = a < _coords.Length ? _coords[a] : 0;
                if (coord == peakCoord) continue;
                if (coord == 0) return 0;
                if (regionStart >= 0)
                {
                    float start = CoordinateAt(regionStart, a), end = CoordinateAt(regionEnd, a);
                    // An invalid region does not restrict the axis.
                    if (start > peakCoord || peakCoord > end || start < 0 && end > 0) continue;
                    if (coord < start || coord > end) return 0;
                    scalar *= coord < peakCoord ? (coord - start) / (peakCoord - start) : (end - coord) / (end - peakCoord);
                }
                else
                {
                    if (coord < Math.Min(0, peakCoord) || coord > Math.Max(0, peakCoord)) return 0;
                    scalar *= coord / peakCoord;
                }
            }
            return scalar;
        }

        private float CoordinateAt(int tuple, int axis) => _reader.S16(tuple + axis * CoordinateLength) / F2Dot14One;

        /// <summary>Infers the deltas of untouched points of each contour from the touched points around them.</summary>
        private static void Interpolate(ReadOnlySpan<int> contourEnds, ReadOnlySpan<float> original, Span<float> deltas, ReadOnlySpan<bool> touched)
        {
            int first = 0;
            foreach (int last in contourEnds)
            {
                if (last < first || last * 2 + 1 >= original.Length) break;
                int firstTouched = -1;
                for (int i = first; i <= last; i++)
                {
                    if (!touched[i]) continue;
                    firstTouched = i;
                    break;
                }
                if (firstTouched >= 0) InterpolateContour(first, last, firstTouched, original, deltas, touched);
                first = last + 1;
            }
        }

        // Walks the contour once round from its first touched point, filling the points strictly between each pair of
        // touched neighbours.
        private static void InterpolateContour(int first, int last, int firstTouched, ReadOnlySpan<float> original, Span<float> deltas, ReadOnlySpan<bool> touched)
        {
            int Next(int i) => i == last ? first : i + 1;
            int prev = firstTouched, point = firstTouched;
            do
            {
                point = Next(point);
                if (!touched[point] && point != firstTouched) continue;
                for (int j = Next(prev); j != point; j = Next(j))
                    for (int axis = 0; axis < 2; axis++)
                        deltas[j * 2 + axis] = Infer(original[j * 2 + axis], original[prev * 2 + axis], original[point * 2 + axis],
                            deltas[prev * 2 + axis], deltas[point * 2 + axis]);
                prev = point;
            } while (point != firstTouched);
        }

        // An untouched point between two touched ones: the nearer delta outside their range, interpolated inside it.
        private static float Infer(float v, float a, float b, float da, float db)
        {
            if (a == b) return da == db ? da : 0;
            if (a > b) { (a, b) = (b, a); (da, db) = (db, da); }
            if (v <= a) return da;
            if (v >= b) return db;
            return da + (v - a) * (db - da) / (b - a);
        }

        // Reads packed point numbers into the buffer and returns how many there are, or AllPoints. Data that ends early
        // leaves the remaining numbers 0; a list at or past the end of the table is empty.
        private static int ReadPoints(ref PackedData packed, ref int[] buffer)
        {
            if (packed.AtEnd) return 0;
            int count = packed.ReadByte();
            if (count == 0) return AllPoints;
            if ((count & PointCountIsWord) != 0) count = (count & PointCountHighMask) << 8 | packed.ReadByte();
            var numbers = Buffer(ref buffer, count);
            int i = 0, value = 0;
            while (i < count && !packed.AtEnd)
            {
                int run = packed.ReadByte();
                bool words = (run & PointsAreWords) != 0;
                int runLength = (run & PointRunCountMask) + 1;
                for (int k = 0; k < runLength && i < count; k++)
                {
                    value += words ? packed.ReadUInt16() : packed.ReadByte();
                    numbers[i++] = value;
                }
            }
            numbers[i..].Clear();
            return count;
        }

        // Reads packed deltas into the buffer; data that ends early leaves the remaining deltas 0.
        private static ReadOnlySpan<float> ReadDeltas(ref PackedData packed, ref float[] buffer, int count)
        {
            var deltas = Buffer(ref buffer, count);
            int i = 0;
            while (i < count && !packed.AtEnd)
            {
                int run = packed.ReadByte();
                int runLength = (run & DeltaRunCountMask) + 1;
                int kind = run & DeltaKindMask;
                for (int k = 0; k < runLength && i < count; k++)
                {
                    deltas[i++] = kind switch
                    {
                        DeltasAreZero => 0,
                        DeltasAreWords => packed.ReadInt16(),
                        DeltasAreLongs => packed.ReadInt32(),
                        _ => (sbyte)packed.ReadByte(),
                    };
                }
            }
            deltas[i..].Clear();
            return deltas;
        }

        private static Span<T> Buffer<T>(ref T[] buffer, int length)
        {
            if (buffer.Length < length) buffer = new T[Math.Max(length, buffer.Length * 2)];
            return buffer.AsSpan(0, length);
        }
    }

    /// <summary>
    /// A read position in packed point numbers or deltas. The end of the table only ends a run's start: the bytes of
    /// a run are read even past it, up to the end of the file.
    /// </summary>
    private ref struct PackedData(ReadOnlySpan<byte> data, int position, int end)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private readonly int _end = end;

        public int Position { get; private set; } = position;

        public readonly bool AtEnd => Position >= _end;

        public byte ReadByte() => _data[Position++];

        public int ReadUInt16()
        {
            int value = _data[Position] << 8 | _data[Position + 1];
            Position += 2;
            return value;
        }

        public short ReadInt16() => (short)ReadUInt16();

        public int ReadInt32()
        {
            int value = _data[Position] << 24 | _data[Position + 1] << 16 | _data[Position + 2] << 8 | _data[Position + 3];
            Position += 4;
            return value;
        }
    }
}
