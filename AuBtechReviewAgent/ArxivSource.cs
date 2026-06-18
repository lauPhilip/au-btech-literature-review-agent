using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AuBtechReviewAgent;

public class ArxivSource : IAcademicSource
{
    private readonly HttpClient _httpClient;
    public string SourceName => "arXiv API";

    public ArxivSource()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AU-BTech-Literature-Review-Agent/1.0");
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

                papers.Add(new AcademicPaper(id, title, summary, $"Published: {publishedYear}", authors, journalRef));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching data from arXiv: {ex.Message}");
        }

        return papers;
    }
}