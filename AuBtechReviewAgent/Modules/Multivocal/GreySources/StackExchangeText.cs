using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>One post (the question or an answer) as the Stack Exchange API gave it.</summary>
/// <param name="Body">The post's HTML, as received; it becomes text through <see cref="PageText"/>.</param>
/// <param name="Licence">The post's licence as the API names it, e.g. "CC BY-SA 4.0".</param>
public sealed record StackExchangePost(long Id, string Body, long Score, bool Accepted, string Author, string AuthorLink, string Link, string Licence);

/// <summary>A question with its answers, best voted first.</summary>
public sealed record StackExchangeThread(long QuestionId, string Title, StackExchangePost Question, IReadOnlyList<StackExchangePost> Answers);

/// <summary>What one batch of questions gave: the threads found, every raw answer as received, or why it stopped.</summary>
/// <param name="Error">Why the batch could not be fetched (a refusal, the day's quota, a wait too long); null when it was.</param>
public sealed record StackExchangeBatch(IReadOnlyDictionary<long, StackExchangeThread> Threads, IReadOnlyList<string> RawResponses, string? Error);

/// <summary>
/// The text of Stack Exchange questions through the API (#453). Stack Overflow refuses automated page requests, so a
/// question the search found through the API is kept from the API too: the question and its answers, with their
/// bodies, up to 100 questions a request. The API's <c>backoff</c> field is obeyed before the next request, and once
/// <c>quota_remaining</c> reaches 0 no further request is sent. Posts are CC BY-SA, so their authors and links are
/// returned for the attribution kept on file.
/// </summary>
public sealed class StackExchangeText
{
    public const int MaxIds = 100;

    /// <summary>A batch never pages through more answers than this many pages of 100.</summary>
    public const int MaxAnswerPages = 10;

    /// <summary>A backoff longer than this ends the batch rather than holding the run up.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private const string Api = "https://api.stackexchange.com/2.3";
    private readonly HttpClient _client;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Func<DateTime> _clock;
    private readonly string _key;
    private readonly object _lock = new();
    private DateTime _notBefore = DateTime.MinValue;
    private bool _quotaUsed;

