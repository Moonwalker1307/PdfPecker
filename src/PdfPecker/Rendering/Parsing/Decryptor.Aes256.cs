using System.Security.Cryptography;
using System.Text;

namespace PdfPecker.Rendering.Parsing;

// The file key of the AES-256 handler, revisions 5 and 6 (ISO 32000-2, algorithms 2.A and 2.B).
internal sealed partial class Decryptor
{
    // The /O and /U entries are a 32 byte hash, an 8 byte validation salt and an 8 byte key salt.
    private const int PasswordEntryBytes = 48;
    private const int HashBytes = 32;
    private const int SaltBytes = 8;
    private const int ValidationSaltOffset = HashBytes;
    private const int KeySaltOffset = HashBytes + SaltBytes;
    private const int MaxPasswordBytes = 127;
    private const int FileKeyBytes = 32;

    private const int Revision5 = 5;

    // Revision 6 hashes in rounds of 64 repetitions, at least 64 rounds, and goes on until the last byte of a round is small.
    private const int Repetitions = 64;
    private const int RoundsBeforeLastByteMatters = 63;
    private const int LastByteOffset = 31;
    private const int MaxRound = 1000;

    private static byte[]? Aes256FileKey(PdfFile file, PdfDict encrypt, int revision, byte[] owner, byte[] user, string password)
    {
        if (owner.Length < PasswordEntryBytes || user.Length < PasswordEntryBytes) return null;
        var passwordBytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        if (passwordBytes.Length > MaxPasswordBytes) passwordBytes = passwordBytes.AsSpan(0, MaxPasswordBytes).ToArray();
        var encryptedUserKey = file.GetBytes(encrypt, "UE");
        var encryptedOwnerKey = file.GetBytes(encrypt, "OE");
        var userEntry = user.AsSpan(0, PasswordEntryBytes);

        // User password.
        var hash = Hash(revision, passwordBytes, user.AsSpan(ValidationSaltOffset, SaltBytes), ReadOnlySpan<byte>.Empty);
        if (hash.AsSpan(0, HashBytes).SequenceEqual(user.AsSpan(0, HashBytes)) && encryptedUserKey is { Length: >= FileKeyBytes })
        {
            var intermediateKey = Hash(revision, passwordBytes, user.AsSpan(KeySaltOffset, SaltBytes), ReadOnlySpan<byte>.Empty);
            return DecryptFileKey(intermediateKey, encryptedUserKey);
        }
        // Owner password.
        hash = Hash(revision, passwordBytes, owner.AsSpan(ValidationSaltOffset, SaltBytes), userEntry);
        if (hash.AsSpan(0, HashBytes).SequenceEqual(owner.AsSpan(0, HashBytes)) && encryptedOwnerKey is { Length: >= FileKeyBytes })
        {
            var intermediateKey = Hash(revision, passwordBytes, owner.AsSpan(KeySaltOffset, SaltBytes), userEntry);
            return DecryptFileKey(intermediateKey, encryptedOwnerKey);
        }
        return null;
    }

    /// <summary>Decrypts the /UE or /OE entry with the intermediate key, which gives the file key.</summary>
    private static byte[] DecryptFileKey(byte[] intermediateKey, byte[] encryptedKey)
    {
        using var aes = Aes.Create();
        aes.Key = intermediateKey;
        return aes.DecryptCbc(encryptedKey.AsSpan(0, FileKeyBytes), new byte[AesBlockBytes], PaddingMode.None);
    }

    /// <summary>The revision 5 and 6 password hash (ISO 32000-2, algorithm 2.B). Also used by the writer.</summary>
    internal static byte[] Hash(int revision, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> userData)
    {
        var input = new byte[password.Length + salt.Length + userData.Length];
        password.CopyTo(input);
        salt.CopyTo(input.AsSpan(password.Length));
        userData.CopyTo(input.AsSpan(password.Length + salt.Length));
        var first = SHA256.HashData(input);
        return revision == Revision5 ? first : HashRevision6(password, userData, first);
    }

    private static byte[] HashRevision6(ReadOnlySpan<byte> password, ReadOnlySpan<byte> userData, byte[] first)
    {
        // K is 32, 48 or 64 bytes; the buffers are sized for the largest round and reused.
        Span<byte> k = stackalloc byte[SHA512.HashSizeInBytes];
        first.CopyTo(k);
        int kLength = HashBytes;
        var repeated = new byte[Repetitions * (password.Length + SHA512.HashSizeInBytes + userData.Length)];
        var encrypted = new byte[repeated.Length];
        var aesKey = new byte[AesBlockBytes];
        using var aes = Aes.Create();
        for (int round = 0; ; round++)
        {
            int blockLength = password.Length + kLength + userData.Length;
            int total = blockLength * Repetitions;
            for (int i = 0; i < Repetitions; i++)
            {
                var block = repeated.AsSpan(i * blockLength, blockLength);
                password.CopyTo(block);
                k.Slice(0, kLength).CopyTo(block.Slice(password.Length));
                userData.CopyTo(block.Slice(password.Length + kLength));
            }
            // The first 16 bytes of K are the AES key and the next 16 the initialization vector.
            k.Slice(0, AesBlockBytes).CopyTo(aesKey);
            aes.Key = aesKey;
            var roundOutput = encrypted.AsSpan(0, total);
            aes.EncryptCbc(repeated.AsSpan(0, total), k.Slice(AesBlockBytes, AesBlockBytes), roundOutput, PaddingMode.None);
            kLength = HashSelectedByRound(roundOutput, k);
            if (round >= RoundsBeforeLastByteMatters && roundOutput[total - 1] <= round - LastByteOffset) break;
            if (round > MaxRound) break;
        }
        return k.Slice(0, FileKeyBytes).ToArray();
    }

    /// <summary>Hashes a round's output with SHA-256, SHA-384 or SHA-512, whichever its first 16 bytes' sum modulo 3 selects.</summary>
    private static int HashSelectedByRound(ReadOnlySpan<byte> roundOutput, Span<byte> destination)
    {
        int sum = 0;
        for (int i = 0; i < AesBlockBytes; i++) sum += roundOutput[i];
        return (sum % 3) switch
        {
            0 => SHA256.HashData(roundOutput, destination),
            1 => SHA384.HashData(roundOutput, destination),
            _ => SHA512.HashData(roundOutput, destination),
        };
    }
}
