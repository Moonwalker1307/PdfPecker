namespace PdfPecker.Images;

/// <summary>
/// A forward-only read position over a byte span. Reads past the end never throw: <see cref="TryReadByte"/> and
/// <see cref="TryRead"/> report the end, and <see cref="Skip"/> clamps, so a decoder can treat truncated input as
/// ordinary damaged data.
/// </summary>
internal ref struct SpanCursor
{
    private readonly ReadOnlySpan<byte> data;

    public SpanCursor(ReadOnlySpan<byte> data, int position = 0)
    {
        this.data = data;
        Position = position;
    }

    public int Position { get; private set; }

    public readonly int Remaining => data.Length - Position;

    public readonly bool AtEnd => Position >= data.Length;

    public bool TryReadByte(out byte value)
    {
        if (AtEnd)
        {
            value = 0;
            return false;
        }
        value = data[Position++];
        return true;
    }

    /// <summary>Reads <paramref name="count"/> bytes, or returns false and leaves the position alone when fewer remain.</summary>
    public bool TryRead(int count, out ReadOnlySpan<byte> bytes)
    {
        if (count > Remaining)
        {
            bytes = default;
            return false;
        }
        bytes = data.Slice(Position, count);
        Position += count;
        return true;
    }

    public void Skip(int count) => Position = (int)Math.Min((long)Position + count, data.Length);
}
