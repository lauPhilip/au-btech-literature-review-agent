using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>One search string sent to one grey source, as written to the ledger.</summary>
public sealed class GreySearchLog
{
    public string Source { get; set; } = "";
    public string SearchString { get; set; } = "";
    public int Requested { get; set; }
    public int Returned { get; set; }

    /// <summary>Sources this search found that no earlier search had found.</summary>
    public int New { get; set; }

    /// <summary>The raw answer, saved as received in SourceResponses/, and its SHA-256.</summary>
    public string RawFile { get; set; } = "";
    public string RawSha256 { get; set; } = "";
    public DateTime SearchedUtc { get; set; }
    public string? Error { get; set; }
}

/// <summary>A grey source the searches found, with every search that found it.</summary>
public sealed class FoundGreySource
{
    public GreyRecord Record { get; set; } = null!;
    public List<string> FoundBy { get; set; } = new();
}

/// <summary>A search of the plan that did not run, and why.</summary>
public sealed record SkippedSearch(string Source, string Reason);

/// <summary>
/// The multivocal run's ledger (multivocal-ledger.json): every search with its raw answer's fingerprint, every source
/// found and which searches found it, and the searches that did not run. Selection and quality follow in block E.
/// </summary>
public sealed class MultivocalLedger
{
    public Guid RunId { get; set; }
    public DateTime SearchedUtc { get; set; }

    /// <summary>The stopping rule as it was applied, in words, for the methods text.</summary>
    public string StoppingApplied { get; set; } = "";
    public List<GreySearchLog> Searches { get; set; } = new();
    public List<FoundGreySource> Sources { get; set; } = new();
    public List<SkippedSearch> Skipped { get; set; } = new();
}

/// <summary>
/// Runs the grey literature searches of a planned multivocal run (MLR block D): every chosen search that is built,
/// with every search string, under the plan's stopping rule. Each raw answer is saved as received with its SHA-256;
/// the sources found are de-duplicated by address. The run goes from "Planned" through "Searching" to "Searched".
/// Searches that are not built yet are listed as skipped, so the ledger never implies they ran.
/// </summary>
public sealed class MultivocalSearcher
{
    public const string LedgerFile = "multivocal-ledger.json";
    public const string StageSearching = "Searching";
    public const string StageSearched = "Searched";

    /// <summary>The most one search can ask for: the APIs return one page of at most 100 hits.</summary>
    public const int MaxPerSearch = 100;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static ILogger _log => AppLog.For("AuBtechReviewAgent.MultivocalSearcher");

    private readonly RunStore _runs;
    private readonly MultivocalPlanner _planner;
    private readonly Func<string, IGreySource?> _sources;

    public MultivocalSearcher(RunStore runs, MultivocalPlanner planner, Func<string, IGreySource?>? sources = null)
    {
        _runs = runs;
        _planner = planner;
        _sources = sources ?? (key => GreySourceCatalog.Create(key));
    }

