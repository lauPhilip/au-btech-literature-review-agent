using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// OpenAlex (openalex.org): a free, open catalogue of about 250 million scholarly works with DOIs, abstracts,
/// venues, open-access links and citation links. Replaces the Google Scholar / ResearchGate route, which went
/// through SerpApi (paid scraping of sites whose terms do not allow it). An API key (OpenSources:OpenAlexApiKey) is
/// sent as an Authorization header, so it never appears in a URL, a log line or the archived raw responses.
/// </summary>
public class OpenAlexSource : IAcademicSource, ICitationGraph
{
    private const string Api = "https://api.openalex.org";
    private readonly string _contactEmail;
    private readonly IDictionary<string, string>? _headers;
    private readonly List<string> _raw = new();

    public OpenAlexSource(string contactEmail = "", string apiKey = "")
    {
        _contactEmail = contactEmail;
        _headers = AuthHeaders(apiKey);
    }

    /// <summary>The request headers that carry the API key, or null without one. Public for unit tests.</summary>
    public static IDictionary<string, string>? AuthHeaders(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : new Dictionary<string, string> { ["Authorization"] = "Bearer " + apiKey.Trim() };

    private Task<string> GetAsync(string url) => OpenSourceHttp.GetStringAsync(OpenSourceHttp.WithMailto(url, _contactEmail), _headers);

    public string SourceName => "OpenAlex";
    public IReadOnlyList<string> LastRawResponses => _raw;

    public async Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
    {
        _raw.Clear();
        string json = await GetAsync($"{Api}/works?search={Uri.EscapeDataString(query)}&per-page={Math.Clamp(maxResults, 1, 200)}");
        _raw.Add(json);
        return ParseWorks(json);
    }

    /// <summary>
    /// Citation chaining (snowballing) for one paper: up to <paramref name="perDirection"/> works it cites
    /// (backward) and that cite it (forward). The paper is looked up by DOI.
    /// </summary>
    public Task<CitationNeighbours> GetCitationNeighboursAsync(string doi, int perDirection) => NeighboursAsync(doi, perDirection, perDirection);

    /// <summary>
    /// The most OpenAlex returns in one answer and the most ids one filter may list, so a seed's pool costs one request
    /// per direction (OpenAlex: per-page at most 200, an OR filter at most 100 values).
    /// </summary>
    public const int MaxPoolPerDirection = 100;

    /// <summary>
    /// A seed study's wider pool, each work with its citation count: up to <paramref name="poolPerDirection"/> of the works
    /// it cites (in the order OpenAlex lists them) and of the works citing it, most cited first. The caller ranks them.
    /// </summary>
    public Task<CitationNeighbours> GetCitationPoolAsync(string doi, int poolPerDirection)
    {
        int pool = Math.Clamp(poolPerDirection, 1, MaxPoolPerDirection);
        return NeighboursAsync(doi, pool, pool);
    }

    private async Task<CitationNeighbours> NeighboursAsync(string doi, int backwardCount, int forwardCount)
    {
        // Local list (not _raw): chaining calls for several papers can run at the same time.
        var raw = new List<string>();
        string workJson = await GetAsync($"{Api}/works/doi:{Uri.EscapeDataString(doi)}");
        raw.Add(workJson);

        using var doc = JsonDocument.Parse(workJson);
        var root = doc.RootElement;
        string id = ShortId(Str(root, "id"));
        var referenced = root.TryGetProperty("referenced_works", out var refs) && refs.ValueKind == JsonValueKind.Array
            ? refs.EnumerateArray().Select(r => ShortId(r.GetString())).Where(r => r.Length > 0).Take(backwardCount).ToList()
            : new List<string>();

        var backward = new List<AcademicPaper>();
        if (referenced.Count > 0)
        {
            string backJson = await GetAsync($"{Api}/works?filter=openalex:{string.Join("|", referenced)}&per-page={referenced.Count}");
            raw.Add(backJson);
            backward = ParseWorks(backJson);
        }

        var forward = new List<AcademicPaper>();
        if (id.Length > 0 && forwardCount > 0)
        {
            string fwdJson = await GetAsync($"{Api}/works?filter=cites:{id}&sort=cited_by_count:desc&per-page={forwardCount}");
            raw.Add(fwdJson);
            forward = ParseWorks(fwdJson);
        }
        return new CitationNeighbours(backward, forward, raw);
    }

    /// <summary>Parses an OpenAlex /works list response. Public so it can be unit-tested with a stored response.</summary>
    public static List<AcademicPaper> ParseWorks(string json)
    {
        var papers = new List<AcademicPaper>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return papers;

        foreach (var w in results.EnumerateArray())
        {
            string id = ShortId(Str(w, "id"));
            if (id.Length == 0) continue;
            string title = Str(w, "display_name");
            if (title.Length == 0) title = Str(w, "title");

            int year = w.TryGetProperty("publication_year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : 0;
            int? citedBy = w.TryGetProperty("cited_by_count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int cites) && cites >= 0 ? cites : null;

            var authors = new List<string>();
            if (w.TryGetProperty("authorships", out var auths) && auths.ValueKind == JsonValueKind.Array)
                foreach (var a in auths.EnumerateArray())
                    if (a.TryGetProperty("author", out var author)) { string n = Str(author, "display_name"); if (n.Length > 0) authors.Add(n); }

            string venue = "";
            if (w.TryGetProperty("primary_location", out var loc) && loc.ValueKind == JsonValueKind.Object
                && loc.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
            {
                venue = Str(src, "display_name");
                // OpenAlex marks preprint servers as "repository"; the citation builder treats a blank venue as a preprint.
                if (Str(src, "type") == "repository" && !venue.Contains("arXiv", StringComparison.OrdinalIgnoreCase)) venue = "";
                if (venue.Contains("arXiv", StringComparison.OrdinalIgnoreCase)) venue = "";
            }

            // OpenAlex gives the DOI as a URL; keep the bare DOI like the other sources.
            string? doi = Str(w, "doi") is { Length: > 0 } d ? System.Text.RegularExpressions.Regex.Replace(d, @"^https?://(dx\.)?doi\.org/", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;
            string? pdf = null;
            if (w.TryGetProperty("best_oa_location", out var oa) && oa.ValueKind == JsonValueKind.Object) pdf = NullIfEmpty(Str(oa, "pdf_url"));

            papers.Add(new AcademicPaper(
                Id: $"OPENALEX_{id}",
                Title: title,
                Abstract: RebuildAbstract(w),
                PublishedDate: year > 0 ? $"Published: {year}" : "Published: ",
                Authors: authors,
                JournalSource: venue,
                Doi: doi,
                Url: $"https://openalex.org/{id}",
                PdfUrl: pdf,
                CitedByCount: citedBy));
        }
        return papers;
    }

    /// <summary>OpenAlex ships abstracts as an inverted index (word -> positions); put the words back in order.</summary>
    public static string RebuildAbstract(JsonElement work)
    {
        if (!work.TryGetProperty("abstract_inverted_index", out var inv) || inv.ValueKind != JsonValueKind.Object) return "";
        var positions = new SortedDictionary<int, string>();
        foreach (var word in inv.EnumerateObject())
            if (word.Value.ValueKind == JsonValueKind.Array)
                foreach (var pos in word.Value.EnumerateArray())
                    if (pos.ValueKind == JsonValueKind.Number) positions[pos.GetInt32()] = word.Name;
        return string.Join(" ", positions.Values);
    }

    private static string ShortId(string? openAlexUrl) =>
        string.IsNullOrWhiteSpace(openAlexUrl) ? "" : openAlexUrl.Trim().Split('/').Last();

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
