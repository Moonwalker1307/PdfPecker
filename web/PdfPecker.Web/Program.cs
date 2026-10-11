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

// POST /api/pdf-to-image
//   multipart/form-data with the PDF in the "file" field, or the raw PDF as the body.
//   Query (or form) options: page (1-based; omit for all pages), dpi (10-300, default 150), width, height, password.
// Returns image/png for one page, or application/zip with one PNG per page.
app.MapPost("/api/pdf-to-image", async (HttpRequest request, CancellationToken ct) =>
{
    const long MaxPdfBytes = 25 * 1024 * 1024;
    const int MaxPages = 100;
    if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        limit.MaxRequestBodySize = MaxPdfBytes;
    byte[] pdf;
    Microsoft.AspNetCore.Http.IFormCollection? form = null;
    string? fileName = null;
    // Only multipart bodies are forms here: a raw PDF posted without a content type is not url-encoded data.
    if (request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true)
    {
        form = await request.ReadFormAsync(ct);
        var file = form.Files["file"] ?? form.Files.FirstOrDefault();
        if (file == null) return Results.Problem("Upload a PDF in the \"file\" field.", statusCode: StatusCodes.Status400BadRequest);
        fileName = file.FileName;
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        pdf = ms.ToArray();
    }
    else
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        pdf = ms.ToArray();
    }
    string? Field(string name) => form != null && form.TryGetValue(name, out var v) ? v.ToString() : request.Query[name].ToString();
    static int? Int(string? s) => int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    var options = new ImageOptions
    {
        Dpi = Math.Clamp(float.TryParse(Field("dpi"), System.Globalization.CultureInfo.InvariantCulture, out var dpi) ? dpi : 150, 10, 300),
        Width = Int(Field("width")) is int w ? Math.Clamp(w, 1, 6000) : null,
        Height = Int(Field("height")) is int h ? Math.Clamp(h, 1, 6000) : null,
        Password = string.IsNullOrEmpty(Field("password")) ? null : Field("password"),
        MaxDimension = 6000,
        MaxDegreeOfParallelism = 2,
    };
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(30));
    try
    {
        var renderer = PdfRenderer.Open(pdf, options.Password);
        var name = Path.GetFileNameWithoutExtension(SafeFileName(fileName));
        if (Int(Field("page")) is int page || renderer.PageCount == 1)
        {
            int index = (Int(Field("page")) ?? 1) - 1;
            if (index < 0 || index >= renderer.PageCount)
                return Results.Problem($"The PDF has {renderer.PageCount} page(s).", statusCode: StatusCodes.Status400BadRequest);
            var png = await Task.Run(() => renderer.RenderPageToPng(index, options, timeout.Token), timeout.Token);
            return Results.File(png, "image/png", $"{name}-{index + 1}.png");
        }
        if (renderer.PageCount > MaxPages)
            return Results.Problem($"At most {MaxPages} pages per request; pass ?page=N for single pages.", statusCode: StatusCodes.Status400BadRequest);
        var pngs = await Task.Run(() => renderer.RenderAllToPng(options, timeout.Token), timeout.Token);
        var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            for (int i = 0; i < pngs.Length; i++)
            {
                // PNGs are already compressed.
                var entry = archive.CreateEntry($"{name}-{i + 1}.png", System.IO.Compression.CompressionLevel.NoCompression);
                using var es = entry.Open();
                es.Write(pngs[i]);
            }
        return Results.File(zip.ToArray(), "application/zip", name + "-pages.zip");
    }
    catch (PdfPasswordException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem("The document took too long to render.", statusCode: StatusCodes.Status422UnprocessableEntity);
    }
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
