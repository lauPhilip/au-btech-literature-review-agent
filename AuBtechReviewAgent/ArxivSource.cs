using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AuBtechReviewAgent;

public class ArxivSource : IAcademicSource
{
    // One shared HttpClient for the whole app; creating one per run exhausts sockets on a busy server.
    private static readonly HttpClient _httpClient = CreateClient();
    public string SourceName => "arXiv API";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("User-Agent", "AU-BTech-Literature-Review-Agent/1.0");
        return client;
    }

 public async Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
    {
        var papers = new List<AcademicPaper>();
        string encodedQuery = Uri.EscapeDataString(query);
        string url = $"https://export.arxiv.org/api/query?search_query=all:{encodedQuery}&max_results={maxResults}";

        try
        {
            string xmlContent = await _httpClient.GetStringAsync(url);
            
            // ─── ADD THIS LINE TO FIX THE COMPILATION ERROR ───
            XDocument doc = XDocument.Parse(xmlContent);
            
            XNamespace ns = "http://www.w3.org/2005/Atom";
            XNamespace arxivNs = "http://arxiv.org/schemas/atom"; 

            var entries = doc.Root?.Elements(ns + "entry") ?? Enumerable.Empty<XElement>();

            foreach (var entry in entries)
            {
                string id = entry.Element(ns + "id")?.Value ?? Guid.NewGuid().ToString();
                string title = entry.Element(ns + "title")?.Value?.Replace("\n", " ").Trim() ?? "Untitled";
                string summary = entry.Element(ns + "summary")?.Value?.Replace("\n", " ").Trim() ?? "No abstract provided.";
                
                string rawDate = entry.Element(ns + "published")?.Value ?? "";
                string publishedYear = !string.IsNullOrEmpty(rawDate) && rawDate.Length >= 4 
                    ? rawDate.Substring(0, 4) 
                    : "N/A";

                var authors = entry.Elements(ns + "author")
                    .Select(a => a.Element(ns + "name")?.Value ?? "")
                    .Where(name => !string.IsNullOrEmpty(name))
                    .ToList();

                string journalRef = entry.Element(arxivNs + "journal_ref")?.Value?.Trim() ?? "";
                if (string.IsNullOrEmpty(journalRef))
                {
                    journalRef = $"arXiv Preprint Repository (arXiv:{id.Split('/').Last()})";
                }

                // Every arXiv paper has an official DataCite DOI of the form 10.48550/arXiv.<id> (no version
                // suffix). Previously the raw id ("2607.02703v1") was pasted after https://doi.org/, which
                // produced a link that does not resolve.
                string arxivId = id.Split("/abs/").Last();
                string bareArxivId = Regex.Replace(arxivId, @"v\d+$", "");
                string? doi = Regex.IsMatch(bareArxivId, @"^\d{4}\.\d{4,5}$") ? $"10.48550/arXiv.{bareArxivId}" : null;
                string landingUrl = $"https://arxiv.org/abs/{arxivId}";

                papers.Add(new AcademicPaper(id, title, summary, $"Published: {publishedYear}", authors, journalRef, doi, landingUrl));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching data from arXiv: {ex.Message}");
        }

        return papers;
    }
}