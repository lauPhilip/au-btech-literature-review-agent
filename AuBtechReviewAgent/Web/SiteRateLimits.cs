using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>Settings bound from the "Site:Limits" section; every value has a default here, so the section may be left out.</summary>
public class SiteLimitOptions
{
    /// <summary>Page requests per minute per address. Generous, because a class behind one campus address shares it.</summary>
    public int PagesPerMinute { get; set; } = 300;

    /// <summary>Scripts, styles, fonts and images per minute per address (a first visit loads about fifteen).</summary>
    public int StaticFilesPerMinute { get; set; } = 1200;

    /// <summary>Run downloads per minute per address (archive, references, protocol, main.tex).</summary>
    public int DownloadsPerMinute { get; set; } = 20;

    /// <summary>Calls to /health and the other plain-text addresses per minute per address.</summary>
    public int ProbesPerMinute { get; set; } = 30;

    /// <summary>New live connections (Blazor negotiations) per minute per address.</summary>
    public int ConnectionsPerMinute { get; set; } = 120;

    /// <summary>Live connections (Blazor circuits) open at once per address; each one holds server memory.</summary>
    public int OpenConnectionsPerClient { get; set; } = 50;

    /// <summary>The largest request body in KB. The API's start is 64 KB, a file upload travels over the live connection.</summary>
    public int MaxRequestBodyKb { get; set; } = 1024;
}

/// <summary>
/// ASP.NET Core rate limiting for every plain HTTP address of the site, per normalised client address (the address the
/// forwarded-headers set-up trusts). The API and MCP addresses keep their own limiter (ApiEndpoints), chained after
/// this one. The server itself is not limited: a request from a loopback address, or with no address, is a health
/// probe or a test on the machine, not a visitor. A refused request gets 429 with Retry-After; a refused live
/// connection gets 429 too, and the browser's reconnect banner offers to try again. Refusals are logged at most once a
/// minute per address and kind, with a fingerprint of the address that changes on every restart, never the address.
/// </summary>
public static partial class SiteRateLimits
{
    public const string Pages = "pages", StaticFiles = "static", Downloads = "downloads", Probes = "probes",
        Negotiations = "negotiate", Connections = "connections", Api = "api";

    /// <summary>The policy the run downloads name (WorkspaceDownloads); it counts per address like the rest.</summary>
    public const string DownloadPolicy = "ArchiveDownloadPolicy";

    private static readonly byte[] LogKey = RandomNumberGenerator.GetBytes(32);
    private static readonly ConcurrentDictionary<string, DateTime> LastLogged = new();

    private static readonly HashSet<string> StaticExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".mjs", ".map", ".png", ".svg", ".ico", ".jpg", ".jpeg", ".webp", ".gif", ".woff", ".woff2", ".webmanifest", ".txt", ".wasm",
    };

    /// <summary>Which limit a request counts against; null for the addresses another limiter guards (API, downloads).</summary>
    public static string? Category(PathString path)
    {
        string value = path.Value ?? "/";
        if (path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase)) return Api;
        if (path.StartsWithSegments("/api/workspace", StringComparison.OrdinalIgnoreCase)) return Downloads;
        // The Metrics page's files (runs.csv, runs.json, stages.csv) are built from every run on each request.
        if (path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase) && System.IO.Path.HasExtension(value)) return Downloads;
        if (path.StartsWithSegments("/_blazor/negotiate", StringComparison.OrdinalIgnoreCase)) return Negotiations;
        if (path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase)) return Connections;
        if (path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/robots.txt", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/sitemap.xml", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase)) return Probes;
        if (path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase)
            || StaticExtensions.Contains(System.IO.Path.GetExtension(value))) return StaticFiles;
        return Pages;
    }

    /// <summary>True for the server itself (loopback) and for a request with no address (a test server).</summary>
    public static bool IsExempt(IPAddress? address) => address == null || IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    public static IServiceCollection AddSiteRateLimits(IServiceCollection services, SiteLimitOptions limits)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, limits));
            // Kept for the downloads, which name it; the global limiter counts them as well, with the same number.
            options.AddPolicy(DownloadPolicy, context => IsExempt(context.Connection.RemoteIpAddress)
                ? RateLimitPartition.GetNoLimiter("server")
                : PerMinute("download-policy:" + RunQuotaService.NormalizeClientAddress(context.Connection.RemoteIpAddress), limits.DownloadsPerMinute));
            options.OnRejected = (context, _) =>
            {
                var http = context.HttpContext;
                string category = Category(http.Request.Path) ?? Pages;
                int seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? (int)Math.Ceiling(retry.TotalSeconds) : 30;
                http.Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                LogRefusal(category, http.Connection.RemoteIpAddress);
                return category == Api ? ValueTask.CompletedTask // the API writes its own problem answer
                    : new ValueTask(http.Response.WriteAsync("Too many requests from your network address. Please wait a minute and try again."));
            };
        });
        return services;
    }

    private static RateLimitPartition<string> Partition(HttpContext context, SiteLimitOptions limits)
    {
        var address = context.Connection.RemoteIpAddress;
        string? category = Category(context.Request.Path);
        if (category is null or Api || IsExempt(address)) return RateLimitPartition.GetNoLimiter("none");
        string key = category + ":" + RunQuotaService.NormalizeClientAddress(address);
        return category switch
        {
            Connections => RateLimitPartition.GetConcurrencyLimiter(key,
                _ => new ConcurrencyLimiterOptions { PermitLimit = Math.Max(1, limits.OpenConnectionsPerClient), QueueLimit = 0 }),
            StaticFiles => PerMinute(key, limits.StaticFilesPerMinute),
            Downloads => PerMinute(key, limits.DownloadsPerMinute),
            Probes => PerMinute(key, limits.ProbesPerMinute),
            Negotiations => PerMinute(key, limits.ConnectionsPerMinute),
            _ => PerMinute(key, limits.PagesPerMinute),
        };
    }

    private static RateLimitPartition<string> PerMinute(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, permits), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

    /// <summary>A short fingerprint of an address for the log: the same within one run of the app, unrelated after a restart.</summary>
    public static string Fingerprint(IPAddress? address)
    {
        using var hmac = new HMACSHA256(LogKey);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(RunQuotaService.NormalizeClientAddress(address))))[..10].ToLowerInvariant();
    }

    /// <summary>As <see cref="LogRefusal(string, IPAddress?)"/>, for the normalised address the pages and the quota use.</summary>
    public static void LogRefusal(string kind, string clientAddress) =>
        LogRefusal(kind, IPAddress.TryParse(clientAddress.Split('/')[0], out var address) ? address : null);

    /// <summary>One log line per kind and address a minute at most, so a flood of refusals cannot flood the log.</summary>
    public static void LogRefusal(string kind, IPAddress? address)
    {
        string fingerprint = Fingerprint(address);
        var now = DateTime.UtcNow;
        string key = kind + ":" + fingerprint;
        if (LastLogged.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
        if (LastLogged.Count > 10_000) LastLogged.Clear();
        LastLogged[key] = now;
        AppLog.For("AuBtechReviewAgent.SiteRateLimits").LogWarning("Refused {Kind} from one address over its limit (fingerprint {Fingerprint}).", kind, fingerprint);
    }
}
