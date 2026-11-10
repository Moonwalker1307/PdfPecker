namespace PdfPecker;

/// <summary>
/// The printing condition of a PDF/X-4 file (ISO 15930-7), for <see cref="PdfOptions.PdfX"/>: the ICC profile of the
/// press the file is made for, which a print shop names, and how the profile is identified.
/// </summary>
/// <remarks>
/// <para>
/// PdfPecker bundles no press profile: the usual ones (FOGRA39, FOGRA51, GRACoL, SWOP and the like) come from the
/// print shop, the ICC (color.org) or the European Color Initiative. The profile must be an ICC output (printer)
/// profile for CMYK.
/// </para>
/// <para>
/// In a PDF/X-4 file, <c>device-cmyk()</c> colours and CMYK JPEG images are written as DeviceCMYK, the inks of that
/// press; sRGB colours and images are tagged with an sRGB ICC profile, so the print shop's workflow converts them to
/// the press's inks. Every page gets a trim box (and a bleed box), and fonts are embedded.
/// </para>
/// </remarks>
public sealed class PdfXOptions
{
    private const int IccHeaderLength = 128;

    /// <summary>The printing condition of <paramref name="outputProfile"/>, identified as <paramref name="outputCondition"/>.</summary>
    /// <param name="outputProfile">The ICC output profile of the press, for CMYK.</param>
    /// <param name="outputCondition">
    /// The printing condition's name in the ICC's registry of characterization data, such as "FOGRA39" or
    /// "CGATS TR 006", or the print shop's own name for it.
    /// </param>
    /// <exception cref="ArgumentNullException">One of the arguments is null.</exception>
    /// <exception cref="ArgumentException">
    /// The profile is not an ICC output profile for CMYK, or the condition is empty.
    /// </exception>
    public PdfXOptions(byte[] outputProfile, string outputCondition)
    {
        ArgumentNullException.ThrowIfNull(outputProfile);
        ArgumentNullException.ThrowIfNull(outputCondition);
        if (string.IsNullOrWhiteSpace(outputCondition))
            throw new ArgumentException("The output condition needs a name, such as \"FOGRA39\".", nameof(outputCondition));
        CheckProfile(outputProfile, nameof(outputProfile));
        OutputProfile = outputProfile;
        OutputCondition = outputCondition;
    }

    /// <summary>The ICC output profile of the press, embedded in the file as the output intent's profile.</summary>
    public byte[] OutputProfile { get; }

    /// <summary>The name of the printing condition, written as the output intent's OutputConditionIdentifier.</summary>
    public string OutputCondition { get; }

    /// <summary>
    /// A description of the printing condition for people, such as "Coated FOGRA39 (ISO 12647-2:2004)". Default: the
    /// <see cref="OutputCondition"/>.
    /// </summary>
    public string? OutputConditionInfo { get; init; }

    /// <summary>
    /// The registry where <see cref="OutputCondition"/> is defined. Default: "http://www.color.org", the ICC's
    /// registry. Set it to null for a condition of the print shop's own, which is not registered anywhere.
    /// </summary>
    public string? RegistryName { get; init; } = "http://www.color.org";

    /// <summary>
    /// Whether the file has been trapped, which PDF/X files must state. Default: false. PdfPecker does no trapping,
    /// so set it only for a file that has been trapped by other means.
    /// </summary>
    public bool Trapped { get; init; }

    /// <summary>
    /// Checks the profile's header: an ICC profile (the "acsp" signature) of the output device class ("prtr") whose data
    /// are CMYK. PDF/X takes the press's profile, and a display or input profile (sRGB, say) is not one.
    /// </summary>
    private static void CheckProfile(byte[] profile, string paramName)
    {
        static bool At(byte[] p, int offset, string signature)
        {
            for (int i = 0; i < 4; i++)
                if (p[offset + i] != signature[i]) return false;
            return true;
        }

        if (profile.Length < IccHeaderLength || !At(profile, 36, "acsp"))
            throw new ArgumentException("The output profile is not an ICC profile.", paramName);
        uint declared = (uint)(profile[0] << 24 | profile[1] << 16 | profile[2] << 8 | profile[3]);
        if (declared > profile.Length || declared < IccHeaderLength)
            throw new ArgumentException("The output profile is cut short: its header gives a size the data do not have.", paramName);
        if (!At(profile, 12, "prtr"))
            throw new ArgumentException("The output profile is not an output (printer) profile: PDF/X needs the press's profile, "
                + "such as FOGRA39 or GRACoL, not a display or input profile.", paramName);
        if (!At(profile, 16, "CMYK"))
            throw new ArgumentException("The output profile is not for CMYK: PdfPecker writes PDF/X for CMYK presses.", paramName);
    }
}
