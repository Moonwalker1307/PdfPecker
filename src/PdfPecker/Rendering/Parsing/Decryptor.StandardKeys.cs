using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PdfPecker.Rendering.Parsing;

// The file key of the RC4 and AES-128 handlers, revisions 2 to 4 (ISO 32000-1, algorithms 2 to 7).
internal sealed partial class Decryptor
{
    private const int PaddedPasswordBytes = 32;

    // The user and owner password entries are 32 bytes (revision 2) or 16 bytes (revisions 3 and 4) long to compare.
    private const int UserCheckBytesRevision2 = 32;
    private const int UserCheckBytesRevision3 = 16;

    // Revisions 3 and up rehash the key 50 times and run the user check through 19 more RC4 passes.
    private const int KeyRehashRounds = 50;
    private const int UserCheckRc4Passes = 19;

    // The fixed string that fills a password out to 32 bytes.
    private static ReadOnlySpan<byte> PasswordPadding =>
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    /// <summary>
    /// Derives the file key from the password as the user password and, failing that, as the owner password. Returns
    /// null when it is neither.
    /// </summary>
    private static byte[]? StandardFileKey(string password, byte[] owner, byte[] user, int permissions, byte[] id0, int revision, int keyBytes, bool encryptMetadata)
    {
        var paddedPassword = PadPassword(password);
        var fileKey = LegacyKey(paddedPassword, owner, permissions, id0, revision, keyBytes, encryptMetadata);
        if (IsUserKey(fileKey, user, id0, revision)) return fileKey;

        // Try the password as the owner password.
        var userPassword = OwnerToUserPassword(paddedPassword, owner, revision, keyBytes);
        fileKey = LegacyKey(userPassword, owner, permissions, id0, revision, keyBytes, encryptMetadata);
        return IsUserKey(fileKey, user, id0, revision) ? fileKey : null;
    }

    /// <summary>Cuts a password to 32 bytes, or fills it out with the standard padding (algorithm 2, step a).</summary>
    private static byte[] PadPassword(string password)
    {
        var bytes = Encoding.Latin1.GetBytes(password);
        var padded = new byte[PaddedPasswordBytes];
        int length = Math.Min(PaddedPasswordBytes, bytes.Length);
        bytes.AsSpan(0, length).CopyTo(padded);
        PasswordPadding.Slice(0, PaddedPasswordBytes - length).CopyTo(padded.AsSpan(length));
        return padded;
    }

    private static byte[] LegacyKey(byte[] paddedPassword, byte[] owner, int permissions, byte[] id0, int revision, int keyBytes, bool encryptMetadata)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(paddedPassword);
        md5.AppendData(owner.AsSpan(0, Math.Min(PaddedPasswordBytes, owner.Length)));
        Span<byte> permissionBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(permissionBytes, permissions);
        md5.AppendData(permissionBytes);
        md5.AppendData(id0);
        if (revision >= 4 && !encryptMetadata) md5.AppendData([0xFF, 0xFF, 0xFF, 0xFF]);
        var hash = md5.GetHashAndReset();
        if (revision >= 3)
            for (int i = 0; i < KeyRehashRounds; i++) hash = MD5.HashData(hash.AsSpan(0, keyBytes));
        return hash.AsSpan(0, keyBytes).ToArray();
    }

    /// <summary>Checks a file key against the /U entry (algorithms 4 and 5).</summary>
    private static bool IsUserKey(byte[] fileKey, byte[] user, byte[] id0, int revision)
    {
        if (revision == 2)
        {
            var encryptedPadding = Rc4(fileKey, PasswordPadding);
            return user.Length >= UserCheckBytesRevision2 && encryptedPadding.AsSpan().SequenceEqual(user.AsSpan(0, UserCheckBytesRevision2));
        }

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(PasswordPadding);
        md5.AppendData(id0);
        var check = Rc4(fileKey, md5.GetHashAndReset());
        var passKey = new byte[fileKey.Length];
        for (int pass = 1; pass <= UserCheckRc4Passes; pass++)
        {
            XorEach(fileKey, pass, passKey);
            check = Rc4(passKey, check);
        }
        return user.Length >= UserCheckBytesRevision3 && check.AsSpan(0, UserCheckBytesRevision3).SequenceEqual(user.AsSpan(0, UserCheckBytesRevision3));
    }

    /// <summary>Recovers the user password from the /O entry with the owner password (algorithm 7).</summary>
    private static byte[] OwnerToUserPassword(byte[] paddedOwnerPassword, byte[] owner, int revision, int keyBytes)
    {
        var hash = MD5.HashData(paddedOwnerPassword);
        if (revision >= 3)
            for (int i = 0; i < KeyRehashRounds; i++) hash = MD5.HashData(hash);
        var ownerKey = hash.AsSpan(0, keyBytes).ToArray();
        var data = owner.AsSpan(0, Math.Min(PaddedPasswordBytes, owner.Length)).ToArray();
        if (revision == 2) return Rc4(ownerKey, data);

        var passKey = new byte[keyBytes];
        for (int pass = UserCheckRc4Passes; pass >= 0; pass--)
        {
            XorEach(ownerKey, pass, passKey);
            data = Rc4(passKey, data);
        }
        return data;
    }

    private static void XorEach(byte[] source, int value, byte[] destination)
    {
        for (int i = 0; i < destination.Length; i++) destination[i] = (byte)(source[i] ^ value);
    }
}
