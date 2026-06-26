using AuBtechReviewAgent.Components;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
var builder = WebApplication.CreateBuilder(args);

// 1. FORWARDED HEADERS CONFIGURATION (Resolves Simply.com SSL termination reverse proxy redirect warning)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// 2. ENDPOINT RATE LIMITER DEFINITION (Protects downstream engine instances from automated queue spam)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    
    options.AddPolicy("ExtractionQueuePolicy", httpContext =>
    {
        string ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        
        // Check if the user has activated their own keys
        bool isUsingByok = httpContext.Request.Headers.TryGetValue("X-BYOK-Active", out var byokValue) 
                           && byokValue == "true";

        if (isUsingByok)
        {
            // BYOK Tier: Standard 3 searches per minute
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: $"byok_{ipAddress}",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
        }
        else
        {
            // Server Default Key Tier: Strict limit of 1 use per day (1440 minutes) per IP address
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: $"default_{ipAddress}",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 1,
                    Window = TimeSpan.FromDays(1),
                    QueueLimit = 0
                });
        }
    });
});

// Add standard interactive services to the container
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddScoped<AuBtechReviewAgent.UiStateContainer>();

// Read global environment configuration fallback boundaries securely
string mistralApiKey = builder.Configuration["MISTRAL_API_KEY"] ?? throw new InvalidOperationException("Mistral Key missing.");
string elsevierApiKey = builder.Configuration["ELSEVIER_API_KEY"] ?? "";
string ieeeApiKey = builder.Configuration["IEEE_API_KEY"] ?? "";
string scholarApiKey = builder.Configuration["SCHOLAR_API_KEY"] ?? ""; 

builder.Services.AddSingleton(new AuBtechReviewAgent.PrismaReviewEngine(mistralApiKey, elsevierApiKey, ieeeApiKey, scholarApiKey));

// Register the storage cleanup background worker
builder.Services.AddHostedService<AuBtechReviewAgent.SessionCleanupWorker>();

var app = builder.Build(); 

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();