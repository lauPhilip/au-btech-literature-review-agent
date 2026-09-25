using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace AuBtechReviewAgent;

public class IeeeXploreSource : IAcademicSource
{
    private readonly string? _apiKey;
    private static readonly HttpClient _httpClient = new();

    public string SourceName => "IEEE Xplore API";

    public IeeeXploreSource(string? apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public async Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
    {
        var papers = new List<AcademicPaper>();

        if (_apiKey == null)
        {
            Console.WriteLine("[IEEE Xplore Gateway] Skipped retrieval pass: No active app credential token found.");
            return papers;
        }

        try
        {
            string encodedQuery = HttpUtility.UrlEncode(query);
            string requestUrl = $"https://ieeexploreapi.ieee.org/api/v1/search/articles?apikey={_apiKey}&querytext={encodedQuery}&max_records={maxResults}&format=json";

            var response = await _httpClient.GetAsync(requestUrl);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[IEEE Xplore Gateway Warning] Retrieval cycle refused connection: {response.StatusCode}");
                return papers;
            }

            string rawJson = await response.Content.ReadAsStringAsync();
            using var jsonDocument = JsonDocument.Parse(rawJson);

            if (jsonDocument.RootElement.TryGetProperty("articles", out var articlesElement) && articlesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var article in articlesElement.EnumerateArray())
                {
                    string articleId = article.TryGetProperty("article_number", out var idProp) ? $"IEEE_{idProp.GetString()}" : $"IEEE_{Guid.NewGuid()}";
                    string title = article.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "Untitled Document" : "Untitled Document";
                    string abstractText = article.TryGetProperty("abstract", out var absProp) ? absProp.GetString() ?? string.Empty : string.Empty;
                    string venue = article.TryGetProperty("publication_title", out var publicationProp) ? publicationProp.GetString() ?? "IEEE Publication" : "IEEE Publication";
                    // publication_year is the reliable field; publication_date is free text ("12-15 Nov. 2025").
                    string dateText = article.TryGetProperty("publication_year", out var yearProp)
                        ? (yearProp.ValueKind == JsonValueKind.Number ? yearProp.GetInt32().ToString() : yearProp.GetString() ?? "")
                        : article.TryGetProperty("publication_date", out var dateProp) ? dateProp.GetString() ?? "" : "";
                    string? doi = article.TryGetProperty("doi", out var doiProp) ? doiProp.GetString() : null;
                    string? htmlUrl = article.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;
                    
                    var authorsList = new List<string>();
                    if (article.TryGetProperty("authors", out var authorsRoot) && authorsRoot.ValueKind == JsonValueKind.Object && authorsRoot.TryGetProperty("authors", out var authorsArray) && authorsArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var authorObj in authorsArray.EnumerateArray())
                        {
                            if (authorObj.TryGetProperty("full_name", out var nameProp))
                            {
                                authorsList.Add(nameProp.GetString() ?? "Unknown Author");
                            }
                        }
                    }

                    // No invented fallback author: an empty list renders as an author-less APA reference.
                    // Argument order is (Id, Title, Abstract, PublishedDate, Authors, JournalSource) - the date
                    // and venue used to be swapped here, like the Scholar/ResearchGate bug fixed earlier.
                    papers.Add(new AcademicPaper(
                        articleId,
                        title,
                        abstractText,
                        $"Published: {dateText}",
                        authorsList,
                        venue,
                        doi,
                        htmlUrl
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IEEE Xplore Adapter Interface Fault] Tracking breakdown: {ex.Message}");
        }

        return papers;
    }
}