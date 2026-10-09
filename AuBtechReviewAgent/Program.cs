using AuBtechReviewAgent.Components;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// 0. LOGGING
// Log lines carry their scopes, so everything a run writes includes its RunId (see RunReviewAsync). On the
// server the console log is one JSON object per line with a UTC timestamp, which log tools can filter by run;
// in development it stays readable text.
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(options => { options.IncludeScopes = true; options.SingleLine = true; options.TimestampFormat = "HH:mm:ss "; });
}
else
{
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = true;
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    });
}

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
builder.Services.AddSingleton(quotaOptions); // read by the privacy page
builder.Services.AddSingleton(new AuBtechReviewAgent.RunQuotaService(
    quotaOptions, builder.Environment.IsDevelopment(), builder.Environment.ContentRootPath));
builder.Services.AddHttpContextAccessor();

// Add standard interactive services to the container
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<AuBtechReviewAgent.UiStateContainer>();

// Read global environment configuration fallback boundaries securely
// Language model: Mistral (default) or any OpenAI-compatible server, e.g. a local Ollama (see the Llm section).
var llmOptions = builder.Configuration.GetSection("Llm").Get<AuBtechReviewAgent.LlmOptions>() ?? new AuBtechReviewAgent.LlmOptions();
string mistralApiKey = builder.Configuration["MISTRAL_API_KEY"] ?? "";
if (!llmOptions.IsOpenAICompatible && string.IsNullOrWhiteSpace(mistralApiKey))
    throw new InvalidOperationException("MISTRAL_API_KEY is missing. Set it with dotnet user-secrets, or set Llm:Provider to OpenAICompatible.");
string elsevierApiKey = builder.Configuration["ELSEVIER_API_KEY"] ?? "";
string ieeeApiKey = builder.Configuration["IEEE_API_KEY"] ?? "";
// Optional Semantic Scholar key (the open sources OpenAlex, Semantic Scholar and Crossref need no key).
var openSources = builder.Configuration.GetSection("OpenSources").Get<AuBtechReviewAgent.OpenSourcesOptions>() ?? new AuBtechReviewAgent.OpenSourcesOptions();
string scholarApiKey = openSources.SemanticScholarApiKey;

string? supportStatement = builder.Configuration["Report:SupportStatement"];

var runsOptions = builder.Configuration.GetSection("Runs").Get<AuBtechReviewAgent.RunsOptions>() ?? new AuBtechReviewAgent.RunsOptions();
builder.Services.AddSingleton(runsOptions);

