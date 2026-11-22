using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AuBtechReviewAgent;

/// <summary>
/// The MCP server (Model Context Protocol, official C# SDK) over Streamable HTTP at /mcp. ApiEndpoints registers and
/// maps it together with the REST API, so the same Api:Enabled switch turns both on and off.
/// </summary>
public static class McpSetup
{
    public const string Path = "/mcp";

    public static IServiceCollection AddReviewMcp(this IServiceCollection services)
    {
        // Stateless: every call stands on its own, so no session lives on the server between calls, and nothing
        // from one caller (their key least of all) is kept for the next.
        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<ReviewMcpTools>();
        return services;
    }

    public static WebApplication MapReviewMcp(this WebApplication app)
    {
        // A tool's answer can carry an edit key, and MCP answers stream; neither is compressed (BREACH).
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(Path)) ResponseCompressionSetup.DoNotCompress(context);
            return next(context);
        });
        app.MapMcp(Path);
        return app;
    }
}
