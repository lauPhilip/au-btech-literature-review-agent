using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace AuBtechReviewAgent;

public class ResearchGateSource : IAcademicSource
{
    private readonly string? _apiKey;
    private static readonly HttpClient _httpClient = new();

    public string SourceName => "ResearchGate Gateway";

    public ResearchGateSource(string? apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public async Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
    {
        var papers = new List<AcademicPaper>();

        if (_apiKey == null)
        {
            Console.WriteLine("[ResearchGate Gateway] Pass skipped: No active credential token available.");
            return papers;
        }

        try
        {
            string encodedQuery = HttpUtility.UrlEncode(query);
            // Route through Google Scholar engine clustering, specifying standard domain constraints
            string requestUrl = $"https://serpapi.com/search.json?engine=google_scholar&q={encodedQuery}+site:researchgate.net&num={maxResults}&api_key={_apiKey}";

            var response = await _httpClient.GetAsync(requestUrl);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[ResearchGate Gateway Warning] Endpoint rejected loop query: {response.StatusCode}");
                return papers;
            }

            string rawJson = await response.Content.ReadAsStringAsync();
            using var jsonDocument = JsonDocument.Parse(rawJson);

            if (jsonDocument.RootElement.TryGetProperty("organic_results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var result in resultsElement.EnumerateArray())
                {
                    string paperId = result.TryGetProperty("result_id", out var idProp) ? $"RG_{idProp.GetString()}" : $"RG_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                    string title = result.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "Untitled Document" : "Untitled Document";
                    string snippet = result.TryGetProperty("snippet", out var snippetProp) ? snippetProp.GetString() ?? string.Empty : string.Empty;
                    
                    string venue = "ResearchGate Institutional Preprint";
                    string dateText = DateTime.UtcNow.ToString("yyyy");
                    var authorsList = new List<string>();

                    if (result.TryGetProperty("publication_info", out var pubInfo) && pubInfo.TryGetProperty("summary", out var summaryProp))
                    {
                        string summaryStr = summaryProp.GetString() ?? "";
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
                            var yearMatch = System.Text.RegularExpressions.Regex.Match(venue, @"\b(20\d{2})\b");
                            if (yearMatch.Success) dateText = yearMatch.Groups[1].Value;
                        }
                    }

                    if (authorsList.Count == 0) authorsList.Add("ResearchGate Independent Researcher");

                    // Map fields directly into your strict constructor signature
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
            Console.WriteLine($"[ResearchGate Gateway Fault] Retrieval failure during stream execution: {ex.Message}");
        }

        return papers;
    }
}