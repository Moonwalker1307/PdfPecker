namespace PdfPecker.Css;

/// <summary>A device-cmyk() colour: cyan, magenta, yellow and black from 0 to 1.</summary>
internal readonly record struct Cmyk(float C, float M, float Y, float K)
{
    /// <summary>
    /// The sRGB colour (0xRRGGBB) that stands for this one wherever the document works in sRGB: the naive conversion
    /// of CSS Color 5, which is what the specification gives when no colour profile is known.
    /// </summary>
    public uint Fallback
    {
        get
        {
            static uint Channel(float ink, float k) => (uint)MathF.Round((1 - MathF.Min(1, ink * (1 - k) + k)) * 255);
            return Channel(C, K) << 16 | Channel(M, K) << 8 | Channel(Y, K);
        }
    }
}

/// <summary>
/// The device-cmyk() colours of one conversion, so that the PDF writer can write them as DeviceCMYK although styles,
/// layout and painting carry every colour as 0xAARRGGBB.
/// </summary>
/// <remarks>
/// <para>
/// Each CMYK colour gets an sRGB stand-in, its naive conversion, and the writer writes a fill or stroke whose colour is
/// a stand-in as the CMYK colour it stands for. Two CMYK colours with the same conversion (rich black and plain black,
/// say) would be one colour that way, so a stand-in that is taken, or that is one of the named colours an author may
/// also write as sRGB, is moved by the smallest step that frees it, which is one 255th of a channel and invisible.
/// Colours worked out from a stand-in (a mix, a shadow, a gradient between two of them) are new sRGB colours and stay
/// sRGB.
/// </para>
/// <para>
/// Colours are parsed by static methods all through the style resolver, so the registry of the running conversion is
/// reached through a thread static, as <see cref="FormValidity"/> does for its patterns. A conversion runs on one
/// thread from start to end; outside one, device-cmyk() is its stand-in and nothing more.
/// </para>
/// </remarks>
internal sealed class CmykColors
{
    // A document with more CMYK colours than this gets the rest as sRGB.
    internal const int MaxColors = 4096;
    // How far a stand-in may move from the conversion before the colour is left as sRGB.
    private const int MaxStep = 3;

    [ThreadStatic] private static CmykColors? t_current;

    private static readonly HashSet<uint> NamedValues = new(CssColor.NamedValues);

    private readonly Dictionary<uint, Cmyk> _byFallback = new();
    private readonly Dictionary<Cmyk, uint> _byColor = new();

    /// <summary>The registry of the conversion running on this thread, if any.</summary>
    public static CmykColors? Current => t_current;

    /// <summary>Whether the document has any CMYK colour.</summary>
    public bool Any => _byColor.Count > 0;

    /// <summary>Makes this the registry of the thread until the returned scope is disposed.</summary>
    public Scope Use()
    {
        var previous = t_current;
        t_current = this;
        return new Scope(previous);
    }

    /// <summary>Restores the registry that was current before <see cref="Use"/>.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly CmykColors? _previous;

        internal Scope(CmykColors? previous) => _previous = previous;

        /// <inheritdoc/>
        public void Dispose() => t_current = _previous;
    }

    /// <summary>The stand-in (0xRRGGBB) of a colour in the running conversion, or its plain conversion outside one.</summary>
    public static uint StandIn(Cmyk color) => t_current?.Register(color) ?? color.Fallback;

    /// <summary>The colour's stand-in, chosen the first time the colour is seen.</summary>
    public uint Register(Cmyk color)
    {
        if (_byColor.TryGetValue(color, out uint known)) return known;
        uint fallback = color.Fallback;
        if (_byColor.Count >= MaxColors || !TryFree(fallback, out uint standIn)) return fallback;
        _byColor[color] = standIn;
        _byFallback[standIn] = color;
        return standIn;
    }

    /// <summary>The CMYK colour that an 0xAARRGGBB colour stands for, if it stands for one.</summary>
    public bool TryGet(uint argb, out Cmyk color)
    {
        if (_byFallback.Count == 0) { color = default; return false; }
        return _byFallback.TryGetValue(argb & 0xFFFFFF, out color);
    }

    /// <summary>The nearest colour to <paramref name="rgb"/>, nearest first by the largest channel step, that no other CMYK colour or named colour has.</summary>
    private bool TryFree(uint rgb, out uint free)
    {
        int r0 = (int)(rgb >> 16) & 0xFF, g0 = (int)(rgb >> 8) & 0xFF, b0 = (int)rgb & 0xFF;
        for (int step = 0; step <= MaxStep; step++)
            for (int dr = -step; dr <= step; dr++)
                for (int dg = -step; dg <= step; dg++)
                    for (int db = -step; db <= step; db++)
                    {
                        if (Math.Max(Math.Abs(dr), Math.Max(Math.Abs(dg), Math.Abs(db))) != step) continue;
                        int r = r0 + dr, g = g0 + dg, b = b0 + db;
                        if ((uint)r > 255 || (uint)g > 255 || (uint)b > 255) continue;
                        uint candidate = (uint)(r << 16 | g << 8 | b);
                        if (_byFallback.ContainsKey(candidate) || NamedValues.Contains(candidate)) continue;
                        free = candidate;
                        return true;
                    }
        free = 0;
        return false;
    }
}
