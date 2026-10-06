using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// Questions on a Stack Exchange site (Stack Overflow by default), through the free Stack Exchange API 2.3. Without
/// a key the API allows a few hundred requests a day per address, enough for a review; a free key raises that.
/// Score, views and answers are kept for the impact item of the quality checklist.
/// </summary>
public sealed class StackExchangeSource : IGreySource
{
    private const string Api = "https://api.stackexchange.com/2.3";
    private readonly string _site;
    private readonly string _key;
    private readonly List<string> _raw = new();

    public StackExchangeSource(string site = "stackoverflow", string key = "")
    {
        _site = site;
        _key = key;
    }

    public string Key => "stackexchange";
    public string Name => "Stack Exchange";
    public IReadOnlyList<string> LastRawResponses => _raw;

    public string SearchUrl(string query, int maxResults) =>
        $"{Api}/search/advanced?order=desc&sort=relevance&q={Uri.EscapeDataString(query)}&site={Uri.EscapeDataString(_site)}" +
        $"&pagesize={Math.Clamp(maxResults, 1, 100)}&filter=withbody{(string.IsNullOrWhiteSpace(_key) ? "" : $"&key={Uri.EscapeDataString(_key)}")}";

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
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return records;
        foreach (var item in items.EnumerateArray())
        {
            string url = GreySourceHttp.WebAddress(GreyJson.Str(item, "link"));
            if (url.Length == 0) continue;
            long? id = GreyJson.Long(item, "question_id");
            string owner = item.TryGetProperty("owner", out var o) ? WebUtility.HtmlDecode(GreyJson.Str(o, "display_name")) : "";
            records.Add(new GreyRecord(
                $"stackexchange:{id}",
                WebUtility.HtmlDecode(GreyJson.Str(item, "title")),
                GreySourceHttp.PlainText(GreyJson.Str(item, "body")),
                url,
                owner,
                GreySourceHttp.HostOf(url),
                "qa",
                GreyJson.Long(item, "creation_date") is long t ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime : null,
                GreySourceHttp.Signals(("score", GreyJson.Long(item, "score")), ("views", GreyJson.Long(item, "view_count")), ("answers", GreyJson.Long(item, "answer_count")))));
        }
        return records;
    }
}
