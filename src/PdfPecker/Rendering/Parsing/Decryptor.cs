using System.Security.Cryptography;
using System.Text;

namespace PdfPecker.Rendering.Parsing;

/// <summary>The PDF standard security handler (RC4 40-128 bit, AES-128, AES-256; revisions 2 to 6).</summary>
internal sealed class Decryptor
{
    private enum Method { None, Rc4, Aes128, Aes256 }

    private static readonly byte[] Padding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private readonly byte[] _key;
    private readonly Method _streamMethod, _stringMethod;

    private Decryptor(byte[] key, Method stm, Method str)
    {
        _key = key;
        _streamMethod = stm;
        _stringMethod = str;
    }

    public static Decryptor Create(PdfFile file, PdfDict encrypt, byte[] id0, string? password)
    {
        var filter = file.GetName(encrypt, "Filter");
        if (filter != null && filter != "Standard") throw new NotSupportedException("Unsupported security handler " + filter + ".");
        int v = file.GetInt(encrypt, "V", 0);
        int r = file.GetInt(encrypt, "R", 2);
        byte[] o = file.GetBytes(encrypt, "O") ?? Array.Empty<byte>();
        byte[] u = file.GetBytes(encrypt, "U") ?? Array.Empty<byte>();
        int p = file.GetInt(encrypt, "P", 0);
        int lengthBits = file.GetInt(encrypt, "Length", 40);
        bool encryptMetadata = (file.Resolve(encrypt["EncryptMetadata"]) as PdfBool)?.Value ?? true;

        Method stm = Method.Rc4, str = Method.Rc4;
        if (v >= 4)
        {
            var cf = file.Resolve(encrypt["CF"]) as PdfDict;
            Method Lookup(string? name)
            {
                if (name == null || name == "Identity") return Method.None;
                var f = cf == null ? null : file.Resolve(cf[name]) as PdfDict;
                var cfm = f == null ? null : file.GetName(f, "CFM");
                if (f != null && file.GetInt(f, "Length", 0) is int l && l > 0) lengthBits = l <= 32 ? l * 8 : l;
                return cfm switch { "AESV2" => Method.Aes128, "AESV3" => Method.Aes256, "None" => Method.None, _ => Method.Rc4 };
            }
            stm = Lookup(file.GetName(encrypt, "StmF"));
            str = Lookup(file.GetName(encrypt, "StrF"));
        }

        byte[]? key;
        if (r >= 5)
        {
            key = Aes256Key(file, encrypt, r, o, u, password ?? "");
            if (stm != Method.None) stm = Method.Aes256;
            if (str != Method.None) str = Method.Aes256;
        }
        else
        {
            int n = r == 2 ? 5 : Math.Clamp(lengthBits / 8, 5, 16);
            var pw = PasswordBytes(password ?? "");
            key = LegacyKey(pw, o, p, id0, r, n, encryptMetadata);
            if (!CheckUser(key, u, id0, r))
            {
                // Try the password as the owner password.
                var userPw = OwnerToUser(pw, o, r, n);
                key = LegacyKey(userPw, o, p, id0, r, n, encryptMetadata);
                if (!CheckUser(key, u, id0, r)) key = null;
            }
        }
        if (key == null)
            throw new PdfPasswordException(string.IsNullOrEmpty(password) ? "The PDF is password protected." : "The password is incorrect.");
        return new Decryptor(key, stm, str);
    }

    public byte[] DecryptStream(byte[] data, int num, int gen) => Decrypt(data, num, gen, _streamMethod);

    public byte[] DecryptString(byte[] data, int num, int gen) => Decrypt(data, num, gen, _stringMethod);

    private byte[] Decrypt(byte[] data, int num, int gen, Method m)
    {
        if (m == Method.None || data.Length == 0) return data;
        if (m == Method.Aes256) return AesCbc(_key, data);
        var objKey = ObjectKey(num, gen, m);
        return m == Method.Aes128 ? AesCbc(objKey, data) : Rc4(objKey, data);
    }

    /// <summary>Encrypts a string of a new object written in an incremental update to this file.</summary>
    public byte[] EncryptString(byte[] data, int num, int gen) => Encrypt(data, num, gen, _stringMethod);

