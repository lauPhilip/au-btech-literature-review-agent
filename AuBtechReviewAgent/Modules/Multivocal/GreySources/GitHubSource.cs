using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// Repositories on GitHub, through its free search API (a few searches a minute without a token, more with one).
/// The repository's description is what the search returns; its README is fetched later with the other source
/// texts. Stars and forks are kept as signals.
/// </summary>
public sealed class GitHubSource : IGreySource
{
    private const string Api = "https://api.github.com";
    private readonly string _token;
    private readonly List<string> _raw = new();

    public GitHubSource(string token = "") => _token = token;

    public string Key => "github";
    public string Name => "GitHub";
    public IReadOnlyList<string> LastRawResponses => _raw;

    public static string SearchUrl(string query, int maxResults) =>
        $"{Api}/search/repositories?q={Uri.EscapeDataString(query)}&per_page={Math.Clamp(maxResults, 1, 100)}";

    public async Task<List<GreyRecord>> SearchAsync(string query, int maxResults)
    {
        _raw.Clear();
        var headers = new Dictionary<string, string> { ["Accept"] = "application/vnd.github+json", ["X-GitHub-Api-Version"] = "2022-11-28" };
        if (!string.IsNullOrWhiteSpace(_token)) headers["Authorization"] = $"Bearer {_token}";
        string json = await GreySourceHttp.GetAsync(SearchUrl(query, maxResults), headers);
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
            string url = GreySourceHttp.WebAddress(GreyJson.Str(item, "html_url"));
            string name = GreyJson.Str(item, "full_name");
            if (url.Length == 0 || name.Length == 0) continue;
            records.Add(new GreyRecord(
                $"github:{name}",
                name,
                GreySourceHttp.PlainText(GreyJson.Str(item, "description")),
                url,
                item.TryGetProperty("owner", out var o) ? GreyJson.Str(o, "login") : "",
                "github.com",
                "code",
                DateTime.TryParse(GreyJson.Str(item, "updated_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null,
                GreySourceHttp.Signals(("stars", GreyJson.Long(item, "stargazers_count")), ("forks", GreyJson.Long(item, "forks_count")))));
        }
        return records;
    }
}
