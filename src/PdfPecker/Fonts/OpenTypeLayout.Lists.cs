namespace PdfPecker.Fonts;

// The script, feature and lookup lists of GSUB and GPOS: which lookups a feature of a script uses, and where a lookup is.
internal sealed partial class OpenTypeLayout
{
    private const int ScriptListField = 4, FeatureListField = 6, LookupListField = 8;

    // The script and feature lists hold a count and then records of a tag and an offset from the list.
    private const int ListRecordsField = 2, ListRecordLength = 6, ListRecordOffsetField = 4;

    // A script table starts with the offset of its default language system; this shaper uses no other.
    private const int DefaultLanguageSystemField = 0, ScriptTableMinimumLength = 2;

    // A language system: the lookup order (reserved), the required feature, and the indexes of its other features.
    private const int LanguageSystemHeaderLength = 6;
    private const int RequiredFeatureField = 2, FeatureIndexCountField = 4, FeatureIndexesField = 6;
    private const int NoRequiredFeature = 0xFFFF;

    // A feature table: its parameters (unused), then the indexes of its lookups.
    private const int FeatureHeaderLength = 4, LookupIndexCountField = 2, LookupIndexesField = 4;

    // The lookup list: a count and the offsets of the lookups from the list.
    private const int LookupCountField = 0, LookupOffsetsField = 2;

    /// <summary>The scripts a requested script falls back to, in order, as HarfBuzz does.</summary>
    private static readonly uint[] FallbackScripts = [FontProgram.Tag("DFLT"), FontProgram.Tag("dflt"), FontProgram.Tag("latn")];

    // A language system may name one feature many times and a feature may name one lookup many times, and the arrays
    // of both may share bytes, so a few kilobytes of font could otherwise ask for billions of lookup indexes. Each
    // feature and each lookup counts once, as in HarfBuzz, and reading stops at this many lookup indexes, which is as
    // many as a lookup list can hold and far more than any real font's feature names.
    internal const int MaxLookupIndexReads = 0x10000;

    /// <summary>
    /// The distinct lookups of a feature for a script (its default language system), in the order the font first names
    /// them; empty when the font does not have the feature. The script falls back to DFLT, dflt and latn, as HarfBuzz
    /// does.
    /// </summary>
    public List<int> FeatureLookups(bool gpos, uint script, uint feature)
    {
        var result = new List<int>();
        int table = gpos ? _gpos : _gsub;
        if (table < 0) return result;
        int languageSystem = LanguageSystem(table, script);
        if (languageSystem < 0) return result;
        int features = table + _reader.U16(table + FeatureListField);
        var collector = new LookupCollector(result);
        AddFeatureLookups(ref collector, features, _reader.U16(languageSystem + RequiredFeatureField), feature);
        for (int i = 0, count = _reader.U16(languageSystem + FeatureIndexCountField); i < count && !collector.Full; i++)
            AddFeatureLookups(ref collector, features, _reader.U16(languageSystem + FeatureIndexesField + i * 2), feature);
        return result;
    }

    /// <summary>True when GSUB or GPOS lists the script itself, not only a script it would fall back to.</summary>
    public bool HasScript(bool gpos, uint script)
    {
        int table = gpos ? _gpos : _gsub;
        return table >= 0 && FindScript(table + _reader.U16(table + ScriptListField), script) >= 0;
    }

    /// <summary>True when the font's script has the feature.</summary>
    public bool HasFeature(bool gpos, uint script, uint feature) => FeatureLookups(gpos, script, feature).Count > 0;

    // Adds the lookups of the feature at an index of the feature list, when it is there, has the tag and was not added
    // before.
    private void AddFeatureLookups(ref LookupCollector collector, int features, int featureIndex, uint tag)
    {
        if (featureIndex == NoRequiredFeature || featureIndex >= _reader.U16(features)) return;
        int record = features + ListRecordsField + featureIndex * ListRecordLength;
        if (_reader.U32(record) != tag || !collector.FirstVisit(featureIndex)) return;
        int feature = features + _reader.U16(record + ListRecordOffsetField);
        for (int j = 0, count = _reader.U16(feature + LookupIndexCountField); j < count && !collector.Full; j++)
            collector.Add(_reader.U16(feature + LookupIndexesField + j * 2));
    }

