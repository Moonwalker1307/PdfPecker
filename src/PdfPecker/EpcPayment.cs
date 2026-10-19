using System.Globalization;
using System.Text;

namespace PdfPecker;

/// <summary>
/// A euro credit transfer for <see cref="Barcode.EpcQrCode"/>, following the European Payments Council's guideline
/// EPC069-12 (version 002, UTF-8). Set <see cref="Name"/> and <see cref="Iban"/>; the rest is optional.
/// </summary>
public sealed class EpcPayment
{
    /// <summary>The beneficiary's name, up to 70 characters.</summary>
    public string Name { get; set; } = "";

    /// <summary>The beneficiary's IBAN. Spaces are allowed and removed; the check digits are verified.</summary>
    public string Iban { get; set; } = "";

    /// <summary>The beneficiary bank's BIC (8 or 11 characters), or null. Optional within the EEA.</summary>
    public string? Bic { get; set; }

    /// <summary>The amount in euros, from 0.01 to 999,999,999.99 with at most two decimals, or null to let the payer enter it.</summary>
    public decimal? Amount { get; set; }

    /// <summary>A four-letter ISO 20022 purpose code such as "SUPP", or null.</summary>
    public string? Purpose { get; set; }

    /// <summary>
    /// A structured creditor reference (ISO 11649, "RF" and up to 33 more characters), or null. Use either this or
    /// <see cref="Text"/>, not both.
    /// </summary>
    public string? Reference { get; set; }

    /// <summary>An unstructured message to the beneficiary, such as an invoice number, up to 140 characters, or null.</summary>
    public string? Text { get; set; }

    /// <summary>A note shown to the payer in the banking app, up to 70 characters, or null.</summary>
    public string? Information { get; set; }

    /// <summary>The text the QR code holds: one field per line, as the guideline lays it out.</summary>
    /// <exception cref="ArgumentException">A field is missing, too long or invalid.</exception>
    public string ToPayload()
    {
        var name = Required(Name, nameof(Name), 70);
        var iban = (Iban ?? "").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (!IsValidIban(iban))
            throw new ArgumentException($"\"{Iban}\" is not a valid IBAN.", nameof(Iban));

        var bic = Bic?.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant() ?? "";
        if (bic.Length != 0 && (bic.Length is not (8 or 11) || !bic.All(char.IsAsciiLetterOrDigit)))
            throw new ArgumentException($"\"{Bic}\" is not a BIC: it has 8 or 11 letters and digits.", nameof(Bic));

        var amount = "";
        if (Amount is { } value)
        {
            if (value < 0.01m || value > 999_999_999.99m || decimal.Round(value, 2) != value)
                throw new ArgumentException("The amount must be from 0.01 to 999,999,999.99 euros with at most two decimals.", nameof(Amount));
            amount = "EUR" + value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        var purpose = Purpose ?? "";
        if (purpose.Length != 0 && (purpose.Length != 4 || !purpose.All(char.IsAsciiLetterUpper)))
            throw new ArgumentException($"\"{Purpose}\" is not a purpose code: it has four capital letters.", nameof(Purpose));

        var reference = Optional(Reference, nameof(Reference), 35);
        var text = Optional(Text, nameof(Text), 140);
        if (reference.Length != 0 && text.Length != 0)
            throw new ArgumentException("Set either a structured reference or a text, not both.", nameof(Reference));
        var information = Optional(Information, nameof(Information), 70);

        var payload = string.Join("\n", "BCD", "002", "1", "SCT", bic, name, iban, amount, purpose, reference, text, information).TrimEnd('\n');
        if (Encoding.UTF8.GetByteCount(payload) > 331)
            throw new ArgumentException("The payment's fields are longer than the 331 bytes an EPC QR code may hold.", nameof(Text));
        return payload;
    }

    private static string Required(string? value, string field, int max)
    {
        var result = Optional(value, field, max);
        return result.Length != 0 ? result : throw new ArgumentException($"{field} is required.", field);
    }

    private static string Optional(string? value, string field, int max)
    {
        var result = value?.Trim() ?? "";
        if (result.Length > max)
            throw new ArgumentException($"{field} has {result.Length} characters; at most {max} are allowed.", field);
        if (result.Any(char.IsControl))
            throw new ArgumentException($"{field} must not contain line breaks or other control characters.", field);
        return result;
    }

    /// <summary>Checks the length, the characters and the mod-97 check digits of ISO 13616.</summary>
    internal static bool IsValidIban(string iban)
    {
        if (iban.Length is < 15 or > 34 || !char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1])
            || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3]) || !iban.All(char.IsAsciiLetterOrDigit))
            return false;
        var remainder = 0;
        for (var i = 0; i < iban.Length; i++)
        {
            var c = iban[(i + 4) % iban.Length];
            remainder = char.IsAsciiDigit(c)
                ? (remainder * 10 + (c - '0')) % 97
                : (remainder * 100 + (c - 'A' + 10)) % 97;
        }
        return remainder == 1;
    }
}
