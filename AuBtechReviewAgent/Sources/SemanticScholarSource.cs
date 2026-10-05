using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// Semantic Scholar Academic Graph API (api.semanticscholar.org): free, strong coverage of computer science,
/// with abstracts, DOIs and open-access PDF links. Works without a key (shared rate limit); a key is optional.
/// </summary>
public class SemanticScholarSource : IAcademicSource
{
    private const string Fields = "title,abstract,year,authors,venue,externalIds,openAccessPdf,url,publicationTypes";
    private readonly string _apiKey;
    private readonly List<string> _raw = new();

    // Semantic Scholar rate-limits per key, and all callers without a key share one pool that often answers
    // 429. Requests from every run in this app therefore go one at a time, spaced out (1 s with a key, 3 s
    // without), instead of several runs and search strings hitting it at once.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    public SemanticScholarSource(string apiKey = "") => _apiKey = apiKey;

    /// <summary>One request at a time across the whole app, at least <paramref name="spacing"/> apart.</summary>
    private static async Task<string> GetSpacedAsync(string url, Dictionary<string, string>? headers, TimeSpan spacing)
    {
        await Gate.WaitAsync();
        try
        {
            var wait = _lastRequestUtc + spacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            try { return await OpenSourceHttp.GetStringAsync(url, headers); }
            finally { _lastRequestUtc = DateTime.UtcNow; }
        }
        finally { Gate.Release(); }
    }

    public string SourceName => "Semantic Scholar";
    public IReadOnlyList<string> LastRawResponses => _raw;

    public async Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
    {
        _raw.Clear();
        string url = $"https://api.semanticscholar.org/graph/v1/paper/search?query={Uri.EscapeDataString(query)}&limit={Math.Clamp(maxResults, 1, 100)}&fields={Fields}";
        bool hasKey = SourceCatalog.IsUsableKey(_apiKey);
        var headers = hasKey ? new Dictionary<string, string> { ["x-api-key"] = _apiKey } : null;
        string json = await GetSpacedAsync(url, headers, TimeSpan.FromSeconds(hasKey ? 1 : 3));
        _raw.Add(json);
        return Parse(json);
    }

    public static List<AcademicPaper> Parse(string json)
    {
        var papers = new List<AcademicPaper>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return papers;

        foreach (var p in data.EnumerateArray())
        {
            string id = Str(p, "paperId");
            if (id.Length == 0) continue;
            int year = p.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : 0;

            var authors = p.TryGetProperty("authors", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => Str(x, "name")).Where(n => n.Length > 0).ToList()
                : new List<string>();

            string? doi = null, arxiv = null;
            if (p.TryGetProperty("externalIds", out var ext) && ext.ValueKind == JsonValueKind.Object)
            {
                doi = Str(ext, "DOI") is { Length: > 0 } d ? d : null;
                arxiv = Str(ext, "ArXiv") is { Length: > 0 } x ? x : null;
            }
            // arXiv papers without a publisher DOI still have the official arXiv DataCite DOI.
            if (doi == null && arxiv != null) doi = $"10.48550/arXiv.{arxiv}";

            string venue = Str(p, "venue");
            if (venue.Contains("arXiv", StringComparison.OrdinalIgnoreCase)) venue = "";

            string? pdf = p.TryGetProperty("openAccessPdf", out var oa) && oa.ValueKind == JsonValueKind.Object && Str(oa, "url") is { Length: > 0 } u ? u : null;

            papers.Add(new AcademicPaper(
                Id: $"S2_{id}",
                Title: Str(p, "title"),
                Abstract: Str(p, "abstract"),
                PublishedDate: year > 0 ? $"Published: {year}" : "Published: ",
                Authors: authors,
                JournalSource: venue,
                Doi: doi,
                Url: Str(p, "url") is { Length: > 0 } link ? link : $"https://www.semanticscholar.org/paper/{id}",
                PdfUrl: pdf));
        }
        return papers;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