    // The features already visited and the lookups already collected, both 16-bit indexes, and the lookup index budget.
    private struct LookupCollector(List<int> lookups)
    {
        private HashSet<int>? _features;
        private HashSet<int>? _lookups;
        private int _reads;

        public readonly bool Full => _reads >= MaxLookupIndexReads;

        public bool FirstVisit(int featureIndex) => (_features ??= []).Add(featureIndex);

        public void Add(int lookupIndex)
        {
            _reads++;
            if ((_lookups ??= []).Add(lookupIndex)) lookups.Add(lookupIndex);
        }
    }

    // The default language system of the script, or of the first fallback script the font has. A script the font
    // lists without a default language system does not fall back.
    private int LanguageSystem(int table, uint script)
    {
        int scripts = table + _reader.U16(table + ScriptListField);
        int scriptTable = FindScript(scripts, script);
        for (int k = 0; scriptTable < 0 && k < FallbackScripts.Length; k++) scriptTable = FindScript(scripts, FallbackScripts[k]);
        if (scriptTable < 0) return -1;
        int defaultLanguageSystem = _reader.U16(scriptTable + DefaultLanguageSystemField);
        return defaultLanguageSystem != 0 ? scriptTable + defaultLanguageSystem : -1;
    }

    // The script table of the first record with the tag, or -1.
    private int FindScript(int scripts, uint tag)
    {
        for (int i = 0, count = _reader.U16(scripts); i < count; i++)
        {
            int record = scripts + ListRecordsField + i * ListRecordLength;
            if (_reader.U32(record) == tag) return scripts + _reader.U16(record + ListRecordOffsetField);
        }
        return -1;
    }

    /// <summary>The offset of the lookup with an index in the table's lookup list, or -1 for an index past it.</summary>
    private int LookupOffset(int table, int index)
    {
        int lookups = table + _reader.U16(table + LookupListField);
        if (index >= _reader.U16(lookups + LookupCountField)) return -1;
        return lookups + _reader.U16(lookups + LookupOffsetsField + index * 2);
    }

    /// <summary>
    /// Whether everything <see cref="FeatureLookups"/> and <see cref="ApplyLookup"/> read before reaching a lookup's
    /// subtables is inside the font: the script list with each script's default language system, the feature list
    /// with each feature's lookup indices, and the lookup list's offsets.
    /// </summary>
    private bool ValidLists(int table)
        => ValidScriptList(table + _reader.U16(table + ScriptListField))
            && ValidFeatureList(table + _reader.U16(table + FeatureListField))
            && ValidLookupList(table + _reader.U16(table + LookupListField));

    private bool ValidScriptList(int scripts)
    {
        if (!ValidRecordList(scripts)) return false;
        for (int i = 0, count = _reader.U16(scripts); i < count; i++)
        {
            int script = scripts + _reader.U16(scripts + ListRecordsField + i * ListRecordLength + ListRecordOffsetField);
            if (!Fits(script, ScriptTableMinimumLength)) return false;
            int defaultLanguageSystem = _reader.U16(script + DefaultLanguageSystemField);
            if (defaultLanguageSystem == 0) continue;
            int languageSystem = script + defaultLanguageSystem;
            if (!Fits(languageSystem, LanguageSystemHeaderLength)
                || !Fits(languageSystem + FeatureIndexesField, _reader.U16(languageSystem + FeatureIndexCountField) * 2))
                return false;
        }
        return true;
    }

    private bool ValidFeatureList(int features)
    {
        if (!ValidRecordList(features)) return false;
        for (int i = 0, count = _reader.U16(features); i < count; i++)
        {
            int feature = features + _reader.U16(features + ListRecordsField + i * ListRecordLength + ListRecordOffsetField);
            if (!Fits(feature, FeatureHeaderLength) || !Fits(feature + LookupIndexesField, _reader.U16(feature + LookupIndexCountField) * 2))
                return false;
        }
        return true;
    }

    private bool ValidLookupList(int lookups)
        => Fits(lookups, 2) && Fits(lookups + LookupOffsetsField, _reader.U16(lookups + LookupCountField) * 2);

    // Whether the count and the records of a script or feature list are inside the font.
    private bool ValidRecordList(int list) => Fits(list, 2) && Fits(list + ListRecordsField, _reader.U16(list) * ListRecordLength);
}
