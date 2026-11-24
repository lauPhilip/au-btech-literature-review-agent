using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>A raw API answer saved as received, with its SHA-256.</summary>
public sealed record ApiResponseFile(string File, string Sha256);

/// <summary>Who wrote a part of a kept text, for its licence's attribution (CC BY-SA for Stack Exchange).</summary>
/// <param name="Part">"question" or "answer 2".</param>
/// <param name="Url">The post's own address.</param>
public sealed record ContentCredit(string Part, string Author, string AuthorUrl, string Url, string Licence);

/// <summary>
/// The kept text of a Stack Exchange question (#453). Stack Overflow refuses automated page requests, so the question
/// and its answers are kept from the API the search used: in a fixed layout (the title, the question, then each answer
/// with its score and whether it was accepted), with the raw API answers saved beside the kept texts and their SHA-256
/// on file, and each post's author, link and licence on file. Like a page's text, the raw answers stay in the run
/// folder and are not in the archive; the reference still points to the question's page.
/// </summary>
public sealed partial class MultivocalPages
{
    public const string StackExchangeBasisPrefix = "Stack Exchange API";

    private static readonly Regex QuestionPath = new(@"/questions/(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The site and question id of a source the Stack Exchange search found; null for any other source.</summary>
    public static (string Site, long Id)? StackExchangeQuestion(GreyRecord record)
    {
        if (!record.Id.StartsWith("stackexchange:", StringComparison.Ordinal) || record.Site.Length == 0) return null;
        if (long.TryParse(record.Id["stackexchange:".Length..], out long id) && id > 0) return (record.Site, id);
        var m = QuestionPath.Match(record.Url);
        return m.Success && long.TryParse(m.Groups[1].Value, out id) ? (record.Site, id) : null;
    }

    /// <summary>
    /// Keeps the text of found Stack Exchange questions from the API, up to 100 to a request, one site at a time.
    /// Every record gets a snapshot: its text, or why there is none. Needs the run's edit key.
    /// </summary>
    public async Task<List<PageSnapshot>> KeepStackExchangeAsync(Guid runId, string? editKey, IReadOnlyList<GreyRecord> records)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can keep its pages.");
        var ledger = _searcher.LoadLedger(runId) ?? throw new InvalidOperationException("Search the run first.");
        var foundAddresses = ledger.Sources.Select(s => MultivocalSearcher.AddressKey(s.Record.Url)).ToHashSet();
        var questions = records
            .Where(r => foundAddresses.Contains(MultivocalSearcher.AddressKey(r.Url)))
            .Select(r => (Record: r, Question: StackExchangeQuestion(r)))
            .Where(q => q.Question != null)
            .Select(q => (q.Record, Site: q.Question!.Value.Site, Id: q.Question.Value.Id))
            .ToList();
        if (questions.Count < records.Count) throw new InvalidOperationException("Only Stack Exchange questions the searches found can be kept this way.");

        var kept = new List<PageSnapshot>();
        foreach (var site in questions.GroupBy(q => q.Site, StringComparer.OrdinalIgnoreCase))
            foreach (var batch in site.Chunk(StackExchangeText.MaxIds))
            {
                var accessed = DateTime.UtcNow;
                StackExchangeBatch answer;
                try { answer = await _stackExchange.FetchAsync(site.Key, batch.Select(q => q.Id).Distinct().ToList()); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    answer = new StackExchangeBatch(new Dictionary<long, StackExchangeThread>(), Array.Empty<string>(),
                        $"the Stack Exchange API could not be reached ({PrismaReviewEngine.SanitizeLogMessage(ex.Message)})");
                }
                var raw = await SaveRawAsync(runId, answer.RawResponses);
                foreach (var (record, _, id) in batch)
                    kept.Add(await KeepThreadAsync(runId, record, answer.Threads.GetValueOrDefault(id), answer.Error, raw, accessed));
            }
        return kept;
    }

