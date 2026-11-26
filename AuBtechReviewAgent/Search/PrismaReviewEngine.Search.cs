using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>Search stage: search strings, querying the sources, removing duplicates, citation chaining.</summary>
public partial class PrismaReviewEngine
{
    /// <summary>Test hook: the citation index used for citation chaining (default: OpenAlex).</summary>
    public Func<ICitationGraph>? CitationGraphFactory { get; init; }

    public const string RawResponsesFolder = RunArchive.RawResponsesFolder;
    public const string OriginDatabase = "database search";
    public const string OriginCitations = "citation chaining";

    private sealed class SearchCacheEntry
    {
        public List<AcademicPaper> Papers { get; set; } = new();
        public List<string> Raw { get; set; } = new();
    }

    private sealed record SearchPass(PlatformSearchLog Log, List<AcademicPaper> Papers, int StringIndex);

    private async Task SearchAndScreenAsync(RunContext ctx, int yearFrom, int yearTo)
    {
        var request = ctx.Request;
        var reviewState = ctx.State;
        int maxResults = request.MaxResultsPerSource;

        // STORM-style multi-perspective search: survey the topic from a few distinct angles before
        // querying each source, instead of relying on the single literal query phrase alone.
        // When the reviewer has seen and approved the extra strings on the form, those are used as they are.
        List<string> searchPerspectives;
        ReportProgress(ctx.RunId, reviewState, RunProgress.Search, 0, "Preparing search strings");
        if (request.SearchStrings != null)
        {
            searchPerspectives = WithPrimaryQuery(request.Query, request.SearchStrings);
            reviewState.SearchStringsReviewed = true;
        }
        else
        {
            using (LlmStage.Begin("search-perspectives"))
                searchPerspectives = await GenerateSearchPerspectivesAsync(ctx.Chat, request.Query, request.Objective, request.Inclusion);
        }
        reviewState.SearchPerspectives = searchPerspectives;
        reviewState.ProtocolHash = MethodsSectionWriter.ComputeProtocolHash(
            request.Query, request.Objective, request.Inclusion, request.Exclusion,
            searchPerspectives, reviewState.SelectedSources, maxResults, request.PeerReviewOnly);
        try
        {
            // The extra search strings are only known now, so they are added to the protocol as a dated amendment.
            await File.AppendAllTextAsync(Path.Join(ctx.Workspace, "protocol.md"), ProtocolWriter.Amendment(searchPerspectives, DateTime.UtcNow, reviewState.SearchStringsReviewed));
        }
        catch (Exception ex) { _log.LogWarning("Run {RunId}: protocol amendment not written: {Message}", ctx.RunId, ex.Message); }
        await SaveStateAsync(ctx.RunId, reviewState);
        ctx.SearchPassesTotal = Math.Max(1, ctx.Sources.Count * searchPerspectives.Count);
        ReportProgress(ctx.RunId, reviewState, RunProgress.Search, 0.05, $"Searching {ctx.Sources.Count} source(s) with {searchPerspectives.Count} search string(s)");

        // The search strings are run in rounds: each round asks every source that has not yet filled its cap with the
        // same string, all sources at the same time. The answers of a round are processed in source order, so the
        // duplicate decisions and the ledger do not depend on which source happened to answer first. A source stops
        // taking strings once its cap is filled by records that go on to screening; duplicates and records outside
        // the year range do not use up the cap (issue #562).
        int perPerspectiveCap = Math.Max(1, (int)Math.Ceiling(maxResults / (double)searchPerspectives.Count));
        var kept = new int[ctx.Sources.Count];
        var passesBySource = ctx.Sources.Select(_ => new List<SearchPass>()).ToArray();
        var candidatesBySource = ctx.Sources.Select(_ => new List<(AcademicPaper Paper, string SourceName)>()).ToArray();
        for (int p = 0; p < searchPerspectives.Count; p++)
        {
            var open = Enumerable.Range(0, ctx.Sources.Count).Where(s => kept[s] < maxResults).ToList();
            if (open.Count == 0) break; // every source has filled its cap: the remaining strings are not run at all
            string query = searchPerspectives[p];
            int round = p;
            var answers = await Task.WhenAll(open.Select(s => FetchPassAsync(ctx, ctx.Sources[s], s, query, round, perPerspectiveCap)));
            for (int i = 0; i < open.Count; i++)
            {
                int s = open[i];
                var pass = answers[i];
                passesBySource[s].Add(pass);
                reviewState.Stats.TotalIdentified += pass.Papers.Count;

                // Every identified record lands in exactly one bucket (duplicate, outside years, over cap, or
                // candidate), so the PRISMA flow diagram adds up, and every removal is written to the ledger.
                foreach (var candidate in pass.Papers)
                {
                    if (!Admit(ctx, candidate, ctx.Sources[s].SourceName, query, yearFrom, yearTo, capReached: kept[s] >= maxResults)) continue;
                    kept[s]++;
                    candidatesBySource[s].Add((candidate, ctx.Sources[s].SourceName));
                }
            }
        }
        // The ledger lists the searches and the candidates by source, then by search string, as before.
        var fetches = passesBySource;
        foreach (var pass in fetches.SelectMany(f => f))
        {
            reviewState.SearchLogs.Add(pass.Log);
            if (pass.Log.FromCache) reviewState.Stats.CacheHits++;
        }
        var candidates = candidatesBySource.SelectMany(c => c).ToList();
        reviewState.SearchStringYields = SearchSaturation.Compute(searchPerspectives,
            fetches.SelectMany(f => f).Select<SearchPass, (int, IReadOnlyList<AcademicPaper>)>(pass => (pass.StringIndex, pass.Papers)));
        OnProgressUpdated?.Invoke(ctx.RunId, reviewState.Stats);
        await SaveStateAsync(ctx.RunId, reviewState);

        await ScreenCandidatesAsync(ctx, candidates, OriginDatabase);
    }