    /// <summary>A reader of the public API; the app keeps one, so its backoff and quota hold across runs.</summary>
    /// <param name="client">For tests; by default the grey sources' client, which decompresses the API's answers.</param>
    /// <param name="key">A free Stack Exchange API key, which raises the day's quota; optional.</param>
    public StackExchangeText(HttpClient? client = null, Func<TimeSpan, Task>? delay = null, string key = "", Func<DateTime>? clock = null)
    {
        _client = client ?? GreySourceHttp.Client;
        _delay = delay ?? (wait => Task.Delay(wait));
        _key = key;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public string QuestionsUrl(string site, IEnumerable<long> ids) =>
        $"{Api}/questions/{string.Join(';', ids)}?pagesize={MaxIds}&filter=withbody&site={Uri.EscapeDataString(site)}{KeyPart}";

    public string AnswersUrl(string site, IEnumerable<long> ids, int page) =>
        $"{Api}/questions/{string.Join(';', ids)}/answers?order=desc&sort=votes&pagesize={MaxIds}&page={page}&filter=withbody&site={Uri.EscapeDataString(site)}{KeyPart}";

    private string KeyPart => string.IsNullOrWhiteSpace(_key) ? "" : $"&key={Uri.EscapeDataString(_key)}";

    /// <summary>The questions <paramref name="ids"/> (at most <see cref="MaxIds"/>) on <paramref name="site"/>, with their answers.</summary>
    public async Task<StackExchangeBatch> FetchAsync(string site, IReadOnlyList<long> ids)
    {
        if (ids.Count is 0 or > MaxIds) throw new ArgumentOutOfRangeException(nameof(ids), $"Between 1 and {MaxIds} questions a batch.");
        var raw = new List<string>();
        var threads = new Dictionary<long, StackExchangeThread>();

        var (questionsJson, error) = await GetAsync(QuestionsUrl(site, ids), raw);
        if (questionsJson is not { } questions) return new StackExchangeBatch(threads, raw, error);
        var answers = new List<JsonElement>();
        for (int page = 1; page <= MaxAnswerPages; page++)
        {
            var (answersJson, answerError) = await GetAsync(AnswersUrl(site, ids, page), raw);
            if (answersJson is not { } json) return new StackExchangeBatch(threads, raw, answerError);
            answers.AddRange(Items(json));
            if (!(json.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True)) break;
        }

        foreach (var q in Items(questions))
        {
            if (GreyJson.Long(q, "question_id") is not long id) continue;
            long? accepted = GreyJson.Long(q, "accepted_answer_id");
            var own = answers.Where(a => GreyJson.Long(a, "question_id") == id)
                .Select(a => Post(a, GreyJson.Long(a, "answer_id") ?? 0, accepted))
                .GroupBy(a => a.Id).Select(g => g.First())
                .OrderByDescending(a => a.Score).ThenBy(a => a.Id).ToList();
            threads[id] = new StackExchangeThread(id, WebUtility.HtmlDecode(GreyJson.Str(q, "title")), Post(q, id, null), own);
        }
        return new StackExchangeBatch(threads, raw, null);
    }

    /// <summary>One request, after any backoff the API asked for; the parsed answer, or why there is none.</summary>
    private async Task<(JsonElement? Json, string? Error)> GetAsync(string url, List<string> raw)
    {
        TimeSpan wait;
        lock (_lock)
        {
            if (_quotaUsed) return (null, "the Stack Exchange API's quota for the day is used up");
            wait = _notBefore - _clock();
        }
        if (wait > MaxBackoff) return (null, $"the Stack Exchange API asked to wait {Math.Ceiling(wait.TotalMinutes)} minutes, longer than a run waits");
        if (wait > TimeSpan.Zero) await _delay(wait);

        string body;
        int status;
        using (var response = await _client.GetAsync(url))
        {
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync();
        }
        raw.Add(body);

        JsonElement root;
        try { using var doc = JsonDocument.Parse(body); root = doc.RootElement.Clone(); }
        catch (JsonException) { return (null, $"the Stack Exchange API answered {status} without JSON"); }

        lock (_lock)
        {
            if (GreyJson.Long(root, "backoff") is long seconds && seconds > 0)
            {
                var until = _clock() + TimeSpan.FromSeconds(seconds);
                if (until > _notBefore) _notBefore = until;
            }
            if (GreyJson.Long(root, "quota_remaining") is 0) _quotaUsed = true;
        }
        if (GreyJson.Long(root, "error_id") is long errorId)
        {
            string name = GreyJson.Str(root, "error_name");
            string message = PrismaReviewEngine.SanitizeLogMessage(GreyJson.Str(root, "error_message"));
            if (message.Length > 200) message = message[..200];
            return (null, $"the Stack Exchange API refused the request ({errorId} {name}: {message})".Replace(":  )", ")"));
        }
        if (status is < 200 or >= 300) return (null, $"the Stack Exchange API answered {status}");
        return (root, null);
    }

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static StackExchangePost Post(JsonElement item, long id, long? acceptedId)
    {
        string author = "", authorLink = "";
        if (item.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
        {
            author = WebUtility.HtmlDecode(GreyJson.Str(owner, "display_name"));
            authorLink = GreySourceHttp.WebAddress(GreyJson.Str(owner, "link"));
        }
        bool accepted = item.TryGetProperty("is_accepted", out var a) ? a.ValueKind == JsonValueKind.True : acceptedId == id && acceptedId != null;
        return new StackExchangePost(id, GreyJson.Str(item, "body"), GreyJson.Long(item, "score") ?? 0, accepted, author, authorLink,
            GreySourceHttp.WebAddress(GreyJson.Str(item, "link")), GreyJson.Str(item, "content_license"));
    }
}
