using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// The snapshot of one grey source's page (decision 2, the middle way). The text itself stays in the run folder
/// (<see cref="MultivocalPages.TextFolder"/>) for the run's lifetime, so every citation check can be repeated against
/// it; the archive gets this record only: address, access date, the SHA-256 of the text and a Wayback Machine link.
/// </summary>
public sealed class PageSnapshot
{
    public string Url { get; set; } = "";

    /// <summary>The address the text came from, after redirects.</summary>
    public string FinalUrl { get; set; } = "";
    public DateTime AccessedUtc { get; set; }
    public int? Status { get; set; }
    public string ContentType { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>The text file in the run folder; empty when the page was not kept.</summary>
    public string TextFile { get; set; } = "";
    public string TextSha256 { get; set; } = "";
    public int Characters { get; set; }

    /// <summary>The Wayback Machine's capture nearest to the access date (a lookup, not a capture this app made).</summary>
    public string WaybackUrl { get; set; } = "";

    /// <summary>Why the page was not kept, in words for the reviewer; null when it was.</summary>
    public string? NotKept { get; set; }

    /// <summary>
    /// For a formal source: "full text" or "abstract only", the text the source is quoted and checked against
    /// (decision 8); null, and left out of the file, for a grey source's page.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextBasis { get; set; }

    /// <summary>For a formal source: where its open full text came from, or why there was none, as the systematic review says it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FullTextSource { get; set; }
}

/// <summary>The page snapshots of a multivocal run (multivocal-pages.json), which is in the run archive.</summary>
public sealed class MultivocalPagesFile
{
    public Guid RunId { get; set; }
    public List<PageSnapshot> Pages { get; set; } = new();
}

/// <summary>
/// Keeps the text of the grey sources' pages for a searched multivocal run. Only pages the run's searches found
/// can be fetched, so the run never fetches an address typed in or made up; fetching goes through
/// <see cref="PageFetcher"/>, which obeys robots.txt and paces each site. Keeping a page again replaces its snapshot.
/// A formal source's text is its open full text or, without one, its abstract (MultivocalPages.Formal.cs).
/// </summary>
public sealed partial class MultivocalPages
{
    public const string PagesFile = "multivocal-pages.json";

    /// <summary>The folder in the run with the page texts. It is not in the run archive (decision 2).</summary>
    public const string TextFolder = "PageText";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly RunStore _runs;
    private readonly MultivocalSearcher _searcher;
    private readonly PageFetcher _fetcher;

    private readonly Func<AcademicPaper, string, Task<FullTextResult>>? _fullText;

    /// <summary>The page snapshots of the run folders.</summary>
    /// <param name="runs">The run folders.</param>
    /// <param name="searcher">Reads the run's ledger: only sources it found are kept.</param>
    /// <param name="fetcher">Fetches a grey source's page.</param>
    /// <param name="fullText">Finds a formal source's open full text, as the systematic review does (into the folder
    /// given); without it, every formal source is kept on its abstract.</param>
    public MultivocalPages(RunStore runs, MultivocalSearcher searcher, PageFetcher fetcher, Func<AcademicPaper, string, Task<FullTextResult>>? fullText = null)
    {
        _runs = runs;
        _searcher = searcher;
        _fetcher = fetcher;
        _fullText = fullText;
    }

    public MultivocalPagesFile Load(Guid runId)
    {
        string path = Path.Join(_runs.FolderOf(runId), PagesFile);
        if (!File.Exists(path)) return new MultivocalPagesFile { RunId = runId };
        try { return JsonSerializer.Deserialize<MultivocalPagesFile>(File.ReadAllText(path), Json) ?? new MultivocalPagesFile { RunId = runId }; }
        catch (JsonException) { return new MultivocalPagesFile { RunId = runId }; }
    }

    /// <summary>The kept text of a snapshot, or null when there is none or it no longer matches its SHA-256.</summary>
    public string? ReadText(Guid runId, PageSnapshot snapshot)
    {
        if (snapshot.TextFile.Length == 0) return null;
        string path = Path.Join(_runs.FolderOf(runId), snapshot.TextFile);
        if (!File.Exists(path)) return null;
        byte[] bytes = File.ReadAllBytes(path);
        return Sha256(bytes) == snapshot.TextSha256 ? Encoding.UTF8.GetString(bytes) : null;
    }

    /// <summary>
    /// The citation check of a multivocal review: whether a quote is in the source's kept page text. It never looks at
    /// the live page, which may have changed since the access date; a kept text whose SHA-256 no longer matches counts
    /// as missing. Returns "found", "not found" or "no kept page".
    /// </summary>
    public string CheckQuote(Guid runId, string address, string quote)
    {
        var snapshot = Load(runId).Pages.FirstOrDefault(p => MultivocalSearcher.AddressKey(p.Url) == address);
        string? text = snapshot is { NotKept: null } ? ReadText(runId, snapshot) : null;
        if (text == null) return "no kept page";
        return CitationSupportChecker.QuoteOccursIn(quote, text) ? "found" : "not found";
    }

    /// <summary>Fetches one found source's page and keeps its text. Needs the run's edit key.</summary>
    public async Task<PageSnapshot> KeepAsync(Guid runId, string? editKey, string url)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can keep its pages.");
        var ledger = _searcher.LoadLedger(runId) ?? throw new InvalidOperationException("Search the run first.");
        string address = MultivocalSearcher.AddressKey(url);
        var found = ledger.Sources.FirstOrDefault(s => MultivocalSearcher.AddressKey(s.Record.Url) == address)
            ?? throw new InvalidOperationException("Only pages the searches found can be kept.");
        if (found.Paper is { } paper) return await KeepFormalAsync(runId, found.Record, paper);

        var accessed = DateTime.UtcNow;
        var page = await _fetcher.FetchAsync(found.Record.Url);
        var snapshot = new PageSnapshot
        {
            Url = found.Record.Url,
            FinalUrl = page.FinalUrl,
            AccessedUtc = accessed,
            Status = page.Status,
            ContentType = page.ContentType,
            Title = page.Title,
            WaybackUrl = $"https://web.archive.org/web/{accessed:yyyyMMddHHmmss}/{found.Record.Url}",
            NotKept = page.NotFetched,
        };

        return await SaveAsync(runId, address, snapshot, page.NotFetched == null ? page.Text : null);
    }

    /// <summary>Writes the text (when there is one) to the run folder and the snapshot to the pages file, replacing an earlier one.</summary>
    private async Task<PageSnapshot> SaveAsync(Guid runId, string address, PageSnapshot snapshot, string? text)
    {
        string folder = _runs.FolderOf(runId);
        await Gate.WaitAsync();
        try
        {
            if (text != null)
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                // Named after the address, so keeping the page again replaces the old text.
                string name = $"{TextFolder}/{Sha256(Encoding.UTF8.GetBytes(address))[..16]}.txt";
                Directory.CreateDirectory(Path.Join(folder, TextFolder));
                await File.WriteAllBytesAsync(Path.Join(folder, name), bytes);
                snapshot.TextFile = name;
                snapshot.TextSha256 = Sha256(bytes);
                snapshot.Characters = text.Length;
            }

            var file = Load(runId);
            file.Pages.RemoveAll(p => MultivocalSearcher.AddressKey(p.Url) == address);
            file.Pages.Add(snapshot);
            await SafeFile.WriteAllTextAsync(Path.Join(folder, PagesFile), JsonSerializer.Serialize(file, Json));
            return snapshot;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
