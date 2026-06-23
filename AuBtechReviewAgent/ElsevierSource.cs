using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

public class ElsevierSource : IAcademicSource
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    public string SourceName => "ScienceDirect API";

    public ElsevierSource(string apiKey)
    {
        _apiKey = apiKey;
        _httpClient = new HttpClient();

        // Standard developer connection headers without session cookie bindings
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _httpClient.DefaultRequestHeaders.Add("X-ELS-APIKey", _apiKey);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
    {
        var papers = new List<AcademicPaper>();
        
        string[] terms = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string structuredQuery = string.Join(" AND ", terms);
        string encodedQuery = Uri.EscapeDataString(structuredQuery);
        
        // ─── TARGET SCOPUS WITH STANDARD METADATA VIEW ─────────────────────
        string url = $"https://api.elsevier.com/content/search/scopus?query=ALL({encodedQuery})&count={maxResults}&view=STANDARD";
        Console.WriteLine($"\n[Elsevier Engine] Querying Scopus Index URL: {url}");

        try
        {
            var response = await _httpClient.GetAsync(url);
            Console.WriteLine($"[Elsevier Engine] HTTP Response Code: {response.StatusCode}");

            string jsonResponse = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Elsevier Gateway Refused Request. Code {(int)response.StatusCode}. Details: {jsonResponse}");
            }

            using JsonDocument doc = JsonDocument.Parse(jsonResponse);

            // Scopus search array uses the "entry" element root
            if (doc.RootElement.TryGetProperty("search-results", out var results) &&
                results.TryGetProperty("entry", out var entries) &&
                entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    // Ignore error placeholder rows if returned
                    if (entry.TryGetProperty("error", out _)) continue;

                    string id = entry.TryGetProperty("dc:identifier", out var idProp) ? idProp.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                    string title = entry.TryGetProperty("dc:title", out var titleProp) ? titleProp.GetString() ?? "Untitled Document" : "Untitled Document";
                    
                    // Standard Scopus metadata fallbacks for summary tracking
                    string abstractText = "No abstract metadata available.";
                    if (entry.TryGetProperty("dc:description", out var descProp)) abstractText = descProp.GetString() ?? abstractText;
                    else if (entry.TryGetProperty("subtypeDescription", out var subProp)) abstractText = $"Document Classification: {subProp.GetString()}";

                    string rawDate = entry.TryGetProperty("prism:coverDate", out var dateProp) ? dateProp.GetString() ?? "" : "";
                    string year = !string.IsNullOrEmpty(rawDate) && rawDate.Length >= 4 ? rawDate.Substring(0, 4) : "N/A";
                    string journalName = entry.TryGetProperty("prism:publicationName", out var jProp) ? jProp.GetString() ?? "Scopus Indexed Journal" : jProp.GetString() ?? "Scopus Indexed Journal";

                    var authorsList = new List<string>();
                    if (entry.TryGetProperty("dc:creator", out var creatorProp))
                    {
                        authorsList.Add(creatorProp.GetString() ?? "Unknown Author");
                    }
                    if (authorsList.Count == 0) authorsList.Add("Unknown Author");

                    papers.Add(new AcademicPaper(
                        Id: id,
                        Title: title,
                        Abstract: abstractText,
                        PublishedDate: $"Published: {year}",
                        Authors: authorsList,
                        JournalSource: journalName
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Scopus query pipeline failed: {ex.Message}");
        }

        return papers;
    }
}