    /// <summary>
    /// Duplicate, year-range and cap check for one identified record. Returns true when the record goes on to
    /// screening; otherwise the record is counted and written to the ledger with the reason it was removed.
    /// </summary>
    private bool Admit(RunContext ctx, AcademicPaper candidate, string sourceName, string queryUsed, int yearFrom, int yearTo, bool capReached)
    {
        var stats = ctx.State.Stats;
        int candidateYear = ApaCitationBuilder.ExtractYear(candidate.PublishedDate);
        RemovedRecord Removed(string reason, string? duplicateOf = null) => new(
            candidate.Id, candidate.Title ?? "", sourceName, queryUsed, candidateYear, reason, duplicateOf, NormalizeDoi(candidate.Doi));

        string normalizedTitle = NormalizeTitle(candidate.Title);
        string? doi = NormalizeDoi(candidate.Doi);
        string? firstSeen =
            ctx.SeenIds.TryGetValue(candidate.Id, out var byId) ? byId
            : doi != null && ctx.SeenDois.TryGetValue(doi, out var byDoi) ? byDoi
            : normalizedTitle.Length > 0 && ctx.SeenTitles.TryGetValue(normalizedTitle, out var byTitle) ? byTitle
            : null;
        if (firstSeen != null)
        {
            stats.DuplicatesRemoved++;
            ctx.State.RemovedBeforeScreening.Add(Removed(RemovedRecord.Duplicate, firstSeen));
            return false;
        }
        ctx.SeenIds[candidate.Id] = candidate.Id;
        if (doi != null) ctx.SeenDois[doi] = candidate.Id;
        if (normalizedTitle.Length > 0) ctx.SeenTitles[normalizedTitle] = candidate.Id;

        // Records with no year in the metadata are kept (they cannot be shown to be outside the range).
        if (yearFrom > 0 && yearTo > 0 && candidateYear > 0 && (candidateYear < yearFrom || candidateYear > yearTo))
        {
            stats.OutsideDateRange++;
            ctx.State.RemovedBeforeScreening.Add(Removed(RemovedRecord.OutsideYearRange));
            return false;
        }

        if (capReached)
        {
            stats.CappedBeyondMaxResults++;
            ctx.State.RemovedBeforeScreening.Add(Removed(RemovedRecord.OverCap));
            return false;
        }
        return true;
    }

