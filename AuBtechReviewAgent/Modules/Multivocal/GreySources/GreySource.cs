using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// One grey literature source found by a search: a Q&A post, a repository, an article, a report. What it says is
/// fetched and checked later; this record holds what the search returned, so the selection step can screen it.
/// </summary>
/// <param name="Id">Stable id, prefixed by the source ("stackexchange:123").</param>
/// <param name="Url">The source's own page; always an http(s) address (rule 7).</param>
/// <param name="Producer">Who wrote or published it (author, user, organisation), when known.</param>
/// <param name="Site">Where it was published, e.g. "stackoverflow.com".</param>
/// <param name="Kind">The kind of grey literature (a key of <see cref="MultivocalGuidelines.GreyTypes"/>).</param>
/// <param name="Signals">Counts the source reports, for the quality checklist's impact item (score, views, stars).</param>
public sealed record GreyRecord(
    string Id,
    string Title,
    string Summary,
    string Url,
    string Producer,
    string Site,
    string Kind,
    DateTime? Published,
    IReadOnlyDictionary<string, long> Signals);

/// <summary>
/// A free search over grey literature (decision 1 in docs/roadmap/mlr-todo.md). Each source keeps the raw answers
/// of its last search, so the run can save them exactly as received, as for the academic databases.
/// </summary>
public interface IGreySource
{
    /// <summary>The key used in the plan, e.g. "stackexchange" (see <see cref="MultivocalGuidelines.GreySearches"/>).</summary>
    string Key { get; }

    string Name { get; }

    Task<List<GreyRecord>> SearchAsync(string query, int maxResults);

    IReadOnlyList<string> LastRawResponses { get; }
}

/// <summary>Shared helpers for the grey sources: one HTTP client that accepts compressed answers, and text cleaning.</summary>
public static class GreySourceHttp
{
    /// <summary>Stack Exchange always compresses its answers, so this client decompresses (the academic one does not need to).</summary>
    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(OpenSourceHttp.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public static Task<string> GetAsync(string url, IDictionary<string, string>? headers = null) =>
        OpenSourceHttp.GetStringAsync(url, headers, client: Client);

    /// <summary>HTML from an API turned into one line of plain text, cut to a length that is enough for screening.</summary>
    public static string PlainText(string? html, int maxLength = 1500)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        string text = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Inline formatting disappears without a gap ("<i>summaries</i>."), block elements leave a space.
        text = Regex.Replace(text, @"</?(a|b|i|em|strong|code|span|sub|sup|small|mark)(\s[^>]*)?>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";
    }

    /// <summary>The address when it is an http(s) web address, otherwise empty (rule 7).</summary>
    public static string WebAddress(string? url) => PrismaReviewEngine.IsWebAddress(url) ? url!.Trim() : "";

    public static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";

    public static IReadOnlyDictionary<string, long> Signals(params (string Name, long? Value)[] values) =>
        values.Where(v => v.Value is not null).ToDictionary(v => v.Name, v => v.Value!.Value);
}
