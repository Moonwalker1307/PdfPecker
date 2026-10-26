using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent;

public static partial class ContentExtensions
{
    // Form fields. QuestPDF has none; PdfPecker turns HTML form controls into fillable PDF fields, which the
    // PdfOptions.FormFields setting can also turn off.

    /// <summary>
    /// A fillable text field as wide as the slot, in the slot's text style. PDF form fields with the same name share
    /// their value.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public static TextFieldDescriptor TextField(this IContainer container, string name)
        => new(Field(container, FormFieldKind.Text, name));

    /// <summary>A check box that can be ticked in a PDF viewer.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public static CheckBoxDescriptor CheckBox(this IContainer container, string name)
        => new(Field(container, FormFieldKind.CheckBox, name));

    /// <summary>
    /// A radio button: of the buttons with the same <paramref name="group"/>, one can be selected, and the field then
    /// holds that button's <paramref name="value"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="group"/> or <paramref name="value"/> is empty.</exception>
    public static RadioButtonDescriptor RadioButton(this IContainer container, string group, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var field = Field(container, FormFieldKind.RadioButton, group);
        field.Value = value;
        return new RadioButtonDescriptor(field);
    }

    /// <summary>A drop-down list to pick one option from, as wide as the slot.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public static ChoiceFieldDescriptor ComboBox(this IContainer container, string name)
        => new(Field(container, FormFieldKind.ComboBox, name));

    /// <summary>A list of options that shows several at once, as wide as the slot.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public static ChoiceFieldDescriptor ListBox(this IContainer container, string name)
    {
        var field = Field(container, FormFieldKind.ListBox, name);
        field.Rows = 4;
        return new ChoiceFieldDescriptor(field);
    }

    private static FormFieldElement Field(IContainer container, FormFieldKind kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Container.Of(container).Set(new FormFieldElement(kind, name));
    }
}
