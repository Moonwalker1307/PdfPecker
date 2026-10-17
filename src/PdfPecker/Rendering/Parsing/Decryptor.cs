using System.Security.Cryptography;
using PdfPecker.Pdf;

namespace PdfPecker.Rendering.Parsing;

/// <summary>The PDF standard security handler (RC4 40-128 bit, AES-128, AES-256; revisions 2 to 6).</summary>
internal sealed partial class Decryptor : IPdfObjectEncryption
{
    private enum Method { None, Rc4, Aes128, Aes256 }

    // The first revision that uses the AES-256 scheme, which has no per-object keys and a different password check.
    private const int FirstAes256Revision = 5;

    private const int MinKeyBytes = 5;
    private const int MaxKeyBytes = 16;
    private const int DefaultLengthBits = 40;

    // The object number and generation are appended to the file key, three and two bytes of them, little endian.
    private const int ObjectNumberBytes = 3;
    private const int GenerationBytes = 2;

    private readonly byte[] key;
    private readonly Method streamMethod;
    private readonly Method stringMethod;

    private Decryptor(byte[] key, Method streamMethod, Method stringMethod, bool encryptMetadata)
    {
        this.key = key;
        this.streamMethod = streamMethod;
        this.stringMethod = stringMethod;
        EncryptMetadata = encryptMetadata;
    }

    /// <summary>False when the XMP metadata streams were left unencrypted (/EncryptMetadata false).</summary>
    public bool EncryptMetadata { get; }

    public static Decryptor Create(PdfFile file, PdfDict encrypt, byte[] id0, string? password)
    {
        var filter = file.GetName(encrypt, "Filter");
        if (filter != null && filter != "Standard") throw new NotSupportedException("Unsupported security handler " + filter + ".");
        int version = file.GetInt(encrypt, "V", 0);
        int revision = file.GetInt(encrypt, "R", 2);
        byte[] owner = file.GetBytes(encrypt, "O") ?? Array.Empty<byte>();
        byte[] user = file.GetBytes(encrypt, "U") ?? Array.Empty<byte>();
        // A 32-bit field: some writers give it unsigned (4294963396 for -3900).
        int permissions = (int)(uint)(long)file.Num(encrypt["P"]);
        int lengthBits = file.GetInt(encrypt, "Length", DefaultLengthBits);
        bool encryptMetadata = (file.Resolve(encrypt["EncryptMetadata"]) as PdfBool)?.Value ?? true;

        Method streams = Method.Rc4, strings = Method.Rc4;
        if (version >= 4)
        {
            var filters = file.Resolve(encrypt["CF"]) as PdfDict;
            streams = ReadCryptFilterMethod(file, filters, file.GetName(encrypt, "StmF"), ref lengthBits);
            strings = ReadCryptFilterMethod(file, filters, file.GetName(encrypt, "StrF"), ref lengthBits);
        }

        byte[]? fileKey;
        if (revision >= FirstAes256Revision)
        {
            fileKey = Aes256FileKey(file, encrypt, revision, owner, user, password ?? "");
            if (streams != Method.None) streams = Method.Aes256;
            if (strings != Method.None) strings = Method.Aes256;
        }
        else
        {
            int keyBytes = revision == 2 ? MinKeyBytes : Math.Clamp(lengthBits / 8, MinKeyBytes, MaxKeyBytes);
            fileKey = StandardFileKey(password ?? "", owner, user, permissions, id0, revision, keyBytes, encryptMetadata);
        }
        if (fileKey == null)
            throw new PdfPasswordException(string.IsNullOrEmpty(password) ? "The PDF is password protected." : "The password is incorrect.");
        return new Decryptor(fileKey, streams, strings, encryptMetadata);
    }

    /// <summary>
    /// Looks up how the named crypt filter encrypts. A filter that states its key length also sets the length of the
    /// file key, in bits (some writers give bytes instead).
    /// </summary>
    private static Method ReadCryptFilterMethod(PdfFile file, PdfDict? filters, string? name, ref int lengthBits)
    {
        if (name == null || name == "Identity") return Method.None;
        var filter = filters == null ? null : file.Resolve(filters[name]) as PdfDict;
        string? method = filter == null ? null : file.GetName(filter, "CFM");
        if (filter != null)
        {
            int length = file.GetInt(filter, "Length", 0);
            if (length > 0) lengthBits = length <= 32 ? length * 8 : length;
        }
        return method switch
        {
            "AESV2" => Method.Aes128,
            "AESV3" => Method.Aes256,
            "None" => Method.None,
            _ => Method.Rc4,
        };
    }

    public byte[] DecryptStream(byte[] data, int num, int gen) => Decrypt(data, num, gen, streamMethod);

    public byte[] DecryptString(byte[] data, int num, int gen) => Decrypt(data, num, gen, stringMethod);

    private byte[] Decrypt(byte[] data, int num, int gen, Method method)
    {
        if (method == Method.None || data.Length == 0) return data;
        if (method == Method.Aes256) return AesCbcDecrypt(key, data);
        var objectKey = ObjectKey(num, gen, method);
        return method == Method.Aes128 ? AesCbcDecrypt(objectKey, data) : Rc4(objectKey, data);
    }

    /// <summary>Encrypts a string of a new object written in an incremental update to this file.</summary>
    public byte[] EncryptString(byte[] data, int num, int gen) => Encrypt(data, num, gen, stringMethod);

    /// <summary>Encrypts the data of a new stream written in an incremental update to this file.</summary>
    public byte[] EncryptStream(byte[] data, int num, int gen) => Encrypt(data, num, gen, streamMethod);

    private byte[] Encrypt(byte[] data, int num, int gen, Method method)
    {
        if (method == Method.None) return data;
        if (method == Method.Rc4) return Rc4(ObjectKey(num, gen, method), data);
        using var aes = Aes.Create();
        aes.Key = method == Method.Aes256 ? key : ObjectKey(num, gen, method);
        var iv = RandomNumberGenerator.GetBytes(AesBlockBytes);
        var cipher = aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
        var result = new byte[AesBlockBytes + cipher.Length];
        iv.CopyTo(result, 0);
        cipher.CopyTo(result, AesBlockBytes);
        return result;
    }

    /// <summary>The key of one object: the file key and the object's number and generation, hashed (algorithm 1).</summary>
    private byte[] ObjectKey(int num, int gen, Method method)
    {
        // AES adds a fixed "sAlT" after the object numbers.
        ReadOnlySpan<byte> aesSalt = "sAlT"u8;
        bool salted = method == Method.Aes128;
        Span<byte> input = stackalloc byte[key.Length + ObjectNumberBytes + GenerationBytes + aesSalt.Length];
        key.CopyTo(input);
        var numbers = input.Slice(key.Length);
        numbers[0] = (byte)num;
        numbers[1] = (byte)(num >> 8);
        numbers[2] = (byte)(num >> 16);
        numbers[3] = (byte)gen;
        numbers[4] = (byte)(gen >> 8);
        int length = key.Length + ObjectNumberBytes + GenerationBytes;
        if (salted)
        {
            aesSalt.CopyTo(input.Slice(length));
            length += aesSalt.Length;
        }
        Span<byte> hash = stackalloc byte[MD5.HashSizeInBytes];
        MD5.HashData(input.Slice(0, length), hash);
        return hash.Slice(0, Math.Min(key.Length + ObjectNumberBytes + GenerationBytes, MaxKeyBytes)).ToArray();
    }
}
