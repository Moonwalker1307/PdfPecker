using System.Runtime.CompilerServices;

namespace PdfPecker.Fonts.Cff;

/// <summary>
/// The parts of a Type 2 (CFF and CFF2) charstring interpreter that every walker shares: number decoding, subroutine
/// numbering, the nesting limit and the hint mask size. The walkers themselves stay separate, because each acts on a
/// different set of operators and reacts differently to damaged charstrings (the renderer draws what it can, the
/// subsetter and instancer give up on the font).
/// </summary>
internal static class Type2
{
    /// <summary>The Type 2 limit on subroutine nesting.</summary>
    public const int MaxSubrDepth = 10;

    // The first byte of a number says how it is encoded; bytes below 32 are operators, except 28.
    private const int ShortIntNumber = 28;
    private const int FirstNumberByte = 32, LastSmallIntByte = 246, SmallIntBias = 139;
    private const int LastPositiveByte = 250, FirstPositiveByte = 247, FirstNegativeByte = 251, LastNegativeByte = 254;
    private const int TwoByteBias = 108;
    private const double FixedPointOne = 65536.0;

    // Subroutine numbers are biased by 107, 1131 or 32768, depending on how many subroutines there are.
    private const int SmallSubrLimit = 1240, MediumSubrLimit = 33900;
    private const int SmallSubrBias = 107, MediumSubrBias = 1131, LargeSubrBias = 32768;

    /// <summary>True when the byte starts a number rather than an operator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNumber(int b) => b >= FirstNumberByte || b == ShortIntNumber;

    /// <summary>Decodes the number whose first byte <paramref name="b"/> was just read; pos is the byte after it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadNumber(ReadOnlySpan<byte> cs, int b, ref int pos)
    {
        if (b <= LastSmallIntByte)
        {
            if (b != ShortIntNumber) return b - SmallIntBias;
            pos += 2;
            return (short)(cs[pos - 2] << 8 | cs[pos - 1]);
        }
        if (b <= LastPositiveByte) return (b - FirstPositiveByte) * 256 + cs[pos++] + TwoByteBias;
        if (b <= LastNegativeByte) return -(b - FirstNegativeByte) * 256 - cs[pos++] - TwoByteBias;
        pos += 4;
        return (cs[pos - 4] << 24 | cs[pos - 3] << 16 | cs[pos - 2] << 8 | cs[pos - 1]) / FixedPointOne; // 16.16 fixed
    }

    /// <summary>Subroutine numbers are stored minus a bias that depends on how many subroutines there are.</summary>
    public static int SubrBias(int count)
        => count < SmallSubrLimit ? SmallSubrBias : count < MediumSubrLimit ? MediumSubrBias : LargeSubrBias;

    /// <summary>The bytes of a hintmask or cntrmask: one bit per stem hint declared so far.</summary>
    public static int MaskBytes(int stems) => (stems + 7) / 8;
}

/// <summary>The operand stack of a Type 2 charstring, with a fixed capacity.</summary>
internal sealed class Type2Stack
{
    public readonly double[] Items;
    public int Count { get; set; }

    public Type2Stack(int capacity) => Items = new double[capacity];

    /// <summary>
    /// Decodes a number (see <see cref="Type2.ReadNumber"/>) and pushes it; returns false, dropping the number, when the
    /// stack is full.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PushNumber(ReadOnlySpan<byte> cs, int b, ref int pos)
    {
        double value = Type2.ReadNumber(cs, b, ref pos);
        if (Count >= Items.Length) return false;
        Items[Count++] = value;
        return true;
    }

    /// <summary>An operand counted from the bottom; with Count this also allows stack[^1] for the top.</summary>
    public double this[int i]
    {
        get => Items[i];
        set => Items[i] = value;
    }

    public double Pop() => Items[--Count];
    public void Clear() => Count = 0;
}

/// <summary>
/// Subroutines that each call the next many times expand exponentially within the nesting limit, so every walker
/// bounds the charstring tokens it runs per glyph, and per font where a font may have tens of thousands of glyphs.
/// </summary>
internal struct CharstringBudget
{
    public const int PerGlyph = 200_000;
    private int _glyph, _font;

    public CharstringBudget(int perFont)
    {
        _glyph = PerGlyph;
        _font = perFont;
    }

    public void StartGlyph() => _glyph = PerGlyph;

    /// <summary>Counts one token; false once the glyph or the font has run out.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Spend() => --_glyph >= 0 && --_font >= 0;
}
