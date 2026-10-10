using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PdfPecker;

const long MaxHtmlBytes = 5 * 1024 * 1024;
var conversionTimeout = TimeSpan.FromSeconds(15);

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxHtmlBytes);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("convert", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Text("ok"));

// POST /api/convert
//   multipart/form-data: an uploaded HTML file in the "file" field; options as form fields or query string
//   application/json:    { "html": "...", "pageSize": "A4", "landscape": false, "marginMm": 12.7, "pageNumbers": true }
//   anything else:       the raw HTML as the body, options in the query string (?pageSize=Letter&landscape=true&marginMm=10&pageNumbers=true)
// Returns application/pdf.
app.MapPost("/api/convert", async (HttpRequest request, CancellationToken ct) =>
{
    ConvertRequest? req;
    if (request.HasJsonContentType())
    {
        req = await request.ReadFromJsonAsync(AppJsonContext.Default.ConvertRequest, ct);
    }
    else if (request.HasFormContentType)
    {
        var form = await request.ReadFormAsync(ct);
        var file = form.Files["file"] ?? form.Files.FirstOrDefault();
        string html;
        if (file != null)
        {
            using var fileReader = new StreamReader(file.OpenReadStream());
            html = await fileReader.ReadToEndAsync(ct);
        }
        else html = form["html"].ToString();
        string? Field(string name) => form.TryGetValue(name, out var v) ? v.ToString() : request.Query[name].ToString();
        req = new ConvertRequest
        {
            Html = html,
            FileName = file?.FileName,
            PageSize = Field("pageSize"),
            Landscape = bool.TryParse(Field("landscape"), out var fl) && fl,
            MarginMm = float.TryParse(Field("marginMm"), System.Globalization.CultureInfo.InvariantCulture, out var fm) ? fm : null,
            PageNumbers = bool.TryParse(Field("pageNumbers"), out var fp) && fp,
        };
    }
    else
    {
        using var reader = new StreamReader(request.Body);
        var q = request.Query;
        req = new ConvertRequest
        {
            Html = await reader.ReadToEndAsync(ct),
            PageSize = q["pageSize"],
            Landscape = bool.TryParse(q["landscape"], out var l) && l,
            MarginMm = float.TryParse(q["marginMm"], System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : null,
            PageNumbers = bool.TryParse(q["pageNumbers"], out var p) && p,
        };
    }
    if (req is null || string.IsNullOrWhiteSpace(req.Html))
        return Results.Problem("Send some HTML to convert.", statusCode: StatusCodes.Status400BadRequest);

    var options = BuildOptions(req);
    var sw = Stopwatch.StartNew();
    byte[] pdf;
    try
    {
        pdf = await Task.Run(() => HtmlToPdf.Convert(req.Html, options), ct).WaitAsync(conversionTimeout, ct);
    }
    catch (TimeoutException)
    {
        return Results.Problem("The document took too long to convert.", statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
    }
    sw.Stop();
    request.HttpContext.Response.Headers["X-Conversion-Ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    return Results.File(pdf, "application/pdf", SafeFileName(req.FileName));
}).RequireRateLimiting("convert").DisableAntiforgery();

app.Run();

static PdfOptions BuildOptions(ConvertRequest req)
{
    var size = (req.PageSize ?? "A4").ToUpperInvariant() switch
    {
        "A3" => PageSize.A3,
        "A5" => PageSize.A5,
        "LETTER" => PageSize.Letter,
        "LEGAL" => PageSize.Legal,
        _ => PageSize.A4,
    };
    if (req.Landscape) size = size.Landscape;
    float margin = Math.Clamp(req.MarginMm ?? 12.7f, 0, 50) * 72f / 25.4f;
    return new PdfOptions
    {
        PageSize = size,
        Margins = new PageMargins(margin),
        FooterText = req.PageNumbers ? "Page {page} of {pages}" : null,
        // The HTML comes from anyone on the internet: never let it read files from this server.
        AllowFileAccess = false,
    };
}

static string SafeFileName(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return "document.pdf";
    var clean = new string(Path.GetFileNameWithoutExtension(name).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').ToArray()).Trim();
    return (clean.Length == 0 ? "document" : clean) + ".pdf";
}

public sealed class ConvertRequest
{
    public string Html { get; set; } = "";
    public string? PageSize { get; set; }
    public bool Landscape { get; set; }
    public float? MarginMm { get; set; }
    public bool PageNumbers { get; set; }
    public string? FileName { get; set; }
}

[JsonSerializable(typeof(ConvertRequest))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class AppJsonContext : JsonSerializerContext;
