namespace PdfPecker.Css;

/// <summary>
/// The faces browsers may make up when a font family lacks them (font-synthesis): a bold face by stroking the outlines,
/// an italic face by slanting them, small capitals from capitals, and sub and superscripts from smaller glyphs.
/// A flag set means the synthesis is allowed.
/// </summary>
[Flags]
internal enum FontSynthesis : byte
{
    None = 0,
    Weight = 1,
    Style = 2,
    SmallCaps = 4,
    Position = 8,
    All = Weight | Style | SmallCaps | Position,
}
