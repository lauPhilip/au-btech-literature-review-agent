using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// The seed step of the formal pool, with the systematic review's rule (<see cref="CitationSeeds"/>): after screening, the
/// included academic sources with the most citations per year since publication are chosen in code, never by the model,
/// and only from sources already included. Each is built out one round in both directions through OpenAlex, its
/// neighbours ranked the same way. What they find joins the formal pool in the ledger, with the same duplicate checks as
/// the formal search, each raw answer saved with its SHA-256, and is screened like any academic source.
/// </summary>
public sealed partial class MultivocalScreener
{
    /// <summary>What a source found by the seed step is found by, and the database name of its records not kept.</summary>
    public const string SeedChainingName = "OpenAlex (seed chaining)";

    /// <summary>The citation index for the seed step (OpenAlex on the server); without it the step does not run.</summary>
    public Func<ICitationGraph>? CitationGraph { get; init; }

    /// <summary>How many included academic sources are built out (OpenSources:SeedStudies); 0 switches the step off.</summary>
    public int SeedStudies { get; init; }

    /// <summary>How many references and citing works are kept per seed (OpenSources:SeedChainingPerDirection).</summary>
    public int SeedChainingPerDirection { get; init; } = 20;

    private async Task ChainFromSeedsAsync(Guid runId, MultivocalPlan plan, MultivocalLedger ledger, MultivocalScreeningFile file,
        Dictionary<string, string> byTitle, IChatCompletionService chat, IProgress<string>? progress)
    {
        // A screening started again after a stop finds the seed step on file already, and its sources in the ledger.
        if (CitationGraph == null || SeedStudies <= 0 || !plan.UsesFormalPool || ledger.SeedChaining != null) return;

        var byAddress = ledger.Sources.GroupBy(s => MultivocalSearcher.AddressKey(s.Record.Url)).ToDictionary(g => g.Key, g => g.First());
        var included = file.Sources
            .Where(s => s.Formal && s.Decision == "Included" && byAddress.TryGetValue(s.Address, out var f) && f.Paper != null)
            .Select(s => byAddress[s.Address].Paper!)
            .Select(p => new SeedCandidate(p.Id, p.Title ?? "", ApaCitationBuilder.ExtractYear(p.PublishedDate), p.CitedByCount, p.Doi));
        var record = CitationSeeds.Select(included, Math.Clamp(SeedStudies, 0, 50),
            Math.Clamp(SeedChainingPerDirection, 1, CitationSeeds.PoolPerDirection), file.ScreenedUtc.Year);
        ledger.SeedChaining = record;
        ledger.SeedSearches = new List<GreySearchLog>();
        if (record.Seeds.Count == 0)
        {
            await _searcher.SaveLedgerAsync(runId, ledger);
            return;
        }

        progress?.Report($"Building out from the {record.Seeds.Count} most-cited included academic source{(record.Seeds.Count == 1 ? "" : "s")}");
        var results = await CitationSeeds.BuildOutAsync(CitationGraph(), record, PrismaReviewEngine.SanitizeLogMessage,
            n => progress?.Report($"{n} of {record.Seeds.Count} seed look-ups"));

        string rawFolder = Path.Join(_runs.FolderOf(runId), RunArchive.RawResponsesFolder);
        Directory.CreateDirectory(rawFolder);
        var removed = ledger.FormalRemoved ??= new List<RemovedRecord>();
        var seen = new Dictionary<string, FoundGreySource>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in ledger.Sources) Remember(seen, source);
        var toScreen = new List<(int Index, GreyRecord Record)>();

        foreach (var (seed, backward, forward, raw) in results)
        {
            var log = new GreySearchLog
            {
                Source = "openalex", Formal = true, SearchedUtc = DateTime.UtcNow, Requested = 2 * record.PerDirection, Error = seed.Error,
                SearchString = $"seed {seed.Rank}: references of and citations to doi:{seed.Doi}, the {record.PerDirection} with the most citations per year in each direction",
            };
            ledger.SeedSearches.Add(log);
            if (raw.Count > 0) await MultivocalSearcher.SaveFormalRawAsync(rawFolder, $"seed-{seed.Rank:00}", raw, log);
            var found = backward.Concat(forward).ToList();
            log.Returned = found.Count;
            string foundBy = $"seed chaining: seed {seed.Rank} (doi:{seed.Doi})";
            foreach (var paper in found)
            {
                string address = MultivocalSearcher.FormalAddress(paper);
                var first = Keys(paper, address).Select(k => seen.GetValueOrDefault(k)).FirstOrDefault(f => f != null);
                if (first != null)
                {
                    removed.Add(MultivocalSearcher.Removed(paper, SeedChainingName, log.SearchString, RemovedRecord.Duplicate, first.Record.Id));
                    if (!first.FoundBy.Contains(foundBy)) first.FoundBy.Add(foundBy);
                    continue;
                }
                if (address.Length == 0)
                {
                    removed.Add(MultivocalSearcher.Removed(paper, SeedChainingName, log.SearchString, MultivocalSearcher.NoWebAddress));
                    continue;
                }
                var source = new FoundGreySource { Record = MultivocalSearcher.RecordOf(paper, address, "OpenAlex"), FoundBy = { foundBy }, Paper = paper };
                ledger.Sources.Add(source);
                Remember(seen, source);
                log.New++;
                if (Enter(file, source.Record, byTitle) is { } item) toScreen.Add(item);
            }
        }
        await _searcher.SaveLedgerAsync(runId, ledger);
        await ScreenBatchAsync(plan, file, toScreen, chat, progress, pilot: false);
    }

    /// <summary>The keys a formal source is known by, as in the formal search: its id, DOI, title and address.</summary>
    private static IEnumerable<string> Keys(AcademicPaper paper, string address)
    {
        yield return "id:" + paper.Id;
        if (PrismaReviewEngine.NormalizeDoi(paper.Doi) is { } doi) yield return "doi:" + doi;
        if (PrismaReviewEngine.NormalizeTitle(paper.Title) is { Length: > 0 } title) yield return "title:" + title;
        if (address.Length > 0) yield return "address:" + MultivocalSearcher.AddressKey(address);
    }

    private static void Remember(Dictionary<string, FoundGreySource> seen, FoundGreySource source)
    {
        var keys = source.Paper != null
            ? Keys(source.Paper, source.Record.Url)
            : new[] { "address:" + MultivocalSearcher.AddressKey(source.Record.Url) };
        foreach (var key in keys) seen.TryAdd(key, source);
    }

    /// <summary>Enters a new source in the screening file, as the found sources are: a title already screened makes it a duplicate.</summary>
    private static (int Index, GreyRecord Record)? Enter(MultivocalScreeningFile file, GreyRecord r, Dictionary<string, string> byTitle)
    {
        var entry = new GreyScreening { Address = MultivocalSearcher.AddressKey(r.Url), Title = r.Title, Url = r.Url, Formal = true };
        string title = SameSourceKey(r);
        int index = file.Sources.Count;
        file.Sources.Add(entry);
        if (title.Length > 0 && byTitle.TryGetValue(title, out var first))
        {
            entry.Decision = "Duplicate";
            entry.DuplicateOf = first;
            entry.Reasoning = "The same title was found under another address; that source was screened.";
            return null;
        }
        if (title.Length > 0) byTitle[title] = entry.Address;
        return (index, r);
    }
}
