namespace PdfPecker.Rendering.Images;

internal static partial class CcittFaxDecoder
{
    // An end-of-line code is eleven zeros and a one; up to eight of them, each perhaps after fill bits of up to 64
    // zeros, are skipped before a Group 3 row. Group 4 data ends with an end-of-facsimile-block code, two of them.
    private const int EndOfLineBits = 12;
    private const int EndOfLine = 0b0000_0000_0001;
    private const int MaxEndOfLines = 8;
    private const int MaxFillBits = 64;
    private const int EndOfBlockBits = 2 * EndOfLineBits;
    private const int EndOfBlock = EndOfLine << EndOfLineBits | EndOfLine;

    // Runs are coded as make-up codes for multiples of 64 and a terminating code for the rest (0 to 63). The longest
    // run code has 13 bits, so one lookup of the next 13 bits finds any code. A run of more than 64 codes is damage.
    private const int RunLookupBits = 13;
    private const int MakeupStep = 64;
    private const int MaxCodesPerRun = 64;

    /// <summary>A run-length code found by lookup: its length in bits (0 for no code) and the run it adds.</summary>
    private readonly record struct RunCode(int Length, int Run);

    private static readonly RunCode[] WhiteRuns = BuildRunTable(WhiteCodes());
    private static readonly RunCode[] BlackRuns = BuildRunTable(BlackCodes());

    private static RunCode[] BuildRunTable(List<(string Code, int Run)> codes)
    {
        var table = new RunCode[1 << RunLookupBits];
        foreach (var (code, run) in codes)
        {
            // Every lookup value that starts with the code finds it, whatever bits follow.
            int shift = RunLookupBits - code.Length;
            table.AsSpan(Convert.ToInt32(code, 2) << shift, 1 << shift).Fill(new RunCode(code.Length, run));
        }
        return table;
    }

    /// <summary>The coding modes of a two-dimensional row (T.4 table 4).</summary>
    private enum Mode { Invalid, Pass, Horizontal, Vertical }

    /// <summary>A mode code found by lookup: the mode, a vertical mode's offset of a1 from b1, and the code's length in bits.</summary>
    private readonly record struct ModeCode(Mode Mode, int Offset, int Length);

    // The longest mode code has seven bits. Seven zero bits or 0000001 start an extension or an end-of-line code,
    // which two-dimensional data here does not use; they read as invalid and use up the seven bits.
    private const int ModeLookupBits = 7;

    private static readonly ModeCode[] Modes = BuildModeTable();

    private static ModeCode[] BuildModeTable()
    {
        var table = new ModeCode[1 << ModeLookupBits];
        table.AsSpan().Fill(new ModeCode(Mode.Invalid, 0, ModeLookupBits));
        (string Code, Mode Mode, int Offset)[] codes =
        {
            ("1", Mode.Vertical, 0), ("011", Mode.Vertical, 1), ("010", Mode.Vertical, -1), ("001", Mode.Horizontal, 0),
            ("0001", Mode.Pass, 0), ("000011", Mode.Vertical, 2), ("000010", Mode.Vertical, -2), ("0000011", Mode.Vertical, 3),
            ("0000010", Mode.Vertical, -3),
        };
        foreach (var (code, mode, offset) in codes)
        {
            int shift = ModeLookupBits - code.Length;
            table.AsSpan(Convert.ToInt32(code, 2) << shift, 1 << shift).Fill(new ModeCode(mode, offset, code.Length));
        }
        return table;
    }

    private static ModeCode ReadMode(ref BitReader reader)
    {
        var code = Modes[reader.Peek(ModeLookupBits)];
        reader.Skip(code.Length);
        return code;
    }

    // ------------------------------------------------------------------ code tables (ITU-T T.4)

    private const int FirstExtendedMakeup = 1792;

