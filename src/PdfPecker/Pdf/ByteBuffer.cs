using System.Runtime.CompilerServices;

namespace PdfPecker.Pdf;

/// <summary>A growable byte buffer with allocation-free ASCII and number writers.</summary>
internal sealed class ByteBuffer
{
    private byte[] _buf;
    private int _len;

    public ByteBuffer(int capacity = 4096) => _buf = new byte[capacity];

    public int Length => _len;
    public byte[] RawBuffer => _buf;

    public void Clear() => _len = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int extra)
    {
        if (_len + extra > _buf.Length) Grow(extra);
    }

    private void Grow(int extra)
    {
        int size = Math.Max(_buf.Length * 2, _len + extra);
        Array.Resize(ref _buf, size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ByteBuffer Byte(byte b)
    {
        Ensure(1);
        _buf[_len++] = b;
        return this;
    }

    public ByteBuffer Ascii(string s)
    {
        Ensure(s.Length);
        var buf = _buf;
        int len = _len;
        for (int i = 0; i < s.Length; i++) buf[len + i] = (byte)s[i];
        _len = len + s.Length;
        return this;
    }

    public ByteBuffer Bytes(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
        return this;
    }

    public ByteBuffer Int(long v)
    {
        Ensure(21);
        if (v < 0) { _buf[_len++] = (byte)'-'; v = -v; }
        if (v < 10) { _buf[_len++] = (byte)('0' + v); return this; }
        Span<byte> tmp = stackalloc byte[20];
        int n = 0;
        while (v > 0) { tmp[n++] = (byte)('0' + v % 10); v /= 10; }
        while (n > 0) _buf[_len++] = tmp[--n];
        return this;
    }

    /// <summary>Writes a number with at most 3 decimals and no exponent, as PDF requires.</summary>
    public ByteBuffer Num(float f)
    {
        long scaled = (long)Math.Round(f * 1000.0);
        if (scaled < 0) { Byte((byte)'-'); scaled = -scaled; }
        long ip = scaled / 1000;
        int frac = (int)(scaled % 1000);
        Int(ip);
        if (frac != 0)
        {
            Ensure(4);
            _buf[_len++] = (byte)'.';
            int d1 = frac / 100, d2 = frac / 10 % 10, d3 = frac % 10;
            _buf[_len++] = (byte)('0' + d1);
            if (d2 != 0 || d3 != 0) _buf[_len++] = (byte)('0' + d2);
            if (d3 != 0) _buf[_len++] = (byte)('0' + d3);
        }
        return this;
    }

    /// <summary>Writes a number with up to 6 decimals, for matrix coefficients and function bounds.</summary>
    public ByteBuffer Num6(float f)
    {
        long scaled = (long)Math.Round(f * 1000000.0);
        if (scaled % 1000 == 0) return Num(f);
        if (scaled < 0) { Byte((byte)'-'); scaled = -scaled; }
        Int(scaled / 1000000);
        Byte((byte)'.');
        int frac = (int)(scaled % 1000000);
        int digits = 6;
        while (frac % 10 == 0) { frac /= 10; digits--; }
        Span<byte> tmp = stackalloc byte[6];
        for (int i = digits - 1; i >= 0; i--) { tmp[i] = (byte)('0' + frac % 10); frac /= 10; }
        return Bytes(tmp.Slice(0, digits));
    }

    public ByteBuffer Sp() => Byte((byte)' ');
    public ByteBuffer Nl() => Byte((byte)'\n');

    public void CopyTo(Stream s) => s.Write(_buf, 0, _len);

    public ReadOnlySpan<byte> Span => new(_buf, 0, _len);
}
