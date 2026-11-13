namespace PdfPecker.Pdf;

/// <summary>
/// A write-only stream that appends to a <see cref="ByteBuffer"/>, so that a compressor can write into a buffer whose
/// arrays come from the array pool.
/// </summary>
internal sealed class ByteBufferStream : Stream
{
    private readonly ByteBuffer _buffer;

    public ByteBufferStream(ByteBuffer buffer) => _buffer = buffer;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _buffer.Length;
    public override long Position { get => _buffer.Length; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => _buffer.Bytes(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) => _buffer.Bytes(buffer);
    public override void WriteByte(byte value) => _buffer.Byte(value);
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
