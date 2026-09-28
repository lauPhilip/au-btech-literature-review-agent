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
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;

/// <summary>Search stage: search strings, querying the sources, removing duplicates, citation chaining.</summary>
public partial class PrismaReviewEngine
{
    /// <summary>Test hook: the citation index used for citation chaining (default: OpenAlex).</summary>
    public Func<ICitationGraph>? CitationGraphFactory { get; init; }

    public const string RawResponsesFolder = "SourceResponses";
    public const string OriginDatabase = "database search";
    public const string OriginCitations = "citation chaining";

    private sealed class SearchCacheEntry
    {
        public List<AcademicPaper> Papers { get; set; } = new();
        public List<string> Raw { get; set; } = new();
    }

    private sealed record SearchPass(PlatformSearchLog Log, List<AcademicPaper> Papers);

    private async Task SearchAndScreenAsync(RunContext ctx, int yearFrom, int yearTo)
    {
        var request = ctx.Request;
        var reviewState = ctx.State;
        int maxResults = request.MaxResultsPerSource;

        // STORM-style multi-perspective search: survey the topic from a few distinct angles before
        // querying each source, instead of relying on the single literal query phrase alone.
        List<string> searchPerspectives;
        using (LlmStage.Begin("search-perspectives"))
            searchPerspectives = await GenerateSearchPerspectivesAsync(ctx.Chat, request.Query, request.Objective, request.Inclusion);
        reviewState.SearchPerspectives = searchPerspectives;
        reviewState.ProtocolHash = MethodsSectionWriter.ComputeProtocolHash(
            request.Query, request.Objective, request.Inclusion, request.Exclusion,
            searchPerspectives, reviewState.SelectedSources, maxResults, request.PeerReviewOnly);
        try
        {
            // The extra search strings are only known now, so they are added to the protocol as a dated amendment.
            await File.AppendAllTextAsync(Path.Combine(ctx.Workspace, "protocol.md"), ProtocolWriter.Amendment(searchPerspectives, DateTime.UtcNow));
        }
        catch (Exception ex) { _log.LogWarning("Run {RunId}: protocol amendment not written: {Message}", ctx.RunId, ex.Message); }
        await SaveStateAsync(ctx.RunId, reviewState);

        // All sources are queried at the same time; each source runs its search strings one after another.
        // The results are then processed in a fixed order (source order, then search-string order), so the
        // duplicate decisions and the ledger do not depend on which source happened to answer first.
        var fetches = await Task.WhenAll(ctx.Sources.Select((source, index) =>
            FetchSourceAsync(ctx, source, index, searchPerspectives, maxResults)));

        var candidates = new List<(AcademicPaper Paper, string SourceName)>();
        for (int s = 0; s < ctx.Sources.Count; s++)
        {
            var source = ctx.Sources[s];
            int kept = 0;
            foreach (var pass in fetches[s])
            {
                reviewState.SearchLogs.Add(pass.Log);
                if (pass.Log.FromCache) reviewState.Stats.CacheHits++;
                reviewState.Stats.TotalIdentified += pass.Papers.Count;

                // Every identified record lands in exactly one bucket (duplicate, outside years, over cap, or
                // candidate), so the PRISMA flow diagram adds up, and every removal is written to the ledger.
                foreach (var candidate in pass.Papers)
                {
                    if (!Admit(ctx, candidate, source.SourceName, pass.Log.QueryUsed, yearFrom, yearTo, capReached: kept >= maxResults)) continue;
                    kept++;
                    candidates.Add((candidate, source.SourceName));
                }
            }
        }
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
    /// Runs the search strings against one source, one after another, and stops once the source has returned
    /// enough distinct records to fill its cap. Responses come from the cache when a fresh one exists; every
    /// raw response actually used is saved in SourceResponses/ with its SHA-256 fingerprint.
    /// </summary>
    private async Task<List<SearchPass>> FetchSourceAsync(RunContext ctx, IAcademicSource source, int sourceIndex, List<string> perspectives, int maxResults)
    {
        var passes = new List<SearchPass>();
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // maxResults is a per-source cap: split the budget across the search strings.
        int perPerspectiveCap = Math.Max(1, (int)Math.Ceiling(maxResults / (double)perspectives.Count));

        for (int p = 0; p < perspectives.Count; p++)
        {
            if (distinct.Count >= maxResults) break; // cap met - the remaining search strings are not run at all
            string query = perspectives[p];
            var log = new PlatformSearchLog
            {
                SourceName = source.SourceName,
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                Status = "Running",
                QueryUsed = query,
            };

            List<AcademicPaper> papers = new();
            List<string> raw = new();
            string cacheKey = ReviewCache.Key("search-v1", source.SourceName, query, perPerspectiveCap.ToString());
            try
            {
                if (Cache.TryGet<SearchCacheEntry>("search", cacheKey, out var cached) && cached != null)
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
            foreach (var paper in papers) distinct.Add(paper.Id);
            passes.Add(new SearchPass(log, papers));
        }
        return passes;
    }

    /// <summary>Writes the raw responses of one search call to SourceResponses/ and notes file names and fingerprints in the log.</summary>
    private void SaveRawResponses(RunContext ctx, PlatformSearchLog log, IReadOnlyList<string> raw, string baseName)
    {
        if (raw.Count == 0) return;
        try
        {
            string folder = Path.Combine(ctx.Workspace, RawResponsesFolder);
            Directory.CreateDirectory(folder);
            var names = new List<string>();
            var hashes = new List<string>();
            for (int i = 0; i < raw.Count; i++)
            {
                string ext = raw[i].TrimStart().StartsWith('<') ? ".xml" : ".json";
                string name = baseName + (raw.Count > 1 ? $"-{i + 1}" : "") + ext;
                byte[] bytes = new UTF8Encoding(false).GetBytes(raw[i]);
                File.WriteAllBytes(Path.Combine(folder, name), bytes);
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

    /// <summary>
    /// Citation chaining (one round of backward and forward snowballing): for every included record with a
    /// DOI, the works it cites and the works citing it are looked up in OpenAlex, run through the same
    /// duplicate and year checks, and screened like any other record. They are reported as "identified via
    /// other methods" in the PRISMA flow.
    /// </summary>
    private async Task ChainCitationsAsync(RunContext ctx, int yearFrom, int yearTo)
    {
        var seeds = ctx.State.Phases.Screening
            .Where(l => l.Decision == "Included" && l.Origin == OriginDatabase && NormalizeDoi(l.Doi) != null)
            .ToList();
        if (seeds.Count == 0) return;

        var graph = CitationGraphFactory?.Invoke() ?? new OpenAlexSource(OpenSources.ContactEmail);
        int perDirection = Math.Clamp(OpenSources.ChainingPerPaper, 1, 25);
        string sourceName = $"{graph.SourceName} (citation chaining)";

        using var throttle = new SemaphoreSlim(Math.Max(1, Llm.ScreeningParallelism));
        var lookups = seeds.Select(async seed =>
        {
            await throttle.WaitAsync();
            try { return (Seed: seed, Result: (CitationNeighbours?)await graph.GetCitationNeighboursAsync(NormalizeDoi(seed.Doi)!, perDirection), Error: (string?)null); }
            catch (Exception ex) { return (Seed: seed, Result: (CitationNeighbours?)null, Error: (string?)SanitizeLogMessage(ex.Message)); }
            finally { throttle.Release(); }
        }).ToList();

        var candidates = new List<(AcademicPaper Paper, string SourceName)>();
        for (int i = 0; i < lookups.Count; i++)
        {
            var (seed, result, error) = await lookups[i];
            var log = new PlatformSearchLog
            {
                SourceName = sourceName,
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                QueryUsed = $"references of and citations to doi:{NormalizeDoi(seed.Doi)}",
                Status = error == null ? "Completed Successfully" : "Faulted / Refused Connection",
                ErrorMessage = error ?? "None",
            };
            var found = result == null ? new List<AcademicPaper>() : result.Backward.Concat(result.Forward).ToList();
            log.PapersFound = found.Count;
            if (result != null) SaveRawResponses(ctx, log, result.RawResponses, $"chain-{i + 1:00}");
            ctx.State.SearchLogs.Add(log);

            ctx.State.Stats.TotalIdentified += found.Count;
            ctx.State.Stats.IdentifiedViaCitations += found.Count;
            foreach (var paper in found)
            {
                if (Admit(ctx, paper, sourceName, log.QueryUsed, yearFrom, yearTo, capReached: false))
                    candidates.Add((paper, sourceName));
            }
        }
        OnProgressUpdated?.Invoke(ctx.RunId, ctx.State.Stats);
        await SaveStateAsync(ctx.RunId, ctx.State);

        await ScreenCandidatesAsync(ctx, candidates, OriginCitations);
    }

    private sealed class PerspectivesAnswer
    {
        public List<string>? Perspectives { get; set; }
    }

    /// <summary>
    /// STORM-style perspective-guided question asking, scaled down for search-query expansion: the model
    /// proposes a few distinct phrasings (terminology variants, sub-topics, methodology framing) so the
    /// retrieval pass can find papers the primary phrase alone would miss. Falls back to the primary query
    /// alone on any failure, so a flaky model call never blocks the run.
    /// </summary>
    private async Task<List<string>> GenerateSearchPerspectivesAsync(IChatCompletionService chatService, string initialQuery, string explicitObjective, string inclusionCriteria)
    {
        var perspectives = new List<string> { initialQuery };

        var prompt = $$"""
            You are assisting a systematic literature review search strategy. Following a "survey the topic from multiple perspectives before searching" approach, propose additional search-query phrasings that would surface relevant papers the primary query alone would miss.

            PRIMARY SEARCH QUERY: "{{initialQuery}}"
            REVIEW OBJECTIVE: "{{explicitObjective}}"
            INCLUSION THRESHOLDS: "{{inclusionCriteria}}"

            TASK: Propose exactly 3 additional, distinct search-query phrasings - for example a synonym/terminology variant, a narrower sub-topic or application-domain variant, and a methodology- or evaluation-focused variant. Each must stay tightly scoped to the same review objective; do not drift into unrelated topics. Keep each phrasing short enough to work as a literal database search string (roughly 3-8 words).

            Respond ONLY with a valid minified JSON object matching this structure exactly:
            { "perspectives": ["query variant 1", "query variant 2", "query variant 3"] }
            """;

        try
        {
            var answer = await LlmJson.GetAsync<PerspectivesAnswer>(chatService, prompt, JsonMode(0.2), a =>
                a.Perspectives == null || a.Perspectives.Count == 0 ? "perspectives must be a non-empty list of strings." :
                a.Perspectives.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 200) ? "each perspective must be a short search string." : null);
            foreach (var variant in answer.Perspectives!.Take(3))
            {
                if (!perspectives.Any(p => p.Equals(variant.Trim(), StringComparison.OrdinalIgnoreCase)))
                    perspectives.Add(variant.Trim());
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Search strings: falling back to the primary query only: {Message}", SanitizeLogMessage(ex.Message));
        }

        return perspectives;
    }

    /// <summary>Settings for a prompt that must answer with a JSON object.</summary>
    internal static MistralAIPromptExecutionSettings JsonMode(double temperature)
    {
        var settings = new MistralAIPromptExecutionSettings { Temperature = temperature };
        settings.ExtensionData ??= new Dictionary<string, object>();
        settings.ExtensionData["response_format"] = new { type = "json_object" };
        return settings;
    }
}
