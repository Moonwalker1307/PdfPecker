using System.Runtime.CompilerServices;

namespace PdfPecker.Rendering.Parsing;

/// <summary>Tells whether a byte belongs to a run that <see cref="ByteScanner.SkipWhile{TPredicate}"/> is to skip.</summary>
internal interface IBytePredicate
{
    static abstract bool Matches(byte value);
}

/// <summary>
/// A read position over the bytes of a PDF that the lexer is tokenizing. Unlike a forward-only reader it can look at the
/// byte under the cursor without moving, which the PDF token grammar needs everywhere, and it can be set back. The start
/// position may lie outside the span: <see cref="AtEnd"/> then reports the end for a position past it, and reading a
/// negative position fails with <see cref="IndexOutOfRangeException"/>, as indexing the array did before.
/// </summary>
internal ref struct ByteScanner
{
    private readonly ReadOnlySpan<byte> data;

    public ByteScanner(ReadOnlySpan<byte> data, int position)
    {
        this.data = data;
        Position = position;
    }

    public int Position { get; set; }

    public readonly bool AtEnd => Position >= data.Length;

    /// <summary>The byte under the cursor. The caller has checked <see cref="AtEnd"/>.</summary>
    public readonly byte Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => data[Position];
    }

    /// <summary>The bytes from the cursor to the end.</summary>
    public readonly ReadOnlySpan<byte> Rest => Position >= data.Length ? default : data.Slice(Position);

    /// <summary>Moves past the bytes that <typeparamref name="TPredicate"/> accepts.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipWhile<TPredicate>() where TPredicate : IBytePredicate
    {
        // The position is kept in a local so that the loop does not store it to memory on every byte.
        var bytes = data;
        int position = Position;
        while ((uint)position < (uint)bytes.Length && TPredicate.Matches(bytes[position])) position++;
        Position = position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count = 1) => Position += count;

    /// <summary>Moves past the byte under the cursor when it equals <paramref name="expected"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryConsume(byte expected)
    {
        if (AtEnd || Current != expected) return false;
        Position++;
        return true;
    }

    /// <summary>Moves past the byte under the cursor and returns it, or returns false at the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
}
