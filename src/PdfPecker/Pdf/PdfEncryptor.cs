using System.Security.Cryptography;
using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// The standard security handler, revision 6 (AES-256, ISO 32000-2 section 7.6.4): derives the encryption dictionary from
/// the passwords and encrypts strings and streams. With AES-256 every object uses the same file key, each string or stream
/// getting its own random initialization vector.
/// </summary>
internal sealed class PdfEncryptor : IPdfObjectEncryption, IDisposable
{
    private readonly byte[] _fileKey = RandomNumberGenerator.GetBytes(32);
    private readonly Aes _aes = Aes.Create();

    public readonly byte[] O, U, OE, UE, Perms;
    public readonly int P;

    public PdfEncryptor(PdfSecurity security)
    {
        _aes.Key = _fileKey;
        var user = PasswordBytes(security.UserPassword);
        var owner = string.IsNullOrEmpty(security.OwnerPassword)
            ? RandomNumberGenerator.GetBytes(32) // nobody can lift the permissions
            : PasswordBytes(security.OwnerPassword);

        // Algorithms 8 and 9: U, UE, O and OE. The revision 6 hash is deliberately slow (about 1.3 ms each), so the
        // independent pairs run in parallel: the owner hashes need the finished U.
        var salts = RandomNumberGenerator.GetBytes(32); // user validation, user key, owner validation, owner key salts
        byte[] uHash = null!, ueKey = null!, oHash = null!, oeKey = null!;
        Parallel.Invoke(
            () => uHash = Decryptor.Hash(6, user, salts.AsSpan(0, 8), ReadOnlySpan<byte>.Empty),
            () => ueKey = Decryptor.Hash(6, user, salts.AsSpan(8, 8), ReadOnlySpan<byte>.Empty));
        U = new byte[48];
        uHash.CopyTo(U, 0);
        salts.AsSpan(0, 16).CopyTo(U.AsSpan(32));
        UE = EncryptKey(ueKey);
        var u = U;
        Parallel.Invoke(
            () => oHash = Decryptor.Hash(6, owner, salts.AsSpan(16, 8), u),
            () => oeKey = Decryptor.Hash(6, owner, salts.AsSpan(24, 8), u));
        O = new byte[48];
        oHash.CopyTo(O, 0);
        salts.AsSpan(16, 16).CopyTo(O.AsSpan(32));
        OE = EncryptKey(oeKey);

        // Algorithm 10: Perms. Bits 1-2 must be 0, bits 7-8 and 13-32 must be 1.
        P = (int)((uint)(security.Permissions & PdfPermissions.All) | 0xFFFFF0C0u);
        var perms = new byte[16];
        BitConverter.TryWriteBytes(perms.AsSpan(0, 4), P); // little-endian on every platform .NET runs on
        perms[4] = perms[5] = perms[6] = perms[7] = 0xFF;
        perms[8] = (byte)'T'; // EncryptMetadata
        perms[9] = (byte)'a';
        perms[10] = (byte)'d';
        perms[11] = (byte)'b';
        RandomNumberGenerator.Fill(perms.AsSpan(12));
        Perms = _aes.EncryptEcb(perms, PaddingMode.None);
    }

    /// <summary>Passwords are UTF-8 after normalization (an approximation of SASLprep), at most 127 bytes.</summary>
    private static byte[] PasswordBytes(string? password)
    {
        if (string.IsNullOrEmpty(password)) return Array.Empty<byte>();
        var b = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        return b.Length > 127 ? b.AsSpan(0, 127).ToArray() : b;
    }

    /// <summary>The file key encrypted with an intermediate key: AES-256, no padding, zero IV.</summary>
    private byte[] EncryptKey(byte[] intermediateKey)
    {
        using var aes = Aes.Create();
        aes.Key = intermediateKey;
        return aes.EncryptCbc(_fileKey, new byte[16], PaddingMode.None);
    }

    /// <summary>AES-256-CBC with PKCS#7 padding; the random IV is prepended.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> data)
    {
        int padded = (data.Length / 16 + 1) * 16;
        var result = new byte[16 + padded];
        RandomNumberGenerator.Fill(result.AsSpan(0, 16));
        _aes.EncryptCbc(data, result.AsSpan(0, 16), result.AsSpan(16), PaddingMode.PKCS7);
        return result;
    }

    /// <summary>Writes the encryption dictionary (its strings are never encrypted).</summary>
    public void WriteDictionary(ByteBuffer b)
    {
        b.Ascii("<< /Filter /Standard /V 5 /R 6 /Length 256 /CF << /StdCF << /Type /CryptFilter /AuthEvent /DocOpen /CFM /AESV3 /Length 32 >> >>")
            .Ascii(" /StmF /StdCF /StrF /StdCF /P ").Int(P).Ascii(" /EncryptMetadata true");
        PdfSyntax.Hex(b.Ascii(" /O "), O, upper: true);
        PdfSyntax.Hex(b.Ascii(" /U "), U, upper: true);
        PdfSyntax.Hex(b.Ascii(" /OE "), OE, upper: true);
        PdfSyntax.Hex(b.Ascii(" /UE "), UE, upper: true);
        PdfSyntax.Hex(b.Ascii(" /Perms "), Perms, upper: true);
        b.Ascii(" >>");
    }

    // AES-256 uses the file key for every object, so the object number and generation play no part.
    byte[] IPdfObjectEncryption.EncryptString(byte[] data, int num, int gen) => Encrypt(data);
    byte[] IPdfObjectEncryption.EncryptStream(byte[] data, int num, int gen) => Encrypt(data);

    public void Dispose() => _aes.Dispose();
}