    /// <summary>The white run codes as bit strings with their runs: terminating codes, make-up codes, extended make-up codes.</summary>
    internal static List<(string Code, int Run)> WhiteCodes()
    {
        string[] terminating =
        {
            "00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111", "10011", "10100", "00111", "01000", "001000",
            "000011", "110100", "110101", "101010", "101011", "0100111", "0001100", "0001000", "0010111", "0000011", "0000100",
            "0101000", "0101011", "0010011", "0100100", "0011000", "00000010", "00000011", "00011010", "00011011", "00010010",
            "00010011", "00010100", "00010101", "00010110", "00010111", "00101000", "00101001", "00101010", "00101011",
            "00101100", "00101101", "00000100", "00000101", "00001010", "00001011", "01010010", "01010011", "01010100",
            "01010101", "00100100", "00100101", "01011000", "01011001", "01011010", "01011011", "01001010", "01001011",
            "00110010", "00110011", "00110100",
        };
        string[] makeup =
        {
            "11011", "10010", "010111", "0110111", "00110110", "00110111", "01100100", "01100101", "01101000", "01100111",
            "011001100", "011001101", "011010010", "011010011", "011010100", "011010101", "011010110", "011010111",
            "011011000", "011011001", "011011010", "011011011", "010011000", "010011001", "010011010", "011000", "010011011",
        };
        return Codes(terminating, makeup);
    }

    /// <summary>The black run codes as bit strings with their runs: terminating codes, make-up codes, extended make-up codes.</summary>
    internal static List<(string Code, int Run)> BlackCodes()
    {
        string[] terminating =
        {
            "0000110111", "010", "11", "10", "011", "0011", "0010", "00011", "000101", "000100", "0000100", "0000101",
            "0000111", "00000100", "00000111", "000011000", "0000010111", "0000011000", "0000001000", "00001100111",
            "00001101000", "00001101100", "00000110111", "00000101000", "00000010111", "00000011000", "000011001010",
            "000011001011", "000011001100", "000011001101", "000001101000", "000001101001", "000001101010", "000001101011",
            "000011010010", "000011010011", "000011010100", "000011010101", "000011010110", "000011010111", "000001101100",
            "000001101101", "000011011010", "000011011011", "000001010100", "000001010101", "000001010110", "000001010111",
            "000001100100", "000001100101", "000001010010", "000001010011", "000000100100", "000000110111", "000000111000",
            "000000100111", "000000101000", "000001011000", "000001011001", "000000101011", "000000101100", "000001011010",
            "000001100110", "000001100111",
        };
        string[] makeup =
        {
            "0000001111", "000011001000", "000011001001", "000001011011", "000000110011", "000000110100", "000000110101",
            "0000001101100", "0000001101101", "0000001001010", "0000001001011", "0000001001100", "0000001001101",
            "0000001110010", "0000001110011", "0000001110100", "0000001110101", "0000001110110", "0000001110111",
            "0000001010010", "0000001010011", "0000001010100", "0000001010101", "0000001011010", "0000001011011",
            "0000001100100", "0000001100101",
        };
        return Codes(terminating, makeup);
    }

    /// <summary>Terminating codes for runs 0 to 63, make-up codes for 64 to 1728, and the shared ones for 1792 to 2560.</summary>
    private static List<(string Code, int Run)> Codes(string[] terminating, string[] makeup)
    {
        // Both colours share these.
        string[] extendedMakeup =
        {
            "00000001000", "00000001100", "00000001101", "000000010010", "000000010011", "000000010100", "000000010101",
            "000000010110", "000000010111", "000000011100", "000000011101", "000000011110", "000000011111",
        };
        var list = new List<(string, int)>(terminating.Length + makeup.Length + extendedMakeup.Length);
        for (int i = 0; i < terminating.Length; i++) list.Add((terminating[i], i));
        for (int i = 0; i < makeup.Length; i++) list.Add((makeup[i], MakeupStep * (i + 1)));
        for (int i = 0; i < extendedMakeup.Length; i++) list.Add((extendedMakeup[i], FirstExtendedMakeup + MakeupStep * i));
        return list;
    }
}
