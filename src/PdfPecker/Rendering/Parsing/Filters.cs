using System.IO.Compression;

namespace PdfPecker.Rendering.Parsing;

/// <summary>Stream decoders for the general-purpose PDF filters.</summary>
internal static class Filters
{
    /// <summary>Upper bound on the decoded size of one stream, against decompression bombs.</summary>
    public const int MaxDecodedSize = 512 * 1024 * 1024;

    public static string Canonical(string name) => name switch
    {
        "Fl" => "FlateDecode",
        "AHx" => "ASCIIHexDecode",
        "A85" => "ASCII85Decode",
        "LZW" => "LZWDecode",
        "RL" => "RunLengthDecode",
        "CCF" => "CCITTFaxDecode",
        "DCT" => "DCTDecode",
        _ => name,
    };

    public static bool IsImageFilter(string name) => name is "DCTDecode" or "JPXDecode" or "CCITTFaxDecode" or "JBIG2Decode";

    public static byte[] Decode(string filter, byte[] data, PdfDict? parms, PdfFile? file)
    {
        switch (filter)
        {
            case "FlateDecode":
                return ApplyPredictor(Inflate(data), parms, file);
            case "LZWDecode":
            {
                int early = file?.GetInt(parms, "EarlyChange", 1) ?? 1;
                return ApplyPredictor(Lzw(data, early != 0), parms, file);
            }
            case "ASCIIHexDecode": return AsciiHex(data);
            case "ASCII85Decode": return Ascii85(data);
            case "RunLengthDecode": return RunLength(data);
            case "Crypt": return data; // identity crypt filter; real decryption happens on load
            default: throw new NotSupportedException("Unsupported filter " + filter);
        }
    }

    // ------------------------------------------------------------------ Flate

    public static byte[] Inflate(byte[] data)
    {
        if (data.Length == 0) return data;
        bool zlibHeader = data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0;
        var result = InflateCore(data, zlibHeader ? 2 : 0, out bool failedEarly);
        if (failedEarly && zlibHeader == false && data.Length > 2)
        {
            // Some writers emit a bogus header: try raw deflate after skipping it.
            var alt = InflateCore(data, 2, out _);
            if (alt.Length > result.Length) return alt;
        }
        return result;
    }

    private static byte[] InflateCore(byte[] data, int offset, out bool failedEarly)
    {
        failedEarly = false;
        using var input = new MemoryStream(data, offset, data.Length - offset, writable: false);
        using var z = new DeflateStream(input, CompressionMode.Decompress);
        var output = new MemoryStream(Math.Min(MaxDecodedSize, Math.Max(256, data.Length * 4)));
        var buf = new byte[16384];
        try
        {
            int n;
            while ((n = z.Read(buf, 0, buf.Length)) > 0)
            {
                output.Write(buf, 0, n);
                if (output.Length > MaxDecodedSize) break;
            }
        }
        catch (InvalidDataException)
        {
            // Corrupt or truncated data: keep what was decoded.
            if (output.Length == 0) failedEarly = true;
        }
        return output.ToArray();
    }

    // ------------------------------------------------------------------ predictors

