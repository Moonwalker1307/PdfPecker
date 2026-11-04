using System.Globalization;
using System.Text;

namespace PdfPecker;

/// <summary>
/// A Swiss QR-bill payment for <see cref="Barcode.SwissQrCode"/>, following the Swiss Payment Standards' implementation
/// guidelines for the QR-bill (version 2.3, structured addresses). Set <see cref="Account"/> and
/// <see cref="Creditor"/>; the rest is optional.
/// </summary>
public sealed class SwissQrBill
{
    /// <summary>
    /// The creditor's IBAN or QR-IBAN, from Switzerland (CH) or Liechtenstein (LI). Spaces are allowed and removed; the
    /// check digits are verified. A QR-IBAN (bank code 30000 to 31999) needs a QR reference in <see cref="Reference"/>.
    /// </summary>
    public string Account { get; set; } = "";

    /// <summary>The creditor, who receives the payment. Required.</summary>
    public SwissQrAddress? Creditor { get; set; }

    /// <summary>The amount, from 0.01 to 999,999,999.99 with at most two decimals, or null to let the payer enter it.</summary>
    public decimal? Amount { get; set; }

    /// <summary>The currency: "CHF" (the default) or "EUR".</summary>
    public string Currency { get; set; } = "CHF";

    /// <summary>The debtor, who pays, or null to leave the payer's address to be filled in by hand.</summary>
    public SwissQrAddress? Debtor { get; set; }

    /// <summary>
    /// The payment reference, or null for none. With a QR-IBAN it is a QR reference of 27 digits whose last digit is
    /// the modulo 10 recursive check digit; with an ordinary IBAN it is a creditor reference (ISO 11649, "RF" and its
    /// check digits) or nothing. Spaces are allowed and removed.
    /// </summary>
    public string? Reference { get; set; }

    /// <summary>An unstructured message to the creditor, such as an invoice number, or null.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// Structured bill information for the creditor's software, such as Swico's "//S1/10/..." syntax, or null. It and
    /// <see cref="Message"/> share 140 characters.
    /// </summary>
    public string? BillInformation { get; set; }

    /// <summary>
    /// The text the QR code holds: the "SPC" header, version 0200 and one field per line, as the guidelines lay it out.
    /// </summary>
    /// <exception cref="ArgumentException">A field is missing, too long, has a character the standard does not allow, or is invalid.</exception>
    public string ToPayload() => Validate().Payload;

    /// <summary>Checks every field and returns the payload with the cleaned-up account, reference type and reference.</summary>
    internal (string Payload, string Account, string ReferenceType, string Reference) Validate()
    {
        var account = (Account ?? "").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (account.Length != 21 || !(account.StartsWith("CH", StringComparison.Ordinal) || account.StartsWith("LI", StringComparison.Ordinal))
            || !EpcPayment.IsValidIban(account))
            throw new ArgumentException($"\"{Account}\" is not a Swiss or Liechtenstein IBAN.", nameof(Account));
        var qrIban = int.Parse(account.AsSpan(4, 5), NumberStyles.None, CultureInfo.InvariantCulture) is >= 30000 and <= 31999;

        if (Creditor is null)
            throw new ArgumentException("The creditor is required.", nameof(Creditor));
        var creditor = Creditor.Fields(nameof(Creditor));
        var debtor = Debtor?.Fields(nameof(Debtor)) ?? new string[7];

        var amount = "";
        if (Amount is { } value)
        {
            if (value < 0.01m || value > 999_999_999.99m || decimal.Round(value, 2) != value)
                throw new ArgumentException("The amount must be from 0.01 to 999,999,999.99 with at most two decimals.", nameof(Amount));
            amount = value.ToString("0.00", CultureInfo.InvariantCulture);
        }
        if (Currency is not ("CHF" or "EUR"))
            throw new ArgumentException($"\"{Currency}\" is not a currency of the QR-bill: use CHF or EUR.", nameof(Currency));

        var reference = (Reference ?? "").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        string referenceType;
        if (qrIban)
        {
            if (reference.Length != 27 || reference.AsSpan().ContainsAnyExceptInRange('0', '9') || !HasQrCheckDigit(reference))
                throw new ArgumentException("A QR-IBAN needs a QR reference: 27 digits ending in a modulo 10 recursive check digit.", nameof(Reference));
            referenceType = "QRR";
        }
        else if (reference.Length != 0)
        {
            if (!IsCreditorReference(reference))
                throw new ArgumentException($"\"{Reference}\" is not a creditor reference (ISO 11649): an ordinary IBAN takes an \"RF\" reference or none.", nameof(Reference));
            referenceType = "SCOR";
        }
        else
        {
            referenceType = "NON";
        }

        var message = Clean(Message, nameof(Message), 140);
        var billInformation = Clean(BillInformation, nameof(BillInformation), 140);
        if (message.Length + billInformation.Length > 140)
            throw new ArgumentException("The message and the bill information have more than 140 characters together.", nameof(Message));

        var fields = new List<string>(32) { "SPC", "0200", "1", account };
        fields.AddRange(creditor);
        fields.AddRange(new string[7]); // the ultimate creditor, which is reserved for future use and left empty
        fields.Add(amount);
        fields.Add(Currency);
        fields.AddRange(debtor.Select(f => f ?? ""));
        fields.Add(referenceType);
        fields.Add(reference);
        fields.Add(message);
        fields.Add("EPD");
        if (billInformation.Length != 0)
            fields.Add(billInformation);
        return (string.Join("\n", fields), account, referenceType, reference);
    }

