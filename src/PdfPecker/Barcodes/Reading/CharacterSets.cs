using System.Text;

namespace PdfPecker.Barcodes.Reading;

/// <summary>
/// Turns the bytes of a code into text: in the character set an ECI (extended channel interpretation) names, or else in
/// the symbology's default. Text decodes as it is stored, so an unknown ECI falls back to ISO 8859-1 rather than failing.
/// </summary>
internal static class CharacterSets
{
    /// <summary>The ECI that says the bytes are UTF-8, as PdfPecker's own encoders write.</summary>
    public const int Utf8 = 26;

    /// <summary>The encoding of an ECI assignment number, or null for one this reader does not know.</summary>
    public static Encoding? ForEci(int eci) => eci switch
    {
        0 or 2 => CodePage(437),
        1 or 3 => Encoding.Latin1,
        >= 4 and <= 9 => CodePage(28588 + eci), // ISO 8859-2 to 8859-7
        10 => CodePage(28598), // ISO 8859-8
        11 => CodePage(28599), // ISO 8859-9
        15 => CodePage(28603), // ISO 8859-13
        17 => CodePage(28605), // ISO 8859-15
        20 => CodePage(932), // Shift JIS
        21 => CodePage(1250),
        22 => CodePage(1251),
        23 => CodePage(1252),
        24 => CodePage(1256),
        25 => Encoding.BigEndianUnicode,
        Utf8 => Encoding.UTF8,
        27 or 170 => Encoding.ASCII,
        28 => CodePage(950), // Big5
        29 => CodePage(54936), // GB 18030
        30 => CodePage(949), // EUC-KR
        _ => null,
    };

    private static Encoding? CodePage(int codePage)
    {
        try
        {
            return System.Text.CodePagesEncodingProvider.Instance.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Bytes without an ECI: UTF-8 when they are valid UTF-8 with at least one character beyond ASCII, as most
    /// generators write today; Shift JIS when they read as Japanese text, as many older Japanese codes are written; and
    /// ISO 8859-1, the standards' default, otherwise.
    /// </summary>
    public static string Guess(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IndexOfAnyInRange((byte)0x80, (byte)0xFF) < 0) return Encoding.Latin1.GetString(bytes);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
        }
        return LooksLikeShiftJis(bytes) && CodePage(932) is { } sjis ? sjis.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }

    /// <summary>
    /// Whether bytes that are not UTF-8 are better read as Shift JIS than as ISO 8859-1: they must be valid Shift JIS,
    /// and hold a word of three or more double-byte or half-width katakana characters, or bytes that are rare in
    /// Western text.
    /// </summary>
    private static bool LooksLikeShiftJis(ReadOnlySpan<byte> bytes)
    {
        bool latin1 = true;
        int pending = 0, katakana = 0, katakanaWord = 0, maxKatakanaWord = 0, doubleWord = 0, maxDoubleWord = 0, latinHighOther = 0;
        foreach (byte b in bytes)
        {
            if (b is > 0x7F and < 0xA0) latin1 = false;
            else if (b > 0x9F && (b < 0xC0 || b == 0xD7 || b == 0xF7)) latinHighOther++;
            if (pending > 0)
            {
                if (b < 0x40 || b == 0x7F || b > 0xFC) return false;
                pending--;
            }
            else if (b == 0x80 || b == 0xA0 || b > 0xEF)
            {
                return false;
            }
            else if (b is > 0xA0 and < 0xE0)
            {
                katakana++;
                doubleWord = 0;
                maxKatakanaWord = Math.Max(maxKatakanaWord, ++katakanaWord);
            }
            else if (b > 0x7F)
            {
                pending++;
                katakanaWord = 0;
                maxDoubleWord = Math.Max(maxDoubleWord, ++doubleWord);
            }
            else
            {
                katakanaWord = 0;
                doubleWord = 0;
            }
        }
        if (pending > 0) return false;
        if (maxKatakanaWord >= 3 || maxDoubleWord >= 3) return true;
        return !latin1 || (maxKatakanaWord == 2 && katakana == 2) || latinHighOther * 10 >= bytes.Length;
    }

    /// <summary>
    /// Builds text from runs of bytes, each with the ECI in force when it was read (-1 for none), and from characters
    /// added directly, such as digits and FNC1 separators.
    /// </summary>
    internal sealed class Builder
    {
        private readonly StringBuilder _text = new();
        private readonly List<byte> _bytes = new();
        private readonly bool _guessUtf8;
        private int _eci = -1;

        /// <param name="guessUtf8">
        /// Whether bytes without an ECI may be UTF-8 (QR codes, whose generators often write it without one) or are
        /// always ISO 8859-1 (Data Matrix, whose writers add the ECI).
        /// </param>
        public Builder(bool guessUtf8) => _guessUtf8 = guessUtf8;

        public int Eci() => _eci;

        public int Length => _text.Length + _bytes.Count;

        /// <summary>Every byte of the content, as the code stores it, for callers who need the raw data.</summary>
        public List<byte> Raw { get; } = new();

        public void Byte(byte b)
        {
            _bytes.Add(b);
            Raw.Add(b);
        }

        public void Char(char c)
        {
            Flush();
            _text.Append(c);
            Raw.Add(c < 256 ? (byte)c : (byte)'?');
        }

        public void Eci(int eci)
        {
            Flush();
            _eci = eci;
        }

        private void Flush()
        {
            if (_bytes.Count == 0) return;
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_bytes);
            _text.Append(_eci >= 0 && ForEci(_eci) is { } encoding ? encoding.GetString(span)
                : _guessUtf8 ? Guess(span) : Encoding.Latin1.GetString(span));
            _bytes.Clear();
        }

        public override string ToString()
        {
            Flush();
            return _text.ToString();
        }
    }
}