    /// <summary>Encrypts the data of a new stream written in an incremental update to this file.</summary>
    public byte[] EncryptStream(byte[] data, int num, int gen) => Encrypt(data, num, gen, _streamMethod);

    private byte[] Encrypt(byte[] data, int num, int gen, Method m)
    {
        if (m == Method.None) return data;
        if (m == Method.Rc4) return Rc4(ObjectKey(num, gen, m), data);
        using var aes = Aes.Create();
        aes.Key = m == Method.Aes256 ? _key : ObjectKey(num, gen, m);
        var iv = RandomNumberGenerator.GetBytes(16);
        var cipher = aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
        var result = new byte[16 + cipher.Length];
        iv.CopyTo(result, 0);
        cipher.CopyTo(result, 16);
        return result;
    }

    private byte[] ObjectKey(int num, int gen, Method m)
    {
        var input = new byte[_key.Length + 5 + (m == Method.Aes128 ? 4 : 0)];
        _key.CopyTo(input, 0);
        int k = _key.Length;
        input[k] = (byte)num;
        input[k + 1] = (byte)(num >> 8);
        input[k + 2] = (byte)(num >> 16);
        input[k + 3] = (byte)gen;
        input[k + 4] = (byte)(gen >> 8);
        if (m == Method.Aes128) "sAlT"u8.CopyTo(input.AsSpan(k + 5));
        var hash = MD5.HashData(input);
        return hash.AsSpan(0, Math.Min(_key.Length + 5, 16)).ToArray();
    }

    // ------------------------------------------------------------------ revisions 2-4

    private static byte[] PasswordBytes(string password)
    {
        var b = Encoding.Latin1.GetBytes(password);
        var padded = new byte[32];
        int n = Math.Min(32, b.Length);
        Array.Copy(b, padded, n);
        Array.Copy(Padding, 0, padded, n, 32 - n);
        return padded;
    }

