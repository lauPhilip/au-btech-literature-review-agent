using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;

namespace AuBtechReviewAgent;

/// <summary>
/// Compression of what the server writes as it answers (issue #301): run downloads, the sitemap and JSON. Static files
/// are compressed once at publish by MapStaticAssets and are not compressed again here. Only text data is compressed,
/// never HTML pages and never Blazor's own connection: those carry the antiforgery token and the circuit's
/// connection token, and compressing a secret beside text an attacker can choose opens the BREACH attack over HTTPS.
/// A response that carries a secret of its own (an edit key) calls <see cref="DoNotCompress"/>.
/// </summary>
public static class ResponseCompressionSetup
{
    /// <summary>The text data that is compressed. The .zip archive is already compressed and is left out.</summary>
    public static readonly IReadOnlyList<string> MimeTypes = new[]
    {
        "application/json", "application/problem+json", "application/xml", "text/xml", "text/plain", "text/markdown",
        "text/csv", "application/x-tex", "application/x-bibtex", "application/x-research-info-systems",
    };

    /// <summary>Paths whose answers are never compressed: Blazor's connection (negotiation and long polling).</summary>
    private static readonly PathString[] NeverCompressed = { "/_blazor" };

    public static IServiceCollection AddTextCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = MimeTypes;
        });
        // Fastest: the downloads are small enough that the time saved on the wire outweighs a stronger setting.
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        return services;
    }

    public static IApplicationBuilder UseTextCompression(this IApplicationBuilder app)
    {
        app.UseResponseCompression();
        app.Use((context, next) =>
        {
            if (NeverCompressed.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
                DoNotCompress(context);
            return next(context);
        });
        return app;
    }

    /// <summary>Keeps this response uncompressed, for an answer that holds a secret such as an edit key.</summary>
    public static void DoNotCompress(HttpContext context)
    {
        if (context.Features.Get<IHttpsCompressionFeature>() is { } feature) feature.Mode = HttpsCompressionMode.DoNotCompress;
    }
}