var cacheOptions = builder.Configuration.GetSection("Cache").Get<AuBtechReviewAgent.CacheOptions>() ?? new AuBtechReviewAgent.CacheOptions();
var reviewCache = new AuBtechReviewAgent.ReviewCache(cacheOptions, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(reviewCache);
builder.Services.AddSingleton(cacheOptions); // read by the privacy page

// Per-run quality metrics, kept in App_Data/metrics so they outlive the run folders (see the Metrics section).
var metricsOptions = builder.Configuration.GetSection("Metrics").Get<AuBtechReviewAgent.MetricsOptions>() ?? new AuBtechReviewAgent.MetricsOptions();
var metricsStore = new AuBtechReviewAgent.RunMetricsStore(metricsOptions, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(metricsStore);

// Thematic synthesis, dual coding and citation repair (see the Synthesis section).
var synthesisOptions = builder.Configuration.GetSection("Synthesis").Get<AuBtechReviewAgent.SynthesisOptions>() ?? new AuBtechReviewAgent.SynthesisOptions();

var reviewEngine = new AuBtechReviewAgent.PrismaReviewEngine(mistralApiKey, elsevierApiKey, ieeeApiKey, scholarApiKey, supportStatement, runsOptions)
{
    OpenSources = openSources,
    Llm = llmOptions,
    Cache = reviewCache,
    Synthesis = synthesisOptions,
    MetricsStore = metricsStore,
};
builder.Services.AddSingleton(reviewEngine);
builder.Services.AddSingleton(reviewEngine.Store); // the run folders, shared by every kind of review
var multivocalPlanner = new AuBtechReviewAgent.MultivocalPlanner(reviewEngine.Store);
builder.Services.AddSingleton(multivocalPlanner); // multivocal planning (preview)
var multivocalSearcher = new AuBtechReviewAgent.MultivocalSearcher(reviewEngine.Store, multivocalPlanner, key => AuBtechReviewAgent.GreySourceCatalog.Create(key, openSources.ContactEmail));
builder.Services.AddSingleton(multivocalSearcher); // multivocal grey searches (preview)
var multivocalChat = () => AuBtechReviewAgent.LlmFactory.Create(llmOptions, mistralApiKey);
var multivocalScreener = new AuBtechReviewAgent.MultivocalScreener(reviewEngine.Store, multivocalPlanner, multivocalSearcher,
    multivocalChat, reviewEngine.Cache, llmOptions.Model, llmOptions.ScreeningParallelism);
builder.Services.AddSingleton(multivocalScreener); // grey screening (preview)
var multivocalPages = new AuBtechReviewAgent.MultivocalPages(reviewEngine.Store, multivocalSearcher, new AuBtechReviewAgent.PageFetcher());
builder.Services.AddSingleton(multivocalPages); // page snapshots (preview)
var multivocalQuality = new AuBtechReviewAgent.MultivocalQualityAssessor(reviewEngine.Store, multivocalPlanner, multivocalSearcher, multivocalScreener,
    multivocalPages, multivocalChat, reviewEngine.Cache, llmOptions.Model, llmOptions.ScreeningParallelism);
builder.Services.AddSingleton(multivocalQuality); // grey quality checklist (preview)
var multivocalMapper = new AuBtechReviewAgent.MultivocalMapper(reviewEngine.Store, multivocalPlanner, multivocalSearcher, multivocalQuality,
    multivocalChat, llmOptions.Model);
builder.Services.AddSingleton(multivocalMapper); // systematic map (preview)
var multivocalExtractor = new AuBtechReviewAgent.MultivocalExtractor(reviewEngine.Store, multivocalPlanner, multivocalSearcher, multivocalQuality,
    multivocalMapper, multivocalPages, multivocalChat, reviewEngine.Cache, llmOptions.Model, llmOptions.ScreeningParallelism);
builder.Services.AddSingleton(multivocalExtractor); // extraction (preview)
var multivocalSynthesiser = new AuBtechReviewAgent.MultivocalSynthesiser(reviewEngine.Store, multivocalPlanner, multivocalSearcher, multivocalQuality,
    multivocalMapper, multivocalExtractor, multivocalPages, multivocalChat, llmOptions.Model);
builder.Services.AddSingleton(multivocalSynthesiser); // synthesis (preview)
var multivocalReporter = new AuBtechReviewAgent.MultivocalReporter(reviewEngine.Store, multivocalPlanner, multivocalSearcher, multivocalScreener, multivocalPages,
    multivocalQuality, multivocalMapper, multivocalExtractor, multivocalSynthesiser);
builder.Services.AddSingleton(multivocalReporter); // report, written in code (preview)
builder.Services.AddSingleton(new AuBtechReviewAgent.MultivocalRunner(reviewEngine.Store, multivocalSearcher, multivocalScreener, multivocalQuality,
    multivocalMapper, multivocalExtractor, multivocalSynthesiser, multivocalPages)); // runs the steps one after another (preview)
// Modules shown as a preview while they are being built (Development only; see ReviewModulesOptions).
builder.Services.AddSingleton(AuBtechReviewAgent.ReviewModulesOptions.From(builder.Configuration, builder.Environment.IsDevelopment()));

// Register the storage cleanup background worker
builder.Services.AddHostedService<AuBtechReviewAgent.SessionCleanupWorker>();

var app = builder.Build();

// Classes that are not created by dependency injection (engine, sources, cache) log through AppLog.
AuBtechReviewAgent.AppLog.Factory = app.Services.GetRequiredService<ILoggerFactory>();
var startupLog = AuBtechReviewAgent.AppLog.For("Startup");
startupLog.LogInformation("Language model: {Model}", llmOptions.DisplayName);
if (string.IsNullOrWhiteSpace(openSources.ContactEmail))
    startupLog.LogWarning("OpenSources:ContactEmail is not set. OpenAlex, Crossref and Unpaywall ask for a contact e-mail; Unpaywall does not work without one.");

// Runs do not survive a restart (IIS recycles the app pool when idle and on a schedule). Mark any run that
// was cut off as "Interrupted" so its link shows what happened instead of a spinner that never stops.
int interrupted = reviewEngine.MarkInterruptedRuns();
if (interrupted > 0) startupLog.LogInformation("Marked {Count} unfinished run(s) as interrupted.", interrupted);

// Apply Forwarded Headers immediately before evaluating redirection paths
app.UseForwardedHeaders();

// Content-Security-Policy and other security headers on every response (see SecurityHeaders).
bool isDevelopment = app.Environment.IsDevelopment();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        AuBtechReviewAgent.SecurityHeaders.Apply(context.Response.Headers, isDevelopment);
        // A run's own pages and downloads stay out of search results (see SiteSeo).
        if (AuBtechReviewAgent.SiteSeo.IsNoIndexPath(context.Request.Path.Value))
            context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        return Task.CompletedTask;
    });
    await next();
});

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

