namespace PdfPecker.Razor.Sample;

/// <summary>The data the invoice template shows; a real app would load it from its database.</summary>
public sealed record InvoiceModel(string Number, DateOnly Date, string Customer, string CustomerAddress, IReadOnlyList<LineItem> Lines)
{
    public decimal Total => Lines.Sum(line => line.Amount);
}

public sealed record LineItem(string Description, decimal Quantity, decimal UnitPrice)
{
    public decimal Amount => Quantity * UnitPrice;
}
