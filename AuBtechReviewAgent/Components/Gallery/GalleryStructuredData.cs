using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>
/// The ScholarlyArticle data (schema.org) on a gallery version's page, as the content of a JSON-LD script element. It
/// is built in code from the version's own figures in entry.json, never by the model: title, the day the run finished,
/// the version, the DOI when there is one, TraceableAI as author, and the entry it belongs to. "&lt;" is escaped as
/// in SiteSeo, so a title can never close the element it sits in.
/// </summary>
public static class GalleryStructuredData
{
    public static string ScholarlyArticle(GalleryEntry e, string baseUrl)
    {
        string url = GalleryCitation.Url(baseUrl, e);
        var data = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "ScholarlyArticle",
            ["headline"] = e.Title,
            ["name"] = e.Title,
            ["datePublished"] = e.CompletedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["version"] = e.Version.ToString(CultureInfo.InvariantCulture),
            ["url"] = url,
            ["author"] = new Dictionary<string, object> { ["@type"] = "Organization", ["name"] = ArticleInfo.Author, ["url"] = SiteSeo.Absolute(baseUrl, "/") },
            ["publisher"] = new Dictionary<string, object> { ["@type"] = "Organization", ["name"] = SiteSeo.Organisation },
            ["isAccessibleForFree"] = true,
            ["isPartOf"] = new Dictionary<string, object>
            {
                ["@type"] = "CreativeWork",
                ["name"] = e.Title,
                ["url"] = SiteSeo.Absolute(baseUrl, $"/gallery/{e.Id}"),
            },
        };
        if (GalleryDoi.Normalize(e.Doi) is { } doi)
        {
            data["identifier"] = new Dictionary<string, object> { ["@type"] = "PropertyValue", ["propertyID"] = "DOI", ["value"] = doi };
            data["sameAs"] = GalleryDoi.Resolver + doi;
        }
        return JsonSerializer.Serialize(data).Replace("<", "\\u003c", System.StringComparison.Ordinal);
    }
}
