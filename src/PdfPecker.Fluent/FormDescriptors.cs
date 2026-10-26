using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent;

/// <summary>The settings every form field has.</summary>
public abstract class FormFieldDescriptor<T> where T : FormFieldDescriptor<T>
{
    private protected FormFieldDescriptor(FormFieldElement field) => Field = field;

    private protected FormFieldElement Field { get; }

    /// <summary>Shows the field's value but does not let it be changed.</summary>
    public T ReadOnly(bool value = true)
    {
        Field.ReadOnly = value;
        return (T)this;
    }

    /// <summary>Marks the field as one that must be filled in before the form is sent.</summary>
    public T Required(bool value = true)
    {
        Field.Required = value;
        return (T)this;
    }

    /// <summary>A short description that PDF viewers show over the field, and screen readers and PDF/UA read out.</summary>
    public T Tooltip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Field.Tooltip = text;
        return (T)this;
    }
}

/// <summary>Sets up a text field.</summary>
public sealed class TextFieldDescriptor : FormFieldDescriptor<TextFieldDescriptor>
{
    internal TextFieldDescriptor(FormFieldElement field) : base(field) { }

    /// <summary>The text the field starts with.</summary>
    public TextFieldDescriptor Value(string? text)
    {
        Field.Value = text;
        return this;
    }

    /// <summary>The most characters the field takes.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="characters"/> is not 1 to 100,000.</exception>
    public TextFieldDescriptor MaxLength(int characters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(characters, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(characters, 100_000);
        Field.MaxLength = characters;
        return this;
    }

    /// <summary>Lets the text run over several lines, with <paramref name="lines"/> lines shown.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lines"/> is not 1 to 1,000.</exception>
    public TextFieldDescriptor Multiline(int lines = 3)
    {
        Field.Rows = FormLimits.Rows(lines);
        return this;
    }

    /// <summary>Hides the characters typed into the field.</summary>
    public TextFieldDescriptor Password(bool value = true)
    {
        Field.Password = value;
        return this;
    }
}

/// <summary>Sets up a check box.</summary>
public sealed class CheckBoxDescriptor : FormFieldDescriptor<CheckBoxDescriptor>
{
    internal CheckBoxDescriptor(FormFieldElement field) : base(field) { }

    /// <summary>Ticks the box.</summary>
    public CheckBoxDescriptor Checked(bool value = true)
    {
        Field.Checked = value;
        return this;
    }

    /// <summary>The value the field holds when the box is ticked ("Yes" unless set).</summary>
    public CheckBoxDescriptor ExportValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Field.Value = value;
        return this;
    }

    /// <summary>The width and height of the box (about 10 points by default).</summary>
    public CheckBoxDescriptor Size(float value, Unit unit = Unit.Point)
    {
        Field.Size = Length.Points(value, unit, nameof(value));
        return this;
    }
}

/// <summary>Sets up a radio button.</summary>
public sealed class RadioButtonDescriptor : FormFieldDescriptor<RadioButtonDescriptor>
{
    internal RadioButtonDescriptor(FormFieldElement field) : base(field) { }

    /// <summary>Selects this button of its group.</summary>
    public RadioButtonDescriptor Checked(bool value = true)
    {
        Field.Checked = value;
        return this;
    }

    /// <summary>The width and height of the button (about 10 points by default).</summary>
    public RadioButtonDescriptor Size(float value, Unit unit = Unit.Point)
    {
        Field.Size = Length.Points(value, unit, nameof(value));
        return this;
    }
}

/// <summary>Sets up a combo box or list box: its options and which are selected.</summary>
public sealed class ChoiceFieldDescriptor : FormFieldDescriptor<ChoiceFieldDescriptor>
{
    internal ChoiceFieldDescriptor(FormFieldElement field) : base(field) { }

    /// <summary>
    /// An option, shown as <paramref name="text"/>. The field holds <paramref name="value"/> when it is selected, or the
    /// text when no value is given.
    /// </summary>
    public ChoiceFieldDescriptor Option(string text, string? value = null, bool selected = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Field.Options.Count >= FormLimits.MaxOptions)
            throw new InvalidOperationException($"A field can have at most {FormLimits.MaxOptions:N0} options.");
        Field.Options.Add((text, value, selected));
        return this;
    }

    /// <summary>Lets a list box have several options selected. A combo box always has one.</summary>
    public ChoiceFieldDescriptor MultiSelect(bool value = true)
    {
        Field.MultiSelect = value;
        return this;
    }

    /// <summary>The options a list box shows at once (4 by default).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> is not 1 to 1,000.</exception>
    public ChoiceFieldDescriptor Rows(int rows)
    {
        Field.Rows = FormLimits.Rows(rows);
        return this;
    }
}

internal static class FormLimits
{
    public const int MaxOptions = 10_000;

    public static int Rows(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, 1_000);
        return rows;
    }
}
