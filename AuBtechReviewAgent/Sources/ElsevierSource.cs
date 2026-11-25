using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

public class ElsevierSource : IAcademicSource
{
    // One shared HttpClient for the whole app (a new one per run exhausts sockets under load). The API key
    // differs per user (BYOK), so it is sent per request instead of as a default header.
    private static readonly HttpClient _httpClient = CreateClient();
    private readonly string _apiKey;
    private readonly List<string> _raw = new();
    public string SourceName => "ScienceDirect API";

    /// <summary>The answer of the last search, as received (issue #469), so the run can save it with its SHA-256.</summary>
    public IReadOnlyList<string> LastRawResponses => _raw;

    public ElsevierSource(string apiKey)
    {
        _apiKey = apiKey;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        return client;
    }

    public async Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
    {
        _raw.Clear();
        string[] terms = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string structuredQuery = string.Join(" AND ", terms);
        string encodedQuery = Uri.EscapeDataString(structuredQuery);

        string url = $"https://api.elsevier.com/content/search/scopus?query=ALL({encodedQuery})&count={maxResults}&view=STANDARD";
        AppLog.For<ElsevierSource>().LogDebug("Querying Scopus: {Query}", query);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-ELS-APIKey", _apiKey);
            using var response = await _httpClient.SendAsync(request);
            AppLog.For<ElsevierSource>().LogDebug("Scopus answered {Status}", response.StatusCode);

            string jsonResponse = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Elsevier Gateway Refused Request. Code {(int)response.StatusCode}. Details: {jsonResponse}");
            }

            // The key travels in a header, so the answer should never hold it; it is removed all the same, as no key may reach a run's files.
            _raw.Add(string.IsNullOrEmpty(_apiKey) ? jsonResponse : jsonResponse.Replace(_apiKey, "[key removed]", StringComparison.Ordinal));
            return Parse(jsonResponse, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Scopus query pipeline failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The papers of one Scopus Search answer. The STANDARD view gives only the first author (<c>dc:creator</c>, as
    /// "Liu J."), so such a list ends with <see cref="ApaCitationBuilder.EtAl"/>; the COMPLETE view's <c>author</c>
    /// list is used whole when the answer has it (issue #470).
    /// </summary>
    public static List<AcademicPaper> Parse(string json, DateTime today)
    {
        var papers = new List<AcademicPaper>();
        using JsonDocument doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("search-results", out var results) &&
            results.TryGetProperty("entry", out var entries) &&
            entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                // Ignore error placeholder rows if returned
                if (entry.TryGetProperty("error", out _)) continue;

                string id = Str(entry, "dc:identifier") is { Length: > 0 } idText ? idText : Guid.NewGuid().ToString();
                string title = Str(entry, "dc:title") is { Length: > 0 } t ? t : "Untitled Document";

                // Standard Scopus metadata fallbacks for summary tracking
                string abstractText = "No abstract metadata available.";
                if (entry.TryGetProperty("dc:description", out var descProp)) abstractText = descProp.GetString() ?? abstractText;
                else if (entry.TryGetProperty("subtypeDescription", out var subProp)) abstractText = $"Document Classification: {subProp.GetString()}";

                string? doi = Str(entry, "prism:doi") is { Length: > 0 } d ? d : null;
                string journalName = Str(entry, "prism:publicationName") is { Length: > 0 } j ? j : "Scopus Indexed Journal";

                papers.Add(new AcademicPaper(
                    Id: id,
                    Title: title,
                    Abstract: abstractText,
                    PublishedDate: $"Published: {Year(Str(entry, "prism:coverDate"), doi, today)}",
                    Authors: Authors(entry),
                    JournalSource: journalName,
                    Doi: doi
                ));
            }
        }
        return papers;
    }

    /// <summary>
    /// The year to cite. <c>prism:coverDate</c> is the date of the issue, which for an article in press can lie in the
    /// next year, after the day of the search. Such an article is cited by the year it went online, which an Elsevier
    /// DOI carries ("10.1016/j.ress.2026.113770"); without one it is cited as in press, so with no year (issue #470).
    /// </summary>
    public static string Year(string coverDate, string? doi, DateTime today)
    {
        bool dated = DateTime.TryParseExact(coverDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cover);
        if (!dated) return coverDate.Length >= 4 && Regex.IsMatch(coverDate[..4], @"^\d{4}$") ? coverDate[..4] : "N/A";
        if (cover.Date <= today.Date) return cover.Year.ToString(CultureInfo.InvariantCulture);
        var online = Regex.Match(doi ?? "", @"^10\.1016/j\.[a-z0-9]+\.((?:19|20)\d{2})\.", RegexOptions.IgnoreCase);
        return online.Success && int.Parse(online.Groups[1].Value, CultureInfo.InvariantCulture) <= today.Year ? online.Groups[1].Value : "in press";
    }

    private static List<string> Authors(JsonElement entry)
    {
        var authors = new List<string>();
        if (entry.TryGetProperty("author", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                string given = Str(a, "given-name"), surname = Str(a, "surname");
                string name = surname.Length > 0 ? (given.Length > 0 ? $"{surname}, {given}" : surname) : Str(a, "authname");
                if (name.Length > 0) authors.Add(name);
            }
            if (authors.Count > 0) return authors;
        }
        string creator = Str(entry, "dc:creator");
        if (creator.Length > 0)
        {
            authors.Add(creator);
            authors.Add(ApaCitationBuilder.EtAl);
        }
        return authors;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
}
