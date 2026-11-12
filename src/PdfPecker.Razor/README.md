# PdfPecker.Razor

Razor components (`.razor` files) to PDF with [PdfPecker](https://www.nuget.org/packages/PdfPecker). Write a document
template as a Razor component, with typed parameters, `@foreach`, child components, layouts and dependency injection,
and get a PDF from it in one call. The component is rendered to HTML with Microsoft's `HtmlRenderer`, the static
renderer ASP.NET Core uses for server-side rendering, and PdfPecker converts the HTML. There is no browser, no
ASP.NET Core host and no JavaScript, so it works the same in a console app, a background worker or a web app.

```
dotnet add package PdfPecker.Razor
```

PdfPecker.Razor is released together with PdfPecker and always has the same version. It depends on the PdfPecker
release of that version and on `Microsoft.AspNetCore.Components.Web` of the app's .NET version (8.0, 9.0 or 10.0), which
holds `HtmlRenderer`. 1.25.0 is the last release for .NET 8 and 9; the next release needs .NET 10 or
later.

The project that holds the `.razor` files uses the Razor SDK, so they compile to C# classes:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
```

An invoice template, `Invoice.razor`:

```razor
@using System.Globalization

<!DOCTYPE html>
<html>
<head>
    <style>
        @@page { size: A4; margin: 20mm; }
        td.amount { text-align: right; }
    </style>
</head>
<body>
    <h1>Invoice @Data.Number</h1>
    <table>
        @foreach (var line in Data.Lines)
        {
            <tr><td>@line.Description</td><td class="amount">@line.Amount.ToString("N2", CultureInfo.InvariantCulture)</td></tr>
        }
    </table>
</body>
</html>

@code {
    [Parameter, EditorRequired] public InvoiceModel Data { get; set; } = default!;
}
```

and the code that turns it into a PDF:

```csharp
using PdfPecker;
using PdfPecker.Razor;

var parameters = new Dictionary<string, object?> { [nameof(Invoice.Data)] = invoice };
byte[] pdf = await RazorToPdf.ConvertAsync<Invoice>(parameters);

// Every PdfPecker option works: page size, fonts, PDF/A, signatures, e-invoices
byte[] archived = await RazorToPdf.ConvertAsync<Invoice>(parameters, new PdfOptions { Conformance = PdfConformance.PdfA2b });

// Straight to an ASP.NET Core response, with the request's services for components that @inject them
app.MapGet("/invoice/{id}", async (int id, HttpContext context, InvoiceStore store) =>
{
    var parameters = new Dictionary<string, object?> { ["Data"] = await store.LoadAsync(id) };
    context.Response.ContentType = "application/pdf";
    await RazorToPdf.ConvertAsync<Invoice>(context.Response.Body, parameters, services: context.RequestServices,
        cancellationToken: context.RequestAborted);
});

// The HTML the component renders to, to open in a browser while working on the template
string html = await RazorToPdf.RenderHtmlAsync<Invoice>(parameters);
```

A complete example, with a layout component and a child component for the line table, is in
[samples/PdfPecker.Razor.Sample](https://github.com/Moonwalker1307/PdfPecker/tree/main/samples/PdfPecker.Razor.Sample).

## What works

- **Typed templates.** Parameters are C# properties, so a misspelt field or a wrong type is a compile error, and the
  editor completes them. A parameter name that has no `[Parameter]` property fails with `InvalidOperationException`.
- **Child components and layouts.** Split a document into components (address block, line table, totals) and pass
  data down with parameters or `CascadingValue`. A layout is a component with `ChildContent`, or a
  `LayoutComponentBase` used through `LayoutView`.
- **Dependency injection.** `@inject` and constructor injection take services from the `IServiceProvider` given as
  `services`, such as an ASP.NET Core app's `RequestServices`. An `ILoggerFactory` among them receives the renderer's
  log. Without `services` there are none, and a component that injects one fails with a message that names it.
- **Asynchronous data.** Work in `OnInitializedAsync` and `OnParametersSetAsync` is awaited before the HTML is taken.
- **Everything PdfPecker does.** The HTML goes to `HtmlToPdf`, so the CSS, `@page` rules, running headers and footers,
  page numbers, fonts and `PdfOptions` (PDF/A, PDF/UA, signing, encryption, e-invoices) are all available. Relative
  image paths resolve against `PdfOptions.BaseDirectory`, and other resources load through `PdfOptions.ResourceResolver`.
- **Same output.** `ConvertAsync` gives byte for byte the PDF that `HtmlToPdf.ConvertAsync` gives for the HTML of
  `RenderHtmlAsync`, so a fixed `CreationDate` makes the output reproducible.

## Safety

Razor encodes every value it writes into the markup, so names, addresses and other caller data cannot become HTML. Only
a `MarkupString` inserts HTML as it is, so use it only for markup you trust. PdfPecker runs no scripts and fetches
nothing over the network, as always.

Each call renders in an `HtmlRenderer` of its own, which is disposed with the component when the call returns, so
nothing is kept between conversions and parallel calls are safe. An exception that the component throws reaches the
caller unchanged. The cancellation token stops the PDF conversion; the component's own rendering is not interrupted, so
pass the token to it as a parameter when it does slow work of its own.

Components render once and statically, as in ASP.NET Core's static server-side rendering: event handlers, timers and
JavaScript interop do not run, and services that only an ASP.NET Core host provides, such as `NavigationManager`, are
available only when they are in `services`.

## Trimming and Native AOT

PdfPecker.Razor is trimmable and marked AOT-compatible, and CI publishes the sample with Native AOT and checks the PDF it
writes. Microsoft's `Microsoft.AspNetCore.Components` itself reports trim warnings from its own code, folded into one
`IL2104` warning when you publish trimmed or with Native AOT; Blazor WebAssembly turns those warnings off for the same
reason. The component types are kept all the same, because each one reaches the renderer through an annotated
parameter. The sample's project file shows how to ignore only that assembly's warning, so a warning from anywhere else
still fails the publish.

`@layout` and `LayoutView` set a layout's type by reflection, which the trim analyzer warns about in your own
project. A layout written as a component with `ChildContent`, as in the sample, avoids that.

## Performance

Rendering the sample's invoice component with 20 lines takes about 0.03 ms, against about 0.5 ms for PdfPecker's
conversion of the HTML, measured on a 4-core cloud VM on .NET 10. The package adds about 1.4 MB of Microsoft
assemblies to an app that does not already have ASP.NET Core; an ASP.NET Core app usually takes them from its shared
framework.

## Source

PdfPecker.Razor lives in the [PdfPecker repository](https://github.com/Moonwalker1307/PdfPecker) under
`src/PdfPecker.Razor`, with its tests in `tests/PdfPecker.Razor.Tests`. Issues and pull requests go there.

## License

MIT, like PdfPecker.
