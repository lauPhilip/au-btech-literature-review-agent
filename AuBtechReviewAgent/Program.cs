using AuBtechReviewAgent.Components;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
var builder = WebApplication.CreateBuilder(args);

// 1. FORWARDED HEADERS CONFIGURATION
// X-Forwarded-For is only trusted from proxies listed in ReverseProxy:KnownProxies (default: none besides
// loopback). The old config cleared the trusted list, which meant any visitor could send their own
// X-Forwarded-For header and pick the IP address the run quota counts against.
// Under IIS in-process hosting (e.g. Simply.com) the real client IP arrives without any forwarded header,
// so nothing needs to be listed there. Only add entries if a separate proxy sits in front of the app.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? Array.Empty<string>())
    {
        if (System.Net.IPAddress.TryParse(proxy, out var proxyAddress)) options.KnownProxies.Add(proxyAddress);
    }
});

// 2. RATE LIMITING
// Review runs are limited by RunQuotaService (per client address per day, see the Quota section in
// appsettings.json). Runs start over the Blazor SignalR circuit, which HTTP rate-limiting middleware never
// sees, so the middleware limiter only guards the one plain HTTP endpoint: the archive download.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ArchiveDownloadPolicy", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: AuBtechReviewAgent.RunQuotaService.NormalizeClientAddress(httpContext.Connection.RemoteIpAddress),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var quotaOptions = builder.Configuration.GetSection("Quota").Get<AuBtechReviewAgent.QuotaOptions>() ?? new AuBtechReviewAgent.QuotaOptions();
builder.Services.AddSingleton(new AuBtechReviewAgent.RunQuotaService(
    quotaOptions, builder.Environment.IsDevelopment(), builder.Environment.ContentRootPath));
builder.Services.AddHttpContextAccessor();

// Add standard interactive services to the container
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<AuBtechReviewAgent.UiStateContainer>();

// Read global environment configuration fallback boundaries securely
string mistralApiKey = builder.Configuration["MISTRAL_API_KEY"] ?? throw new InvalidOperationException("Mistral Key missing.");
string elsevierApiKey = builder.Configuration["ELSEVIER_API_KEY"] ?? "";
string ieeeApiKey = builder.Configuration["IEEE_API_KEY"] ?? "";
string scholarApiKey = builder.Configuration["SCHOLAR_API_KEY"] ?? ""; 

string? supportStatement = builder.Configuration["Report:SupportStatement"];

var runsOptions = builder.Configuration.GetSection("Runs").Get<AuBtechReviewAgent.RunsOptions>() ?? new AuBtechReviewAgent.RunsOptions();
builder.Services.AddSingleton(runsOptions);

var reviewEngine = new AuBtechReviewAgent.PrismaReviewEngine(mistralApiKey, elsevierApiKey, ieeeApiKey, scholarApiKey, supportStatement, runsOptions);
builder.Services.AddSingleton(reviewEngine);

// Register the storage cleanup background worker
builder.Services.AddHostedService<AuBtechReviewAgent.SessionCleanupWorker>();

var app = builder.Build();

// Runs do not survive a restart (IIS recycles the app pool when idle and on a schedule). Mark any run that
// was cut off as "Interrupted" so its link shows what happened instead of a spinner that never stops.
int interrupted = reviewEngine.MarkInterruptedRuns();
if (interrupted > 0) Console.WriteLine($"[Startup] Marked {interrupted} unfinished run(s) as interrupted.");

// Apply Forwarded Headers immediately before evaluating redirection paths
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapStaticAssets();

// Apply the rate limiter policies to the application pipeline routing channel
app.UseRateLimiter();

// Serve the workspace footprint (.zip) as a real HTTP download instead of streaming it through the
// Blazor Server SignalR connection: that connection has a small default max message size, so pushing a
// base64-encoded zip through JS interop silently fails once a run has real downloaded source PDFs in it.
app.MapGet("/api/workspace/{sessionId:guid}/archive", (Guid sessionId, AuBtechReviewAgent.PrismaReviewEngine engine) =>
{
    byte[] zipBytes = engine.GenerateWorkspaceArchiveFromDisk(sessionId);
    if (zipBytes.Length == 0)
    {
        return Results.NotFound();
    }

    string fileName = $"PRISMA_Evaluation_Footprint_{DateTime.UtcNow:yyyyMMdd}.zip";
    return Results.File(zipBytes, "application/zip", fileName);
}).RequireRateLimiting("ArchiveDownloadPolicy");

// The included papers as BibTeX or RIS, for Zotero / EndNote / Mendeley.
app.MapGet("/api/workspace/{sessionId:guid}/references.{format}", (Guid sessionId, string format, AuBtechReviewAgent.PrismaReviewEngine engine) =>
{
    if (format is not ("bib" or "ris")) return Results.NotFound();
    string? content = engine.ExportReferences(sessionId, format);
    if (content == null) return Results.NotFound();
    string mime = format == "bib" ? "application/x-bibtex" : "application/x-research-info-systems";
    return Results.File(System.Text.Encoding.UTF8.GetBytes(content), mime, $"references.{format}");
}).RequireRateLimiting("ArchiveDownloadPolicy");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();