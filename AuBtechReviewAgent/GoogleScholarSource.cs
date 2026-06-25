using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Web;

namespace AuBtechReviewAgent;

public class GoogleScholarSource : IAcademicSource
{
    private readonly string? _apiKey;
    private static readonly HttpClient _httpClient = new();

    public string SourceName => "Google Scholar Gateway";

    public GoogleScholarSource(string? apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public async Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
    {
        var papers = new List<AcademicPaper>();

        if (_apiKey == null)
        {
            Console.WriteLine("[Google Scholar Gateway] Pass skipped: No active SerpApi credential found.");
            return papers;
        }

        try
        {
            string encodedQuery = HttpUtility.UrlEncode(query);
            // Utilizing SerpApi's dedicated Google Scholar engine endpoint
            string requestUrl = $"https://serpapi.com/search.json?engine=google_scholar&q={encodedQuery}&num={maxResults}&api_key={_apiKey}";

            var response = await _httpClient.GetAsync(requestUrl);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Google Scholar Gateway Warning] Endpoint refused connection: {response.StatusCode}");
                return papers;
            }

            string rawJson = await response.Content.ReadAsStringAsync();
            using var jsonDocument = JsonDocument.Parse(rawJson);

            if (jsonDocument.RootElement.TryGetProperty("organic_results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var result in resultsElement.EnumerateArray())
                {
                    string paperId = result.TryGetProperty("result_id", out var idProp) ? $"SCHOLAR_{idProp.GetString()}" : $"SCHOLAR_{Guid.NewGuid().ToString("N").Substring(0,8)}";
                    string title = result.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "Untitled Document" : "Untitled Document";
                    string snippet = result.TryGetProperty("snippet", out var snippetProp) ? snippetProp.GetString() ?? string.Empty : string.Empty;
                    
                    string venue = "Google Scholar Indexed Publication";
                    string dateText = DateTime.UtcNow.ToString("yyyy");
                    var authorsList = new List<string>();

                    if (result.TryGetProperty("publication_info", out var pubInfo))
                    {
                        if (pubInfo.TryGetProperty("summary", out var summaryProp))
                        {
                            string summaryStr = summaryProp.GetString() ?? "";
                            // Basic extraction parse for typical format: "Author A, Author B - Venue Name, 2025 - publisher.com"
                            var segments = summaryStr.Split('-');
                            if (segments.Length > 0)
                            {
                                foreach (var author in segments[0].Split(','))
                                {
                                    string cleanAuthor = author.Trim();
                                    if (!string.IsNullOrEmpty(cleanAuthor)) authorsList.Add(cleanAuthor);
                                }
                            }
                            if (segments.Length > 1)
                            {
                                venue = segments[1].Trim();
                                var yearMatch = Regex.Match(venue, @"\b(20\d{2})\b");
                                if (yearMatch.Success) dateText = yearMatch.Groups[1].Value;
                            }
                        }
                    }

                    if (authorsList.Count == 0) authorsList.Add("Scholar Indexed Authors");

                    // Map fields straight into your strict positional constructor contract
                    papers.Add(new AcademicPaper(
                        paperId,
                        title,
                        snippet,
                        venue,
                        authorsList,
                        dateText
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Google Scholar Gateway Fault] Tracking breakdown: {ex.Message}");
        }

        return papers;
    }
}