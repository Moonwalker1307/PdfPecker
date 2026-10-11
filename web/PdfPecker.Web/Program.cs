using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PdfPecker;

const long MaxHtmlBytes = 5 * 1024 * 1024;
// Untrusted documents: a long one is refused instead of filling memory (a few bytes of CSS can ask for millions of pages).
const int MaxPages = 1000;
var conversionTimeout = TimeSpan.FromSeconds(15);
// Conversions are CPU and memory heavy: run a few at a time and turn the rest away quickly, so a burst of hostile
// documents cannot exhaust the server. A large document can need about 512 MB while it is laid out, so the memory the
// runtime may use (the container's memory limit, when one is set) caps the count as well as the CPU count does.
long memoryLimit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
var conversions = new SemaphoreSlim((int)Math.Clamp(memoryLimit / (512L << 20), 1, Math.Max(2, Environment.ProcessorCount)));
var queueTimeout = TimeSpan.FromSeconds(10);

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = MaxHtmlBytes;
    k.AddServerHeader = false;
});
// An HTML form field may be as large as the body allows (the default 4 MB limit would fail with a server error).
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.ValueLengthLimit = (int)MaxHtmlBytes);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("convert", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(ctx.Connection.RemoteIpAddress),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

// Behind a reverse proxy or CDN every request comes from the proxy's address, so the rate limit would be shared by
// everyone. The TrustedProxies setting (or environment variable) lists the proxies' addresses or networks, e.g.
// "10.0.0.0/8,172.16.0.0/12"; X-Forwarded-For is believed only from those. Empty: the connection address is used.
var trustedProxies = (builder.Configuration["TrustedProxies"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (trustedProxies.Length > 0)
{
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (var p in trustedProxies)
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(p.Contains('/') ? p : p + (p.Contains(':') ? "/128" : "/32")));
    });
}

// The page loads its script and stylesheet from this site; previews are blob: frames. Inline styles stay allowed for
// the Markdown-to-HTML preview, which inherits this policy; inline script does not.
const string SiteCsp = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; "
    + "connect-src 'self'; frame-src blob:; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
// API responses are documents, never pages that load anything.
const string ApiCsp = "default-src 'none'; frame-ancestors 'none'";

var app = builder.Build();
if (trustedProxies.Length > 0) app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Content-Security-Policy"] = context.Request.Path.StartsWithSegments("/api") ? ApiCsp : SiteCsp;
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    try
    {
        await next(context);
    }
    catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
    {
        // Too large, malformed or too slowly sent request bodies: the client's fault, not a server error.
        context.Response.Clear();
        context.Response.StatusCode = ex.StatusCode;
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Text("ok"));