    private static byte[] ApplyPredictor(byte[] data, PdfDict? parms, PdfFile? file)
    {
        if (parms == null || file == null) return data;
        int predictor = file.GetInt(parms, "Predictor", 1);
        if (predictor <= 1) return data;
        int colors = Math.Clamp(file.GetInt(parms, "Colors", 1), 1, 32);
        int bpc = file.GetInt(parms, "BitsPerComponent", 8);
        if (bpc is not (1 or 2 or 4 or 8 or 16)) bpc = 8;
        int columns = Math.Max(1, file.GetInt(parms, "Columns", 1));
        long rowBitsL = (long)colors * bpc * columns;
        if (rowBitsL > int.MaxValue / 2) return data;
        int rowBytes = (int)((rowBitsL + 7) / 8);
        int bpp = Math.Max(1, colors * bpc / 8);

        if (predictor == 2)
        {
            var outT = (byte[])data.Clone();
            if (bpc == 8)
            {
                for (int row = 0; row + rowBytes <= outT.Length; row += rowBytes)
                    for (int i = bpp; i < rowBytes; i++) outT[row + i] = (byte)(outT[row + i] + outT[row + i - bpp]);
            }
            else if (bpc == 16)
            {
                for (int row = 0; row + rowBytes <= outT.Length; row += rowBytes)
                    for (int i = 2 * colors; i + 1 < rowBytes; i += 2)
                    {
                        int v = ((outT[row + i] << 8) | outT[row + i + 1]) + ((outT[row + i - 2 * colors] << 8) | outT[row + i + 1 - 2 * colors]);
                        outT[row + i] = (byte)(v >> 8);
                        outT[row + i + 1] = (byte)v;
                    }
            }
            return outT;
        }

        // PNG predictors: each row starts with a filter-type byte.
        int rows = (data.Length + rowBytes) / (rowBytes + 1);
        var output = new byte[rows * rowBytes];
        var prev = new byte[rowBytes];
        int src = 0, dst = 0;
        for (int r = 0; r < rows && src < data.Length; r++)
        {
            int type = data[src++];
            int n = Math.Min(rowBytes, data.Length - src);
            var cur = output.AsSpan(dst, rowBytes);
            data.AsSpan(src, n).CopyTo(cur);
            src += n;
            switch (type)
            {
                case 1:
                    for (int i = bpp; i < rowBytes; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                    break;
                case 2:
                    for (int i = 0; i < rowBytes; i++) cur[i] = (byte)(cur[i] + prev[i]);
                    break;
                case 3:
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int left = i >= bpp ? cur[i - bpp] : 0;
                        cur[i] = (byte)(cur[i] + ((left + prev[i]) >> 1));
                    }
                    break;
                case 4:
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        cur[i] = (byte)(cur[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                    }
                    break;
            }
            cur.CopyTo(prev);
            dst += rowBytes;
        }
        return dst == output.Length ? output : output.AsSpan(0, dst).ToArray();
    }

    // ------------------------------------------------------------------ LZW

    private static byte[] Lzw(byte[] data, bool earlyChange)
    {
        var output = new MemoryStream(data.Length * 3);
        var prefix = new int[4096];
        var suffix = new byte[4096];
        var lengths = new int[4096];
        var stack = new byte[4097];
        for (int i = 0; i < 256; i++) { prefix[i] = -1; suffix[i] = (byte)i; lengths[i] = 1; }
        int next = 258, codeLen = 9, prevCode = -1;
        int bitBuf = 0, bitCount = 0, pos = 0;
        while (true)
        {
            while (bitCount < codeLen && pos < data.Length) { bitBuf = (bitBuf << 8) | data[pos++]; bitCount += 8; }
            if (bitCount < codeLen) break;
            int code = (bitBuf >> (bitCount - codeLen)) & ((1 << codeLen) - 1);
            bitCount -= codeLen;
            if (code == 256) { next = 258; codeLen = 9; prevCode = -1; continue; }
            if (code == 257) break;
            int entry;
            if (prevCode < 0)
            {
                if (code > 255) break;
                output.WriteByte((byte)code);
                prevCode = code;
                continue;
            }
            byte first;
            if (code < next)
            {
                entry = code;
                int len = WriteEntry(entry, prefix, suffix, stack, output);
                first = stack[stack.Length - len];
            }
            else
            {
                // KwKwK case.
                int len = WriteEntry(prevCode, prefix, suffix, stack, output);
                first = stack[stack.Length - len];
                output.WriteByte(first);
            }
            if (next < 4096)
            {
                prefix[next] = prevCode;
                suffix[next] = first;
                lengths[next] = lengths[prevCode] + 1;
                next++;
            }
            int limit = next + (earlyChange ? 1 : 0);
            if (limit >= 512 && codeLen == 9) codeLen = 10;
            else if (limit >= 1024 && codeLen == 10) codeLen = 11;
            else if (limit >= 2048 && codeLen == 11) codeLen = 12;
            prevCode = code;
            if (output.Length > MaxDecodedSize) break;
        }
        return output.ToArray();
    }

    private static int WriteEntry(int code, int[] prefix, byte[] suffix, byte[] stack, MemoryStream output)
    {
        int sp = stack.Length;
        int guard = 0;
        while (code >= 0 && guard++ < 4097)
        {
            stack[--sp] = suffix[code];
            code = prefix[code];
        }
        int len = stack.Length - sp;
        output.Write(stack, sp, len);
        return len;
    }

    // ------------------------------------------------------------------ ASCII filters, run length

    private static byte[] AsciiHex(byte[] data)
    {
        var output = new byte[data.Length / 2 + 1];
        int n = 0, hi = -1;
        foreach (byte c in data)
        {
            if (c == '>') break;
            int v = PdfLexer.HexVal(c);
            if (v < 0) continue;
            if (hi < 0) hi = v;
            else { output[n++] = (byte)(hi << 4 | v); hi = -1; }
        }
        if (hi >= 0) output[n++] = (byte)(hi << 4);
        return output.AsSpan(0, n).ToArray();
    }

    private static byte[] Ascii85(byte[] data)
    {
        var output = new MemoryStream(data.Length);
        int count = 0;
        uint tuple = 0;
        int i = 0;
        if (data.Length >= 2 && data[0] == '<' && data[1] == '~') i = 2;
        for (; i < data.Length; i++)
        {
            byte c = data[i];
            if (c == '~') break;
            if (c == 'z' && count == 0)
            {
                output.Write(new byte[4]);
                continue;
            }
            if (c < '!' || c > 'u') continue;
            tuple = tuple * 85 + (uint)(c - '!');
            if (++count == 5)
            {
                output.WriteByte((byte)(tuple >> 24));
                output.WriteByte((byte)(tuple >> 16));
                output.WriteByte((byte)(tuple >> 8));
                output.WriteByte((byte)tuple);
                count = 0;
                tuple = 0;
            }
        }
        if (count > 1)
        {
            for (int k = count; k < 5; k++) tuple = tuple * 85 + 84;
            for (int k = 0; k < count - 1; k++) output.WriteByte((byte)(tuple >> (24 - 8 * k)));
        }
        return output.ToArray();
    }

    private static byte[] RunLength(byte[] data)
    {
        var output = new MemoryStream(data.Length * 2);
        int i = 0;
        while (i < data.Length)
        {
            int len = data[i++];
            if (len == 128) break;
            if (len < 128)
            {
                int n = Math.Min(len + 1, data.Length - i);
                output.Write(data, i, n);
                i += n;
            }
            else if (i < data.Length)
            {
                byte b = data[i++];
                for (int k = 0; k < 257 - len; k++) output.WriteByte(b);
            }
        }
        return output.ToArray();
    }
}
