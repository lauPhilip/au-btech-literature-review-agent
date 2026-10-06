using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// </summary>
public sealed class MultivocalPages
{
    public const string PagesFile = "multivocal-pages.json";

    /// <summary>The folder in the run with the page texts. It is not in the run archive (decision 2).</summary>
    public const string TextFolder = "PageText";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly RunStore _runs;
    private readonly MultivocalSearcher _searcher;
    private readonly PageFetcher _fetcher;

    public MultivocalPages(RunStore runs, MultivocalSearcher searcher, PageFetcher fetcher)
    {
        _runs = runs;
        _searcher = searcher;
        _fetcher = fetcher;
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

    /// <summary>Fetches one found source's page and keeps its text. Needs the run's edit key.</summary>
    public async Task<PageSnapshot> KeepAsync(Guid runId, string? editKey, string url)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can keep its pages.");
        var ledger = _searcher.LoadLedger(runId) ?? throw new InvalidOperationException("Search the run first.");
        string address = MultivocalSearcher.AddressKey(url);
        var found = ledger.Sources.FirstOrDefault(s => MultivocalSearcher.AddressKey(s.Record.Url) == address)
            ?? throw new InvalidOperationException("Only pages the searches found can be kept.");

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

        string folder = _runs.FolderOf(runId);
        await Gate.WaitAsync();
        try
        {
            if (page.NotFetched == null)
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(page.Text);
                // Named after the address, so keeping the page again replaces the old text.
                string name = $"{TextFolder}/{Sha256(Encoding.UTF8.GetBytes(address))[..16]}.txt";
                Directory.CreateDirectory(Path.Join(folder, TextFolder));
                await File.WriteAllBytesAsync(Path.Join(folder, name), bytes);
                snapshot.TextFile = name;
                snapshot.TextSha256 = Sha256(bytes);
                snapshot.Characters = page.Text.Length;
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