    /// <summary>
    /// Runs one search string against one source. The answer comes from the cache when a fresh one exists; every
    /// raw response actually used is saved in SourceResponses/ with its SHA-256 fingerprint.
    /// </summary>
    private async Task<SearchPass> FetchPassAsync(RunContext ctx, IAcademicSource source, int sourceIndex, string query, int p, int perPerspectiveCap)
    {
        var log = new PlatformSearchLog
        {
            SourceName = source.SourceName,
            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            Status = "Running",
            QueryUsed = query,
        };
        // The live line names the database and string being asked now (LiveActivity); the bar does not move.
        string subject = LiveActivity.SearchSubjectOf(source.SourceName, query);
        int before = Volatile.Read(ref ctx.SearchPassesDone);
        ReportProgress(ctx.RunId, ctx.State, RunProgress.Search, 0.05 + 0.95 * before / Math.Max(1, ctx.SearchPassesTotal), Of(before, ctx.SearchPassesTotal, "searches"), subject);

        List<AcademicPaper> papers = new();
        List<string> raw = new();
        string cacheKey = ReviewCache.Key("search-v1", source.SourceName, query, perPerspectiveCap.ToString());
        try
        {
            // An entry with papers but no raw answer predates the Scopus and IEEE Xplore fix (#469): search again, so the answer is kept.
            if (Cache.TryGet<SearchCacheEntry>("search", cacheKey, out var cached) && cached != null && (cached.Raw.Count > 0 || cached.Papers.Count == 0))
            {
                papers = cached.Papers;
                raw = cached.Raw;
                log.FromCache = true;
            }
            else
            {
                papers = await source.FetchPapersAsync(query, maxResults: perPerspectiveCap);
                raw = source.LastRawResponses.ToList();
                Cache.Set("search", cacheKey, new SearchCacheEntry { Papers = papers, Raw = raw });
            }
            log.Status = "Completed Successfully";
            log.PapersFound = papers.Count;
        }
        catch (Exception ex)
        {
            log.Status = "Faulted / Refused Connection";
            log.ErrorMessage = SanitizeLogMessage(ex.Message);
            _log.LogWarning("Run {RunId}: {Source} failed on \"{Query}\": {Message}", ctx.RunId, source.SourceName, query, log.ErrorMessage);
        }

        SaveRawResponses(ctx, log, raw, $"{sourceIndex + 1:00}-{Slug(source.SourceName)}-q{p + 1}");
        int searched = Interlocked.Increment(ref ctx.SearchPassesDone);
        ReportProgress(ctx.RunId, ctx.State, RunProgress.Search, 0.05 + 0.95 * searched / ctx.SearchPassesTotal, Of(searched, ctx.SearchPassesTotal, "searches"), subject);
        return new SearchPass(log, papers, p);
    }

    /// <summary>Writes the raw responses of one search call to SourceResponses/ and notes file names and fingerprints in the log.</summary>
    private void SaveRawResponses(RunContext ctx, PlatformSearchLog log, IReadOnlyList<string> raw, string baseName)
    {
        if (raw.Count == 0) return;
        try
        {
            string folder = Path.Join(ctx.Workspace, RawResponsesFolder);
            Directory.CreateDirectory(folder);
            var names = new List<string>();
            var hashes = new List<string>();
            for (int i = 0; i < raw.Count; i++)
            {
                string ext = raw[i].TrimStart().StartsWith('<') ? ".xml" : ".json";
                string name = baseName + (raw.Count > 1 ? $"-{i + 1}" : "") + ext;
                byte[] bytes = new UTF8Encoding(false).GetBytes(raw[i]);
                File.WriteAllBytes(Path.Join(folder, name), bytes);
                names.Add($"{RawResponsesFolder}/{name}");
                hashes.Add(RunManifest.Sha256(bytes));
            }
            log.RawResponseFile = string.Join("; ", names);
            log.RawResponseSha256 = string.Join("; ", hashes);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Run {RunId}: raw response not saved: {Message}", ctx.RunId, ex.Message);
        }
    }

    private static string Slug(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
}
