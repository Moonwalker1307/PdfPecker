namespace PdfPecker.Rendering.Graphics;

/// <summary>
/// A soft mask (from an ExtGState /SMask): one byte per device pixel over the rectangle its group drew in, and a single
/// value everywhere else, which is what the mask's backdrop gives where the group draws nothing.
/// </summary>
internal sealed class SoftMask
{
    private readonly byte[] _data;
    private readonly int _x0, _y0, _width, _height;
    private readonly byte _outside;

    /// <summary>
    /// A mask whose top left value is device pixel (<paramref name="x0"/>, <paramref name="y0"/>) and which is
    /// <paramref name="outside"/> outside the <paramref name="width"/> by <paramref name="height"/> rectangle.
    /// </summary>
    public SoftMask(byte[] data, int x0, int y0, int width, int height, byte outside)
    {
        _data = data;
        _x0 = x0;
        _y0 = y0;
        _width = width;
        _height = height;
        _outside = outside;
    }

    /// <summary>
    /// The mask values of <paramref name="count"/> pixels of row <paramref name="y"/> from <paramref name="x0"/>: a
    /// slice of the mask when they lie inside its rectangle, and otherwise put together in <paramref name="scratch"/>.
    /// </summary>
    public ReadOnlySpan<byte> Row(int y, int x0, int count, ref byte[] scratch)
    {
        int row = y - _y0, start = x0 - _x0;
        bool rowInside = row >= 0 && row < _height;
        if (rowInside && start >= 0 && start + count <= _width) return _data.AsSpan(row * _width + start, count);
        if (scratch.Length < count) scratch = new byte[Math.Max(count, scratch.Length * 2)];
        var values = scratch.AsSpan(0, count);
        values.Fill(_outside);
        int from = Math.Max(0, start), to = Math.Min(_width, start + count);
        if (rowInside && to > from) _data.AsSpan(row * _width + from, to - from).CopyTo(values.Slice(from - start));
        return values;
    }
}
