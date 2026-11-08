using System.Numerics;

namespace PdfPecker.Barcodes.Reading;

/// <summary>What a decoded code holds.</summary>
internal sealed record DecodedContent(string Text, byte[] Bytes, bool Gs1);

/// <summary>
/// Reads a QR code (ISO/IEC 18004) from its grid of modules: the format and version information, the codewords under
/// the mask, Reed-Solomon correction of each block and the segments of the bit stream. It shares the module layout with
/// <see cref="QrEncoder"/>, so what that writes this reads.
/// </summary>
internal static class QrDecoder
{
    private const string Alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    // The 32 valid 15-bit format words (level and mask with their BCH code, masked with 0x5412), by their 5 data bits.
    private static readonly int[] FormatWords = BuildFormatWords();

    // The 18-bit version words of versions 7 to 40.
    private static readonly int[] VersionWords = BuildVersionWords();

    /// <summary>Decodes a grid of <c>17 + 4v</c> modules, [y, x] and true for dark; also tries it mirrored.</summary>
    public static DecodedContent? Decode(bool[,] grid)
    {
        if (Decode(grid, mirrored: false) is { } content) return content;
        return Decode(grid, mirrored: true);
    }

    private static DecodedContent? Decode(bool[,] grid, bool mirrored)
    {
        int size = grid.GetLength(0);
        if (size != grid.GetLength(1) || size < 21 || size > 177 || (size - 17) % 4 != 0) return null;
        bool Get(int x, int y) => mirrored ? grid[x, y] : grid[y, x];

        // Both copies of the format word, bit 0 first, as QrEncoder.Symbol.DrawFormat places them.
        int first = 0, second = 0;
        for (int i = 0; i <= 5; i++) first |= Bit(Get(8, i), i);
        first |= Bit(Get(8, 7), 6) | Bit(Get(8, 8), 7) | Bit(Get(7, 8), 8);
        for (int i = 9; i < 15; i++) first |= Bit(Get(14 - i, 8), i);
        for (int i = 0; i < 8; i++) second |= Bit(Get(size - 1 - i, 8), i);
        for (int i = 8; i < 15; i++) second |= Bit(Get(8, size - 15 + i), i);
        int format = Closest(FormatWords, first, second, 3);
        if (format < 0) return null;
        int level = (format >> 3) switch { 1 => 0, 0 => 1, 3 => 2, _ => 3 }; // L, M, Q, H
        int mask = format & 7;

        int version = (size - 17) / 4;
        if (version >= 7)
        {
            int topRight = 0, bottomLeft = 0;
            for (int i = 0; i < 18; i++)
            {
                int a = size - 11 + i % 3, b = i / 3;
                topRight |= Bit(Get(a, b), i);
                bottomLeft |= Bit(Get(b, a), i);
            }
            int read = Closest(VersionWords, topRight, bottomLeft, 3);
            // A version word that disagrees with the size means the grid was sampled at the wrong size.
            if (read >= 0 && read + 7 != version) return null;
        }

        var symbol = new QrEncoder.Symbol(version);
        symbol.DrawFunctionPatterns();
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++) symbol.Modules[y, x] = Get(x, y);
        }
        symbol.ApplyMask(mask);
        var raw = symbol.ReadCodewords();
        var data = CorrectBlocks(raw, version, level);
        return data == null ? null : ParseSegments(data, version);
    }

    private static int Bit(bool dark, int index) => dark ? 1 << index : 0;

    /// <summary>The data bits of the word nearest to either reading, if no more than <paramref name="maxDistance"/> bits away.</summary>
    private static int Closest(int[] words, int a, int b, int maxDistance)
    {
        int best = -1, bestDistance = maxDistance + 1;
        for (int i = 0; i < words.Length; i++)
        {
            int distance = Math.Min(BitOperations.PopCount((uint)(words[i] ^ a)), BitOperations.PopCount((uint)(words[i] ^ b)));
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static int[] BuildFormatWords()
    {
        var words = new int[32];
        for (int data = 0; data < 32; data++)
        {
            int remainder = data;
            for (int i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
            words[data] = ((data << 10) | remainder) ^ 0x5412;
        }
        return words;
    }

    private static int[] BuildVersionWords()
    {
        var words = new int[34];
        for (int version = 7; version <= 40; version++)
        {
            int remainder = version;
            for (int i = 0; i < 12; i++) remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
            words[version - 7] = (version << 12) | remainder;
        }
        return words;
    }

    /// <summary>
    /// Undoes QrEncoder's interleaving and corrects each block; returns the data codewords in order, or null when a
    /// block has more errors than its error correction can mend.
    /// </summary>
    private static byte[]? CorrectBlocks(byte[] raw, int version, int level)
    {
        int blocks = QrEncoder.BlockCount[level][version], eccLength = QrEncoder.EccPerBlock[level][version];
        int total = raw.Length;
        int shortBlocks = blocks - total % blocks, shortLength = total / blocks;
        var all = new int[blocks][];
        for (int j = 0; j < blocks; j++) all[j] = new int[shortLength + 1];
        int k = 0;
        for (int i = 0; i <= shortLength; i++)
        {
            for (int j = 0; j < blocks; j++)
            {
                if (i != shortLength - eccLength || j >= shortBlocks)
                    all[j][i] = raw[k++];
            }
        }

        var data = new List<byte>(total);
        for (int j = 0; j < blocks; j++)
        {
            int dataLength = shortLength - eccLength + (j < shortBlocks ? 0 : 1);
            // A short block's gap at the long blocks' extra data codeword is closed before correcting it.
            int[] block = j < shortBlocks ? [.. all[j].AsSpan(0, dataLength), .. all[j].AsSpan(dataLength + 1, eccLength)] : all[j];
            if (!ReedSolomonDecoder.QrCode.Decode(block, eccLength)) return null;
            for (int i = 0; i < dataLength; i++) data.Add((byte)block[i]);
        }
        return data.ToArray();
    }

    internal static DecodedContent? ParseSegments(byte[] data, int version)
    {
        var bits = new BitReader(data);
        var text = new CharacterSets.Builder(guessUtf8: true);
        bool gs1 = false, fnc1 = false;
        int sizeClass = version <= 9 ? 0 : version <= 26 ? 1 : 2;
        while (bits.Available >= 4)
        {
            int mode = bits.Read(4);
            switch (mode)
            {
                case 0: // terminator
                    return Finish(text, gs1);
                case 1: // numeric
                {
                    if (!Numeric(ref bits, text, bits.Read(sizeClass == 0 ? 10 : sizeClass == 1 ? 12 : 14))) return null;
                    break;
                }
                case 2: // alphanumeric
                {
                    if (!AlphanumericSegment(ref bits, text, bits.Read(sizeClass == 0 ? 9 : sizeClass == 1 ? 11 : 13), fnc1)) return null;
                    break;
                }
                case 4: // bytes
                {
                    int count = bits.Read(sizeClass == 0 ? 8 : 16);
                    if (count * 8 > bits.Available) return null;
                    for (int i = 0; i < count; i++) text.Byte((byte)bits.Read(8));
                    break;
                }
                case 8: // kanji, as Shift JIS
                {
                    int count = bits.Read(sizeClass == 0 ? 8 : sizeClass == 1 ? 10 : 12);
                    if (count * 13 > bits.Available) return null;
                    int previous = text.Eci();
                    text.Eci(20);
                    for (int i = 0; i < count; i++)
                    {
                        int v = bits.Read(13);
                        int assembled = (v / 0xC0) << 8 | v % 0xC0;
                        assembled += assembled < 0x1F00 ? 0x8140 : 0xC140;
                        text.Byte((byte)(assembled >> 8));
                        text.Byte((byte)assembled);
                    }
                    text.Eci(previous);
                    break;
                }
                case 0xD: // Chinese characters (GB 2312), a later addition to the standard
                {
                    int subset = bits.Read(4);
                    int count = bits.Read(sizeClass == 0 ? 8 : sizeClass == 1 ? 10 : 12);
                    if (subset != 1 || count * 13 > bits.Available) return null;
                    int previous = text.Eci();
                    text.Eci(29);
                    for (int i = 0; i < count; i++)
                    {
                        int v = bits.Read(13);
                        int assembled = (v / 0x60) << 8 | v % 0x60;
                        assembled += assembled < 0xA00 ? 0xA1A1 : 0xA6A1;
                        text.Byte((byte)(assembled >> 8));
                        text.Byte((byte)assembled);
                    }
                    text.Eci(previous);
                    break;
                }
                case 7: // ECI
                {
                    int eci = ReadEci(ref bits);
                    if (eci < 0) return null;
                    text.Eci(eci);
                    break;
                }
                case 3: // structured append: the position in a sequence and the parity, which do not change the text
                    if (bits.Available < 16) return null;
                    bits.Read(16);
                    break;
                case 5: // FNC1 in the first position: GS1 data
                    gs1 = fnc1 = true;
                    break;
                case 9: // FNC1 in the second position: an application indicator follows
                    if (bits.Available < 8) return null;
                    bits.Read(8);
                    fnc1 = true;
                    break;
                default:
                    return null;
            }
        }
        // The terminator may be cut short when the data fills the symbol.
        return Finish(text, gs1);
    }

    private static DecodedContent Finish(CharacterSets.Builder text, bool gs1) => new(text.ToString(), text.Raw.ToArray(), gs1);

    private static int ReadEci(ref BitReader bits)
    {
        if (bits.Available < 8) return -1;
        int first = bits.Read(8);
        if ((first & 0x80) == 0) return first & 0x7F;
        if ((first & 0xC0) == 0x80) return bits.Available < 8 ? -1 : (first & 0x3F) << 8 | bits.Read(8);
        if ((first & 0xE0) == 0xC0) return bits.Available < 16 ? -1 : (first & 0x1F) << 16 | bits.Read(16);
        return -1;
    }

    private static bool Numeric(ref BitReader bits, CharacterSets.Builder text, int count)
    {
        while (count >= 3)
        {
            if (bits.Available < 10) return false;
            int v = bits.Read(10);
            if (v >= 1000) return false;
            text.Char((char)('0' + v / 100));
            text.Char((char)('0' + v / 10 % 10));
            text.Char((char)('0' + v % 10));
            count -= 3;
        }
        if (count == 2)
        {
            if (bits.Available < 7) return false;
            int v = bits.Read(7);
            if (v >= 100) return false;
            text.Char((char)('0' + v / 10));
            text.Char((char)('0' + v % 10));
        }
        else if (count == 1)
        {
            if (bits.Available < 4) return false;
            int v = bits.Read(4);
            if (v >= 10) return false;
            text.Char((char)('0' + v));
        }
        return true;
    }

    private static bool AlphanumericSegment(ref BitReader bits, CharacterSets.Builder text, int count, bool fnc1)
    {
        var chars = new System.Text.StringBuilder(count);
        while (count > 1)
        {
            if (bits.Available < 11) return false;
            int v = bits.Read(11);
            if (v >= 45 * 45) return false;
            chars.Append(Alphanumeric[v / 45]).Append(Alphanumeric[v % 45]);
            count -= 2;
        }
        if (count == 1)
        {
            if (bits.Available < 6) return false;
            int v = bits.Read(6);
            if (v >= 45) return false;
            chars.Append(Alphanumeric[v]);
        }
        for (int i = 0; i < chars.Length; i++)
        {
            // In GS1 mode "%" stands for the FNC1 separator, written as the group separator character, and "%%" for "%".
            if (fnc1 && chars[i] == '%')
            {
                if (i + 1 < chars.Length && chars[i + 1] == '%')
                {
                    text.Char('%');
                    i++;
                }
                else text.Char('\u001D');
            }
            else text.Char(chars[i]);
        }
        return true;
    }
}

/// <summary>Reads bits from the most significant end of each byte.</summary>
internal ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public readonly int Available => _data.Length * 8 - _position;

    public int Read(int count)
    {
        int result = 0;
        for (int i = 0; i < count; i++)
        {
            int bit = _position < _data.Length * 8 ? (_data[_position >> 3] >> (7 - (_position & 7))) & 1 : 0;
            result = (result << 1) | bit;
            _position++;
        }
        return result;
    }
}