    private static byte[] LegacyKey(byte[] paddedPw, byte[] o, int p, byte[] id0, int r, int n, bool encryptMetadata)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(paddedPw);
        md5.AppendData(o.AsSpan(0, Math.Min(32, o.Length)));
        md5.AppendData(new[] { (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24) });
        md5.AppendData(id0);
        if (r >= 4 && !encryptMetadata) md5.AppendData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        var hash = md5.GetHashAndReset();
        if (r >= 3)
            for (int i = 0; i < 50; i++) hash = MD5.HashData(hash.AsSpan(0, n));
        return hash.AsSpan(0, n).ToArray();
    }

    private static bool CheckUser(byte[] key, byte[] u, byte[] id0, int r)
    {
        if (r == 2)
        {
            var enc = Rc4(key, Padding);
            return u.Length >= 32 && enc.AsSpan().SequenceEqual(u.AsSpan(0, 32));
        }
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(Padding);
        md5.AppendData(id0);
        var x = Rc4(key, md5.GetHashAndReset());
        var k2 = new byte[key.Length];
        for (int i = 1; i <= 19; i++)
        {
            for (int j = 0; j < key.Length; j++) k2[j] = (byte)(key[j] ^ i);
            x = Rc4(k2, x);
        }
        return u.Length >= 16 && x.AsSpan(0, 16).SequenceEqual(u.AsSpan(0, 16));
    }

    private static byte[] OwnerToUser(byte[] paddedOwnerPw, byte[] o, int r, int n)
    {
        var hash = MD5.HashData(paddedOwnerPw);
        if (r >= 3) for (int i = 0; i < 50; i++) hash = MD5.HashData(hash);
        var key = hash.AsSpan(0, n).ToArray();
        var data = o.AsSpan(0, Math.Min(32, o.Length)).ToArray();
        if (r == 2) return Rc4(key, data);
        var k2 = new byte[n];
        for (int i = 19; i >= 0; i--)
        {
            for (int j = 0; j < n; j++) k2[j] = (byte)(key[j] ^ i);
            data = Rc4(k2, data);
        }
        return data;
    }

    // ------------------------------------------------------------------ revisions 5-6 (AES-256)

    private static byte[]? Aes256Key(PdfFile file, PdfDict encrypt, int r, byte[] o, byte[] u, string password)
    {
        if (o.Length < 48 || u.Length < 48) return null;
        var pw = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        if (pw.Length > 127) pw = pw.AsSpan(0, 127).ToArray();
        var ue = file.GetBytes(encrypt, "UE");
        var oe = file.GetBytes(encrypt, "OE");
        var u48 = u.AsSpan(0, 48).ToArray();

        // User password.
        var h = Hash(r, pw, u.AsSpan(32, 8), ReadOnlySpan<byte>.Empty);
        if (h.AsSpan(0, 32).SequenceEqual(u.AsSpan(0, 32)) && ue != null && ue.Length >= 32)
        {
            var ik = Hash(r, pw, u.AsSpan(40, 8), ReadOnlySpan<byte>.Empty);
            return AesNoPad(ik, ue.AsSpan(0, 32).ToArray());
        }
        // Owner password.
        h = Hash(r, pw, o.AsSpan(32, 8), u48);
        if (h.AsSpan(0, 32).SequenceEqual(o.AsSpan(0, 32)) && oe != null && oe.Length >= 32)
        {
            var ik = Hash(r, pw, o.AsSpan(40, 8), u48);
            return AesNoPad(ik, oe.AsSpan(0, 32).ToArray());
        }
        return null;
    }

    /// <summary>The revision 5 and 6 password hash (ISO 32000-2, algorithm 2.B). Also used by the writer.</summary>
    internal static byte[] Hash(int r, byte[] pw, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> udata)
    {
        var input = new byte[pw.Length + salt.Length + udata.Length];
        pw.CopyTo(input, 0);
        salt.CopyTo(input.AsSpan(pw.Length));
        udata.CopyTo(input.AsSpan(pw.Length + salt.Length));
        var first = SHA256.HashData(input);
        if (r == 5) return first;

        // K is 32, 48 or 64 bytes; the buffers are sized for the largest round and reused.
        Span<byte> k = stackalloc byte[64];
        first.CopyTo(k);
        int kLen = 32;
        var k1 = new byte[64 * (pw.Length + 64 + udata.Length)];
        var e = new byte[k1.Length];
        var key = new byte[16];
        using var aes = Aes.Create();
        for (int round = 0; ; round++)
        {
            int blockLen = pw.Length + kLen + udata.Length, total = blockLen * 64;
            for (int i = 0; i < 64; i++)
            {
                var block = k1.AsSpan(i * blockLen, blockLen);
                pw.CopyTo(block);
                k.Slice(0, kLen).CopyTo(block.Slice(pw.Length));
                udata.CopyTo(block.Slice(pw.Length + kLen));
            }
            k.Slice(0, 16).CopyTo(key);
            aes.Key = key;
            var es = e.AsSpan(0, total);
            aes.EncryptCbc(k1.AsSpan(0, total), k.Slice(16, 16), es, PaddingMode.None);
            int mod = 0;
            for (int i = 0; i < 16; i++) mod += es[i];
            switch (mod % 3)
            {
                case 0: kLen = SHA256.HashData(es, k); break;
                case 1: kLen = SHA384.HashData(es, k); break;
                default: kLen = SHA512.HashData(es, k); break;
            }
            if (round >= 63 && es[total - 1] <= round - 31) break;
            if (round > 1000) break;
        }
        return k.Slice(0, 32).ToArray();
    }

    private static byte[] AesNoPad(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data, new byte[16], PaddingMode.None);
    }

    // ------------------------------------------------------------------ primitives

    private static byte[] AesCbc(byte[] key, byte[] data)
    {
        if (data.Length < 32 || data.Length % 16 != 0)
        {
            if (data.Length < 16) return Array.Empty<byte>();
            int usable = 16 + (data.Length - 16) / 16 * 16;
            if (usable <= 16) return Array.Empty<byte>();
            data = data.AsSpan(0, usable).ToArray();
        }
        using var aes = Aes.Create();
        aes.Key = key;
        try
        {
            return aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.None);
        }
    }

    public static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 255;
            j = (j + s[i]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 255]);
        }
        return output;
    }
}