// The download endpoints use the one engine instance created above instead of taking it as a handler
// parameter: every handler parameter counts as request input for static analysis, and the engine is not.
// Serve the workspace footprint (.zip) as a real HTTP download instead of streaming it through the
// Blazor Server SignalR connection: that connection has a small default max message size, so pushing a
// base64-encoded zip through JS interop silently fails once a run has real downloaded source PDFs in it.
app.MapGet("/api/workspace/{sessionId:guid}/archive", (Guid sessionId) =>
{
    byte[] zipBytes = reviewEngine.GenerateWorkspaceArchiveFromDisk(sessionId);
    if (zipBytes.Length == 0)
    {
        return Results.NotFound();
    }

    string fileName = $"PRISMA_Evaluation_Footprint_{DateTime.UtcNow:yyyyMMdd}.zip";
    return Results.File(zipBytes, "application/zip", fileName);
}).RequireRateLimiting("ArchiveDownloadPolicy");

// The included papers (or a multivocal run's grey sources) as BibTeX or RIS, for Zotero / EndNote / Mendeley.
app.MapGet("/api/workspace/{sessionId:guid}/references.{format}", (Guid sessionId, string format) =>
{
    if (format is not ("bib" or "ris")) return Results.NotFound();
    string? content = reviewEngine.ExportReferences(sessionId, format) ?? multivocalReporter.ExportReferences(sessionId, format);
    if (content == null) return Results.NotFound();
    string mime = format == "bib" ? "application/x-bibtex" : "application/x-research-info-systems";
    return Results.File(System.Text.Encoding.UTF8.GetBytes(content), mime, $"references.{format}");
}).RequireRateLimiting("ArchiveDownloadPolicy");

// Every screened record as RIS, tagged "included" or "excluded: reason", for a Zotero library of the screening.
app.MapGet("/api/workspace/{sessionId:guid}/screened.ris", (Guid sessionId) =>
{
    string? content = reviewEngine.ExportReferences(sessionId, "screened") ?? multivocalReporter.ExportReferences(sessionId, "screened");
    return content == null ? Results.NotFound()
        : Results.File(System.Text.Encoding.UTF8.GetBytes(content), "application/x-research-info-systems", "screened.ris");
}).RequireRateLimiting("ArchiveDownloadPolicy");

// The protocol written before the search (PRISMA item 24), linked from the Review Output page.
app.MapGet("/api/workspace/{sessionId:guid}/protocol.md", (Guid sessionId) =>
{
    string? text = reviewEngine.Store.ReadProtocol(sessionId);
    return text == null ? Results.NotFound() : Results.Text(text, "text/markdown; charset=utf-8");
}).RequireRateLimiting("ArchiveDownloadPolicy");

// The report of a synthesised multivocal run, written in code from its files (MLR block G).
app.MapGet("/api/workspace/{sessionId:guid}/mlr-report.md", (Guid sessionId) =>
{
    string? text = multivocalReporter.Build(sessionId)?.ToMarkdown();
    return text == null ? Results.NotFound()
        : Results.File(System.Text.Encoding.UTF8.GetBytes(text), "text/markdown; charset=utf-8", AuBtechReviewAgent.MultivocalReporter.ReportFile);
}).RequireRateLimiting("ArchiveDownloadPolicy");

// For search engines: which pages to crawl, and the list of public pages (see SiteSeo).
var siteConfiguration = app.Configuration;
app.MapGet("/robots.txt", (HttpRequest request) =>
    Results.Text(AuBtechReviewAgent.SiteSeo.RobotsTxt(AuBtechReviewAgent.SiteSeo.BaseUrl(siteConfiguration, request)), "text/plain; charset=utf-8"));
app.MapGet("/sitemap.xml", (HttpRequest request) =>
    Results.Text(AuBtechReviewAgent.SiteSeo.SitemapXml(AuBtechReviewAgent.SiteSeo.BaseUrl(siteConfiguration, request)), "application/xml; charset=utf-8"));
// For uptime monitoring: storage, model configuration and how the databases have been answering (SiteHealth).
bool serverModelKey = !string.IsNullOrWhiteSpace(mistralApiKey) && !mistralApiKey.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase);
app.MapGet("/health", () =>
{
    var report = AuBtechReviewAgent.SiteHealth.Build(reviewEngine, serverModelKey, DateTime.UtcNow);
    return Results.Json(report, statusCode: report.Status == "unhealthy" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
});

// Where to report a vulnerability (RFC 9116); see SECURITY.md.
app.MapGet("/.well-known/security.txt", (HttpRequest request) =>
    Results.Text(AuBtechReviewAgent.SiteSeo.SecurityTxt(AuBtechReviewAgent.SiteSeo.BaseUrl(siteConfiguration, request), DateTime.UtcNow), "text/plain; charset=utf-8"));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
