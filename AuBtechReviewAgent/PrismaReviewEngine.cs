using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;

public class PrismaReviewEngine
{
    private readonly string _globalMistralKey;
    private readonly string _globalElsevierKey;
    private readonly string? _globalIeeeKey;
    private readonly string? _globalScholarKey;
    private readonly string _supportStatement;

    // The engine is a singleton shared by every connected user, so progress events carry the session id
    // and each dashboard only reacts to its own run (previously every open dashboard showed the counts of
    // whichever run fired last).
    public event Action<Guid, ReviewStats>? OnProgressUpdated;

    public const string DefaultSupportStatement =
        "No funding statement has been declared for this review. The funding and support text can be set in the Report:SupportStatement configuration value.";

    // SECURE ENCAPSULATED ROUTING PATHS
    private string GetWorkspaceFolderPath(Guid sessionId) => 
        Path.Combine(Directory.GetCurrentDirectory(), "WorkspaceStore", sessionId.ToString("N"));

    private string GetStateFilePath(Guid sessionId) => 
        Path.Combine(GetWorkspaceFolderPath(sessionId), "transparent-process.json");

    private string GetReportFilePath(Guid sessionId) => 
        Path.Combine(GetWorkspaceFolderPath(sessionId), "prisma-report.json");

    public PrismaReviewEngine(string mistralApiKey, string elsevierApiKey, string? ieeeApiKey = null, string? scholarApiKey = null, string? supportStatement = null)
    {
        _globalMistralKey = mistralApiKey;
        _globalElsevierKey = elsevierApiKey;
        _globalIeeeKey = ieeeApiKey;
        _globalScholarKey = scholarApiKey;
        _supportStatement = string.IsNullOrWhiteSpace(supportStatement) ? DefaultSupportStatement : supportStatement.Trim();
    }

    /// <summary>
    /// For each gateway in SourceCatalog: can it be queried with the server keys plus the user's own keys?
    /// Used by the dashboard to grey out sources that have no key.
    /// </summary>
    public IReadOnlyDictionary<string, bool> GetSourceAvailability(UserApiKeys? userKeys)
    {
        var keys = ResolveKeys(userKeys);
        return SourceCatalog.All.ToDictionary(
            s => s.Key,
            s => s.KeyKind switch
            {
                SourceKeyKind.None => true,
                SourceKeyKind.Elsevier => SourceCatalog.IsUsableKey(keys.Elsevier),
                SourceKeyKind.Ieee => SourceCatalog.IsUsableKey(keys.Ieee),
                SourceKeyKind.SerpApi => SourceCatalog.IsUsableKey(keys.Scholar),
                _ => false
            });
    }

    private (string Mistral, string Elsevier, string? Ieee, string? Scholar) ResolveKeys(UserApiKeys? userKeys) => (
        !string.IsNullOrWhiteSpace(userKeys?.MistralApiKey) ? userKeys.MistralApiKey : _globalMistralKey,
        !string.IsNullOrWhiteSpace(userKeys?.ElsevierApiKey) ? userKeys.ElsevierApiKey : _globalElsevierKey,
        !string.IsNullOrWhiteSpace(userKeys?.IeeeApiKey) ? userKeys.IeeeApiKey : _globalIeeeKey,
        !string.IsNullOrWhiteSpace(userKeys?.ScholarApiKey) ? userKeys.ScholarApiKey : _globalScholarKey);

