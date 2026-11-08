namespace PdfPecker.Barcodes.Reading;

/// <summary>
/// Corrects a block of codewords with its Reed-Solomon error correction codewords, in GF(256) with the field
/// polynomial of the symbology: QR codes use x^8 + x^4 + x^3 + x^2 + 1 and generator roots from a^0, Data Matrix
/// x^8 + x^5 + x^3 + x^2 + 1 and roots from a^1. It finds the error locator with the extended Euclidean algorithm and
/// the error values with Forney's formula.
/// </summary>
internal sealed class ReedSolomonDecoder
{
    public static readonly ReedSolomonDecoder QrCode = new(0x11D, 0);
    public static readonly ReedSolomonDecoder DataMatrix = new(0x12D, 1);

    private readonly int[] _exp = new int[256];
    private readonly int[] _log = new int[256];
    private readonly int _base;

    private ReedSolomonDecoder(int primitive, int generatorBase)
    {
        _base = generatorBase;
        int x = 1;
        for (int i = 0; i < 256; i++)
        {
            _exp[i] = x;
            x <<= 1;
            if (x >= 256) x ^= primitive;
        }
        for (int i = 0; i < 255; i++) _log[_exp[i]] = i;
    }

    private int Multiply(int a, int b) => a == 0 || b == 0 ? 0 : _exp[(_log[a] + _log[b]) % 255];

    private int Inverse(int a) => _exp[255 - _log[a]];

    /// <summary>
    /// Corrects <paramref name="block"/> in place, whose last <paramref name="eccCount"/> codewords are the error
    /// correction. Returns false when there are more errors than the code can correct, and then the block may be
    /// partly changed.
    /// </summary>
    public bool Decode(Span<int> block, int eccCount)
    {
        var syndromes = new int[eccCount];
        bool clean = true;
        for (int i = 0; i < eccCount; i++)
        {
            int value = Evaluate(block, _exp[(i + _base) % 255]);
            syndromes[eccCount - 1 - i] = value;
            if (value != 0) clean = false;
        }
        if (clean) return true;

        var monomial = new int[eccCount + 1];
        monomial[0] = 1;
        if (!Euclid(monomial, Trim(syndromes), eccCount, out var sigma, out var omega)) return false;
        var locations = ErrorLocations(sigma);
        if (locations == null) return false;
        for (int i = 0; i < locations.Length; i++)
        {
            int position = block.Length - 1 - _log[locations[i]];
            if (position < 0) return false;
            block[position] ^= ErrorMagnitude(omega, locations, i);
        }
        return true;
    }

    /// <summary>The polynomial with these coefficients, highest power first, at <paramref name="x"/> (Horner's rule).</summary>
    private int Evaluate(ReadOnlySpan<int> coefficients, int x)
    {
        if (x == 0) return coefficients[^1];
        int result = 0;
        foreach (var c in coefficients) result = Multiply(result, x) ^ c;
        return result;
    }

    private bool Euclid(int[] a, int[] b, int eccCount, out int[] sigma, out int[] omega)
    {
        sigma = omega = Array.Empty<int>();
        if (Degree(a) < Degree(b)) (a, b) = (b, a);
        int[] rLast = a, r = b, tLast = [0], t = [1];
        while (2 * Degree(r) >= eccCount)
        {
            var rLastLast = rLast;
            var tLastLast = tLast;
            rLast = r;
            tLast = t;
            if (IsZero(rLast)) return false;
            r = rLastLast;
            int[] q = [0];
            int leadInverse = Inverse(rLast[0]);
            while (!IsZero(r) && Degree(r) >= Degree(rLast))
            {
                int shift = Degree(r) - Degree(rLast);
                int scale = Multiply(r[0], leadInverse);
                q = Add(q, Monomial(shift, scale));
                r = Add(r, MultiplyByMonomial(rLast, shift, scale));
            }
            t = Add(MultiplyPolynomials(q, tLast), tLastLast);
            if (!IsZero(r) && Degree(r) >= Degree(rLast)) return false;
        }
        int atZero = t[^1];
        if (atZero == 0) return false;
        int inverse = Inverse(atZero);
        sigma = Scale(t, inverse);
        omega = Scale(r, inverse);
        return true;
    }

    /// <summary>The inverses of the error locator's roots, found by trying every field element (Chien search).</summary>
    private int[]? ErrorLocations(int[] locator)
    {
        int count = Degree(locator);
        if (count == 1) return [locator[^2]];
        var result = new int[count];
        int found = 0;
        for (int i = 1; i < 256 && found < count; i++)
        {
            if (Evaluate(locator, i) == 0) result[found++] = Inverse(i);
        }
        return found == count ? result : null;
    }

    private int ErrorMagnitude(int[] omega, int[] locations, int i)
    {
        int xiInverse = Inverse(locations[i]);
        int denominator = 1;
        for (int j = 0; j < locations.Length; j++)
        {
            if (i == j) continue;
            int term = Multiply(locations[j], xiInverse);
            denominator = Multiply(denominator, term ^ 1);
        }
        int result = Multiply(Evaluate(omega, xiInverse), Inverse(denominator));
        return _base != 0 ? Multiply(result, xiInverse) : result;
    }

    private static int Degree(int[] p) => p.Length - 1;

    private static bool IsZero(int[] p) => p[0] == 0;

    private static int[] Trim(int[] p)
    {
        int first = 0;
        while (first < p.Length - 1 && p[first] == 0) first++;
        return first == 0 ? p : p[first..];
    }

    private static int[] Monomial(int degree, int coefficient)
    {
        if (coefficient == 0) return [0];
        var result = new int[degree + 1];
        result[0] = coefficient;
        return result;
    }

    private static int[] Add(int[] a, int[] b)
    {
        if (IsZero(a)) return b;
        if (IsZero(b)) return a;
        if (a.Length < b.Length) (a, b) = (b, a);
        var sum = (int[])a.Clone();
        int offset = a.Length - b.Length;
        for (int i = 0; i < b.Length; i++) sum[offset + i] ^= b[i];
        return Trim(sum);
    }

    private int[] MultiplyPolynomials(int[] a, int[] b)
    {
        if (IsZero(a) || IsZero(b)) return [0];
        var product = new int[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
        {
            for (int j = 0; j < b.Length; j++) product[i + j] ^= Multiply(a[i], b[j]);
        }
        return Trim(product);
    }

    private int[] MultiplyByMonomial(int[] p, int degree, int coefficient)
    {
        if (coefficient == 0) return [0];
        var product = new int[p.Length + degree];
        for (int i = 0; i < p.Length; i++) product[i] = Multiply(p[i], coefficient);
        return Trim(product);
    }

    private int[] Scale(int[] p, int factor)
    {
        var result = new int[p.Length];
        for (int i = 0; i < p.Length; i++) result[i] = Multiply(p[i], factor);
        return Trim(result);
    }
}
