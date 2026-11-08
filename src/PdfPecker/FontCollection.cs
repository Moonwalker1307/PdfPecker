using PdfPecker.Fonts;

namespace PdfPecker;

/// <summary>
/// TrueType and OpenType fonts that documents can use by family name in CSS (<c>font-family</c>). Fonts that a
/// document uses are embedded in the PDF as subsets with only the glyphs it needs, so text can use any Unicode
/// character the font has. Without registered fonts, text uses the standard PDF fonts (Helvetica, Times, Courier),
/// which cover Western European characters only.
/// </summary>
/// <remarks>
/// Supported formats: .ttf, .otf, .ttc collections, WOFF and WOFF2. A collection can be shared by many conversions,
/// including parallel ones; parsing happens once. Documents can also bring their own fonts with CSS <c>@font-face</c>.
/// </remarks>
public sealed class FontCollection
{
    private readonly object _lock = new();
    private readonly List<FontFace> _faces = new();
    private volatile Snapshot? _snapshot;

    /// <summary>Number of font faces registered.</summary>
    public int Count
    {
        get { lock (_lock) return _faces.Count; }
    }

    /// <summary>The family names that CSS can refer to.</summary>
    public IReadOnlyCollection<string> FamilyNames => GetSnapshot().ByFamily.Values.Select(l => l[0].Family).ToArray();

    /// <summary>
    /// Registers the fonts in a font file's bytes. The family name, weight and style are read from the font; pass
    /// <paramref name="familyName"/> to register it under another name, for example "sans-serif" to make it the
    /// default for that generic family.
    /// </summary>
    /// <exception cref="FormatException">The data is not a supported font.</exception>
    public FontCollection Add(byte[] fontData, string? familyName = null)
    {
        if (fontData == null) throw new ArgumentNullException(nameof(fontData));
        var programs = FontProgram.Load(fontData);
        if (programs.Count == 0) throw new FormatException("The font collection contains no usable fonts.");
        lock (_lock)
        {
            foreach (var p in programs) AddFaces(p, () => p, familyName);
            _snapshot = null;
        }
        return this;
    }

