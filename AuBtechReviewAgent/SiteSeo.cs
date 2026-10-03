using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AuBtechReviewAgent;

/// <summary>
/// What search engines get: robots.txt, sitemap.xml, the page descriptions and the structured data of the
/// landing page. Only the public pages are offered for indexing. A run's dashboard and report pages are
/// reachable by anyone with the link, but they are someone's review, so they carry "noindex" (as a
/// meta tag and as an X-Robots-Tag header). They are not blocked in robots.txt on purpose: a crawler that
/// may not fetch a page never sees its noindex, and could still list the bare link.
/// </summary>
public static class SiteSeo
{
    public const string SiteName = "TraceableAI";

    public const string DefaultDescription =
        "Open-source PRISMA 2020 literature reviews in which every citation is checked against the paper it cites, " +
        "with the supporting quote and a complete, verifiable record of every step.";

    /// <summary>Social preview image (1200×630), relative to the site root.</summary>
    public const string SocialImagePath = "img/social-card.png";

    /// <summary>The pages offered to search engines, with how often they change and their relative priority.</summary>
    public static readonly IReadOnlyList<(string Path, string ChangeFrequency, string Priority)> IndexedPages = new[]
    {
        ("/", "monthly", "1.0"),
        ("/review", "monthly", "0.8"),
        ("/metrics", "daily", "0.5"),
    };

    /// <summary>
    /// The site's public address without a trailing slash: Site:PublicUrl when set (recommended behind a proxy
    /// or with several host names), otherwise the scheme and host of the request.
    /// </summary>
    public static string BaseUrl(IConfiguration configuration, HttpRequest? request)
    {
        string? configured = configuration["Site:PublicUrl"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim().TrimEnd('/');
        if (request == null) return "";
        return $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
    }

    /// <summary>An absolute URL for a site path, for canonical links, Open Graph and the sitemap.</summary>
    public static string Absolute(string baseUrl, string path) =>
        baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    public static string RobotsTxt(string baseUrl) =>
        "User-agent: *\n" +
        "Allow: /\n" +
        "Disallow: /api/\n" +
        $"Sitemap: {Absolute(baseUrl, "sitemap.xml")}\n";

    public static string SitemapXml(string baseUrl)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "urlset",
                IndexedPages.Select(p => new XElement(ns + "url",
                    new XElement(ns + "loc", Absolute(baseUrl, p.Path)),
                    new XElement(ns + "changefreq", p.ChangeFrequency),
                    new XElement(ns + "priority", p.Priority)))));
        return doc.Declaration + "\n" + doc.Root;
    }

    /// <summary>
    /// Paths that must not appear in search results: downloads, a run's own dashboard and report, and the
    /// error pages. Plain /review is the form and stays indexable.
    /// </summary>
    public static bool IsNoIndexPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string p = path.TrimEnd('/').ToLowerInvariant();
        return p.StartsWith("/api/", StringComparison.Ordinal)
            || p.StartsWith("/review/", StringComparison.Ordinal)
            || p == "/spec-matrix" || p.StartsWith("/spec-matrix/", StringComparison.Ordinal)
            || p == "/not-found" || p == "/error";
    }

    /// <summary>
    /// Structured data (schema.org) for the landing page, as the content of a JSON-LD script element.
    /// "&lt;" is escaped so the text can never close the element it sits in.
    /// </summary>
    public static string JsonLd(string baseUrl)
    {
        var data = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "SoftwareApplication",
            ["name"] = SiteName,
            ["description"] = DefaultDescription,
            ["url"] = Absolute(baseUrl, "/"),
            ["image"] = Absolute(baseUrl, SocialImagePath),
            ["applicationCategory"] = "EducationalApplication",
            ["operatingSystem"] = "Web",
            ["isAccessibleForFree"] = true,
            ["license"] = "https://www.apache.org/licenses/LICENSE-2.0",
            ["offers"] = new Dictionary<string, object> { ["@type"] = "Offer", ["price"] = "0", ["priceCurrency"] = "EUR" },
            ["sameAs"] = new[] { "https://github.com/lauPhilip/au-btech-literature-review-agent" },
            ["creator"] = new Dictionary<string, object>
            {
                ["@type"] = "Organization",
                ["name"] = "Department of Business Development and Technology, Aarhus University",
                ["url"] = "https://btech.au.dk/en/",
            },
        };
        return JsonSerializer.Serialize(data).Replace("<", "\\u003c", StringComparison.Ordinal);
    }
}
