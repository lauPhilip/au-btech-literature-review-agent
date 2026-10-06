using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// Reports, theses and standards in OpenAlex, the free catalogue the systematic review already searches, filtered
/// to the work types that are grey literature. Articles, preprints and books belong to the formal pool; "other" is
/// left out because in OpenAlex it is mostly front matter and index pages. The citation count is kept for the
/// quality checklist's impact item, since grey sources rarely have any other.
/// </summary>
public sealed class OpenAlexGreySource : IGreySource
{
    private const string Api = "https://api.openalex.org";

    /// <summary>The OpenAlex work types searched, and the kind of grey literature each one is.</summary>
    public static readonly IReadOnlyDictionary<string, string> GreyWorkTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["report"] = "white-papers",
        ["dissertation"] = "theses",
        ["standard"] = "documentation",
    };

    private readonly string _contactEmail;
    private readonly List<string> _raw = new();

    public OpenAlexGreySource(string contactEmail = "") => _contactEmail = contactEmail;

    public string Key => "openalex-grey";
    public string Name => "OpenAlex (grey literature)";
    public IReadOnlyList<string> LastRawResponses => _raw;

    public static string SearchUrl(string query, int maxResults, string contactEmail = "") =>
        OpenSourceHttp.WithMailto(
            $"{Api}/works?search={Uri.EscapeDataString(query.Trim())}&filter=type:{string.Join("|", GreyWorkTypes.Keys)}&per-page={Math.Clamp(maxResults, 1, 100)}",
            contactEmail);

    public async Task<List<GreyRecord>> SearchAsync(string query, int maxResults)
    {
        _raw.Clear();
        string json = await GreySourceHttp.GetAsync(SearchUrl(query, maxResults, _contactEmail));
        _raw.Add(json);
        return Parse(json);
    }

    public static List<GreyRecord> Parse(string json)
    {
        var records = new List<GreyRecord>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return records;
        foreach (var w in results.EnumerateArray())
        {
            // The filter asks for grey types only; a work of another type is dropped all the same.
            if (!GreyWorkTypes.TryGetValue(GreyJson.Str(w, "type"), out var kind)) continue;
            string id = GreyJson.Str(w, "id").Split('/').Last();
            string title = GreyJson.Str(w, "display_name");
            if (id.Length == 0 || title.Length == 0) continue;

            string url = AddressOf(w, id);
            if (url.Length == 0) continue;

            var authors = w.TryGetProperty("authorships", out var auths) && auths.ValueKind == JsonValueKind.Array
                ? auths.EnumerateArray()
                    .Select(a => a.TryGetProperty("author", out var author) ? GreyJson.Str(author, "display_name") : "")
                    .Where(n => n.Length > 0).Take(5).ToList()
                : new List<string>();
            string producer = authors.Count > 0 ? string.Join("; ", authors) : SourceName(w);

            records.Add(new GreyRecord(
                $"openalex:{id}",
                GreySourceHttp.PlainText(title, 500),
                GreySourceHttp.PlainText(OpenAlexSource.RebuildAbstract(w)),
                url,
                producer,
                GreySourceHttp.HostOf(url),
                kind,
                DateTime.TryParse(GreyJson.Str(w, "publication_date"), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null,
                GreySourceHttp.Signals(("citations", GreyJson.Long(w, "cited_by_count")))));
        }
        return records;
    }

    /// <summary>The page where the work itself is published, then its DOI, then its OpenAlex page; http(s) only.</summary>
    private static string AddressOf(JsonElement work, string id)
    {
        if (work.TryGetProperty("primary_location", out var loc) && loc.ValueKind == JsonValueKind.Object)
        {
            string landing = GreySourceHttp.WebAddress(GreyJson.Str(loc, "landing_page_url"));
            if (landing.Length > 0) return landing;
        }
        string doi = GreySourceHttp.WebAddress(GreyJson.Str(work, "doi"));
        return doi.Length > 0 ? doi : GreySourceHttp.WebAddress($"https://openalex.org/{id}");
    }

    private static string SourceName(JsonElement work) =>
        work.TryGetProperty("primary_location", out var loc) && loc.ValueKind == JsonValueKind.Object
        && loc.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object
            ? GreyJson.Str(src, "display_name")
            : "";
}
