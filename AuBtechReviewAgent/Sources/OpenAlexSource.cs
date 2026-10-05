using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// OpenAlex (openalex.org): a free, open catalogue of about 250 million scholarly works with DOIs, abstracts,
/// venues, open-access links and citation links. Replaces the Google Scholar / ResearchGate route, which went
/// through SerpApi (paid scraping of sites whose terms do not allow it). No API key is needed.
/// </summary>
public class OpenAlexSource : IAcademicSource, ICitationGraph
{
    private const string Api = "https://api.openalex.org";
    private readonly string _contactEmail;
    private readonly List<string> _raw = new();

    public OpenAlexSource(string contactEmail = "") => _contactEmail = contactEmail;

    public string SourceName => "OpenAlex";
    public IReadOnlyList<string> LastRawResponses => _raw;

    public async Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
    {
        _raw.Clear();
        string url = OpenSourceHttp.WithMailto(
            $"{Api}/works?search={Uri.EscapeDataString(query)}&per-page={Math.Clamp(maxResults, 1, 200)}", _contactEmail);
        string json = await OpenSourceHttp.GetStringAsync(url);
        _raw.Add(json);
        return ParseWorks(json);
    }

    /// <summary>
    /// Citation chaining (snowballing) for one paper: up to <paramref name="perDirection"/> works it cites
    /// (backward) and that cite it (forward). The paper is looked up by DOI.
    /// </summary>
    public async Task<CitationNeighbours> GetCitationNeighboursAsync(string doi, int perDirection)
    {
        // Local list (not _raw): chaining calls for several papers can run at the same time.
        var raw = new List<string>();
        string workJson = await OpenSourceHttp.GetStringAsync(OpenSourceHttp.WithMailto($"{Api}/works/doi:{Uri.EscapeDataString(doi)}", _contactEmail));
        raw.Add(workJson);

        using var doc = JsonDocument.Parse(workJson);
        var root = doc.RootElement;
        string id = ShortId(Str(root, "id"));
        var referenced = root.TryGetProperty("referenced_works", out var refs) && refs.ValueKind == JsonValueKind.Array
            ? refs.EnumerateArray().Select(r => ShortId(r.GetString())).Where(r => r.Length > 0).Take(perDirection).ToList()
            : new List<string>();

        var backward = new List<AcademicPaper>();
        if (referenced.Count > 0)
        {
            string backJson = await OpenSourceHttp.GetStringAsync(OpenSourceHttp.WithMailto(
                $"{Api}/works?filter=openalex:{string.Join("|", referenced)}&per-page={referenced.Count}", _contactEmail));
            raw.Add(backJson);
            backward = ParseWorks(backJson);
        }

        var forward = new List<AcademicPaper>();
        if (id.Length > 0 && perDirection > 0)
        {
            string fwdJson = await OpenSourceHttp.GetStringAsync(OpenSourceHttp.WithMailto(
                $"{Api}/works?filter=cites:{id}&sort=cited_by_count:desc&per-page={perDirection}", _contactEmail));
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
                PdfUrl: pdf));
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
