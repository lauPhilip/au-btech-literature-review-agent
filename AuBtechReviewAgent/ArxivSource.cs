using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

public class ArxivSource : IAcademicSource
{
    public string SourceName => "arXiv API";
    private readonly List<string> _raw = new();
    public IReadOnlyList<string> LastRawResponses => _raw;

    // arXiv asks API clients for one request at a time, about three seconds apart, and answers bursts with
    // 503. A run sends several search strings and several runs can be active, so every request in the app
    // goes through one gate, spaced out, with the shared wait-and-retry on 429/5xx (OpenSourceHttp).
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    private readonly HttpClient? _client;
    private readonly TimeSpan _spacing;
    private readonly Func<int, TimeSpan>? _backoff;

    /// <param name="client">Test hook: the HTTP client (default: the shared OpenSourceHttp client).</param>
    /// <param name="spacing">Minimum time between two arXiv requests (default 3 seconds).</param>
    /// <param name="backoff">Test hook: wait before retry n (default: OpenSourceHttp's 3, 6, 12, 24 seconds).</param>
    public ArxivSource(HttpClient? client = null, TimeSpan? spacing = null, Func<int, TimeSpan>? backoff = null)
    {
        _client = client;
        _spacing = spacing ?? TimeSpan.FromSeconds(3);
        _backoff = backoff;
    }

    private async Task<string> GetSpacedAsync(string url)
    {
        await Gate.WaitAsync();
        try
        {
            var wait = _lastRequestUtc + _spacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            try { return await OpenSourceHttp.GetStringAsync(url, backoff: _backoff, client: _client); }
            finally { _lastRequestUtc = DateTime.UtcNow; }
        }
        finally { Gate.Release(); }
    }

    public async Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
    {
        _raw.Clear();
        var papers = new List<AcademicPaper>();
        string encodedQuery = Uri.EscapeDataString(query);
        string url = $"https://export.arxiv.org/api/query?search_query=all:{encodedQuery}&max_results={maxResults}";

        try
        {
            string xmlContent = await GetSpacedAsync(url);
            _raw.Add(xmlContent);
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
        catch (Exception ex) when (ex is not HttpRequestException and not TaskCanceledException)
        {
            // A malformed entry is skipped; network errors propagate so the search log shows the source as failed.
            AppLog.For<ArxivSource>().LogWarning("arXiv response could not be parsed: {Message}", ex.Message);
        }

        return papers;
    }
}