    public async Task RunReviewLoopAsync(Guid sessionId, string initialQuery, string explicitObjective, string inclusionCriteria, string exclusionCriteria, int maxResults, bool requirePeerReview = false, string synthesisDirective = "", UserApiKeys? userKeys = null,
        IReadOnlyCollection<string>? selectedSources = null, int yearFrom = 0, int yearTo = 0)
    {
        // 1. Resolve runtime credentials (BYOK fallback chain)
        var (activeMistral, activeElsevier, activeIeee, activeScholar) = ResolveKeys(userKeys);

        // 2. Build isolated kernel for this specific thread pass
        var builder = Kernel.CreateBuilder();
        builder.AddMistralChatCompletion("mistral-large-latest", activeMistral);
        var kernel = builder.Build();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        // 3. Assemble the source gateways the user ticked. A ticked source without a usable key is not
        //    queried, and is recorded as unavailable so the report says so instead of implying it was searched.
        var selectedKeys = (selectedSources == null || selectedSources.Count == 0 ? SourceCatalog.AllKeys : selectedSources)
            .Where(k => SourceCatalog.Find(k) != null)
            .Select(k => SourceCatalog.Find(k)!.Key)
            .Distinct()
            .ToList();
        var availability = GetSourceAvailability(userKeys);
        var activeSources = new List<IAcademicSource>();
        var unavailableKeys = new List<string>();
        foreach (var key in selectedKeys)
        {
            if (!availability.TryGetValue(key, out bool ok) || !ok) { unavailableKeys.Add(key); continue; }
            activeSources.Add(key switch
            {
                "arxiv" => new ArxivSource(),
                "scopus" => new ElsevierSource(activeElsevier),
                "ieee" => new IeeeXploreSource(activeIeee!),
                "scholar" => new GoogleScholarSource(activeScholar),
                "researchgate" => new ResearchGateSource(activeScholar),
                _ => throw new InvalidOperationException($"Unknown source key '{key}'.")
            });
        }
        if (yearFrom > 0 && yearTo > 0 && yearFrom > yearTo) (yearFrom, yearTo) = (yearTo, yearFrom);

        string userWorkspace = GetWorkspaceFolderPath(sessionId);
        Directory.CreateDirectory(userWorkspace);

        var reviewState = new ReviewState
        {
            SearchQuery = initialQuery,
            PeerReviewOnlyToggle = requirePeerReview,
            SynthesisTargetDirective = synthesisDirective,
            SelectedSources = selectedKeys,
            UnavailableSources = unavailableKeys,
            MaxResultsPerSource = maxResults,
            YearFrom = yearFrom,
            YearTo = yearTo
        };
        reviewState.Stats.ProcessingStage = "Screening";
        await SaveStateAsync(sessionId, reviewState);

        // STORM-style multi-perspective search: survey the topic from a few distinct angles before
        // querying each source, instead of relying on the single literal query phrase alone.
        var searchPerspectives = await GenerateSearchPerspectivesAsync(chatService, initialQuery, explicitObjective, inclusionCriteria);
        reviewState.SearchPerspectives = searchPerspectives;
        reviewState.ProtocolHash = MethodsSectionWriter.ComputeProtocolHash(
            initialQuery, explicitObjective, inclusionCriteria, exclusionCriteria,
            searchPerspectives, selectedKeys, maxResults, requirePeerReview);
        await SaveStateAsync(sessionId, reviewState);

        var allGroundedChunks = new List<DocumentChunk>();

        foreach (var source in activeSources)
        {
            var sourceCandidates = new List<AcademicPaper>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // maxResults is a hard per-source cap, not a per-query cap: split the budget across the
            // perspective phrasings so a source never contributes more than maxResults papers overall,
            // regardless of how many perspectives were generated. Keeps testing with a small maxResults
            // cheap and predictable instead of silently multiplying by the perspective count.
            int perPerspectiveCap = Math.Max(1, (int)Math.Ceiling(maxResults / (double)searchPerspectives.Count));

            foreach (var perspectiveQuery in searchPerspectives)
            {
                if (sourceCandidates.Count >= maxResults) break; // per-source cap already met - skip remaining perspectives entirely

                var currentLog = new PlatformSearchLog
                {
                    SourceName = source.SourceName,
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                    Status = "Running",
                    QueryUsed = perspectiveQuery
                };
                reviewState.SearchLogs.Add(currentLog);
                await SaveStateAsync(sessionId, reviewState);

                List<AcademicPaper> perspectiveResults = new();
                try
                {
                    perspectiveResults = await source.FetchPapersAsync(perspectiveQuery, maxResults: perPerspectiveCap);
                    currentLog.Status = "Completed Successfully";
                    currentLog.PapersFound = perspectiveResults.Count;
                }
                catch (Exception ex)
                {
                    currentLog.Status = "Faulted / Refused Connection";
                    currentLog.ErrorMessage = SanitizeLogMessage(ex.Message);
                    Console.WriteLine($"[Source Exception Logging] {source.SourceName} faulted on perspective \"{perspectiveQuery}\": {currentLog.ErrorMessage}");
                }

                reviewState.Stats.TotalIdentified += perspectiveResults.Count;

                foreach (var candidate in perspectiveResults)
                {
                    string normalizedTitle = Regex.Replace((candidate.Title ?? "").ToLowerInvariant(), @"\s+", " ").Trim();
                    bool isDuplicate = seenIds.Contains(candidate.Id) || (normalizedTitle.Length > 0 && seenTitles.Contains(normalizedTitle));
                    if (isDuplicate)
                    {
                        reviewState.Stats.DuplicatesRemoved++;
                        continue;
                    }
                    seenIds.Add(candidate.Id);
                    if (normalizedTitle.Length > 0) seenTitles.Add(normalizedTitle);

                    // Publication-year window from the dashboard. Records with no year in the metadata are
                    // kept (they cannot be shown to be outside the range) and the report says so.
                    int candidateYear = ApaCitationBuilder.ExtractYear(candidate.PublishedDate);
                    if (yearFrom > 0 && yearTo > 0 && candidateYear > 0 && (candidateYear < yearFrom || candidateYear > yearTo))
                    {
                        reviewState.Stats.OutsideDateRange++;
                        continue;
                    }
                    sourceCandidates.Add(candidate);

                    if (sourceCandidates.Count >= maxResults) break; // stop absorbing more candidates once the per-source cap is hit
                }

                OnProgressUpdated?.Invoke(sessionId, reviewState.Stats);
                await SaveStateAsync(sessionId, reviewState);
            }

            // Hard safety net: never let more than maxResults candidates per source reach screening, even if
            // a source adapter under-respects the requested cap.
            int overCap = Math.Max(0, sourceCandidates.Count - maxResults);
            if (overCap > 0)
            {
                reviewState.Stats.CappedBeyondMaxResults += overCap;
                sourceCandidates = sourceCandidates.Take(maxResults).ToList();
            }

            var candidates = sourceCandidates;

            var filteredCandidates = new List<AcademicPaper>();
            foreach (var paper in candidates)
            {
                if (reviewState.PeerReviewOnlyToggle)
                {
                    bool isPeerReviewed = false;

                    if (paper.Id.StartsWith("SCOPUS_ID:") || source.SourceName.Contains("ScienceDirect", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(paper.JournalSource) && !paper.JournalSource.Contains("Preprint", StringComparison.OrdinalIgnoreCase))
                        {
                            isPeerReviewed = true;
                        }
                    }
                    else if (paper.Id.StartsWith("IEEE_") || source.SourceName.Contains("IEEE", StringComparison.OrdinalIgnoreCase))
                    {
                        isPeerReviewed = true;
                    }
                    else if (paper.Id.Contains("arxiv.org", StringComparison.OrdinalIgnoreCase) || source.SourceName.Contains("arXiv", StringComparison.OrdinalIgnoreCase))
                    {
                        string combinedMetadata = $"{paper.Title} {paper.JournalSource} {paper.Abstract}";
                        bool hasHintOfPublication = combinedMetadata.Contains("Proceedings of", StringComparison.OrdinalIgnoreCase) || 
                                                    combinedMetadata.Contains("Journal of", StringComparison.OrdinalIgnoreCase) || 
                                                    combinedMetadata.Contains("Transactions on", StringComparison.OrdinalIgnoreCase) ||
                                                    combinedMetadata.Contains("Published in", StringComparison.OrdinalIgnoreCase);

                        if (hasHintOfPublication) isPeerReviewed = await VerifyPeerReviewStatusViaLLMAsync(chatService, paper);
                    }
                    else
                    {
                        isPeerReviewed = await VerifyPeerReviewStatusViaLLMAsync(chatService, paper);
                    }

                    if (!isPeerReviewed)
                    {
                        reviewState.Stats.FailedPeerReviewCheck++;
                        reviewState.Stats.Screened++;

                        var excludedApa = ApaCitationBuilder.Build(paper);
                        reviewState.Phases.Screening.Add(new ScreeningLog(
                            paper.Id, paper.Title, "Excluded", 
                            "Excluded during post-retrieval pre-screening: Document was classified as an un-reviewed preprint or working paper, violating the active Peer-Reviewed Only configuration threshold.",
                            excludedApa.Citation, "N/A", excludedApa.VenueType, excludedApa.VenueName, excludedApa.Year
                        ));
                        
                        OnProgressUpdated?.Invoke(sessionId, reviewState.Stats);
                        await SaveStateAsync(sessionId, reviewState);
                        continue;
                    }

                    reviewState.Stats.PassedPeerReviewCheck++;
                }

                filteredCandidates.Add(paper);
            }

            foreach (var paper in filteredCandidates)
            {
                string authorList = string.Join(", ", paper.Authors);
                var prompt = $$"""
                    Evaluate the following academic paper against the provided systematically structured PRISMA parameters.
                    
                    CRITERIA DIRECTIVES:
                    - Inclusion Thresholds: {{inclusionCriteria}}
                    - Exclusion Thresholds: {{exclusionCriteria}}
                    
                    PAPER TARGET DATA:
                    - Title: {{paper.Title}}
                    - Authors: {{authorList}}
                    - Date Context: {{paper.PublishedDate}}
                    - Publication/Journal Venue: {{paper.JournalSource}}
                    - Abstract: {{paper.Abstract}}

                    TASK:
                    1. Determine if it should be Included or Excluded.
                    2. Create a brief 1-2 sentence executive summary of the paper.
                    (The reference string is built separately from the source metadata - do not write one.)

                    Respond ONLY with a valid minified JSON object matching this structure exactly:
                    {
                        "decision": "Included" or "Excluded",
                        "reasoning": "Why it meets inclusion or hits exclusion parameters.",
                        "briefSummary": "The 1-2 sentence executive summary overview."
                    }
                    """;

                try
                {
                    var response = await chatService.GetChatMessageContentAsync(prompt);
                    var decisionData = DeserializeDecision(response.ToString());

                    UpdateReviewState(reviewState, paper, decisionData);
                    OnProgressUpdated?.Invoke(sessionId, reviewState.Stats);
                    await SaveStateAsync(sessionId, reviewState);

                    string decision = decisionData.TryGetProperty("decision", out var d) ? d.GetString() ?? "Excluded" : "Excluded";
                    if (decision.Equals("Included", StringComparison.OrdinalIgnoreCase))
                    {
                        var paperChunks = await DocumentRAGUtility.IngestAndChunkPaperAsync(paper.Id, paper.Title, userWorkspace);
                        allGroundedChunks.AddRange(paperChunks);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Screening Exception] Problem processing paper '{paper.Title}': {SanitizeLogMessage(ex.Message)}");
                }
            }
        }

        reviewState.Stats.ProcessingStage = "Synthesizing";
        OnProgressUpdated?.Invoke(sessionId, reviewState.Stats);

        var includedLogs = reviewState.Phases.Screening
            .Where(p => p.Decision.Equals("Included", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // One ordering for the whole report. The reference list shown to the model, the [n] markers it
        // writes, the extraction table and the LaTeX bibliography are all numbered from this list. They used
        // to be ordered differently (alphabetical for the model, screening order for the bibliography), so a
        // correctly cited [5] pointed at the wrong paper in main.tex.
        var includedPapers = includedLogs
            .OrderBy(p => p.ApaCitation ?? "", ApaCitationBuilder.ReferenceOrder)
            .ThenBy(p => p.PaperId, StringComparer.Ordinal)
            .ToList();

        reviewState.SynthesizedRecords.Clear();
        for (int i = 0; i < includedPapers.Count; i++)
        {
            var log = includedPapers[i];
            // Venue type, venue name and year come from the source metadata (ApaCitationBuilder). Runs from
            // before that change only have the citation string, so fall back to parsing it.
            string venue = !string.IsNullOrEmpty(log.VenueType) ? log.VenueType : ClassifyVenueFromCitation(log.ApaCitation);
            int parsedYear = log.Year > 0 ? log.Year : ExtractYearFromCitation(log.ApaCitation);

            string cleanCategory = log.PaperId.StartsWith("SCOPUS_ID:") ? "ScienceDirect" 
                                 : log.PaperId.StartsWith("IEEE_") ? "IEEE" 
                                 : log.PaperId.StartsWith("SCHOLAR_") ? "Scholar" 
                                 : log.PaperId.StartsWith("RG_") ? "ResearchGate" 
                                 : "Arxiv";

            // Quartile only from an exact Scimago title match. There is no default: the old code filled in
            // "Q1" for every IEEE and Scholar paper it could not match, which inflated the quality figures.
            string quartile = "N/A";
            if (venue is "Journals" or "Transactions" && !string.IsNullOrWhiteSpace(log.VenueName))
            {
                quartile = JournalRankingMatcher.LookupQuartile(log.VenueName, parsedYear);
            }

            string platformFull = log.PaperId.StartsWith("SCOPUS_ID:") ? "Scopus API" 
                                : log.PaperId.StartsWith("IEEE_") ? "IEEE Xplore API" 
                                : log.PaperId.StartsWith("SCHOLAR_") ? "Google Scholar API" 
                                : log.PaperId.StartsWith("RG_") ? "ResearchGate API" 
                                : "arXiv API";

            reviewState.SynthesizedRecords.Add(new IncludedPaperMetricRow
            {
                Title = log.Title,
                Summary = log.BriefSummary,
                ApaCitation = log.ApaCitation ?? "",
                SourcePlatform = platformFull,
                Year = parsedYear,
                VenueType = venue,
                InclusionRationale = log.Reasoning,
                Category = cleanCategory,
                Quartile = quartile,
                // No conference-ranking data source is wired in yet, so no rating is claimed (this used to
                // print "A" for every ScienceDirect conference paper).
                ConferenceRating = "N/A",
                ReferenceNumber = i + 1
            });
        }
        await SaveStateAsync(sessionId, reviewState);

        var sbReferences = new StringBuilder();
        for (int i = 0; i < includedPapers.Count; i++)
        {
            sbReferences.AppendLine($"[{i + 1}] - {includedPapers[i].ApaCitation} (Paper ID Token: {includedPapers[i].PaperId.Split('/').Last()})");
        }
        string exactReferenceListForLLM = sbReferences.ToString();

        var sbContext = new StringBuilder();
        int chunkIdx = 1;
        
        foreach (var chunk in allGroundedChunks.Take(40))
        {
            sbContext.AppendLine($"[Source Context Anchor {chunkIdx}]: (Origin Paper ID: {chunk.SourceId})");
            sbContext.AppendLine($"Content text: {chunk.Text}");
            sbContext.AppendLine("---");
            chunkIdx++;
        }

        foreach (var paper in includedPapers)
        {
            string cleanPaperIdToken = paper.PaperId.Split('/').Last();
            bool hasChunks = allGroundedChunks.Any(c => c.SourceId == cleanPaperIdToken);
            
            if (!hasChunks)
            {
                sbContext.AppendLine($"[Source Context Anchor {chunkIdx}]: (Origin Paper ID: {cleanPaperIdToken})");
                sbContext.AppendLine($"Content text: Abstract Summary: {paper.BriefSummary} Title Metadata: {paper.Title}");
                sbContext.AppendLine("---");
                chunkIdx++;
            }
        }

        await GeneratePrismaChecklistReportWithRAGAsync(sessionId, activeSources.Count, chatService, initialQuery, explicitObjective,
            inc: inclusionCriteria, exc: exclusionCriteria, sbContext.ToString(), exactReferenceListForLLM, reviewState, searchPerspectives, referenceCount: includedPapers.Count);

        reviewState.Stats.ProcessingStage = "Complete";
        OnProgressUpdated?.Invoke(sessionId, reviewState.Stats);
        await SaveStateAsync(sessionId, reviewState);
    }

    private string SanitizeLogMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        return Regex.Replace(message, @"([a-zA-Z0-9]{24,64})", "[REDACTED_API_CREDENTIAL]");
    }

    private static string ClassifyVenueFromCitation(string? citation)
    {
        string c = citation ?? "";
        if (c.Contains("Proceedings of", StringComparison.OrdinalIgnoreCase) || c.Contains("Conference", StringComparison.OrdinalIgnoreCase)) return "Conferences";
        if (c.Contains("Transactions on", StringComparison.OrdinalIgnoreCase)) return "Transactions";
        if (c.Contains("Journal of", StringComparison.OrdinalIgnoreCase)) return "Journals";
        return "Preprints";
    }

    private int ExtractYearFromCitation(string? citation)
    {
        if (string.IsNullOrEmpty(citation)) return 2026;
        var yearMatch = Regex.Match(citation, @"\((20\d{2})\)");
        return yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out int yr) ? yr : 2026;
    }

