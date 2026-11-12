using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Tests;

/// <summary>Minimal PDF inspection helpers for assertions (not a PDF parser).</summary>
internal static class PdfTestHelpers
{
    public static string Latin1(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    /// <summary>
    /// The PDF as text with every date blanked, for comparing two runs that may fall in different seconds. Dates appear
    /// as literal strings and, in the VRI /TU entry of long-term validation data, as hex strings.
    /// </summary>
    public static string WithoutDates(byte[] pdf) =>
        Regex.Replace(Regex.Replace(Latin1(pdf), @"D:\d{14}", "D:00000000000000"),
            "(?i)443A(?:3[0-9]){14}", "443A3030303030303030303030303030");

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
        Assert.Matches(@"^%PDF-(1\.[467]|2\.0)\n", text);
        Assert.EndsWith("%%EOF\n", text);
        var startxref = Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$");
        Assert.True(startxref.Success, "Missing startxref");
        int xref = int.Parse(startxref.Groups[1].Value);
        if (!text.AsSpan(xref).StartsWith("xref", StringComparison.Ordinal))
        {
            AssertXrefStreamValid(pdf, text, xref);
            return;
        }
        var header = Regex.Match(text.Substring(xref), @"^xref\n0 (\d+)\n");
        int count = int.Parse(header.Groups[1].Value);
        int pos = xref + header.Length + 20; // skip the free entry
        for (int obj = 1; obj < count; obj++, pos += 20)
        {
            int offset = int.Parse(text.Substring(pos, 10));
            Assert.StartsWith(obj + " 0 obj", text.Substring(offset, 20));
        }
    }

    /// <summary>
    /// Checks a cross-reference stream: every object in use starts at its offset, and every object in an object stream
    /// is listed, at its index, in the header of an object stream that the cross-reference stream lists itself.
    /// </summary>
    private static void AssertXrefStreamValid(byte[] pdf, string text, int xref)
    {
        var dict = Regex.Match(text.Substring(xref), @"^(\d+) 0 obj\n<<(.*?)>>stream\n", RegexOptions.Singleline);
        Assert.True(dict.Success, "startxref points at neither a table nor a stream");
        string entries = dict.Groups[2].Value;
        Assert.Contains("/Type/XRef", entries);
        int size = int.Parse(Regex.Match(entries, @"/Size (\d+)").Groups[1].Value);
        var w = Regex.Match(entries, @"/W\[(\d+) (\d+) (\d+)\]");
        int[] widths = [int.Parse(w.Groups[1].Value), int.Parse(w.Groups[2].Value), int.Parse(w.Groups[3].Value)];
        int columns = int.Parse(Regex.Match(entries, @"/Columns (\d+)").Groups[1].Value);
        Assert.Equal(widths.Sum(), columns);
        int length = int.Parse(Regex.Match(entries, @"/Length (\d+)").Groups[1].Value);
        var rows = Inflate(pdf.AsSpan(xref + dict.Length, length).ToArray());
        Assert.Equal(size * (columns + 1), rows.Length);
        var previous = new byte[columns];
        var streams = new Dictionary<int, string>();
        var packed = new List<(int Obj, int Stream, int Index)>();
        for (int obj = 0; obj < size; obj++)
        {
            int at = obj * (columns + 1);
            Assert.Equal(2, rows[at]); // the PNG Up predictor
            var row = new byte[columns];
            for (int i = 0; i < columns; i++) row[i] = (byte)(rows[at + 1 + i] + previous[i]);
            previous = row;
            long Field(int start, int width) { long v = 0; for (int i = 0; i < width; i++) v = v << 8 | row[start + i]; return v; }
            long type = Field(0, widths[0]), second = Field(widths[0], widths[1]), third = Field(widths[0] + widths[1], widths[2]);
            if (obj == 0 || type == 0) continue;
            if (type == 1)
            {
                Assert.StartsWith(obj + " 0 obj", text.Substring((int)second, 20));
                var objStm = Regex.Match(text.Substring((int)second), @"^\d+ 0 obj\n<</Type/ObjStm/N (\d+)/First (\d+)/Filter/FlateDecode/Length (\d+)>>stream\n");
                if (objStm.Success)
                {
                    var content = Inflate(pdf.AsSpan((int)second + objStm.Length, int.Parse(objStm.Groups[3].Value)).ToArray());
                    streams[obj] = Encoding.Latin1.GetString(content, 0, int.Parse(objStm.Groups[2].Value));
                }
                continue;
            }
            Assert.Equal(2, type);
            packed.Add((obj, (int)second, (int)third));
        }
        foreach (var (obj, stream, index) in packed)
        {
            Assert.True(streams.TryGetValue(stream, out var header), $"Object {obj} is in {stream}, which is not an object stream");
            var numbers = header.Split(' ', '\n').Where(t => t.Length > 0).ToArray();
            Assert.Equal(obj.ToString(System.Globalization.CultureInfo.InvariantCulture), numbers[index * 2]);
        }
    }

    private static byte[] Inflate(byte[] data)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return ms.ToArray();
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

    /// <summary>A PNG of one RGBA colour.</summary>
    public static byte[] SolidPng(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++) { raw.WriteByte(r); raw.WriteByte(g); raw.WriteByte(b); raw.WriteByte(a); }
        }
        return Png(w, h, 6, raw.ToArray());
    }

    /// <summary>An opaque RGB PNG whose pixels <paramref name="color"/> gives, from x and y.</summary>
    public static byte[] PixelPng(int w, int h, Func<int, int, (byte R, byte G, byte B)> color)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = color(x, y);
                raw.WriteByte(r); raw.WriteByte(g); raw.WriteByte(b);
            }
        }
        return Png(w, h, 2, raw.ToArray());
    }

    /// <summary>A black RGBA PNG whose alpha <paramref name="alpha"/> gives, from x and y.</summary>
    public static byte[] AlphaPng(int w, int h, Func<int, int, byte> alpha)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++) { raw.WriteByte(0); raw.WriteByte(0); raw.WriteByte(0); raw.WriteByte(alpha(x, y)); }
        }
        return Png(w, h, 6, raw.ToArray());
    }

    public static string DataUri(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);

    /// <summary>A PNG of the given colour type and bit depth from its filtered rows.</summary>
    public static byte[] Png(int w, int h, byte colorType, byte[] raw, byte bitDepth = 8)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w);
        WriteBE(ihdr, 4, h);
        ihdr[8] = bitDepth;
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
