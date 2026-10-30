using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using PdfPecker.Css;

namespace PdfPecker.Tests;

/// <summary>
/// <see cref="ComputedStyle.CreateChild"/> copies the whole style and then resets the properties that are not inherited.
/// A field added to <see cref="ComputedStyle"/> is therefore inherited unless someone remembers to reset it, so this test
/// makes every field a decision: it must be named below, and the child must take it from the parent or reset it as the
/// list says. Reflection is fine here, in the tests; the library never uses it.
/// </summary>
public class ComputedStyleInheritanceTests
{
    // The fields a child takes from its parent. The three inherited groups hold the rarely set inherited properties.
    private static readonly string[] Inherited =
    [
        "Color", "FontFamily", "Bold", "FontWeight", "Italic", "FontStretch", "FontSize", "FontSizeKeyword",
        "FontSizeAbsolute", "LineHeightKind", "LineHeightValue", "TextAlign", "Rtl", "WritingMode", "NoHyphens",
        "HyphensAuto", "Lang", "Quirks", "Hyphenation", "SmallCaps", "WhiteSpace", "TextWrap", "OverflowWrap", "WordBreak",
        "ListStyleType", "ListStylePositionInside", "TextTransform", "TextDecoration", "TextIndent", "LetterSpacing",
        "NoLigatures", "NoKerning", "WordSpacing", "Visible", "BorderCollapse", "BorderSpacingH", "BorderSpacingV",
        "FontFamilyName", "Fonts", "MissingFonts", "CustomProperties", "PageName", "BoxReverse",
        "_typography", "_decoration", "_svgPaint",
        // The font depends only on inherited properties, so the parent's cached font is the child's too.
        "_font",
        // Copied, but only read when vertical-align is a length, which the child resets.
        "VerticalAlignLength",
        // Copied, and recomputed by the cascade for every box.
        "InlineFade",
    ];

    // The fields CreateChild resets to their initial values. The three groups hold the rarely set ones.
    private static readonly string[] Reset =
    [
        "Display", "InlineTable", "Contents", "UnicodeBidi", "BackgroundColor", "CanvasBackground",
        "MarginTop", "MarginRight", "MarginBottom", "MarginLeft", "PaddingTop", "PaddingRight", "PaddingBottom", "PaddingLeft",
        "BorderTopWidth", "BorderRightWidth", "BorderBottomWidth", "BorderLeftWidth",
        "BorderTopStyle", "BorderRightStyle", "BorderBottomStyle", "BorderLeftStyle", "BorderHiddenSides",
        "BorderTopColor", "BorderRightColor", "BorderBottomColor", "BorderLeftColor", "BorderRadii", "CornerShapes",
        "Width", "Height", "MinWidth", "MaxWidth", "MinHeight", "MaxHeight", "BorderBox", "VerticalAlign",
        "PageBreakBefore", "PageBreakAfter", "AvoidBreakInside", "AvoidBreakBefore", "AvoidBreakAfter",
        "Floated", "Float", "ClearLeft", "ClearRight", "Opacity", "Position", "Top", "Right", "Bottom", "Left",
        "FormattingRoot", "OverflowX", "OverflowY",
        "_visual", "_containers", "_rare",
    ];

    // The fields CreateChild sets from the parent itself rather than copying or resetting them.
    private static readonly string[] SetFromParent = ["ParentFontSize", "ParentBaselineShift", "ParentStyle"];

    private static readonly FieldInfo[] Fields = typeof(ComputedStyle)
        .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

    [Fact]
    public void Every_field_is_either_inherited_or_reset()
    {
        var named = Inherited.Concat(Reset).Concat(SetFromParent).ToList();
        Assert.Equal(named.Count, named.Distinct(StringComparer.Ordinal).Count());
        var unnamed = Fields.Select(Name).Except(named, StringComparer.Ordinal).ToList();
        Assert.True(unnamed.Count == 0,
            "Decide whether these ComputedStyle fields inherit, reset them in CreateChild if they do not, and add them to a list here: "
            + string.Join(", ", unnamed));
        var gone = named.Except(Fields.Select(Name), StringComparer.Ordinal).ToList();
        Assert.True(gone.Count == 0, "These names are no longer ComputedStyle fields: " + string.Join(", ", gone));
    }

    [Fact]
    public void CreateChild_copies_the_inherited_fields_and_resets_the_rest()
    {
        // Every field of the parent differs from what a child of a fresh style gets, so the child's value shows whether
        // CreateChild copied the field or reset it.
        var initial = new ComputedStyle().CreateChild();
        var parent = new ComputedStyle();
        foreach (var f in Fields) f.SetValue(parent, Different(f.FieldType, f.GetValue(initial)));
        // An inline parent would make CreateChild work out its baseline shift from the placeholder font and groups above.
        Assert.NotEqual(Display.Inline, parent.Display);

        var child = parent.CreateChild();
        var copied = new List<string>();
        var reset = new List<string>();
        foreach (var f in Fields)
        {
            if (SetFromParent.Contains(Name(f), StringComparer.Ordinal)) continue;
            object? value = f.GetValue(child);
            if (Same(f.FieldType, value, f.GetValue(parent))) copied.Add(Name(f));
            else if (Same(f.FieldType, value, f.GetValue(initial))) reset.Add(Name(f));
            else Assert.Fail($"CreateChild neither copied nor reset {Name(f)}.");
        }

        Assert.Equal(Inherited.Order(StringComparer.Ordinal), copied.Order(StringComparer.Ordinal));
        Assert.Equal(Reset.Order(StringComparer.Ordinal), reset.Order(StringComparer.Ordinal));
        Assert.Same(parent, child.ParentStyle);
        Assert.Equal(parent.FontSize, child.ParentFontSize);
    }

    private static string Name(FieldInfo f)
    {
        // An auto-property's field is named <Name>k__BackingField.
        string name = f.Name;
        return name.StartsWith('<') ? name[1..name.IndexOf('>', StringComparison.Ordinal)] : name;
    }

    private static bool Same(Type type, object? a, object? b)
        => type.IsValueType || type == typeof(string) ? Equals(a, b) : ReferenceEquals(a, b);

    /// <summary>A value of <paramref name="type"/> that is not the same as <paramref name="current"/>.</summary>
    private static object? Different(Type type, object? current)
    {
        if (type == typeof(bool)) return !(bool)current!;
        if (type.IsEnum)
            return Enum.GetValues(type).Cast<object>().First(v => !v.Equals(current));
        if (type.IsPrimitive)
        {
            double d = Convert.ToDouble(current, CultureInfo.InvariantCulture);
            return Convert.ChangeType(d == 7 ? 8 : 7, type, CultureInfo.InvariantCulture);
        }
        if (type == typeof(string)) return current + "x";
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return current == null ? Different(underlying, Activator.CreateInstance(underlying)) : null;
        if (type.IsValueType)
        {
            // A struct such as Length: change its first field.
            object copy = current ?? Activator.CreateInstance(type)!;
            var first = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0];
            first.SetValue(copy, Different(first.FieldType, first.GetValue(copy)));
            return copy;
        }
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 1);
        if (type.IsInterface && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
            return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(type.GetGenericArguments()));
        // Any other class: a new instance, which is a different reference. Its fields are never read, so an abstract
        // class can be stood for by any type that derives from it.
        var concrete = type.IsAbstract ? type.Assembly.GetTypes().First(t => !t.IsAbstract && t.IsAssignableTo(type)) : type;
        return RuntimeHelpers.GetUninitializedObject(concrete);
    }
}
