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

    /// <summary>
    /// Social preview image (1200×630), relative to the site root. A new design gets a new file name, because link
    /// previews are cached by address.
    /// </summary>
    public const string SocialImagePath = "img/og-card.png";

    /// <summary>The pages offered to search engines, with how often they change and their relative priority.</summary>
    public static readonly IReadOnlyList<(string Path, string ChangeFrequency, string Priority)> IndexedPages = new[]
    {
        ("/", "monthly", "1.0"),
        ("/start", "monthly", "0.8"),
        ("/review", "monthly", "0.7"),
        ("/metrics", "daily", "0.5"),
        ("/glossary", "monthly", "0.4"),
        ("/about", "monthly", "0.6"),
        ("/model-card", "monthly", "0.5"),
        ("/privacy", "yearly", "0.3"),
        ("/verify", "yearly", "0.3"),
        ("/accessibility", "yearly", "0.3"),
    };

    public const string RepositoryUrl = "https://github.com/lauPhilip/au-btech-literature-review-agent";
    public const string Organisation = "Department of Business Development and Technology, Aarhus University";
    public const string Authors = "Philip S. P. Ø. O. Lau and Nidhi";

    /// <summary>
    /// A new GitHub issue, filled in with what is needed to find the run in the server log (run ID, version,
    /// the step it stopped at) and nothing the person typed: the question and criteria stay out of a public issue.
    /// </summary>
    public static string ProblemReportUrl(Guid runId, string version, string stage)
    {
        string title = $"Run failed during {stage} ({runId.ToString()[..8]})";
        string body =
            "**What happened**\n\nA review run stopped with an error.\n\n" +
            $"- Run ID: `{runId}`\n- App version: `{version}`\n- Stopped during: {stage}\n- Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm}\n\n" +
            "**What were you doing?**\n\n(Please describe. Do not paste anything confidential: issues are public.)\n";
        return $"{RepositoryUrl}/issues/new?labels=bug&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
    }

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

    /// <summary>When the running app was built, from its assembly file; null when that cannot be read (single-file builds).</summary>
    public static DateTime? BuildDate()
    {
        try
        {
            string path = typeof(SiteSeo).Assembly.Location;
            return string.IsNullOrEmpty(path) || !System.IO.File.Exists(path) ? null : System.IO.File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The crawlers of AI systems that robots.txt names (issue #611). Being cited correctly by answer engines is part
    /// of what the tool is for, so they are welcome on purpose and not by implication. A crawler that is named
    /// ignores the "*" group, so the group repeats the /api/ rule instead of leaving it to the "*" group.
    /// </summary>
    public static readonly IReadOnlyList<string> AiCrawlers = new[]
    {
        "GPTBot", "OAI-SearchBot", "ChatGPT-User", "ClaudeBot", "Claude-User", "Claude-SearchBot", "anthropic-ai",
        "PerplexityBot", "Perplexity-User", "Google-Extended", "CCBot", "Applebot-Extended", "Bytespider", "cohere-ai",
    };

    public static string RobotsTxt(string baseUrl)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# AI crawlers are welcome on the public pages: being cited correctly by answer engines is part of what\n");
        sb.Append("# TraceableAI is for. See also /llms.txt. The downloads and the API under /api/ are for programs that\n");
        sb.Append("# call them on purpose, not for crawling. A named crawler ignores the group for all crawlers below, so\n");
        sb.Append("# the /api/ rule is repeated here.\n");
        sb.Append("# A run's own pages (/review/{id}) are not blocked on purpose: they carry a visitor's research question,\n");
        sb.Append("# but they are marked noindex, and a crawler that may not fetch a page never sees its noindex.\n");
        foreach (string crawler in AiCrawlers) sb.Append("User-agent: ").Append(crawler).Append('\n');
        sb.Append("Allow: /\n");
        sb.Append("Disallow: /api/\n");
        sb.Append('\n');
        sb.Append("# Every other crawler\n");
        sb.Append("User-agent: *\n");
        sb.Append("Allow: /\n");
        sb.Append("Disallow: /api/\n");
        sb.Append('\n');
        sb.Append("Sitemap: ").Append(Absolute(baseUrl, "sitemap.xml")).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// The sitemap of the public pages. <paramref name="lastModified"/> is the day the running version was built: the
    /// pages change only with a new version, so that is when each of them last changed.
    /// </summary>
    public static string SitemapXml(string baseUrl, DateTime? lastModified = null)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "urlset",
                IndexedPages.Select(p => new XElement(ns + "url",
                    new XElement(ns + "loc", Absolute(baseUrl, p.Path)),
                    lastModified is { } day ? new XElement(ns + "lastmod", day.ToUniversalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)) : null,
                    new XElement(ns + "changefreq", p.ChangeFrequency),
                    new XElement(ns + "priority", p.Priority)))));
        return doc.Declaration + "\n" + doc.Root;
    }

    /// <summary>
    /// Paths that must not appear in search results: downloads, a run's own dashboard and report, the error pages
    /// and the design previews. Plain /review is the form and stays indexable.
    /// </summary>
    public static bool IsNoIndexPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string p = path.TrimEnd('/').ToLowerInvariant();
        return p.StartsWith("/api/", StringComparison.Ordinal)
            || p.StartsWith("/review/", StringComparison.Ordinal)
            || p == "/spec-matrix" || p.StartsWith("/spec-matrix/", StringComparison.Ordinal)
            || p == "/not-found" || p == "/error" || p == "/health"
            || p == "/design" || p.StartsWith("/design/", StringComparison.Ordinal);
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
            ["sameAs"] = new[] { RepositoryUrl },
            ["creator"] = Creator(),
        };
        return Serialize(data);
    }

    /// <summary>JSON for a script element: "&lt;" escaped so the text can never close the element it sits in.</summary>
    private static string Serialize(object data) =>
        JsonSerializer.Serialize(data).Replace("<", "\\u003c", StringComparison.Ordinal);

    private static Dictionary<string, object> Creator() => new()
    {
        ["@type"] = "Organization",
        ["name"] = Organisation,
        ["url"] = "https://btech.au.dk/en/",
    };

    /// <summary>The trail from the start page to this page, for search results (schema.org BreadcrumbList).</summary>
    public static string BreadcrumbJsonLd(string baseUrl, IReadOnlyList<(string Name, string Path)> trail) => Serialize(new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "BreadcrumbList",
        ["itemListElement"] = trail.Select((t, i) => new Dictionary<string, object>
        {
            ["@type"] = "ListItem",
            ["position"] = i + 1,
            ["name"] = t.Name,
            ["item"] = Absolute(baseUrl, t.Path),
        }).ToList(),
    });

    /// <summary>The public run metrics as a dataset (schema.org Dataset): what is measured, by whom, under which licence.</summary>
    public static string DatasetJsonLd(string baseUrl, int runs) => Serialize(new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "Dataset",
        ["name"] = "TraceableAI run quality metrics",
        ["description"] = "Citation-check verdicts, coverage, full-text share, agreement (Cohen's kappa) and cost of every completed " +
                          $"TraceableAI review on this server ({runs} runs), grouped by app version and settings. No research questions or paper text are stored.",
        ["url"] = Absolute(baseUrl, "/metrics"),
        ["isAccessibleForFree"] = true,
        ["license"] = "https://www.apache.org/licenses/LICENSE-2.0",
        ["creator"] = Creator(),
        ["variableMeasured"] = new[]
        {
            "Share of supported citations", "Share of partly supported citations", "Share of not supported citations",
            "Share of included studies cited", "Share of studies read in full text", "Cohen's kappa of the theme coding",
            "Model calls per run", "Minutes per run",
        },
        ["measurementTechnique"] = "Automated citation support check against the cited paper, with verbatim quote verification",
    });

    /// <summary>The OSSYM 2026 paper describing the tool (schema.org ScholarlyArticle).</summary>
    public static string ScholarlyArticleJsonLd(string baseUrl) => Serialize(new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "ScholarlyArticle",
        ["headline"] = "TraceableAI: An Open-Source Agentic Framework for PRISMA-Compliant Literature Synthesis",
        ["author"] = new[]
        {
            new Dictionary<string, object> { ["@type"] = "Person", ["name"] = "Philip S. P. Ø. O. Lau", ["affiliation"] = Creator() },
            new Dictionary<string, object> { ["@type"] = "Person", ["name"] = "Nidhi" },
        },
        ["datePublished"] = "2026",
        ["isPartOf"] = new Dictionary<string, object>
        {
            ["@type"] = "PublicationEvent",
            ["name"] = "8th International Open Search Symposium (OSSYM 2026)",
        },
        ["about"] = new Dictionary<string, object> { ["@type"] = "SoftwareApplication", ["name"] = SiteName, ["url"] = Absolute(baseUrl, "/") },
        ["url"] = Absolute(baseUrl, "/about"),
    });

    /// <summary>
    /// /.well-known/security.txt (RFC 9116): where to report a vulnerability. Expires must be less than a year
    /// ahead, so it is computed when served instead of being written down once and forgotten.
    /// </summary>
    public static string SecurityTxt(string baseUrl, DateTime utcNow) =>
        $"Contact: {RepositoryUrl}/security/advisories/new\n" +
        $"Expires: {utcNow.Date.AddDays(180):yyyy-MM-dd}T00:00:00.000Z\n" +
        "Preferred-Languages: en, da\n" +
        $"Canonical: {Absolute(baseUrl, ".well-known/security.txt")}\n" +
        $"Policy: {RepositoryUrl}/blob/master/SECURITY.md\n";
}