    /// <summary>Registers the fonts in a font file (.ttf, .otf, .ttc, .woff or .woff2).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="FormatException">The file is not a supported font.</exception>
    /// <exception cref="IOException">The file cannot be read, for example because it does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    public FontCollection AddFile(string path, string? familyName = null)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        return Add(File.ReadAllBytes(path), familyName);
    }

    /// <summary>
    /// Registers every font file in a folder, for example C:\Windows\Fonts or /usr/share/fonts. Files are read once
    /// to learn their names and loaded again only when a document uses them. Files that are not fonts are skipped.
    /// </summary>
    /// <returns>The number of font faces added.</returns>
    public int AddDirectory(string path, bool recursive = true)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        int added = 0;
        var option = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var file in Directory.EnumerateFiles(path, "*", option))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".ttf" or ".otf" or ".ttc" or ".otc" or ".woff" or ".woff2")) continue;
            List<FontProgram> programs;
            try
            {
                programs = FontProgram.Load(File.ReadAllBytes(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || FontProgram.IsFormatError(ex))
            {
                SwallowedErrors.Record(ex);
                continue;
            }
            lock (_lock)
            {
                for (int i = 0; i < programs.Count; i++)
                {
                    int index = i;
                    string f = file;
                    AddFaces(programs[i], () => FontProgram.Load(File.ReadAllBytes(f))[index], null);
                    added++;
                }
                _snapshot = null;
            }
        }
        return added;
    }

    /// <summary>
    /// Registers a font face whose file is read only when a document needs it: when CSS asks for its family, or when
    /// text has a character that the fonts before it lack and that <paramref name="unicodeRange"/> includes. Font
    /// packages use it to offer large fonts, such as CJK ones, that cost no time or memory until a document uses them.
    /// </summary>
    /// <param name="familyName">The family name that CSS <c>font-family</c> refers to.</param>
    /// <param name="fontData">
    /// Returns the font file (.ttf, .otf, .ttc, WOFF or WOFF2). It is called at most once for the face, by the first
    /// conversion that needs it, and may return the same array for several faces of one collection file. When it throws
    /// an <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>, or returns data that is not a font,
    /// the face is left out as if it had not been registered.
    /// </param>
    /// <param name="weight">The face's weight, from 1 to 1000, as CSS <c>font-weight</c> matches it.</param>
    /// <param name="italic">Whether the face is italic.</param>
    /// <param name="unicodeRange">
    /// The characters the face has, in the syntax of the CSS <c>unicode-range</c> descriptor, such as
    /// <c>"U+0000-00FF, U+4E00-9FFF"</c>. Text outside it is never drawn with the face, and the face is not read to find
    /// a character outside it. Null means the font's own characters, which reads the font the first time the fonts
    /// before it lack a character.
    /// </param>
    /// <param name="faceIndex">The face's index in a font collection (.ttc); 0 for a single font.</param>
    /// <exception cref="ArgumentNullException"><paramref name="familyName"/> or <paramref name="fontData"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="familyName"/> is empty, or <paramref name="unicodeRange"/> has no valid range.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="weight"/> or <paramref name="faceIndex"/> is out of range.</exception>
    /// <example><code>options.Fonts.AddDeferred("Noto Sans CJK SC", () => File.ReadAllBytes("NotoSansCJK-Regular.ttc"), unicodeRange: "U+3000-9FFF", faceIndex: 2);</code></example>
    public FontCollection AddDeferred(string familyName, Func<byte[]> fontData, int weight = 400, bool italic = false, string? unicodeRange = null, int faceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(familyName);
        ArgumentNullException.ThrowIfNull(fontData);
        string family = familyName.Trim();
        if (family.Length == 0) throw new ArgumentException("The family name is empty.", nameof(familyName));
        if (weight is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(weight), weight, "The weight must be from 1 to 1000.");
        if (faceIndex < 0) throw new ArgumentOutOfRangeException(nameof(faceIndex), faceIndex, "The face index must not be negative.");
        UnicodeRange? range = null;
        if (unicodeRange != null && (range = UnicodeRange.Parse(unicodeRange)) == null)
            throw new ArgumentException("The unicode-range has no valid range.", nameof(unicodeRange));
        var program = new Lazy<FontProgram?>(() =>
        {
            try
            {
                var programs = FontProgram.Load(fontData() ?? throw new FormatException("The font data is null."));
                return faceIndex < programs.Count ? programs[faceIndex] : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || FontProgram.IsFormatError(ex))
            {
                SwallowedErrors.Record(ex);
                return null;
            }
        });
        lock (_lock)
        {
            _faces.Add(new FontFace(family, weight, weight, italic, range, program));
            _snapshot = null;
        }
        return this;
    }

    private static readonly Lazy<FontCollection> SystemFonts = new(LoadSystemFonts);

    /// <summary>The fonts installed on this machine, for <see cref="PdfOptions.UseSystemFonts"/>. Scanned once per process.</summary>
    internal static FontCollection Installed => SystemFonts.Value;

    private static FontCollection LoadSystemFonts()
    {
        var fonts = new FontCollection();
        foreach (var dir in SystemFontDirectories())
        {
            try
            {
                if (Directory.Exists(dir)) fonts.AddDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // an unreadable folder just has no fonts for us
            }
        }
        return fonts;
    }

    /// <summary>The folders where Windows, macOS and Linux keep installed fonts, for all users and the current one.</summary>
    internal static IEnumerable<string> SystemFontDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            if (home.Length > 0) yield return Path.Combine(home, "Library", "Fonts");
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(dataHome) && home.Length > 0) dataHome = Path.Combine(home, ".local", "share");
            if (!string.IsNullOrEmpty(dataHome)) yield return Path.Combine(dataHome, "fonts");
            if (home.Length > 0) yield return Path.Combine(home, ".fonts");
        }
    }

    private void AddFaces(FontProgram p, Func<FontProgram> load, string? familyName)
    {
        var names = new List<string>(2);
        if (familyName != null) names.Add(familyName.Trim());
        else
        {
            if (p.TypographicFamily != null) names.Add(p.TypographicFamily);
            if (!names.Contains(p.FamilyName, StringComparer.OrdinalIgnoreCase)) names.Add(p.FamilyName);
        }
        // Registered fonts load lazily; the program parsed now gives the metadata and is kept only for in-memory fonts.
        var program = new Lazy<FontProgram?>(() =>
        {
            try { return load(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || FontProgram.IsFormatError(ex)) { SwallowedErrors.Record(ex); return null; }
        });
        foreach (var name in names)
        {
            // A variable font covers the weights of its weight axis.
            int wMin = p.WeightClass, wMax = p.WeightClass;
            if (p.Axis(FontVariations.Wght) is { } wght) { wMin = (int)Math.Ceiling(wght.Min); wMax = (int)Math.Floor(wght.Max); }
            _faces.Add(new FontFace(name, wMin, wMax, p.IsItalic, null, program, p.FullName, p.PostScriptName));
        }
    }

    internal sealed class Snapshot
    {
        public readonly Dictionary<string, List<FontFace>> ByFamily = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<FontFace> All = new();
    }

    internal Snapshot GetSnapshot()
    {
        var s = _snapshot;
        if (s != null) return s;
        lock (_lock)
        {
            if (_snapshot != null) return _snapshot;
            s = new Snapshot();
            foreach (var f in _faces)
            {
                s.All.Add(f);
                if (!s.ByFamily.TryGetValue(f.Family, out var list)) s.ByFamily[f.Family] = list = new List<FontFace>();
                list.Add(f);
            }
            _snapshot = s;
            return s;
        }
    }
}