    /// <summary>The ledger of a searched run, or null.</summary>
    public MultivocalLedger? LoadLedger(Guid runId)
    {
        string path = Path.Join(_runs.FolderOf(runId), LedgerFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MultivocalLedger>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Searches for a planned run. Needs the run's edit key; refuses a run that is not at the "Planned" stage.
    /// <paramref name="progress"/> gets one short line per search.
    /// </summary>
    public async Task<MultivocalLedger> SearchAsync(Guid runId, string? editKey, IProgress<string>? progress = null)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can start its search.");
        var planned = _planner.Load(runId) ?? throw new InvalidOperationException("This run has no plan.");

        await Gate.WaitAsync();
        try
        {
            var header = _runs.LoadHeader(runId) ?? throw new InvalidOperationException("This run could not be found.");
            if (header.Stage != MultivocalPlanner.StagePlanned) throw new InvalidOperationException("This run has been searched already.");
            string folder = _runs.FolderOf(runId);
            await RunHeader.WriteAsync(folder, header with { Stage = StageSearching });

            var ledger = await RunSearchesAsync(runId, folder, planned.Plan, progress);

            await SafeFile.WriteAllTextAsync(Path.Join(folder, LedgerFile), JsonSerializer.Serialize(ledger, Json));
            await RunHeader.WriteAsync(folder, header with { Stage = StageSearched });
            return ledger;
        }
        catch
        {
            // A failed search leaves the run planned, so it can be tried again.
            if (_runs.LoadHeader(runId) is { Stage: StageSearching } current)
                await RunHeader.WriteAsync(_runs.FolderOf(runId), current with { Stage = MultivocalPlanner.StagePlanned });
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<MultivocalLedger> RunSearchesAsync(Guid runId, string folder, MultivocalPlan plan, IProgress<string>? progress)
    {
        var ledger = new MultivocalLedger { RunId = runId, SearchedUtc = DateTime.UtcNow };
        var strings = plan.EffectiveGreySearchStrings;
        int perSearch = plan.StoppingRule switch
        {
            "exhaustion" => MaxPerSearch,
            _ => Math.Min(plan.TopHits, MaxPerSearch),
        };
        ledger.StoppingApplied = plan.StoppingRule switch
        {
            "effort" when plan.TopHits > MaxPerSearch => $"Effort bounded: the top {MaxPerSearch} hits of each search string (the plan asked for {plan.TopHits}; each search returns one page of at most {MaxPerSearch}).",
            "effort" => $"Effort bounded: the top {perSearch} hits of each search string.",
            "saturation" => $"Theoretical saturation: the strings were searched in order, up to {perSearch} hits each, and a search stopped taking further strings once a string added no new source.",
            _ => $"Evidence exhaustion, as far as the searches allow: every hit on the first page of each search, at most {MaxPerSearch}.",
        };

        var byAddress = new Dictionary<string, FoundGreySource>(StringComparer.OrdinalIgnoreCase);
        string rawFolder = Path.Join(folder, RunArchive.RawResponsesFolder);
        Directory.CreateDirectory(rawFolder);

        foreach (string key in plan.GreySearches)
        {
            var source = _sources(key);
            if (source == null)
            {
                ledger.Skipped.Add(new SkippedSearch(key, MultivocalGuidelines.GreySearches.Any(s => s.Key == key)
                    ? "Not built yet."
                    : "No longer offered."));
                continue;
            }

            for (int i = 0; i < strings.Count; i++)
            {
                string text = strings[i];
                progress?.Report($"{source.Name}: \"{text}\"");
                var log = new GreySearchLog { Source = key, SearchString = text, Requested = perSearch, SearchedUtc = DateTime.UtcNow };
                ledger.Searches.Add(log);
                try
                {
                    var records = await source.SearchAsync(text, perSearch);
                    log.Returned = records.Count;
                    for (int r = 0; r < source.LastRawResponses.Count; r++)
                    {
                        string name = $"grey-{key}-{i + 1}{(r > 0 ? $"-{r + 1}" : "")}.json";
                        byte[] bytes = new UTF8Encoding(false).GetBytes(source.LastRawResponses[r]);
                        await File.WriteAllBytesAsync(Path.Join(rawFolder, name), bytes);
                        log.RawFile = $"{RunArchive.RawResponsesFolder}/{name}";
                        log.RawSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    }
                    foreach (var record in records)
                    {
                        string address = AddressKey(record.Url);
                        string foundBy = $"{key}: {text}";
                        if (byAddress.TryGetValue(address, out var known))
                        {
                            if (!known.FoundBy.Contains(foundBy)) known.FoundBy.Add(foundBy);
                            continue;
                        }
                        var found = new FoundGreySource { Record = record, FoundBy = { foundBy } };
                        byAddress[address] = found;
                        ledger.Sources.Add(found);
                        log.New++;
                    }
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or JsonException)
                {
                    // One failing search does not stop the others; the ledger says what failed.
                    log.Error = $"{ex.GetType().Name}: {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}";
                    _log.LogWarning("Run {RunId}: grey search {Source} failed: {Message}", runId, key, log.Error);
                }

                if (plan.StoppingRule == "saturation" && log.Error == null && log.New == 0) break;
            }
        }
        return ledger;
    }

    /// <summary>The address used to spot the same source found twice: no scheme, no "www.", no trailing slash or fragment.</summary>
    public static string AddressKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url.Trim().ToLowerInvariant();
        string host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return $"{host}{u.AbsolutePath.TrimEnd('/')}{u.Query}".ToLowerInvariant();
    }
}