    internal static string Clean(string? value, string field, int max)
    {
        var result = value?.Trim() ?? "";
        if (result.Length > max)
            throw new ArgumentException($"{field} has {result.Length} characters; at most {max} are allowed.", field);
        foreach (var c in result)
        {
            // The guidelines' character set: printable Basic Latin, Latin-1 Supplement and Latin Extended-A, the
            // Romanian letters with a comma below, and the euro sign.
            if (c is not ((>= ' ' and <= '~') or (>= ' ' and <= 'ſ') or (>= 'Ș' and <= 'ț') or '€'))
                throw new ArgumentException($"{field} has a character the QR-bill does not allow: U+{(int)c:X4}.", field);
        }
        return result;
    }

    /// <summary>The modulo 10 recursive check digit of QR and ISR references.</summary>
    internal static bool HasQrCheckDigit(string digits)
    {
        ReadOnlySpan<byte> table = [0, 9, 4, 6, 8, 2, 7, 1, 3, 5];
        var carry = 0;
        foreach (var c in digits.AsSpan(0, digits.Length - 1))
            carry = table[(carry + c - '0') % 10];
        return (10 - carry) % 10 == digits[^1] - '0';
    }

    /// <summary>Checks an ISO 11649 creditor reference: "RF", two check digits and up to 21 letters and digits.</summary>
    internal static bool IsCreditorReference(string reference)
    {
        if (reference.Length is < 5 or > 25 || !reference.StartsWith("RF", StringComparison.Ordinal)
            || !char.IsAsciiDigit(reference[2]) || !char.IsAsciiDigit(reference[3]) || !reference.All(char.IsAsciiLetterOrDigit))
            return false;
        var remainder = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            var c = reference[(i + 4) % reference.Length];
            remainder = char.IsAsciiDigit(c)
                ? (remainder * 10 + (c - '0')) % 97
                : (remainder * 100 + (c - 'A' + 10)) % 97;
        }
        return remainder == 1;
    }
}

/// <summary>
/// A structured postal address of a <see cref="SwissQrBill"/>. Set <see cref="Name"/>, <see cref="PostalCode"/>,
/// <see cref="Town"/> and <see cref="Country"/>; the street and house number are optional.
/// </summary>
public sealed class SwissQrAddress
{
    /// <summary>The name of the person or company, up to 70 characters.</summary>
    public string Name { get; set; } = "";

    /// <summary>The street, up to 70 characters, or null.</summary>
    public string? Street { get; set; }

    /// <summary>The house number, up to 16 characters, or null.</summary>
    public string? HouseNumber { get; set; }

    /// <summary>The postal code without a country prefix, up to 16 characters.</summary>
    public string PostalCode { get; set; } = "";

    /// <summary>The town, up to 35 characters.</summary>
    public string Town { get; set; } = "";

    /// <summary>The two-letter ISO 3166 country code, such as "CH".</summary>
    public string Country { get; set; } = "CH";

    /// <summary>The seven address fields of the payload, starting with the address type "S" for structured.</summary>
    internal string[] Fields(string role)
    {
        var name = SwissQrBill.Clean(Name, role + "." + nameof(Name), 70);
        var postalCode = SwissQrBill.Clean(PostalCode, role + "." + nameof(PostalCode), 16);
        var town = SwissQrBill.Clean(Town, role + "." + nameof(Town), 35);
        if (name.Length == 0 || postalCode.Length == 0 || town.Length == 0)
            throw new ArgumentException($"{role} needs a name, a postal code and a town.", role);
        var country = (Country ?? "").Trim().ToUpperInvariant();
        if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
            throw new ArgumentException($"\"{Country}\" is not a two-letter country code.", role + "." + nameof(Country));
        return new[]
        {
            "S", name, SwissQrBill.Clean(Street, role + "." + nameof(Street), 70),
            SwissQrBill.Clean(HouseNumber, role + "." + nameof(HouseNumber), 16), postalCode, town, country,
        };
    }
}
