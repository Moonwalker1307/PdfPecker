using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

/// <summary>Minimal PDF inspection helpers for assertions (not a PDF parser).</summary>
internal static class PdfTestHelpers
{
    public static string Latin1(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    public static byte[] ConvertUncompressed(string html, PdfOptions? options = null)
    {
        options ??= new PdfOptions();
        options.Compress = false;
        return HtmlToPdf.Convert(html, options);
    }

    public static int PageCount(byte[] pdf)
    {
        var m = Regex.Match(Latin1(pdf), @"/Type /Pages /Count (\d+)");
        Assert.True(m.Success, "No page tree found");
        return int.Parse(m.Groups[1].Value);
    }

    /// <summary>Checks the overall file structure and that every xref offset points at its object.</summary>
    public static void AssertStructurallyValid(byte[] pdf)
    {
        var text = Latin1(pdf);
        Assert.Matches(@"^%PDF-1\.[47]\n", text);
        Assert.EndsWith("%%EOF\n", text);
        var startxref = Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$");
        Assert.True(startxref.Success, "Missing startxref");
        int xref = int.Parse(startxref.Groups[1].Value);
        Assert.Equal("xref", text.Substring(xref, 4));
        var header = Regex.Match(text.Substring(xref), @"^xref\n0 (\d+)\n");
        int count = int.Parse(header.Groups[1].Value);
        int pos = xref + header.Length + 20; // skip the free entry
        for (int obj = 1; obj < count; obj++, pos += 20)
        {
            int offset = int.Parse(text.Substring(pos, 10));
            Assert.StartsWith(obj + " 0 obj", text.Substring(offset, 20));
        }
    }

    /// <summary>All page content streams, decompressed, concatenated.</summary>
    public static string AllContent(byte[] pdf)
    {
        var text = Latin1(pdf);
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(text, @"<< /Length (\d+)( /Filter /FlateDecode)? >>\nstream\n"))
        {
            int len = int.Parse(m.Groups[1].Value);
            var data = new byte[len];
            Array.Copy(pdf, m.Index + m.Length, data, 0, len);
            if (m.Groups[2].Success)
            {
                using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                z.CopyTo(ms);
                data = ms.ToArray();
            }
            sb.Append(Encoding.Latin1.GetString(data));
        }
        return sb.ToString();
    }

    /// <summary>A small RGBA PNG (opaque red square with a transparent corner).</summary>
    public static byte[] RgbaPng(int size = 4)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < size; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < size; x++)
            {
                raw.WriteByte(255); raw.WriteByte(0); raw.WriteByte(0);
                raw.WriteByte((byte)(x == 0 && y == 0 ? 0 : 255));
            }
        }
        return Png(size, size, 6, raw.ToArray());
    }

    /// <summary>A small opaque RGB PNG.</summary>
    public static byte[] RgbPng(int size = 4)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < size; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < size; x++) { raw.WriteByte(0); raw.WriteByte(128); raw.WriteByte(255); }
        }
        return Png(size, size, 2, raw.ToArray());
    }

    private static byte[] Png(int w, int h, byte colorType, byte[] raw)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w);
        WriteBE(ihdr, 4, h);
        ihdr[8] = 8;
        ihdr[9] = colorType;
        Chunk(ms, "IHDR", ihdr);
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crcInput = typeBytes.Concat(data).ToArray();
        var crc = new byte[4];
        WriteBE(crc, 0, (int)Crc32(crcInput));
        s.Write(crc);
    }

    private static void WriteBE(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
