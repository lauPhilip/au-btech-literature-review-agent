using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace AuBtechReviewAgent;

/// <summary>
/// The limits below the rate limiter: how large a request body may be, and what one live connection (Blazor circuit)
/// may hold. The circuit values are Blazor's own defaults except the number of disconnected circuits kept, which is
/// halved; they are written out so they are on file and can be changed in one place.
/// </summary>
public static partial class SiteRateLimits
{
    /// <summary>Disconnected circuits kept in memory for a reconnect; the oldest is dropped beyond this.</summary>
    public const int DisconnectedCircuitsKept = 50;

    /// <summary>The largest message the browser may send over the live connection; a file upload is sent in smaller pieces.</summary>
    public const int MaxHubMessageBytes = 32 * 1024;

    /// <summary>Sets the body limit on Kestrel (Docker, development). Under IIS the middleware below sets it per request.</summary>
    public static void LimitRequestBodies(WebApplicationBuilder builder, SiteLimitOptions limits)
    {
        long bytes = Math.Max(64, limits.MaxRequestBodyKb) * 1024L;
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = bytes);
    }

    /// <summary>
    /// Refuses a body that says it is too large with 413 before it is read, and lowers the server's own limit for a
    /// body without a length (IIS in-process allows 30 MB by default; this feature is how its limit is set per request).
    /// </summary>
    public static void UseRequestBodyLimit(WebApplication app, SiteLimitOptions limits)
    {
        long bytes = Math.Max(64, limits.MaxRequestBodyKb) * 1024L;
        app.Use(async (context, next) =>
        {
            if (context.Request.ContentLength > bytes)
            {
                LogRefusal("large-body", context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature
                && (feature.MaxRequestBodySize == null || feature.MaxRequestBodySize > bytes))
                feature.MaxRequestBodySize = bytes;
            await next(context);
        });
    }

    public static void LimitCircuits(CircuitOptions options)
    {
        options.DisconnectedCircuitMaxRetained = DisconnectedCircuitsKept;
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
        options.MaxBufferedUnacknowledgedRenderBatches = 10;
        options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(1);
    }

    public static void LimitHub(HubOptions options)
    {
        options.MaximumReceiveMessageSize = MaxHubMessageBytes;
        options.MaximumParallelInvocationsPerClient = 1;
    }
}