    private async Task<PageSnapshot> KeepThreadAsync(Guid runId, GreyRecord record, StackExchangeThread? thread, string? error, List<ApiResponseFile> raw, DateTime accessed)
    {
        var snapshot = new PageSnapshot
        {
            Url = record.Url,
            FinalUrl = thread?.Question.Link is { Length: > 0 } link ? link : record.Url,
            AccessedUtc = accessed,
            ContentType = "application/json",
            Title = thread?.Title ?? record.Title,
            WaybackUrl = $"https://web.archive.org/web/{accessed:yyyyMMddHHmmss}/{record.Url}",
            ApiResponses = raw.Count > 0 ? raw : null,
            NotKept = thread == null ? error ?? "the Stack Exchange API no longer has this question (it may have been deleted)" : null,
        };
        if (thread == null) return await SaveAsync(runId, MultivocalSearcher.AddressKey(record.Url), snapshot, null);

        var (text, removed, hidden) = StackExchangeLayout(thread);
        snapshot.TextBasis = StackExchangeBasis(thread.Answers.Count);
        snapshot.Attribution = Credits(thread);
        return await SaveAsync(runId, MultivocalSearcher.AddressKey(record.Url), snapshot, text, removed, hidden);
    }

    public static string StackExchangeBasis(int answers) =>
        $"{StackExchangeBasisPrefix}: question and {answers} answer{(answers == 1 ? "" : "s")}";

    /// <summary>
    /// The thread as plain text in a fixed layout, each post's HTML read by <see cref="PageText"/>, so hidden parts and
    /// invisible characters are taken out and counted as on any page. The same API answer always gives the same text.
    /// </summary>
    public static (string Text, HiddenTextRemoved? Removed, string Hidden) StackExchangeLayout(StackExchangeThread thread)
    {
        var text = new StringBuilder();
        var hidden = new StringBuilder();
        int invisible = 0, elements = 0;
        void Add(string heading, string html)
        {
            text.Append(heading).Append('\n');
            var body = PageText.Read(html);
            invisible += body.InvisibleCharacters;
            elements += body.HiddenElements;
            if (body.Hidden.Length > 0) hidden.Append(body.Hidden).Append('\n');
            if (body.Text.Length > 0) text.Append(body.Text).Append('\n');
        }

        Add($"Question: {thread.Title}", thread.Question.Body);
        for (int i = 0; i < thread.Answers.Count; i++)
        {
            var a = thread.Answers[i];
            Add($"Answer {i + 1} of {thread.Answers.Count} (score {a.Score}{(a.Accepted ? ", accepted" : "")})", a.Body);
        }
        string kept = PageText.FromPlainText(text.ToString());
        return (kept, HiddenTextRemoved.Of(invisible, elements), hidden.ToString());
    }

    private static List<ContentCredit> Credits(StackExchangeThread thread) =>
        new[] { new ContentCredit("question", thread.Question.Author, thread.Question.AuthorLink, thread.Question.Link, thread.Question.Licence) }
            .Concat(thread.Answers.Select((a, i) => new ContentCredit($"answer {i + 1}", a.Author, a.AuthorLink, a.Link, a.Licence)))
            .ToList();

    /// <summary>Saves raw API answers as received in PageText/, named by their SHA-256, so saving one again changes nothing.</summary>
    private async Task<List<ApiResponseFile>> SaveRawAsync(Guid runId, IReadOnlyList<string> responses)
    {
        var files = new List<ApiResponseFile>();
        string folder = Path.Join(_runs.FolderOf(runId), TextFolder);
        foreach (string response in responses)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(response);
            string sha = Sha256(bytes);
            string name = $"stackexchange-{sha[..16]}.json";
            Directory.CreateDirectory(folder);
            string path = Path.Join(folder, name);
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
            files.Add(new ApiResponseFile($"{TextFolder}/{name}", sha));
        }
        return files;
    }
}