    /// <summary>
    /// STORM-style perspective-guided question asking, scaled down for search-query expansion: instead of
    /// hitting every source with only the single literal query phrase, ask the model to survey the topic
    /// from a few distinct angles first (terminology variants, sub-topics, methodology/evaluation framing)
    /// so the retrieval pass has a real chance of finding papers the primary phrase alone would miss.
    /// Falls back to the primary query alone on any failure, so a flaky LLM call never blocks the run.
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
            var response = await chatService.GetChatMessageContentAsync(prompt);
            string cleanResponse = response.ToString().Replace("```json", "").Replace("```", "").Trim();
            using var doc = JsonDocument.Parse(cleanResponse);
            if (doc.RootElement.TryGetProperty("perspectives", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    string? variant = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(variant) && !perspectives.Any(p => p.Equals(variant, StringComparison.OrdinalIgnoreCase)))
                    {
                        perspectives.Add(variant.Trim());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Perspective Expansion] Falling back to single-query search: {SanitizeLogMessage(ex.Message)}");
        }

        return perspectives;
    }

    private async Task<bool> VerifyPeerReviewStatusViaLLMAsync(IChatCompletionService chatService, AcademicPaper paper)
    {
        var prompt = $$"""
            Analyze the following document metadata and determine if it has undergone formal peer review (e.g., published in an academic journal, peer-reviewed conference proceedings, or transactional series) or if it remains an un-reviewed preprint/working paper.

            Document Context:
            - Title: {{paper.Title}}
            - Venue/Source: {{paper.JournalSource}}
            - Metadata Abstract Segment: {{paper.Abstract}}

            Respond ONLY with a valid minified JSON object matching this structure exactly:
            { "isPeerReviewed": true } or { "isPeerReviewed": false }
            """;

        try
        {
            var response = await chatService.GetChatMessageContentAsync(prompt);
            string cleanResponse = response.ToString().Replace("```json", "").Replace("```", "").Trim();
            using var doc = JsonDocument.Parse(cleanResponse);
            if (doc.RootElement.TryGetProperty("isPeerReviewed", out var prop))
            {
                return prop.GetBoolean();
            }
        }
        catch
        {
            if ((paper.JournalSource ?? "").Contains("arXiv", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return false;
    }

    /// <summary>
    /// STORM-style outline-before-prose stage. Asks the model to identify a handful of themes and, per
    /// theme, the specific claims the grounded chunks actually support along with which reference numbers
    /// back each claim - so the final synthesis prose is written against a pre-checked claim map rather
    /// than generating everything in one uncontrolled pass. Returns an empty string (never throws) on any
    /// failure, so the calling report generation falls back to grounding directly on the raw chunks.
    /// </summary>
    private async Task<string> GenerateGroundedOutlineAsync(IChatCompletionService chat, string query, string explicitObjective, string groundedChunksText, string referenceListMapping)
    {
        if (string.IsNullOrWhiteSpace(groundedChunksText)) return string.Empty;

        var outlinePrompt = $$"""
            You are preparing a grounded synthesis outline for a systematic literature review: before writing prose, map out the specific claims the literature supports and which sources back each claim.

            REVIEW OBJECTIVE: "{{explicitObjective}}"
            PRIMARY TOPIC: "{{query}}"

            OFFICIAL ALPHABETIZED REFERENCE LIST FOR THIS RUN:
            {{referenceListMapping}}

            GROUNDED MANUSCRIPT RAW CONTEXT DATA CHUNKS:
            {{groundedChunksText}}

            TASK: Identify 3 to 6 distinct themes that emerge across the included sources. For each theme, list 1-3 concrete, specific claims that the grounded chunks actually support, and for each claim list which reference numbers from the list above support it. Only use reference numbers that appear in the list above. If the grounded chunks are too sparse to support a claim, omit it rather than inventing one.

            Respond ONLY with a valid minified JSON object matching this structure exactly:
            { "themes": [ { "theme": "short theme label", "claims": [ { "text": "specific claim grounded in the sources", "refs": [1, 3] } ] } ] }
            """;

        try
        {
            var structuralSettings = new MistralAIPromptExecutionSettings { Temperature = 0.2 };
            structuralSettings.ExtensionData ??= new Dictionary<string, object>();
            structuralSettings.ExtensionData["response_format"] = new { type = "json_object" };

            var response = await chat.GetChatMessageContentAsync(outlinePrompt, structuralSettings);
            string raw = response.ToString().Replace("```json", "").Replace("```", "").Trim();

            using var doc = JsonDocument.Parse(raw);
            var sb = new StringBuilder();
            if (doc.RootElement.TryGetProperty("themes", out var themes) && themes.ValueKind == JsonValueKind.Array)
            {
                foreach (var theme in themes.EnumerateArray())
                {
                    string themeLabel = theme.TryGetProperty("theme", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(themeLabel)) continue;
                    sb.AppendLine($"- Theme: {themeLabel}");

                    if (theme.TryGetProperty("claims", out var claims) && claims.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var claim in claims.EnumerateArray())
                        {
                            string claimText = claim.TryGetProperty("text", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                            if (string.IsNullOrWhiteSpace(claimText)) continue;

                            var refs = new List<string>();
                            if (claim.TryGetProperty("refs", out var refsArr) && refsArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var r in refsArr.EnumerateArray())
                                {
                                    if (r.ValueKind == JsonValueKind.Number) refs.Add($"[{r.GetInt32()}]");
                                }
                            }
                            sb.AppendLine($"    * {claimText} {string.Join("", refs)}");
                        }
                    }
                }
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Outline Generation] Skipped grounded outline stage: {SanitizeLogMessage(ex.Message)}");
            return string.Empty;
        }
    }

    // Dedicated, citation-mandatory generation of the two sections that must carry inline citations:
    // Results & Synthesis (Item 20a) and Discussion (Item 23a). Generating them on their own - instead of
    // as two fields buried in the big multi-field JSON call, and without the generic stylistic refiner that
    // drops [n] markers - is what keeps the citations present and correct. The grounded claim-to-source
    // outline is fed in so every specific claim can be tied to a real reference number.
    private async Task<(string Synthesis, string Discussion)> GenerateCitedSectionsAsync(
        IChatCompletionService chat, string query, string explicitObjective,
        string groundedOutline, string referenceListMapping, string groundedChunksText)
    {
        var prompt = $$"""
            You are an expert systematic-review author writing two sections of a PRISMA 2020 review:
            the Results & Synthesis section (Item 20a) and the Discussion section (Item 23a).

            REVIEW OBJECTIVE: "{{explicitObjective}}"
            PRIMARY TOPIC: "{{query}}"

            OFFICIAL ALPHABETIZED REFERENCE LIST (cite ONLY these numbers):
            {{referenceListMapping}}

            PRE-CHECKED CLAIM-TO-SOURCE OUTLINE (each claim already mapped to supporting reference numbers):
            {{(string.IsNullOrWhiteSpace(groundedOutline) ? "(No outline available - ground your writing directly on the source context chunks below.)" : groundedOutline)}}

            GROUNDED SOURCE CONTEXT CHUNKS:
            {{groundedChunksText}}

            REQUIREMENTS:
            1. Depth. For the synthesis, write 2 to 4 substantial paragraphs that group findings by theme,
               compare and contrast what different sources report, and name agreements, tensions, and gaps.
               For the discussion, write 2 to 3 paragraphs interpreting what the findings mean, their
               limitations, and their implications - grounded in the same sources, not new claims.
            2. MANDATORY INLINE CITATIONS. Every sentence that states a specific finding, comparison, or claim
               drawn from the literature MUST end with an inline citation marker in square brackets that
               references the reference list by number, for example "...separating inference from governance [3]."
               or "...reported in both [2] and [5]." Use ONLY numbers that appear in the reference list above;
               never invent a number. Purely general framing sentences need no marker, but most sentences in
               these two sections should carry at least one citation.
            3. No placeholder venue names, no fabricated statistics. Ground every claim in the outline and
               context above.

            Respond ONLY with a valid minified JSON object matching this structure exactly:
            { "synthesis": "the full Results and Synthesis text with inline [n] citations", "discussion": "the full Discussion text with inline [n] citations" }
            """;

        try
        {
            var settings = new MistralAIPromptExecutionSettings { Temperature = 0.3 };
            settings.ExtensionData ??= new Dictionary<string, object>();
            settings.ExtensionData["response_format"] = new { type = "json_object" };

            var response = await chat.GetChatMessageContentAsync(prompt, settings);
            string raw = response.ToString().Replace("```json", "").Replace("```", "").Trim();
            int start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            if (start >= 0 && end > start) raw = raw.Substring(start, end - start + 1);

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string synthesis = root.TryGetProperty("synthesis", out var s) ? s.GetString() ?? "" : "";
            string discussion = root.TryGetProperty("discussion", out var d) ? d.GetString() ?? "" : "";
            return (synthesis, discussion);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cited Sections] Falling back to base draft: {SanitizeLogMessage(ex.Message)}");
            return (string.Empty, string.Empty);
        }
    }

    // Documented "peer reviewer" pass. An LLM reviews the synthesis and discussion for depth, citation
    // coverage, grounding, coherence, and over-claiming, then a second pass revises the two sections in
    // response to that feedback. Both the comments and the before/after text are returned so the whole
    // exchange can be written to peer-review-feedback.json for transparency. A single round keeps the
    // cost and latency bounded.
    private async Task<PeerReviewLog> PeerReviewAndReviseAsync(
        IChatCompletionService chat, string synthesis, string discussion,
        string referenceListMapping, string groundedOutline)
    {
        var log = new PeerReviewLog
        {
            GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            SynthesisBefore = synthesis,
            SynthesisAfter = synthesis,
            DiscussionBefore = discussion,
            DiscussionAfter = discussion,
            Verdict = "No changes applied"
        };

        if (string.IsNullOrWhiteSpace(synthesis) && string.IsNullOrWhiteSpace(discussion))
            return log;

        // 1. CRITIQUE
        var critiquePrompt = $$"""
            You are a strict but constructive peer reviewer for a systematic literature review. Review the
            two sections below for: (a) depth and specificity, (b) whether each specific claim carries an
            inline [n] citation, (c) whether cited numbers stay within the reference list, (d) coherence and
            logical flow, and (e) over-claiming beyond what the sources support.

            REFERENCE LIST (valid citation numbers):
            {{referenceListMapping}}

            CLAIM-TO-SOURCE OUTLINE:
            {{(string.IsNullOrWhiteSpace(groundedOutline) ? "(not available)" : groundedOutline)}}

            RESULTS AND SYNTHESIS (Item 20a):
            {{synthesis}}

            DISCUSSION (Item 23a):
            {{discussion}}

            Respond ONLY with a valid minified JSON object:
            { "comments": [ { "section": "synthesis or discussion", "severity": "high, medium, or low", "issue": "what is wrong or weak", "suggestion": "how to fix it" } ] }
            If a section is already strong, return few or no comments for it.
            """;

        var comments = new List<PeerReviewComment>();
        try
        {
            var settings = new MistralAIPromptExecutionSettings { Temperature = 0.2 };
            settings.ExtensionData ??= new Dictionary<string, object>();
            settings.ExtensionData["response_format"] = new { type = "json_object" };

            var response = await chat.GetChatMessageContentAsync(critiquePrompt, settings);
            string raw = response.ToString().Replace("```json", "").Replace("```", "").Trim();
            int st = raw.IndexOf('{');
            int en = raw.LastIndexOf('}');
            if (st >= 0 && en > st) raw = raw.Substring(st, en - st + 1);

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("comments", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in arr.EnumerateArray())
                {
                    comments.Add(new PeerReviewComment(
                        c.TryGetProperty("section", out var se) ? se.GetString() ?? "" : "",
                        c.TryGetProperty("severity", out var sv) ? sv.GetString() ?? "" : "",
                        c.TryGetProperty("issue", out var iss) ? iss.GetString() ?? "" : "",
                        c.TryGetProperty("suggestion", out var sug) ? sug.GetString() ?? "" : ""
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Peer Review Critique] Skipped: {SanitizeLogMessage(ex.Message)}");
        }

        log.Comments = comments;

        if (comments.Count == 0)
        {
            log.Verdict = "No revisions needed - reviewer found no actionable issues";
            return log;
        }

        // 2. REVISE in response to the feedback
        string feedbackText = string.Join("\n", comments.Select(c => $"- [{c.Severity}] ({c.Section}) {c.Issue} -> {c.Suggestion}"));
        var revisePrompt = $$"""
            You are the author revising two sections of a systematic review in response to peer-review
            feedback. Apply the feedback to improve depth and citation coverage. Keep every valid inline [n]
            citation, add the citations the feedback asks for using ONLY numbers from the reference list, and
            do not invent numbers or fabricate claims.

            REFERENCE LIST (valid citation numbers):
            {{referenceListMapping}}

            PEER-REVIEW FEEDBACK:
            {{feedbackText}}

            CURRENT RESULTS AND SYNTHESIS:
            {{synthesis}}

            CURRENT DISCUSSION:
            {{discussion}}

            Respond ONLY with a valid minified JSON object:
            { "synthesis": "the revised synthesis with inline [n] citations", "discussion": "the revised discussion with inline [n] citations" }
            """;

        try
        {
            var settings = new MistralAIPromptExecutionSettings { Temperature = 0.3 };
            settings.ExtensionData ??= new Dictionary<string, object>();
            settings.ExtensionData["response_format"] = new { type = "json_object" };

            var response = await chat.GetChatMessageContentAsync(revisePrompt, settings);
            string raw = response.ToString().Replace("```json", "").Replace("```", "").Trim();
            int st = raw.IndexOf('{');
            int en = raw.LastIndexOf('}');
            if (st >= 0 && en > st) raw = raw.Substring(st, en - st + 1);

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string revSynth = root.TryGetProperty("synthesis", out var s) ? s.GetString() ?? "" : "";
            string revDisc = root.TryGetProperty("discussion", out var d) ? d.GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(revSynth)) log.SynthesisAfter = revSynth;
            if (!string.IsNullOrWhiteSpace(revDisc)) log.DiscussionAfter = revDisc;
            log.Verdict = $"Revisions applied in response to {comments.Count} reviewer comment(s)";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Peer Review Revision] Skipped: {SanitizeLogMessage(ex.Message)}");
            log.Verdict = "Reviewer produced comments but the revision step failed; original text retained";
        }

        return log;
    }

private async Task GeneratePrismaChecklistReportWithRAGAsync(Guid sessionId, int sourceCount, IChatCompletionService chat, string query, string explicitObjective, string inc, string exc, string groundedChunksText, string referenceListMapping, ReviewState finalState, List<string>? searchPerspectives = null, int referenceCount = 0)
    {
        var effectivePerspectives = searchPerspectives ?? new List<string> { query };
        string supplementaryPerspectives = effectivePerspectives.Count > 1
            ? string.Join(" | ", effectivePerspectives.Skip(1))
            : "(none - single literal query only)";

        // STORM-style outline stage: before writing prose, ask the model to map out which specific,
        // grounded claims the sources actually support and which reference numbers back each one. This
        // is persisted to disk as its own audit artifact and then fed back in below so synthesisResultsItem
        // and discussionItem are written against a pre-checked claim map instead of free-associating.
        string groundedOutline = await GenerateGroundedOutlineAsync(chat, query, explicitObjective, groundedChunksText, referenceListMapping);
        if (!string.IsNullOrWhiteSpace(groundedOutline))
        {
            try
            {
                string outlinePath = Path.Combine(GetWorkspaceFolderPath(sessionId), "grounded-outline.txt");
                await File.WriteAllTextAsync(outlinePath, groundedOutline);
            }
            catch { }
        }

        // Facts about the included set, so the abstract cannot call preprints "peer-reviewed studies".
        var venueBreakdown = finalState.SynthesizedRecords
            .GroupBy(r => r.VenueType)
            .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}")
            .ToList();
        string includedFacts = $"{finalState.SynthesizedRecords.Count} included record(s)" +
            (venueBreakdown.Count > 0 ? $" ({string.Join(", ", venueBreakdown)})" : "") +
            $"; the peer-reviewed-only filter was {(finalState.PeerReviewOnlyToggle ? "ON" : "OFF")}.";

        var reportPrompt = $$"""
            You are an elite academic meta-analyst preparing an evaluation report mapped to the PRISMA 2020 Expanded Checklist.

            CRITICAL LIVE RUN CONSTRAINTS (ZERO HALLUCINATION DIRECTIVE):
            - The primary search query for this run was exactly: "{{query}}"
            - To broaden recall (multi-perspective search), the engine additionally queried every source with these supplementary phrasings: {{supplementaryPerspectives}}
            - The review objective specified by the user was exactly: "{{explicitObjective}}"
            - The screening inclusion thresholds were exactly: "{{inc}}"
            - The screening exclusion thresholds were exactly: "{{exc}}"
            - Included set: {{includedFacts}} Do not describe the included studies as peer-reviewed unless the filter was ON, and do not state any count that is not given here.
            - Screening was done by a language model only; do not claim that any human screened, verified or reviewed records during the run.

            OFFICIAL ALPHABETIZED REFERENCE LIST FOR THIS RUN:
            {{referenceListMapping}}

            GROUNDED MANUSCRIPT RAW CONTEXT DATA CHUNKS:
            {{groundedChunksText}}

            PRE-COMPUTED GROUNDED OUTLINE (claims already checked against the sources above - ground synthesisResultsItem and discussionItem on these claims):
            {{(string.IsNullOrWhiteSpace(groundedOutline) ? "(No outline could be generated - ground your synthesis directly on the raw context chunks above instead.)" : groundedOutline)}}

            CITATION GROUNDING REQUIREMENT (mandatory traceability):
            When writing "synthesisResultsItem" and "discussionItem", every specific claim, finding, or comparison drawn from the literature MUST end with an inline citation marker in square brackets referencing the OFFICIAL ALPHABETIZED REFERENCE LIST above by its number, e.g. "...improves fault isolation [3]." or "...as shown in both [2] and [5]." Only cite numbers that actually appear in that list - never invent one. General framing sentences that state no specific finding do not need a citation marker.

            TASK:
            Generate a fully formed, detailed academic paragraph for each checklist field below.

            STRICT COMPOSITION SEPARATION RULES:
            You MUST separate your response text into two completely isolated segments:
            SEGMENT 1: Provide a clean, single minified JSON object matching the template below. The "synthesisResultsItem" field MUST contain only the standard text summary paragraph. Do NOT place diagram code, backslashes, or syntax inside the JSON text blocks.
            SEGMENT 2: Completely OUTSIDE and AFTER the JSON object, write your model codes using these exact tag blocks:
            
            [MERMAID_START]
            graph LR
            ...
            [MERMAID_END]

            [TIKZ_START]
            \begin{tikzpicture}[node distance=1.5cm, auto, >=Stealth]
            ...
            \end{tikzpicture}
            [TIKZ_END]

            JSON TEMPLATE SCHEMA:
            {
                "titleItem": "A single-line systematic review title (at most 25 words, no full stop) on '{{query}}' mapping to PRISMA Item 1.",
                "abstractItem": "A formal abstract overview addressing the core objective ('{{explicitObjective}}') mapping to PRISMA Item 2.",
                "rationaleItem": "A rigorous context justification detailing the current state of software frameworks regarding '{{query}}' mapping to PRISMA Item 3.",
                "objectivesItem": "The explicit question formulation matching the stated goal: '{{explicitObjective}}' mapping to PRISMA Item 4.",
                "biasAssessmentItem": "This field will be post-processed. Output exactly: 'PREDEFINED_METADATA_MARKER'",
                "synthesisResultsItem": "Your simple-English factual literature summary paragraph mapping to PRISMA Item 20a. Do not place code syntax here.",
                "discussionItem": "Provide a general architectural interpretation with multi-source citations mapping to PRISMA Item 23a.",
                "supportItem": "This field will be post-processed. Output exactly: 'PREDEFINED_SUPPORT_MARKER'",
                "availabilityItem": "This field will be post-processed. Output exactly: 'PREDEFINED_REPO_MARKER'"
            }
            """;

        try
        {
            // FORCE JSON MODE: Configure Mistral to guarantee mathematically valid JSON token structures
            var structuralSettings = new MistralAIPromptExecutionSettings
            {
                Temperature = 0.2
            };

            // Universally force JSON mode via the underlying extension dictionary properties
            structuralSettings.ExtensionData ??= new Dictionary<string, object>();
            structuralSettings.ExtensionData["response_format"] = new { type = "json_object" };

            var response = await chat.GetChatMessageContentAsync(reportPrompt, structuralSettings);
            string rawResponse = response.ToString().Trim();

            string mermaidBlock = "";
            var mermaidMatch = Regex.Match(rawResponse, @"\[MERMAID_START\](.*?)\[MERMAID_END\]", RegexOptions.Singleline);
            if (mermaidMatch.Success)
            {
                mermaidBlock = "\n\n[MERMAID_START]\n" + mermaidMatch.Groups[1].Value.Trim() + "\n[MERMAID_END]\n";
            }

            string tikzBlock = "";
            var tikzMatch = Regex.Match(rawResponse, @"\[TIKZ_START\](.*?)\[TIKZ_END\]", RegexOptions.Singleline);
            if (tikzMatch.Success)
            {
                tikzBlock = "\n\n[TIKZ_START]\n" + tikzMatch.Groups[1].Value.Trim() + "\n[TIKZ_END]\n";
            }

            string jsonSearchText = rawResponse;
            jsonSearchText = Regex.Replace(jsonSearchText, @"\[MERMAID_START\].*?\[MERMAID_END\]", "", RegexOptions.Singleline);
            jsonSearchText = Regex.Replace(jsonSearchText, @"\[TIKZ_START\].*?\[TIKZ_END\]", "", RegexOptions.Singleline);

            int jsonStartIdx = jsonSearchText.IndexOf('{');
            int jsonEndIdx = jsonSearchText.LastIndexOf('}');
            string jsonPart = "{}";
            if (jsonStartIdx != -1 && jsonEndIdx != -1 && jsonEndIdx > jsonStartIdx)
            {
                jsonPart = jsonSearchText.Substring(jsonStartIdx, jsonEndIdx - jsonStartIdx + 1).Trim();
            }

            jsonPart = jsonPart.Replace("```json", "").Replace("```", "").Trim();
            
            // CLEANING PASS: Clean hidden ASCII control characters and wrap unescaped path backslashes
            jsonPart = Regex.Replace(jsonPart, "[\x00-\x1F]", " "); 
            jsonPart = Regex.Replace(jsonPart, @"\\(?![""\\/bfnrtu])", @"\\");

            using var doc = JsonDocument.Parse(jsonPart);
            var root = doc.RootElement;

            string ResolveField(string propertyName) =>
                root.TryGetProperty(propertyName, out var element) ? element.GetString() ?? "" : "Extraction failed.";

            // 1. EXTRACT RAW BASELINE DATA FIELDS
            string rawTitle = ResolveField("titleItem");
            string rawAbstract = ResolveField("abstractItem");
            string rawRationale = ResolveField("rationaleItem");
            string rawObjectives = ResolveField("objectivesItem");
            // PRISMA Items 5-8 describe what the run did, so they are rendered from the run's own configuration
            // and ledger rather than written by the model (see MethodsSectionWriter for why).
            var selectedNames = finalState.SelectedSources.Select(SourceCatalog.DisplayNameFor).ToList();
            var unavailableNames = finalState.UnavailableSources.Select(SourceCatalog.DisplayNameFor).ToList();
            string methodsEligibility = MethodsSectionWriter.Eligibility(inc, exc, finalState.PeerReviewOnlyToggle);
            string methodsSources = MethodsSectionWriter.InformationSources(selectedNames, unavailableNames, finalState.SearchLogs, finalState.Timestamp);
            string methodsSearch = MethodsSectionWriter.SearchStrategy(query, effectivePerspectives, finalState.MaxResultsPerSource,
                finalState.Stats.DuplicatesRemoved, finalState.Stats.CappedBeyondMaxResults, finalState.YearFrom, finalState.YearTo, finalState.Stats.OutsideDateRange);
            string methodsSelection = MethodsSectionWriter.SelectionProcess(finalState.PeerReviewOnlyToggle, finalState.Stats);
            string rawDiscussion = ResolveField("discussionItem");
            string rawSynthesisText = ResolveField("synthesisResultsItem");

            var deltas = new List<StyleDeltaLog>();

            // 2. PASS THE PROSE FIELDS THROUGH THE STYLISTIC REFINER
            // Only free prose is copy-edited. The title is left alone (the refiner once turned it into a
            // five-sentence paragraph), and the methods items are generated from run data so there is nothing
            // to copy-edit - rewriting them is how a search string got silently shortened in the report.
            string cleanTitle = CleanTitle(rawTitle, query);
            deltas.Add(new StyleDeltaLog("Title", rawTitle, cleanTitle, Applied: false, Note: "Title is not sent through the stylistic pass."));
            var (cleanAbstract, dAbstract) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Abstract", rawAbstract); deltas.Add(dAbstract);
            var (cleanRationale, dRationale) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Rationale", rawRationale); deltas.Add(dRationale);
            var (cleanObjectives, dObjectives) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Objectives", rawObjectives); deltas.Add(dObjectives);
            // The Results & Synthesis (20a) and Discussion (23a) sections are NOT run through the generic
            // stylistic refiner - that rewrite step tends to drop the inline [n] citation markers. They are
            // instead produced by a dedicated citation-mandatory pass and then improved by a documented
            // peer-review pass, so the shipped text keeps its traceable citations and gains depth.
            var (citedSynthesis, citedDiscussion) = await GenerateCitedSectionsAsync(
                chat, query, explicitObjective, groundedOutline, referenceListMapping, groundedChunksText);

            // Fall back to the base multi-field draft only if the dedicated pass returned nothing.
            if (string.IsNullOrWhiteSpace(citedSynthesis)) citedSynthesis = rawSynthesisText;
            if (string.IsNullOrWhiteSpace(citedDiscussion)) citedDiscussion = rawDiscussion;

            // Documented "peer reviewer" pass: an LLM critiques the two sections and revises them, and the
            // whole exchange is written to peer-review-feedback.json in the workspace for transparency.
            var peerReview = await PeerReviewAndReviseAsync(chat, citedSynthesis, citedDiscussion, referenceListMapping, groundedOutline);
            try
            {
                string peerReviewPath = Path.Combine(GetWorkspaceFolderPath(sessionId), "peer-review-feedback.json");
                await File.WriteAllTextAsync(peerReviewPath, JsonSerializer.Serialize(peerReview, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }

            // Re-append the isolated architectural diagram strings back onto the reviewed synthesis field
            string fullSynthesisField = peerReview.SynthesisAfter + mermaidBlock + tikzBlock;
            string preValidationDiscussion = peerReview.DiscussionAfter;

            // STORM-style traceability guardrail: every [n] citation marker the model writes must resolve to
            // an entry that actually exists in the run's reference list. The model occasionally invents or
            // miscounts numbers (e.g. citing [56] against a 53-entry list); strip those instead of shipping
            // a report whose citations don't trace back to a real source, and log every removal to an
            // on-disk audit artifact so the discrepancy itself stays visible rather than silently vanishing.
            // The stripping logic lives in CitationValidator so it can be unit-tested in isolation.
            var citationStrips = new List<StrippedCitation>();

            var synthesisValidation = CitationValidator.ValidateAndStrip(fullSynthesisField, referenceCount, "synthesisResultsItem");
            fullSynthesisField = synthesisValidation.CleanedText;
            citationStrips.AddRange(synthesisValidation.Stripped);

            var discussionValidation = CitationValidator.ValidateAndStrip(preValidationDiscussion, referenceCount, "discussionItem");
            string cleanDiscussionValidated = discussionValidation.CleanedText;
            citationStrips.AddRange(discussionValidation.Stripped);

            finalState.Stats.InvalidCitationsStripped += citationStrips.Count;

            try
            {
                string auditPath = Path.Combine(GetWorkspaceFolderPath(sessionId), "citation-audit.json");
                var auditPayload = new
                {
                    ReferenceListSize = referenceCount,
                    InvalidMarkersStripped = citationStrips.Count,
                    Details = citationStrips
                };
                await File.WriteAllTextAsync(auditPath, JsonSerializer.Serialize(auditPayload, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }

            var reportObj = new PrismaReport
            {
                GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                TitleItem = cleanTitle,
                AbstractItem = !string.IsNullOrWhiteSpace(cleanAbstract) ? cleanAbstract : rawAbstract,
                RationaleItem = !string.IsNullOrWhiteSpace(cleanRationale) ? cleanRationale : rawRationale,
                ObjectivesItem = !string.IsNullOrWhiteSpace(cleanObjectives) ? cleanObjectives : rawObjectives,
                EligibilityItem = methodsEligibility,
                SourcesItem = methodsSources,
                SearchStrategyItem = methodsSearch,
                SelectionProcessItem = methodsSelection,
                SynthesisResultsItem = fullSynthesisField,
                DiscussionItem = cleanDiscussionValidated,
                BiasAssessmentItem = "Internal risk of bias is controlled via mandatory post-generation human verification. Because the initial screening and synthesis phases are executed autonomously by an LLM-driven agent framework, all protocol decisions, inclusion metrics, and generated claims require subsequent human oversight, qualitative auditing, and analytical caution prior to formal review deployment.",
                SupportItem = _supportStatement,
                ProtocolHash = finalState.ProtocolHash,
                AvailabilityItem = "All automated retrieval configurations, screening criteria matrices, and synthesis generation pipeline code structures are publicly accessible via the project repository on GitHub at https://github.com/lauPhilip/au-btech-literature-review-agent."
            };

            // 3. ARCHIVE THE AUDIT DELTA LEDGER DIRECTLY INTO THE ACTIVE WORKSPACE STORE SUBFOLDER
            string ledgerPath = Path.Combine(GetWorkspaceFolderPath(sessionId), "stylistic-transformation-ledger.json");
            await File.WriteAllTextAsync(ledgerPath, JsonSerializer.Serialize(deltas, new JsonSerializerOptions { WriteIndented = true }));

            // 4. WRITE THE MAIN CONSOLIDATED REPORT
            await File.WriteAllTextAsync(GetReportFilePath(sessionId), JsonSerializer.Serialize(reportObj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception fatalEx)
        {
            Console.WriteLine($"[Extraction Fault] Fallback trace triggered: {SanitizeLogMessage(fatalEx.Message)}");
            var failureReport = new PrismaReport
            {
                GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                TitleItem = $"Checklist Synthesis generation completely faulted: {SanitizeLogMessage(fatalEx.Message)}"
            };
            await File.WriteAllTextAsync(GetReportFilePath(sessionId), JsonSerializer.Serialize(failureReport, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// Keeps the title a title: first line only, no trailing full stop, and if the model returned a
    /// paragraph (or nothing) fall back to a neutral title built from the query.
    /// </summary>
    public static string CleanTitle(string rawTitle, string query)
    {
        string t = (rawTitle ?? "").Split('\n')[0].Trim().TrimEnd('.');
        int words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        bool looksLikeParagraph = Regex.Matches(t, @"[.!?]\s+[A-Z]").Count > 0 || words > 30;
        if (string.IsNullOrWhiteSpace(t) || t == "Extraction failed" || looksLikeParagraph)
            return $"{query.Trim()}: A Systematic Literature Review";
        return t;
    }

    private void CleanOldManuscriptArtifacts(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath)) return;
            var targets = Directory.GetFiles(directoryPath, "manuscript_*.*")
                .Where(f => f.EndsWith(".tex") || f.EndsWith(".pdf") || f.EndsWith(".log") || f.EndsWith(".aux"));
            foreach (var file in targets)
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup Cleanup Warning] Safely skipped file purge: {ex.Message}");
        }
    }

    private string EscapeLatexText(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        string cleanInput = input.Replace("https://doi.org/XXXX-XXXXXX", "")
                                 .Replace("https://doi.org/XXXXXXX.XXXXXXX", "")
                                 .Replace("https://doi.org/XX.XXXX/", "")
                                 .Replace("https://doi.org/XXXX", "")
                                 .Replace("https://doi.org/XXX", "")
                                 .Replace("XXXXXX.XXXXX", "")
                                 .TrimEnd(' ', ',', '.', '/');

        string escaped = cleanInput.Replace(@"\", @"\textbackslash ")
                         .Replace("&", @"\&")
                         .Replace("%", @"\%")
                         .Replace("$", @"\$")
                         .Replace("_", @"\_")
                         .Replace("#", @"\#")
                         .Replace("{", @"\{")
                         .Replace("}", @"\}")
                         .Replace("~", @"\textasciitilde ")
                         .Replace("^", @"\textasciicircum ");
        // Markdown-style *italics* (used for venues in the APA references) -> real LaTeX italics.
        return Regex.Replace(escaped, @"\*([^*\n]+)\*", @"\textit{$1}");
    }
    
    /// <summary>
    /// Loads the persisted report + synthesized records for a session straight off disk and builds the
    /// workspace archive from them. Lets the download be served from a plain HTTP endpoint (Program.cs)
    /// instead of round-tripping the whole zip through the Blazor Server SignalR connection, which has a
    /// small default max message size and would silently fail once real source PDFs are included.
    /// </summary>
    public byte[] GenerateWorkspaceArchiveFromDisk(Guid sessionId)
    {
        var report = new PrismaReport();
        string reportPath = GetReportFilePath(sessionId);
        if (File.Exists(reportPath))
        {
            try
            {
                string content = File.ReadAllText(reportPath);
                report = JsonSerializer.Deserialize<PrismaReport>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PrismaReport();
            }
            catch { }
        }

        var records = new List<IncludedPaperMetricRow>();
        string statePath = GetStateFilePath(sessionId);
        if (File.Exists(statePath))
        {
            try
            {
                string stateJson = File.ReadAllText(statePath);
                using var doc = JsonDocument.Parse(stateJson);
                if (doc.RootElement.TryGetProperty("SynthesizedRecords", out var recordsProp))
                {
                    records = JsonSerializer.Deserialize<List<IncludedPaperMetricRow>>(recordsProp.GetRawText()) ?? new();
                }
            }
            catch { }
        }

        return GenerateWorkspaceArchive(sessionId, report, records);
    }

    // NOTE: historically named GenerateManuscriptPdf, but it never compiled a PDF - it always returned a
    // zip bundle (LaTeX source + JSON ledger + source PDFs). Renamed to match what it actually produces.
    public byte[] GenerateWorkspaceArchive(Guid sessionId, PrismaReport report, List<IncludedPaperMetricRow> records)
    {
        // The LaTeX below interpolates decimals (pie-slice angles, axis limits). On a machine with a Danish
        // (or any comma-decimal) culture those came out as "51,43", which breaks the TikZ syntax, so the
        // document is always written with the invariant culture.
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            return BuildWorkspaceArchive(sessionId, report, records);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private byte[] BuildWorkspaceArchive(Guid sessionId, PrismaReport report, List<IncludedPaperMetricRow> records)
    {
        string projectRoot = Directory.GetCurrentDirectory();
        CleanOldManuscriptArtifacts(projectRoot);

        string uniqueId = Guid.NewGuid().ToString("N").Substring(0, 8);
        string texFilePath = Path.Combine(projectRoot, $"manuscript_{uniqueId}.tex");

        // Table 3.1 and the bibliography must follow the reference numbers the synthesis cites.
        // (Older runs have ReferenceNumber = 0 everywhere; OrderBy is stable, so they keep their order.)
        records = records.OrderBy(r => r.ReferenceNumber).ToList();

        int journalCount = records.Count(r => r.VenueType.Equals("Journals", StringComparison.OrdinalIgnoreCase));
        int conferenceCount = records.Count(r => r.VenueType.Equals("Conferences", StringComparison.OrdinalIgnoreCase));
        int transactionsCount = records.Count(r => r.VenueType.Equals("Transactions", StringComparison.OrdinalIgnoreCase));
        int proceedingsCount = records.Count(r => r.VenueType.Equals("Proceedings", StringComparison.OrdinalIgnoreCase));
        int preprintCount = records.Count(r => r.VenueType.Equals("Preprints", StringComparison.OrdinalIgnoreCase) || r.VenueType.Equals("Other", StringComparison.OrdinalIgnoreCase));

        int arxivCount = records.Count(r => r.Category.Equals("Arxiv", StringComparison.OrdinalIgnoreCase));
        int scopusCount = records.Count(r => r.Category.Equals("ScienceDirect", StringComparison.OrdinalIgnoreCase));
        int ieeeCount = records.Count(r => r.Category.Equals("IEEE", StringComparison.OrdinalIgnoreCase));
        int scholarCount = records.Count(r => r.Category.Equals("Scholar", StringComparison.OrdinalIgnoreCase));
        int rgCount = records.Count(r => r.Category.Equals("ResearchGate", StringComparison.OrdinalIgnoreCase));

        double totalPie = journalCount + conferenceCount + transactionsCount + proceedingsCount + preprintCount;
        double totalSourcesPie = arxivCount + scopusCount + ieeeCount + scholarCount + rgCount;

        // Year axis built from the included papers themselves. It used to be fixed at 2023-2026, so papers
        // from other years (or with no year) silently disappeared from the chart.
        var publicationsByYear = records
            .GroupBy(r => r.Year > 0 ? r.Year.ToString() : "nodate")
            .OrderBy(g => g.Key == "nodate" ? "9999" : g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());
        if (publicationsByYear.Count == 0) publicationsByYear[DateTime.UtcNow.Year.ToString()] = 0;
        // Plotted at x = 1..n with the years as tick labels. (pgfplots symbolic coords cannot hold "n.d.".)
        var yearKeys = publicationsByYear.Keys.ToList();
        string yearTicks = string.Join(",", Enumerable.Range(1, yearKeys.Count));
        string yearLabels = string.Join(",", yearKeys.Select(k => k == "nodate" ? "n.d." : k));
        string yearPoints = string.Join(" ", yearKeys.Select((k, i) => $"({i + 1},{publicationsByYear[k]})"));
        int yearAxisMax = Math.Max(4, publicationsByYear.Values.DefaultIfEmpty(0).Max() + 1);

        string sanitizedTitleItem = EscapeLatexText((report.TitleItem ?? "Systematic Review Manuscript").Replace("[Source Context Anchor 1]", "").Trim());
        string sanitizedAbstractItem = EscapeLatexText(report.AbstractItem ?? "");
        string sanitizedRationaleItem = EscapeLatexText(report.RationaleItem ?? "");
        string sanitizedObjectivesItem = EscapeLatexText(report.ObjectivesItem ?? "");
        string sanitizedEligibilityItem = EscapeLatexText(report.EligibilityItem ?? "");
        string sanitizedSourcesItem = EscapeLatexText(report.SourcesItem ?? "");
        string sanitizedSearchStrategyItem = EscapeLatexText(report.SearchStrategyItem ?? "");
        string sanitizedSelectionProcessItem = EscapeLatexText(report.SelectionProcessItem ?? "");
        string sanitizedBiasAssessmentItem = EscapeLatexText(report.BiasAssessmentItem ?? "");
        
        string rawSynthesis = report.SynthesisResultsItem ?? "";
        string textSynthesisOnly = Regex.Replace(rawSynthesis, @"\[MERMAID_START\].*?\[MERMAID_END\]", "", RegexOptions.Singleline);
        textSynthesisOnly = Regex.Replace(textSynthesisOnly, @"\[TIKZ_START\].*?\[TIKZ_END\]", "", RegexOptions.Singleline).Trim();
        string sanitizedSynthesisResultsItem = EscapeLatexText(textSynthesisOnly);

        string tikzDiagramCode = "";
        var tikzMatch = Regex.Match(rawSynthesis, @"\[TIKZ_START\](.*?)\[TIKZ_END\]", RegexOptions.Singleline);
        if (tikzMatch.Success) {
            tikzDiagramCode = tikzMatch.Groups[1].Value.Trim();
            // "-->" is not a TikZ arrow tip (it made every generated main.tex fail to compile); only repair the
            // mangled "-¿" some model outputs contain.
            tikzDiagramCode = tikzDiagramCode.Replace("-¿", "->");
        }

        string sanitizedDiscussionItem = EscapeLatexText(report.DiscussionItem ?? "");
        string sanitizedSupportItem = EscapeLatexText(report.SupportItem ?? "");

        var sb = new StringBuilder();
        sb.AppendLine(@"\documentclass[9pt,a4paper]{article}");
        sb.AppendLine(@"\usepackage[utf8]{inputenc}");
        sb.AppendLine(@"\usepackage[margin=0.8in]{geometry}");
        sb.AppendLine(@"\usepackage{amsmath,amsfonts,amssymb}");
        sb.AppendLine(@"\usepackage{booktabs}");
        sb.AppendLine(@"\usepackage{ltablex}"); 
        sb.AppendLine(@"\usepackage{xcolor}");
        sb.AppendLine(@"\usepackage{fancyhdr}");
        sb.AppendLine(@"\usepackage{float}");
        sb.AppendLine(@"\usepackage{pgfplots}");
        sb.AppendLine(@"\usepgfplotslibrary{statistics}");
        sb.AppendLine(@"\pgfplotsset{compat=1.18}");
        sb.AppendLine(@"\usetikzlibrary{matrix,arrows.meta,positioning}");
        
        sb.AppendLine(@"\usepackage{tgtermes}"); 
        sb.AppendLine(@"\usepackage{tgheros}");  
        sb.AppendLine(@"\usepackage{tgcursor}");  
        sb.AppendLine(@"\usepackage{multicol}");
        
        sb.AppendLine(@"\definecolor{auBlue}{HTML}{003B5C}");
        sb.AppendLine(@"\definecolor{greyText}{HTML}{4B5563}");
        sb.AppendLine(@"\definecolor{customIndigo}{HTML}{312E81}");
        
        sb.AppendLine(@"\pagestyle{fancy}");
        sb.AppendLine(@"\fancyhf{}");
        sb.AppendLine(@"\renewcommand{\headrulewidth}{0pt}");
        sb.AppendLine(@"\fancyfoot[C]{\sffamily\scriptsize\color{greyText} Page \thepage}");

        sb.AppendLine(@"\begin{document}");

        sb.AppendLine(@"\begin{center}");
        sb.AppendLine(@"    {\sffamily\bfseries\scriptsize\color{gray} AI-GENERATED SYSTEMATIC LITERATURE REVIEW  \\ \vspace{4pt}}");
        sb.AppendLine("    {\\rmfamily\\LARGE\\bfseries " + sanitizedTitleItem + " \\\\ \\vspace{10pt}}");
        sb.AppendLine(@"    {\sffamily\small\color{greyText} Department of Business Development and Technology, Aarhus University \\ \vspace{4pt}}");
        string protocolHash = string.IsNullOrWhiteSpace(report.ProtocolHash) ? "not recorded" : report.ProtocolHash;
        sb.AppendLine("    {\\ttfamily\\scriptsize\\color{gray} Protocol Hash: " + protocolHash + " | Generated: " + (report.GeneratedAt ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC")) + " \\\\ \\vspace{15pt}}");
        sb.AppendLine(@"    \color{lightgray}\hrule\vspace{15pt}");
        sb.AppendLine(@"    \color{black}");
        sb.AppendLine(@"\end{center}");

        sb.AppendLine(@"\noindent\colorbox{black!4}{");
        sb.AppendLine(@"\parbox{\dimexpr\linewidth-2\fboxsep\relax}{");
        sb.AppendLine(@"    \small\sffamily\bfseries\color{auBlue} ABSTRACT \\ \vspace{4pt}");
        sb.AppendLine("    \\itshape\\rmfamily\\color{black} " + sanitizedAbstractItem);
        sb.AppendLine(@"}}");
        sb.AppendLine(@"\vspace{15pt}");

        sb.AppendLine(@"\begin{multicols}{2}");

        sb.AppendLine(@"\section{Introduction}");
        sb.AppendLine(@"\subsection{Rationale}");
        sb.AppendLine(sanitizedRationaleItem);
        sb.AppendLine(@"\subsection{Objectives}");
        sb.AppendLine(sanitizedObjectivesItem);

        sb.AppendLine(@"\section{Methodology}");
        sb.AppendLine(@"\subsection{Eligibility Criteria}");
        sb.AppendLine(sanitizedEligibilityItem);
        sb.AppendLine(@"\subsection{Information Sources}");
        sb.AppendLine(sanitizedSourcesItem);
        sb.AppendLine(@"\subsection{Search Execution}");
        sb.AppendLine(sanitizedSearchStrategyItem);
        sb.AppendLine(@"\subsection{Selection Automation}");
        sb.AppendLine(sanitizedSelectionProcessItem);
        sb.AppendLine(@"\subsection{Internal Risk of Bias Assessment}");
        sb.AppendLine(sanitizedBiasAssessmentItem);

        sb.AppendLine(@"\end{multicols}");
        sb.AppendLine(@"\section{Data \& Collection Metrics}");
        sb.AppendLine(@"The empirical data metrics trace key research trends regarding structural database distributions. ");
        sb.AppendLine($"In total, {records.Count} papers were chosen for final data extraction.");
        sb.AppendLine(@"\vspace{10pt}");

        sb.AppendLine(@"\begin{figure}[H]");
        sb.AppendLine(@"\centering");
        
        sb.AppendLine(@"\begin{minipage}{0.32\textwidth}");
        sb.AppendLine(@"\centering");
        sb.AppendLine(@"\begin{tikzpicture}[scale=0.65]");
        sb.AppendLine($"\\begin{{axis}}[ybar, xtick={{{yearTicks}}}, xticklabels={{{yearLabels}}}, xmin=0.5, xmax={yearKeys.Count + 0.5}, ymin=0, ymax={yearAxisMax}, ylabel={{Papers}}, xlabel={{Year}}, nodes near coords, width=\\textwidth, height=5.5cm, bar width=10pt]");
        sb.AppendLine($"\\addplot[fill=blue!50] coordinates {{{yearPoints}}};");
        sb.AppendLine(@"\end{axis}");
        sb.AppendLine(@"\end{tikzpicture}");
        sb.AppendLine(@"\caption{Papers per Year}");
        sb.AppendLine(@"\end{minipage}\hfill");

        sb.AppendLine(@"\begin{minipage}{0.32\textwidth}");
        sb.AppendLine(@"\centering");
        sb.AppendLine(@"\begin{tikzpicture}[scale=0.55]");
        if (totalSourcesPie > 0)
        {
            double startSrc = 0;
            if (arxivCount > 0) { double d = (arxivCount / totalSourcesPie) * 360; sb.AppendLine($"\\draw[fill=teal!40] (0,0) -- ({startSrc}:1.2cm) arc ({startSrc}:{startSrc + d}:1.2cm) -- cycle; \\node at ({startSrc + d/2}:0.8cm) {{\\scriptsize {arxivCount}}};"); startSrc += d; }
            if (scopusCount > 0) { double d = (scopusCount / totalSourcesPie) * 360; sb.AppendLine($"\\draw[fill=orange!40] (0,0) -- ({startSrc}:1.2cm) arc ({startSrc}:{startSrc + d}:1.2cm) -- cycle; \\node at ({startSrc + d/2}:0.8cm) {{\\scriptsize {scopusCount}}};"); startSrc += d; }
            if (ieeeCount > 0) { double d = (ieeeCount / totalSourcesPie) * 360; sb.AppendLine($"\\draw[fill=blue!30] (0,0) -- ({startSrc}:1.2cm) arc ({startSrc}:{startSrc + d}:1.2cm) -- cycle; \\node at ({startSrc + d/2}:0.8cm) {{\\scriptsize {ieeeCount}}};"); startSrc += d; }
            if (scholarCount > 0) { double d = (scholarCount / totalSourcesPie) * 360; sb.AppendLine($"\\draw[fill=yellow!40] (0,0) -- ({startSrc}:1.2cm) arc ({startSrc}:{startSrc + d}:1.2cm) -- cycle; \\node at ({startSrc + d/2}:0.8cm) {{\\scriptsize {scholarCount}}};"); startSrc += d; }
            if (rgCount > 0) { double d = (rgCount / totalSourcesPie) * 360; sb.AppendLine($"\\draw[fill=purple!30] (0,0) -- ({startSrc}:1.2cm) arc ({startSrc}:{startSrc + d}:1.2cm) -- cycle; \\node at ({startSrc + d/2}:0.8cm) {{\\scriptsize {rgCount}}};"); }

            sb.AppendLine(@"\matrix [draw,below,matrix of nodes,font=\tiny,at={(current bounding box.south)},yshift=-0.4cm] {");
            sb.AppendLine($"\\node[fill=teal!40,label=right:({arxivCount}) Arxiv] {{}}; & \\node[fill=orange!40,label=right:({scopusCount}) ScienceDirect] {{}}; \\\\");
            sb.AppendLine($"\\node[fill=blue!30,label=right:({ieeeCount}) IEEE Xplore] {{}}; & \\node[fill=yellow!40,label=right:({scholarCount}) Scholar] {{}}; \\\\");
            sb.AppendLine($"\\node[fill=purple!30,label=right:({rgCount}) ResearchGate] {{}}; & \\\\");
            sb.AppendLine(@"};");
        }
        else
        {
            sb.AppendLine(@"\draw[fill=gray!20] (0,0) circle (1.2cm); \node at (0,0) {No Data};");
        }
        sb.AppendLine(@"\end{tikzpicture}");
        sb.AppendLine(@"\caption{Platform Distribution}");
        sb.AppendLine(@"\end{minipage}\hfill");

        sb.AppendLine(@"\begin{minipage}{0.32\textwidth}");
        sb.AppendLine(@"\centering");
        sb.AppendLine(@"\begin{tikzpicture}[scale=0.55]");
        if (totalPie > 0)
        {
            double start = 0;
            if (journalCount > 0) { double d = (journalCount / totalPie) * 360; sb.AppendLine($"\\draw[fill=blue!50] (0,0) -- ({start}:1.2cm) arc ({start}:{start + d}:1.2cm) -- cycle; \\node at ({start + d/2}:0.8cm) {{\\scriptsize {journalCount}}};"); start += d; }
            if (conferenceCount > 0) { double d = (conferenceCount / totalPie) * 360; sb.AppendLine($"\\draw[fill=customIndigo!50] (0,0) -- ({start}:1.2cm) arc ({start}:{start + d}:1.2cm) -- cycle; \\node at ({start + d/2}:0.8cm) {{\\scriptsize {conferenceCount}}};"); start += d; }
            if (transactionsCount > 0) { double d = (transactionsCount / totalPie) * 360; sb.AppendLine($"\\draw[fill=teal!50] (0,0) -- ({start}:1.2cm) arc ({start}:{start + d}:1.2cm) -- cycle; \\node at ({start + d/2}:0.8cm) {{\\scriptsize {transactionsCount}}};"); start += d; }
            if (proceedingsCount > 0) { double d = (proceedingsCount / totalPie) * 360; sb.AppendLine($"\\draw[fill=orange!50] (0,0) -- ({start}:1.2cm) arc ({start}:{start + d}:1.2cm) -- cycle; \\node at ({start + d/2}:0.8cm) {{\\scriptsize {proceedingsCount}}};"); start += d; }
            if (preprintCount > 0) { double d = (preprintCount / totalPie) * 360; sb.AppendLine($"\\draw[fill=red!50] (0,0) -- ({start}:1.2cm) arc ({start}:{start + d}:1.2cm) -- cycle; \\node at ({start + d/2}:0.8cm) {{\\scriptsize {preprintCount}}};"); }
            
            sb.AppendLine(@"\matrix [draw,below,matrix of nodes,font=\tiny,at={(current bounding box.south)},yshift=-0.4cm] {");
            sb.AppendLine($"\\node[fill=blue!50,label=right:({journalCount}) Journals] {{}}; & \\node[fill=customIndigo!50,label=right:({conferenceCount}) Conferences] {{}}; \\\\");
            sb.AppendLine($"\\node[fill=teal!50,label=right:({transactionsCount}) Transactions] {{}}; & \\node[fill=orange!50,label=right:({proceedingsCount}) Proceedings] {{}}; \\\\");
            sb.AppendLine($"\\node[fill=red!50,label=right:({preprintCount}) Preprints] {{}}; & \\\\");
            sb.AppendLine(@"};");
        }
        else
        {
            sb.AppendLine(@"\draw[fill=gray!20] (0,0) circle (1.2cm); \node at (0,0) {No Data};");
        }
        sb.AppendLine(@"\end{tikzpicture}");
        sb.AppendLine(@"\caption{Venue Share}");
        sb.AppendLine(@"\end{minipage}");
        sb.AppendLine(@"\end{figure}");
        sb.AppendLine(@"\vspace{10pt}");

        sb.AppendLine(@"\small");
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.6\hsize}X >{\hsize=1.2\hsize}X >{\hsize=1.0\hsize}X >{\hsize=0.5\hsize}X >{\hsize=1.7\hsize}X}");
        sb.AppendLine(@"\multicolumn{5}{l}{\textbf{Table 3.1: Systematic Synthesis Extraction Registry}} \\\\");
        sb.AppendLine(@"\toprule");
        sb.AppendLine(@"\textbf{Paper Name} & \textbf{Executive Summary} & \textbf{Inclusion Rationale} & \textbf{Category} & \textbf{Citation (APA 7th)} \\");
        sb.AppendLine(@"\midrule");
        foreach (var row in records)
        {
            string title = EscapeLatexText(row.Title);
            string summary = EscapeLatexText(row.Summary);
            string rationale = EscapeLatexText(row.InclusionRationale);
            string category = EscapeLatexText(row.Category);
            string citation = EscapeLatexText(row.ApaCitation);
            sb.AppendLine($"{title} & {summary} & {rationale} & {category} & {citation} \\\\ \\hline");
        }
        sb.AppendLine(@"\end{tabularx}");
        sb.AppendLine(@"\vspace{4pt}\noindent\small ");
        sb.AppendLine(@"\textbf{Table Overview:} The extraction logs archived inside Table 3.1 summarize the qualitative characteristics of each selected paper.");
        sb.AppendLine(@"\vspace{10pt}");

        sb.AppendLine(@"\begin{multicols}{2}");
        sb.AppendLine(@"\section{Results \& Synthesis}");
        sb.AppendLine(sanitizedSynthesisResultsItem);
        
        if (!string.IsNullOrEmpty(tikzDiagramCode))
        {
            sb.AppendLine(@"\end{multicols}");
            sb.AppendLine(@"\begin{figure}[H]");
            sb.AppendLine(@"\centering");
            sb.AppendLine(@"\resizebox{\linewidth}{!}{");
            sb.AppendLine(tikzDiagramCode);
            sb.AppendLine(@"}");
            sb.AppendLine(@"\caption{Grounded Architectural Synthesis System Model Diagram}");
            sb.AppendLine(@"\end{figure}");
            sb.AppendLine(@"\begin{multicols}{2}");
        }

        sb.AppendLine(@"\section{Discussion}");
        sb.AppendLine(sanitizedDiscussionItem);

        sb.AppendLine(@"\section{Administrative Declarations}");
        sb.AppendLine(@"\subsection{Support \& Funding}");
        sb.AppendLine(sanitizedSupportItem);
        sb.AppendLine(@"\subsection{Open Science Code Availability}");
        sb.AppendLine((report.AvailabilityItem ?? "").Replace("_", @"\_"));
        sb.AppendLine(@"\end{multicols}");

        sb.AppendLine(@"\clearpage");
        sb.AppendLine(@"\begin{thebibliography}{99}");
        int refIdx = 1;
        foreach (var row in records)
        {
            string citation = EscapeLatexText(row.ApaCitation);
            sb.AppendLine($"\\bibitem{{ref{refIdx}}} {citation}");
            refIdx++;
        }
        sb.AppendLine(@"\end{thebibliography}");
        sb.AppendLine(@"\end{document}");

        File.WriteAllText(texFilePath, sb.ToString());

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            if (File.Exists(texFilePath)) 
                archive.CreateEntryFromFile(texFilePath, "main.tex");

            string statePath = GetStateFilePath(sessionId);
            string reportPath = GetReportFilePath(sessionId);
            string isolatedFolder = GetWorkspaceFolderPath(sessionId);
            string ledgerPath = Path.Combine(isolatedFolder, "stylistic-transformation-ledger.json");
            string citationAuditPath = Path.Combine(isolatedFolder, "citation-audit.json");
            string peerReviewPath = Path.Combine(isolatedFolder, "peer-review-feedback.json");

            if (File.Exists(reportPath)) archive.CreateEntryFromFile(reportPath, "prisma-report.json");
            if (File.Exists(statePath)) archive.CreateEntryFromFile(statePath, "transparent-process.json");
            if (File.Exists(ledgerPath)) archive.CreateEntryFromFile(ledgerPath, "stylistic-transformation-ledger.json");
            if (File.Exists(citationAuditPath)) archive.CreateEntryFromFile(citationAuditPath, "citation-audit.json");
            if (File.Exists(peerReviewPath)) archive.CreateEntryFromFile(peerReviewPath, "peer-review-feedback.json");

            if (Directory.Exists(isolatedFolder))
            {
                var files = Directory.GetFiles(isolatedFolder);
                foreach (var file in files)
                {
                    if (file.EndsWith(".json")) continue;
                    string filename = Path.GetFileName(file);

                    // The grounded outline is an audit artifact, not a source manuscript - keep it at the
                    // archive root instead of filing it alongside the downloaded source PDFs.
                    if (filename.Equals("grounded-outline.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        archive.CreateEntryFromFile(file, filename);
                    }
                    else
                    {
                        archive.CreateEntryFromFile(file, $"SourcePapers/{filename}");
                    }
                }
            }
        }

        try { File.Delete(texFilePath); } catch { }
        return memoryStream.ToArray();
    }

    private async Task SaveStateAsync(Guid sessionId, ReviewState state)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(GetStateFilePath(sessionId), JsonSerializer.Serialize(state, options));
    }

    private JsonElement DeserializeDecision(string rawJson)
    {
        try 
        { 
            string cleanJson = rawJson.Replace("```json", "").Replace("```", "").Trim();
            using var doc = JsonDocument.Parse(cleanJson);
            return doc.RootElement.Clone(); 
        }
        catch 
        { 
            try
            {
                var decisionMatch = Regex.Match(rawJson, "\"decision\"\\s*:\\s*\"(.*?)\"");
                var reasoningMatch = Regex.Match(rawJson, "\"reasoning\"\\s*:\\s*\"(.*?)\"");
                var citationMatch = Regex.Match(rawJson, "\"apaCitation\"\\s*:\\s*\"(.*?)\"");
                var summaryMatch = Regex.Match(rawJson, "\"briefSummary\"\\s*:\\s*\"(.*?)\"");
                if (decisionMatch.Success)
                {
                    string rescuedJson = $"{{\"decision\":\"{decisionMatch.Groups[1].Value}\",\"reasoning\":\"{reasoningMatch.Groups[1].Value}\",\"apaCitation\":\"{citationMatch.Groups[1].Value}\",\"briefSummary\":\"{summaryMatch.Groups[1].Value}\"}}";
                    return JsonDocument.Parse(rescuedJson).RootElement.Clone();
                }
            }
            catch { }

            string fallback = """{"decision":"Excluded","reasoning":"Failed to parse LLM structured payload.","apaCitation":"N/A","briefSummary":"N/A"}""";
            return JsonDocument.Parse(fallback).RootElement.Clone();
        }
    }

    private void UpdateReviewState(ReviewState state, AcademicPaper paper, JsonElement decisionData)
    {
        string GetJsonString(JsonElement element, string propertyName)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return prop.Value.GetString() ?? "N/A";
                }
            }
            return "N/A";
        }

        string decision = GetJsonString(decisionData, "decision");
        string reasoning = GetJsonString(decisionData, "reasoning");
        string summary = GetJsonString(decisionData, "briefSummary");
        var apaResult = ApaCitationBuilder.Build(paper);

        if (decision == "N/A") decision = "Excluded";

        state.Stats.Screened++;
        if (decision.Equals("Included", StringComparison.OrdinalIgnoreCase)) 
            state.Stats.Included++;
        else 
            state.Stats.Excluded++;
        state.Phases.Screening.Add(new ScreeningLog(paper.Id, paper.Title, decision, reasoning, apaResult.Citation, summary,
            apaResult.VenueType, apaResult.VenueName, apaResult.Year));
    }
}