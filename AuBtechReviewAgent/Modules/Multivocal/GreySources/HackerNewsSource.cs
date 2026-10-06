using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// Stories on Hacker News, through the free search API that Algolia runs for it. A story usually links to a
/// practitioner article elsewhere; that article is the source, and the discussion's points and comments are kept
/// as signals. A story without a link (Ask HN) points to its own discussion page.
/// </summary>
public sealed class HackerNewsSource : IGreySource
{
    private const string Api = "https://hn.algolia.com/api/v1";
    private readonly List<string> _raw = new();

    public string Key => "hackernews";
    public string Name => "Hacker News";
    public IReadOnlyList<string> LastRawResponses => _raw;

    public static string SearchUrl(string query, int maxResults) =>
        $"{Api}/search?query={Uri.EscapeDataString(query)}&tags=story&hitsPerPage={Math.Clamp(maxResults, 1, 100)}";

    public async Task<List<GreyRecord>> SearchAsync(string query, int maxResults)
    {
        _raw.Clear();
        string json = await GreySourceHttp.GetAsync(SearchUrl(query, maxResults));
        _raw.Add(json);
        return Parse(json);
    }

    public static List<GreyRecord> Parse(string json)
    {
        var records = new List<GreyRecord>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return records;
        foreach (var hit in hits.EnumerateArray())
        {
            string id = GreyJson.Str(hit, "objectID");
            string title = GreyJson.Str(hit, "title");
            if (id.Length == 0 || title.Length == 0) continue;
            string discussion = $"https://news.ycombinator.com/item?id={Uri.EscapeDataString(id)}";
            string url = GreySourceHttp.WebAddress(GreyJson.Str(hit, "url"));
            if (url.Length == 0) url = discussion;
            records.Add(new GreyRecord(
                $"hackernews:{id}",
                title,
                GreySourceHttp.PlainText(GreyJson.Str(hit, "story_text")),
                url,
                GreyJson.Str(hit, "author"),
                GreySourceHttp.HostOf(url),
                url == discussion ? "qa" : "blogs",
                DateTime.TryParse(GreyJson.Str(hit, "created_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null,
                GreySourceHttp.Signals(("points", GreyJson.Long(hit, "points")), ("comments", GreyJson.Long(hit, "num_comments")))));
        }
        return records;
    }
}
