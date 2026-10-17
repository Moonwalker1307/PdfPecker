namespace PdfPecker.Markdown;

/// <summary>The destination and title of a link reference definition such as <c>[label]: /url "title"</c>.</summary>
internal readonly record struct LinkReference(string Destination, string Title);
