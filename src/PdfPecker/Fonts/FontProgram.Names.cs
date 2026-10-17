using System.Text;

namespace PdfPecker.Fonts;

/// <summary>The family, full and PostScript names from the name table.</summary>
internal sealed partial class FontProgram
{
    // name header: the record count and the offset of the string storage, then the records.
    private const int NameCountField = 2, NameStringOffsetField = 4, NameRecordsField = 6, NameRecordLength = 12;
    private const int NameEncodingField = 2, NameLanguageField = 4, NameIdField = 6, NameLengthField = 8, NameOffsetField = 10;

    // The name IDs read: the family, the full name, the PostScript name and the typographic family.
    private const int FamilyNameId = 1, FullNameId = 4, PostScriptNameId = 6, TypographicFamilyId = 16, NameIdCount = 26;

    private const int WindowsEnglishLanguage = 0x409, MacintoshEnglishLanguage = 0;

    // A PDF name is ASCII without delimiters; longer names are cut, as PDF readers do not need them whole.
    private const string PdfNameDelimiters = "()<>[]{}/%#";
    private const int MaxPostScriptNameLength = 100;

    private readonly record struct FontNames(string? Family, string? TypographicFamily, string? Full, string? PostScript);

    // For each name, the English Windows Unicode record is preferred, then any Windows Unicode or Unicode platform
    // record, then an English Macintosh Roman one.
    private FontNames ReadNames((int Offset, int Length) table)
    {
        string? family = null, typographicFamily = null, full = null, postScript = null;
        int count = Reader.U16(table.Offset + NameCountField), strings = table.Offset + Reader.U16(table.Offset + NameStringOffsetField);
        var chosen = new int[NameIdCount]; // per name ID: one more than the score of the record chosen so far
        for (int i = 0; i < count; i++)
        {
            int record = table.Offset + NameRecordsField + i * NameRecordLength;
            if (record + NameRecordLength > table.Offset + table.Length) break;
            int platform = Reader.U16(record), encoding = Reader.U16(record + NameEncodingField);
            int language = Reader.U16(record + NameLanguageField), id = Reader.U16(record + NameIdField);
            int length = Reader.U16(record + NameLengthField), offset = strings + Reader.U16(record + NameOffsetField);
            if (id is not (FamilyNameId or FullNameId or PostScriptNameId or TypographicFamilyId) || offset + length > Data.Length) continue;
            int score = NameScore(platform, encoding, language);
            if (score < 0 || score + 1 <= chosen[id]) continue;
            chosen[id] = score + 1;
            var encodingUsed = platform == MacintoshPlatform ? Encoding.Latin1 : Encoding.BigEndianUnicode;
            string value = encodingUsed.GetString(Data, offset, length).Trim('\0', ' ');
            if (value.Length == 0) continue;
            switch (id)
            {
                case FamilyNameId: family = value; break;
                case FullNameId: full = value; break;
                case PostScriptNameId: postScript = value; break;
                case TypographicFamilyId: typographicFamily = value; break;
            }
        }
        return new FontNames(family, typographicFamily, full, postScript);
    }

    private static int NameScore(int platform, int encoding, int language) => platform switch
    {
        WindowsPlatform when encoding is WindowsUnicodeBmpEncoding or WindowsUnicodeFullEncoding => language == WindowsEnglishLanguage ? 4 : 3,
        UnicodePlatform => 2,
        MacintoshPlatform when encoding == MacintoshRomanEncoding => language == MacintoshEnglishLanguage ? 1 : 0,
        _ => -1,
    };

    private static string SanitizeName(string name)
    {
        var sanitized = new StringBuilder(name.Length);
        foreach (char c in name)
            if (c > ' ' && c < 0x7F && PdfNameDelimiters.IndexOf(c) < 0) sanitized.Append(c);
        if (sanitized.Length == 0) sanitized.Append("Font");
        return sanitized.Length > MaxPostScriptNameLength ? sanitized.ToString(0, MaxPostScriptNameLength) : sanitized.ToString();
    }
}
