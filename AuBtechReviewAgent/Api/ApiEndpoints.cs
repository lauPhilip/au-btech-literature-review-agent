using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuBtechReviewAgent;

/// <summary>
/// The REST API under /api/v1: its services, its call limits and its routes. Program.cs calls
/// <see cref="AddReviewApi"/> and <see cref="MapReviewApi"/> only when Api:Enabled is on, so with the switch off
/// none of these addresses exists and each answers 404.
/// </summary>
public static class ApiEndpoints
{
    public const string MistralKeyHeader = "X-Mistral-Key";
    public const string ElsevierKeyHeader = "X-Elsevier-Key";
    public const string IeeeKeyHeader = "X-Ieee-Key";
    public const string ScholarKeyHeader = "X-Semantic-Scholar-Key";
    public const string EditKeyHeader = "X-Edit-Key";

    /// <summary>Calls per minute per client address, and per key, on every API and MCP address.</summary>
    public const int CallsPerMinute = 120;

    /// <summary>The largest request body a review start may have; the form's fields together are far smaller.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>Where the OpenAPI description of the API is served (Microsoft.AspNetCore.OpenApi's default address).</summary>
    public const string OpenApiPath = "/openapi/v1.json";

    private static bool IsApiPath(PathString path) =>
        path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase);

    public static IServiceCollection AddReviewApi(this IServiceCollection services)
    {
        var fingerprint = new ApiKeyFingerprint();
        services.AddSingleton(fingerprint);
        services.AddSingleton<ReviewApi>();
        services.AddReviewMcp();
        // The description covers the API's own routes only, not the site's downloads or health check.
        services.AddOpenApi(options => options.ShouldInclude = description =>
            description.RelativePath?.StartsWith("api/v1/", StringComparison.OrdinalIgnoreCase) == true);
        // Every call counts once per client address and once per key (an HMAC, never the key itself). The
        // limiter only looks at the API's own addresses; the site and its downloads keep their own limits.
        services.Configure<RateLimiterOptions>(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context => IsApiPath(context.Request.Path)
                    ? PerMinute("address:" + RunQuotaService.NormalizeClientAddress(context.Connection.RemoteIpAddress))
                    : RateLimitPartition.GetNoLimiter("site")),
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    string key = SecurityUtility.SanitizeApiKey(context.Request.Headers[MistralKeyHeader].ToString());
                    return IsApiPath(context.Request.Path) && key.Length > 0
                        ? PerMinute("key:" + fingerprint.Of(key))
                        : RateLimitPartition.GetNoLimiter("site");
                }));
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (!IsApiPath(context.HttpContext.Request.Path)) return;
                await Problem(ApiProblems.TooManyCalls()).ExecuteAsync(context.HttpContext);
            };
        });
        return services;
    }

    private static RateLimitPartition<string> PerMinute(string partition) =>
        RateLimitPartition.GetFixedWindowLimiter(partition,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = CallsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

    public static WebApplication MapReviewApi(this WebApplication app)
    {
        var configuration = app.Configuration;
        var api = app.MapGroup("/api/v1");
        // Answers carry an edit key or a run's live state: no cache may keep them.
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        api.MapPost("/reviews", async (HttpContext context, ReviewApi reviews) =>
        {
            // The answer holds the edit key, so it is never compressed (BREACH; see ResponseCompressionSetup).
            ResponseCompressionSetup.DoNotCompress(context);
            var (body, problem) = await ReadBodyAsync(context.Request);
            if (problem != null) return Problem(problem);
            var result = reviews.Start(body, KeysFrom(context.Request),
                RunQuotaService.NormalizeClientAddress(context.Connection.RemoteIpAddress), SiteSeo.BaseUrl(configuration, context.Request));
            if (result.Problem != null)
            {
                if (result.Problem.Extensions?.TryGetValue("retryAfterSeconds", out var seconds) == true)
                    context.Response.Headers.RetryAfter = seconds?.ToString();
                return Problem(result.Problem);
            }
            return Results.Accepted(result.Value!.Links.Status, result.Value);
        })
            .WithName("StartReview").WithSummary("Start a systematic review with your own Mistral key (X-Mistral-Key).")
            .Accepts<StartReviewBody>("application/json")
            .Produces<ReviewStarted>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity).ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/reviews/{id:guid}", (Guid id, HttpContext context, ReviewApi reviews) =>
            ToResult(reviews.Status(id, SiteSeo.BaseUrl(configuration, context.Request))))
            .WithName("GetReviewStatus").WithSummary("Where a review stands: its stage, the step of seven and its counts.")
            .Produces<ReviewStatus>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/reviews/{id:guid}/report", (Guid id, ReviewApi reviews) => ToResult(reviews.Report(id)))
            .WithName("GetReviewReport").WithSummary("The finished report as data: sections, every citation with its verdict and quote, references and PRISMA counts.")
            .Produces<ApiReviewReport>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        api.MapDelete("/reviews/{id:guid}", (Guid id, string? reason, HttpContext context, ReviewApi reviews) =>
        {
            var result = reviews.Cancel(id, context.Request.Headers[EditKeyHeader].ToString(), reason,
                SiteSeo.BaseUrl(configuration, context.Request));
            return result.Problem != null ? Problem(result.Problem) : Results.Accepted(result.Value!.Links.Status, result.Value);
        })
            .WithName("CancelReview").WithSummary("Cancel a queued or running review with its edit key (X-Edit-Key), and an optional reason.")
            .Produces<ReviewCancelled>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        app.MapOpenApi(); // at OpenApiPath
        app.MapReviewMcp();

        return app;
    }

    /// <summary>
    /// With the switch off nothing is mapped, but another route can still match the path by its shape and answer 405
    /// to a POST. This makes every API address answer a plain 404 instead, so the API cannot be told from nothing.
    /// </summary>
    public static WebApplication HideReviewApi(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (IsApiPath(context.Request.Path)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            await next(context);
        });
        return app;
    }

    /// <summary>The caller's keys from the headers, cleaned as the form cleans them; null without a Mistral key.</summary>
    public static ApiCallerKeys? KeysFrom(HttpRequest request)
    {
        string Read(string header) => SecurityUtility.SanitizeApiKey(request.Headers[header].ToString());
        string mistral = Read(MistralKeyHeader);
        return mistral.Length == 0 ? null : new ApiCallerKeys(mistral, Read(ElsevierKeyHeader), Read(IeeeKeyHeader), Read(ScholarKeyHeader));
    }

    internal static IResult ToResult<T>(ApiResult<T> result) => result.Problem != null ? Problem(result.Problem) : Results.Ok(result.Value);

    internal static IResult Problem(ApiProblem problem) =>
        Results.Problem(problem.Detail, statusCode: problem.Status, title: problem.Title,
            extensions: problem.Extensions?.ToDictionary(e => e.Key, e => e.Value));

    /// <summary>Reads the JSON body itself, so a body that is too large or not JSON gets a plain answer too.</summary>
    private static async Task<(StartReviewBody? Body, ApiProblem? Problem)> ReadBodyAsync(HttpRequest request)
    {
        if (request.ContentLength > MaxBodyBytes) return (null, TooLarge());
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return (null, TooLarge());
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) return (null, null);
        try
        {
            buffer.Position = 0;
            return (await JsonSerializer.DeserializeAsync<StartReviewBody>(buffer, JsonSerializerOptions.Web), null);
        }
        catch (JsonException)
        {
            return (null, ApiProblems.Invalid("The body is not valid JSON, or a field has the wrong type.", Array.Empty<string>()));
        }
    }

    private static ApiProblem TooLarge() =>
        ApiProblems.Invalid($"The body is larger than {MaxBodyBytes / 1024} KB. The review's fields fit in far less.", Array.Empty<string>());
}
