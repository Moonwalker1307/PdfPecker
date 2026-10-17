using System.Security.Cryptography;

namespace PdfPecker.Rendering.Parsing;

// The two ciphers behind the handlers, with the leniency that damaged files need.
internal sealed partial class Decryptor
{
    private const int AesBlockBytes = 16;
    private const int Rc4StateBytes = 256;

    /// <summary>
    /// Decrypts AES-CBC data that starts with its initialization vector. Data that is not a whole number of blocks loses
    /// its last partial block, and data that does not end in valid padding is returned with the padding bytes in it.
    /// </summary>
    private static byte[] AesCbcDecrypt(byte[] key, byte[] data)
    {
        ReadOnlySpan<byte> encrypted = data;
        if (data.Length < 2 * AesBlockBytes || data.Length % AesBlockBytes != 0)
        {
            if (data.Length < AesBlockBytes) return Array.Empty<byte>();
            int usable = AesBlockBytes + (data.Length - AesBlockBytes) / AesBlockBytes * AesBlockBytes;
            if (usable <= AesBlockBytes) return Array.Empty<byte>();
            encrypted = encrypted.Slice(0, usable);
        }

        var iv = encrypted.Slice(0, AesBlockBytes);
        var cipherText = encrypted.Slice(AesBlockBytes);
        using var aes = Aes.Create();
        aes.Key = key;
        try
        {
            return aes.DecryptCbc(cipherText, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return aes.DecryptCbc(cipherText, iv, PaddingMode.None);
        }
    }

    public static byte[] Rc4(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        Span<byte> state = stackalloc byte[Rc4StateBytes];
        for (int i = 0; i < Rc4StateBytes; i++) state[i] = (byte)i;
        for (int i = 0, j = 0; i < Rc4StateBytes; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
        }

        var output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 255;
            j = (j + state[i]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
            output[k] = (byte)(data[k] ^ state[(state[i] + state[j]) & 255]);
        }
        return output;
    }
}