// POST /api/convert
//   multipart/form-data: an uploaded HTML or Markdown (.md) file in the "file" field; options as form fields or query string
//   application/json:    { "html": "...", "pageSize": "A4", "landscape": false, "marginMm": 12.7, "pageNumbers": true,
//                          "watermark": "DRAFT", "password": "secret", "pdfa": false }
//                        or { "markdown": "# Title", "output": "html" }
//   anything else:       the raw HTML or Markdown as the body, options in the query string
//                        (?pageSize=Letter&landscape=true&marginMm=10&pageNumbers=true&format=markdown&output=pdf)
// The input is Markdown when it comes as "markdown", from a .md/.markdown file, with Content-Type text/markdown, or with format=markdown.
// Returns application/pdf, or text/html for output=html (Markdown input only).
app.MapPost("/api/convert", async (HttpRequest request, CancellationToken ct) =>
{
    ConvertRequest? req;
    if (request.HasJsonContentType())
    {
        try
        {
            req = await request.ReadFromJsonAsync(AppJsonContext.Default.ConvertRequest, ct);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.Problem("The JSON body is not valid.", statusCode: StatusCodes.Status400BadRequest);
        }
    }
    else if (request.HasFormContentType)
    {
        var form = await request.ReadFormAsync(ct);
        var file = form.Files["file"] ?? form.Files.FirstOrDefault();
        string html = "";
        string? markdown = null;
        if (file != null)
        {
            using var fileReader = new StreamReader(file.OpenReadStream());
            var text = await fileReader.ReadToEndAsync(ct);
            if (IsMarkdownFile(file.FileName, file.ContentType)) markdown = text;
            else html = text;
        }
        else if (form.ContainsKey("markdown")) markdown = form["markdown"].ToString();
        else html = form["html"].ToString();
        string? Field(string name) => form.TryGetValue(name, out var v) ? v.ToString() : request.Query[name].ToString();
        req = new ConvertRequest
        {
            Html = html,
            Markdown = markdown,
            Format = Field("format"),
            Output = Field("output"),
            FileName = file?.FileName,
            PageSize = Field("pageSize"),
            Landscape = bool.TryParse(Field("landscape"), out var fl) && fl,
            MarginMm = float.TryParse(Field("marginMm"), System.Globalization.CultureInfo.InvariantCulture, out var fm) ? fm : null,
            PageNumbers = bool.TryParse(Field("pageNumbers"), out var fp) && fp,
            Watermark = Field("watermark"),
            Password = Field("password"),
            PdfA = bool.TryParse(Field("pdfa"), out var fa) && fa,
        };
    }
    else
    {
        using var reader = new StreamReader(request.Body);
        var q = request.Query;
        var body = await reader.ReadToEndAsync(ct);
        bool isMarkdown = IsMarkdownFile(null, request.ContentType);
        req = new ConvertRequest
        {
            Html = isMarkdown ? "" : body,
            Markdown = isMarkdown ? body : null,
            Format = q["format"],
            Output = q["output"],
            PageSize = q["pageSize"],
            Landscape = bool.TryParse(q["landscape"], out var l) && l,
            MarginMm = float.TryParse(q["marginMm"], System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : null,
            PageNumbers = bool.TryParse(q["pageNumbers"], out var p) && p,
            Watermark = q["watermark"],
            Password = q["password"],
            PdfA = bool.TryParse(q["pdfa"], out var a) && a,
        };
    }
    if (req is null)
        return Results.Problem("Send some HTML or Markdown to convert.", statusCode: StatusCodes.Status400BadRequest);

    // Markdown arrives in "markdown", or in "html" when format=markdown says so.
    bool markdownInput = req.Markdown != null || IsMarkdownFormat(req.Format);
    var input = req.Markdown ?? req.Html ?? "";
    if (string.IsNullOrWhiteSpace(input))
        return Results.Problem("Send some HTML or Markdown to convert.", statusCode: StatusCodes.Status400BadRequest);

    var output = (req.Output ?? "").Trim().ToLowerInvariant();
    bool htmlOutput = output == "html";
    if (output is not ("" or "pdf" or "html"))
        return Results.Problem("output must be \"pdf\" or \"html\".", statusCode: StatusCodes.Status400BadRequest);
    if (htmlOutput && !markdownInput)
        return Results.Problem("HTML output is for Markdown input; send Markdown or ask for a PDF.", statusCode: StatusCodes.Status400BadRequest);

    var options = BuildOptions(req);
    if (!await TryStartConversion(conversions, queueTimeout, ct))
        return Results.Problem("The server is busy; try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable);
    var sw = Stopwatch.StartNew();
    byte[] result;
    // The token reaches into the converter, so a document that runs over the time limit really stops using the CPU.
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(conversionTimeout);
    try
    {
        result = await Task.Run(() =>
        {
            if (!markdownInput) return HtmlToPdf.Convert(input, options, timeout.Token);
            var html = MarkdownToHtml.ConvertDocument(input);
            return htmlOutput ? System.Text.Encoding.UTF8.GetBytes(html) : HtmlToPdf.Convert(html, options, timeout.Token);
        }, timeout.Token);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem("The document took too long to convert.", statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (PdfLimitException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (OutOfMemoryException)
    {
        // Past the container's memory limit the runtime fails the allocation, not the process: refuse this document.
        return Results.Problem("The document needs more memory than the server can give it.", statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
    }
    finally
    {
        conversions.Release();
    }
    sw.Stop();
    request.HttpContext.Response.Headers["X-Conversion-Ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    return htmlOutput
        ? Results.File(result, "text/html; charset=utf-8", SafeFileName(req.FileName, ".html"))
        : Results.File(result, "application/pdf", SafeFileName(req.FileName, ".pdf"));
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
    if (!await TryStartConversion(conversions, queueTimeout, ct))
        return Results.Problem("The server is busy; try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(30));
    try
    {
        // Opening parses the uploaded file, so it runs off the request thread under the same time limit.
        var renderer = await Task.Run(() => PdfRenderer.Open(pdf, options.Password), timeout.Token).WaitAsync(timeout.Token);
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
    catch (OutOfMemoryException)
    {
        return Results.Problem("The document needs more memory than the server can give it.", statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem("The document took too long to render.", statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    finally
    {
        conversions.Release();
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
        Watermark = string.IsNullOrWhiteSpace(req.Watermark) ? null : new PdfWatermark { Text = req.Watermark.Length > 200 ? req.Watermark[..200] : req.Watermark },
        Security = string.IsNullOrEmpty(req.Password) ? null : new PdfSecurity { UserPassword = req.Password, Permissions = PdfPermissions.All },
        Conformance = req.PdfA ? PdfConformance.PdfA2b : PdfConformance.None,
        // The HTML comes from anyone on the internet: never let it read files from this server.
        AllowFileAccess = false,
        MaxPages = MaxPages,
    };
}

// One rate-limit bucket per IPv4 address or IPv6 /64 network (a single client usually owns a whole /64).
static string ClientKey(System.Net.IPAddress? ip)
{
    if (ip == null) return "unknown";
    if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
    if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.ToString();
    var bytes = ip.GetAddressBytes();
    Array.Clear(bytes, 8, 8);
    return new System.Net.IPAddress(bytes) + "/64";
}

// Starts a conversion if a slot is free within the queue time and memory is not nearly used up.
static async Task<bool> TryStartConversion(SemaphoreSlim conversions, TimeSpan queueTimeout, CancellationToken ct)
{
    if (!await conversions.WaitAsync(queueTimeout, ct)) return false;
    var gc = GC.GetGCMemoryInfo();
    if (gc.MemoryLoadBytes < gc.HighMemoryLoadThresholdBytes) return true;
    conversions.Release();
    return false;
}

static string SafeFileName(string? name, string extension = ".pdf")
{
    if (string.IsNullOrWhiteSpace(name)) return "document" + extension;
    var clean = new string(Path.GetFileNameWithoutExtension(name).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').ToArray()).Trim();
    return (clean.Length == 0 ? "document" : clean) + extension;
}

static bool IsMarkdownFormat(string? format) => (format ?? "").Trim().ToLowerInvariant() is "markdown" or "md";

static bool IsMarkdownFile(string? fileName, string? contentType)
{
    var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
    if (ext is ".md" or ".markdown" or ".mdown" or ".mkd" or ".mkdn") return true;
    return (contentType ?? "").StartsWith("text/markdown", StringComparison.OrdinalIgnoreCase)
           || (contentType ?? "").StartsWith("text/x-markdown", StringComparison.OrdinalIgnoreCase);
}

public sealed class ConvertRequest
{
    public string Html { get; set; } = "";
    public string? Markdown { get; set; }
    /// <summary>"html" (default) or "markdown": how to read <see cref="Html"/>.</summary>
    public string? Format { get; set; }
    /// <summary>"pdf" (default) or "html" (Markdown input only).</summary>
    public string? Output { get; set; }
    public string? PageSize { get; set; }
    public bool Landscape { get; set; }
    public float? MarginMm { get; set; }
    public bool PageNumbers { get; set; }
    /// <summary>Text stamped diagonally on every page.</summary>
    public string? Watermark { get; set; }
    /// <summary>Password needed to open the PDF (AES-256).</summary>
    public string? Password { get; set; }
    /// <summary>Produce PDF/A-2b for archiving (cannot be combined with a password).</summary>
    public bool PdfA { get; set; }
    public string? FileName { get; set; }
}

[JsonSerializable(typeof(ConvertRequest))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class AppJsonContext : JsonSerializerContext;
