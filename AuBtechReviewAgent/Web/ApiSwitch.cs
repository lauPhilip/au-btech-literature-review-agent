using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace AuBtechReviewAgent;

/// <summary>
/// Whether the REST API and the MCP server are on (Api:Enabled, read once in Program.cs). The documentation page
/// at <see cref="DocsPath"/> follows it: with the switch off the page answers 404 and no page links to it.
/// </summary>
public sealed record ApiSwitch(bool Enabled)
{
    public const string DocsPath = "/docs/api";
    public const string DocsLabel = "API and MCP";

    /// <summary>Registers the switch, and with the API on points the OpenAPI description at the documentation page.</summary>
    public static void Register(IServiceCollection services, bool enabled)
    {
        services.AddSingleton(new ApiSwitch(enabled));
        if (!enabled) return;
        // The description is made per request, so the link carries the address the reader came on (or Site:PublicUrl).
        services.Configure<OpenApiOptions>("v1", options => options.AddDocumentTransformer((document, context, _) =>
        {
            var services = context.ApplicationServices;
            var request = services.GetService<IHttpContextAccessor>()?.HttpContext?.Request;
            string baseUrl = SiteSeo.BaseUrl(services.GetRequiredService<IConfiguration>(), request);
            document.Info.Description = "Start a systematic review with your own Mistral key, follow it, read the report and cancel it. " +
                "Examples, errors and the MCP setup are on the documentation page.";
            document.ExternalDocs = new OpenApiExternalDocs
            {
                Description = "How to use the API and the MCP server",
                Url = new Uri(baseUrl + DocsPath, baseUrl.Length > 0 ? UriKind.Absolute : UriKind.Relative),
            };
            return Task.CompletedTask;
        }));
    }
}
