using System.Buffers.Binary;

namespace PdfPecker.Fonts;

/// <summary>A design axis of a variable font (fvar): weight, width, slant, optical size or a custom one.</summary>
internal readonly record struct VariationAxis(uint Tag, float Min, float Default, float Max)
{
    public string Name => new(new[] { (char)(Tag >> 24), (char)(Tag >> 16 & 0xFF), (char)(Tag >> 8 & 0xFF), (char)(Tag & 0xFF) });
}

/// <summary>The design space: the axes of fvar, and the normalized coordinates of a point in it, mapped through avar.</summary>
internal static partial class FontVariations
{
    // fvar header: the offset of the axis records, their count and their size; named instances follow them.
    private const int FvarHeaderLength = 16;
    private const int AxesArrayOffsetField = 4, AxisCountField = 8, AxisSizeField = 10;

    // An axis record: tag, then the minimum, default and maximum as 16.16 fixed numbers, then flags and a name.
    private const int MinAxisRecordLength = 20;
    private const int AxisTagField = 0, AxisMinField = 4, AxisDefaultField = 8, AxisMaxField = 12;
    private const float FixedOne = 65536f;

    // avar header: version and a reserved field, then the axis count and one segment map per axis. A map is a count
    // of (from, to) pairs of F2Dot14 coordinates.
    private const int AvarHeaderLength = 8, AvarAxisCountField = 6;
    private const int SegmentMapCountLength = 2, AxisValueMapLength = 4, AxisValueMapToField = 2;

    // Normalized coordinates are F2Dot14 numbers: 1.0 is 1 << 14. A map below the lowest one starts from -1.
    private const float F2Dot14One = 16384f;
    private const float NoPreviousMapping = -1;

    /// <summary>The axes of a variable font, or an empty array.</summary>
    public static VariationAxis[] ReadAxes(FontProgram p)
    {
        if (p.Table("fvar") is not { Length: >= FvarHeaderLength } fvar) return Array.Empty<VariationAxis>();
        var table = p.Data.AsSpan(fvar.Offset, fvar.Length);
        int axesOffset = BinaryPrimitives.ReadUInt16BigEndian(table[AxesArrayOffsetField..]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(table[AxisCountField..]);
        int size = BinaryPrimitives.ReadUInt16BigEndian(table[AxisSizeField..]);
        if (size < MinAxisRecordLength || axesOffset + (long)count * size > table.Length) return Array.Empty<VariationAxis>();
        var axes = new VariationAxis[count];
        for (int i = 0; i < count; i++) axes[i] = ReadAxis(table.Slice(axesOffset + i * size, MinAxisRecordLength));
        return axes;
    }

    // A minimum above the default or a maximum below it is taken to be the default, so the range always holds it.
    private static VariationAxis ReadAxis(ReadOnlySpan<byte> record)
    {
        float min = Fixed(record[AxisMinField..]), def = Fixed(record[AxisDefaultField..]), max = Fixed(record[AxisMaxField..]);
        if (min > def) min = def;
        if (max < def) max = def;
        return new VariationAxis(BinaryPrimitives.ReadUInt32BigEndian(record[AxisTagField..]), min, def, max);
    }

    private static float Fixed(ReadOnlySpan<byte> field) => BinaryPrimitives.ReadInt32BigEndian(field) / FixedOne;

    /// <summary>
    /// The normalized coordinates (-1 to 1, after avar) of a point in the design space, rounded to F2Dot14 as the
    /// spec requires. Axes not given keep their default.
    /// </summary>
    public static float[] Normalize(FontProgram p, VariationAxis[] axes, IReadOnlyDictionary<uint, float> values)
    {
        var coords = new float[axes.Length];
        for (int i = 0; i < axes.Length; i++)
        {
            var axis = axes[i];
            float value = values.TryGetValue(axis.Tag, out var given) ? Math.Clamp(given, axis.Min, axis.Max) : axis.Default;
            coords[i] = F2Dot14(Math.Clamp(Normalize(axis, value), -1, 1));
        }
        ApplyAvar(p, coords);
        return coords;
    }

    // The default maps to 0, the minimum to -1 and the maximum to 1, linearly in between; an axis that does not extend
    // to one side stays at 0 there.
    private static float Normalize(VariationAxis axis, float value)
    {
        if (value < axis.Default) return axis.Default == axis.Min ? 0 : -(axis.Default - value) / (axis.Default - axis.Min);
        if (value > axis.Default) return axis.Max == axis.Default ? 0 : (value - axis.Default) / (axis.Max - axis.Default);
        return 0;
    }

    private static float F2Dot14(float v) => MathF.Round(v * F2Dot14One) / F2Dot14One;

    private static float F2Dot14At(ReadOnlySpan<byte> field) => BinaryPrimitives.ReadInt16BigEndian(field) / F2Dot14One;

    // avar maps each axis's coordinate through its own piecewise linear segment map. A map cut off by the end of the
    // table ends the mapping there: the axes before it keep their new values, the rest their old ones.
    private static void ApplyAvar(FontProgram p, float[] coords)
    {
        if (p.Table("avar") is not { Length: >= AvarHeaderLength } avar) return;
        var table = p.Data.AsSpan(avar.Offset, avar.Length);
        int axisCount = BinaryPrimitives.ReadUInt16BigEndian(table[AvarAxisCountField..]);
        int at = AvarHeaderLength;
        for (int i = 0; i < axisCount && i < coords.Length; i++)
        {
            if (at + SegmentMapCountLength > table.Length) return;
            int mapsLength = BinaryPrimitives.ReadUInt16BigEndian(table[at..]) * AxisValueMapLength;
            at += SegmentMapCountLength;
            if (at + mapsLength > table.Length) return;
            coords[i] = MapThroughSegments(table.Slice(at, mapsLength), coords[i]);
            at += mapsLength;
        }
    }

    // The maps are in order of their "from" coordinate: a coordinate on one takes its "to" value, one between two is
    // interpolated, and one outside every pair, or after pairs out of order, keeps its value.
    private static float MapThroughSegments(ReadOnlySpan<byte> maps, float v)
    {
        float prevFrom = NoPreviousMapping, prevTo = NoPreviousMapping;
        for (int at = 0; at < maps.Length; at += AxisValueMapLength)
        {
            float from = F2Dot14At(maps[at..]), to = F2Dot14At(maps[(at + AxisValueMapToField)..]);
            if (v == from) return to;
            if (v < from) return at > 0 && from > prevFrom ? F2Dot14(prevTo + (to - prevTo) * (v - prevFrom) / (from - prevFrom)) : v;
            prevFrom = from;
            prevTo = to;
        }
        return v;
    }
}
