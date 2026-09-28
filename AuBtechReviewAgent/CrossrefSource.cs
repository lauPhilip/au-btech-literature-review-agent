using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// Crossref (api.crossref.org): the DOI registration agency's open metadata for ~160 million records. Best for
/// exact bibliographic metadata of published journal and conference papers; abstracts are often missing.
/// </summary>
public class CrossrefSource : IAcademicSource
{
    private readonly string _contactEmail;
    private readonly List<string> _raw = new();

    public CrossrefSource(string contactEmail = "") => _contactEmail = contactEmail;

    public string SourceName => "Crossref";
    public IReadOnlyList<string> LastRawResponses => _raw;

    public async Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
    {
        _raw.Clear();
        // Only research outputs: journal and proceedings articles and posted preprints.
        string url = OpenSourceHttp.WithMailto(
            $"https://api.crossref.org/works?query.bibliographic={Uri.EscapeDataString(query)}&rows={Math.Clamp(maxResults, 1, 100)}" +
            "&filter=type:journal-article,type:proceedings-article,type:posted-content&select=DOI,title,author,container-title,issued,type,abstract,URL,link",
            _contactEmail);
        string json = await OpenSourceHttp.GetStringAsync(url);
        _raw.Add(json);
        return Parse(json);
    }

    public static List<AcademicPaper> Parse(string json)
    {
        var papers = new List<AcademicPaper>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("message", out var msg) || !msg.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return papers;

        foreach (var it in items.EnumerateArray())
        {
            string doi = Str(it, "DOI");
            if (doi.Length == 0) continue;

            var authors = it.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => $"{Str(x, "given")} {Str(x, "family")}".Trim()).Where(n => n.Length > 0).ToList()
                : new List<string>();

            int year = 0;
            if (it.TryGetProperty("issued", out var issued) && issued.TryGetProperty("date-parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0)
            {
                var first = parts[0];
                if (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0 && first[0].ValueKind == JsonValueKind.Number) year = first[0].GetInt32();
            }

            string type = Str(it, "type");
            string venue = type == "posted-content" ? "" : First(it, "container-title");

            string? pdf = null;
            if (it.TryGetProperty("link", out var links) && links.ValueKind == JsonValueKind.Array)
                pdf = links.EnumerateArray()
                    .Where(l => Str(l, "content-type") == "application/pdf" && Str(l, "content-version") == "vor")
                    .Select(l => Str(l, "URL")).FirstOrDefault(u => u.Length > 0);

            papers.Add(new AcademicPaper(
                Id: $"CROSSREF_{doi.ToLowerInvariant()}",
                Title: First(it, "title"),
                Abstract: StripJats(Str(it, "abstract")),
                PublishedDate: year > 0 ? $"Published: {year}" : "Published: ",
                Authors: authors,
                JournalSource: venue,
                Doi: doi,
                Url: Str(it, "URL") is { Length: > 0 } u ? u : $"https://doi.org/{doi}",
                PdfUrl: pdf));
        }
        return papers;
    }

    /// <summary>Crossref abstracts are JATS XML ("&lt;jats:p&gt;...&lt;/jats:p&gt;"); keep only the text.</summary>
    public static string StripJats(string jats)
    {
        if (string.IsNullOrWhiteSpace(jats)) return "";
        string text = Regex.Replace(jats, "<jats:title>.*?</jats:title>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
    }

    private static string First(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 && v[0].ValueKind == JsonValueKind.String
            ? v[0].GetString() ?? "" : "";

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
