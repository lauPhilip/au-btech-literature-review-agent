using Microsoft.AspNetCore.Http;

namespace AuBtechReviewAgent;

/// <summary>
/// HTTP security headers for every response. The page loads no third-party scripts (Tailwind is compiled,
/// Mermaid is served from wwwroot/lib), so the Content-Security-Policy can allow scripts from this site only.
/// Styles keep 'unsafe-inline' because Blazor and Mermaid set style attributes and SVG style elements; no
/// script can run from those. In development, dotnet watch's browser refresh connects to another localhost
/// port, so that is allowed there only.
/// </summary>
public static class SecurityHeaders
{
    public static string ContentSecurityPolicy(bool isDevelopment) => string.Join("; ", new[]
    {
        "default-src 'self'",
        "script-src 'self'",
        "style-src 'self' 'unsafe-inline'",
        "img-src 'self' data:",
        "font-src 'self'",
        isDevelopment ? "connect-src 'self' ws://localhost:* wss://localhost:* http://localhost:*" : "connect-src 'self'",
        "object-src 'none'",
        "base-uri 'self'",
        "form-action 'self'",
        "frame-ancestors 'none'",
    });

    public static void Apply(IHeaderDictionary headers, bool isDevelopment)
    {
        headers["Content-Security-Policy"] = ContentSecurityPolicy(isDevelopment);
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    }
}
