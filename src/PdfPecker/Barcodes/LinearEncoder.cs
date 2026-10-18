namespace PdfPecker.Barcodes;

/// <summary>
/// Encodes Code 128 (ISO/IEC 15417) and EAN-13 (ISO/IEC 15420) as a row of modules, true for a bar. The quiet zones are
/// added by the SVG writer.
/// </summary>
internal static class LinearEncoder
{
    // Bar and space widths of the 107 Code 128 symbols, starting with a bar. The last is the stop symbol.
    internal static readonly string[] Code128Patterns =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    };

    private const int StartA = 103, StartB = 104, StartC = 105, Stop = 106;
    private const int CodeA = 101, CodeB = 100, CodeC = 99;

    /// <summary>The most characters <see cref="Code128"/> accepts; longer barcodes do not fit a page or a scanner.</summary>
    public const int MaxCode128Length = 1000;

    /// <summary>
    /// Code 128 for ASCII text. Runs of four or more digits use code set C (two digits per symbol), control characters
    /// use set A and everything else set B.
    /// </summary>
    public static bool[] Code128(string text)
    {
        if (text.Length == 0)
            throw new ArgumentException("A Code 128 barcode needs at least one character.", nameof(text));
        if (text.Length > MaxCode128Length)
            throw new ArgumentException($"A Code 128 barcode holds at most {MaxCode128Length} characters.", nameof(text));
        foreach (var c in text)
        {
            if (c > 127)
                throw new ArgumentException("Code 128 encodes only ASCII characters (U+0000 to U+007F).", nameof(text));
        }

        var values = new List<int>(text.Length + 4);
        var set = 0;
        var i = 0;
        while (i < text.Length)
        {
            var digits = DigitRun(text, i);
            var pair = set == 'C' ? digits >= 2 : digits >= 4 || (digits == 2 && text.Length == 2);
            if (pair && (set == 'C' || digits % 2 == 0))
            {
                EnsureSet(values, ref set, 'C');
                values.Add((text[i] - '0') * 10 + text[i + 1] - '0');
                i += 2;
                continue;
            }

            // An odd run of digits starts with one digit in set A or B, so that set C gets an even number of them.
            var c = text[i++];
            var wanted = NeedsA(c) ? 'A' : NeedsB(c) ? 'B' : set is 'A' or 'B' ? set : NextControlBeforeLowercase(text, i) ? 'A' : 'B';
            EnsureSet(values, ref set, wanted);
            values.Add(set == 'A' && c < 32 ? c + 64 : c - 32);
        }

        var checksum = values[0];
        for (var k = 1; k < values.Count; k++)
            checksum += k * values[k];
        values.Add(checksum % 103);
        values.Add(Stop);

        var modules = new List<bool>(values.Count * 11 + 2);
        foreach (var v in values)
        {
            var pattern = Code128Patterns[v];
            for (var k = 0; k < pattern.Length; k++)
            {
                for (var w = 0; w < pattern[k] - '0'; w++)
                    modules.Add(k % 2 == 0);
            }
        }
        return modules.ToArray();
    }

    private static int DigitRun(string text, int start)
    {
        var end = start;
        while (end < text.Length && text[end] is >= '0' and <= '9')
            end++;
        return end - start;
    }

    private static bool NeedsA(char c) => c < 32;

    private static bool NeedsB(char c) => c >= 96;

    private static bool NextControlBeforeLowercase(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (NeedsA(text[i]))
                return true;
            if (NeedsB(text[i]))
                return false;
        }
        return false;
    }

    private static void EnsureSet(List<int> values, ref int set, int wanted)
    {
        if (set == wanted)
            return;
        values.Add(set == 0
            ? wanted switch { 'A' => StartA, 'B' => StartB, _ => StartC }
            : wanted switch { 'A' => CodeA, 'B' => CodeB, _ => CodeC });
        set = wanted;
    }

    // EAN-13 left-hand digits with odd parity (set L); set R is their complement and set G is R reversed.
    private static readonly int[] EanL = { 0b0001101, 0b0011001, 0b0010011, 0b0111101, 0b0100011, 0b0110001, 0b0101111, 0b0111011, 0b0110111, 0b0001011 };

    // For each first digit, which of the six left-hand digits use set G (bit 5 is the second digit).
    private static readonly int[] EanParity = { 0b000000, 0b001011, 0b001101, 0b001110, 0b010011, 0b011001, 0b011100, 0b010101, 0b010110, 0b011010 };

    /// <summary>
    /// The 13 digits of an EAN-13 code from 12 digits (the check digit is added) or 13 digits (the check digit is
    /// verified).
    /// </summary>
    public static string Ean13Digits(string digits)
    {
        if (digits.Length is not (12 or 13))
            throw new ArgumentException("An EAN-13 code has 12 digits, or 13 with the check digit.", nameof(digits));
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
                throw new ArgumentException("An EAN-13 code contains only the digits 0 to 9.", nameof(digits));
        }
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (digits[i] - '0') * (i % 2 == 0 ? 1 : 3);
        var check = (char)('0' + (10 - sum % 10) % 10);
        if (digits.Length == 13 && digits[12] != check)
            throw new ArgumentException($"The EAN-13 check digit is {digits[12]}, but the first 12 digits give {check}.", nameof(digits));
        return digits.Length == 13 ? digits : string.Concat(digits, check.ToString());
    }

    /// <summary>The 95 modules of an EAN-13 code; <paramref name="digits"/> comes from <see cref="Ean13Digits"/>.</summary>
    public static bool[] Ean13(string digits)
    {
        var modules = new bool[95];
        var p = 0;
        void Put(int bits, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                modules[p++] = ((bits >> i) & 1) != 0;
        }

        Put(0b101, 3);
        var parity = EanParity[digits[0] - '0'];
        for (var i = 1; i <= 6; i++)
        {
            var l = EanL[digits[i] - '0'];
            Put(((parity >> (6 - i)) & 1) != 0 ? Reverse7(l ^ 0x7F) : l, 7);
        }
        Put(0b01010, 5);
        for (var i = 7; i <= 12; i++)
            Put(EanL[digits[i] - '0'] ^ 0x7F, 7);
        Put(0b101, 3);
        return modules;
    }

    private static int Reverse7(int bits)
    {
        var result = 0;
        for (var i = 0; i < 7; i++)
            result |= ((bits >> i) & 1) << (6 - i);
        return result;
    }
}
