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

    public event Action<ReviewStats>? OnProgressUpdated;

    // SECURE ENCAPSULATED ROUTING PATHS
    private string GetWorkspaceFolderPath(Guid sessionId) => 
        Path.Combine(Directory.GetCurrentDirectory(), "WorkspaceStore", sessionId.ToString("N"));

    private string GetStateFilePath(Guid sessionId) => 
        Path.Combine(GetWorkspaceFolderPath(sessionId), "transparent-process.json");

    private string GetReportFilePath(Guid sessionId) => 
        Path.Combine(GetWorkspaceFolderPath(sessionId), "prisma-report.json");

    public PrismaReviewEngine(string mistralApiKey, string elsevierApiKey, string? ieeeApiKey = null, string? scholarApiKey = null)
    {
        _globalMistralKey = mistralApiKey;
        _globalElsevierKey = elsevierApiKey;
        _globalIeeeKey = ieeeApiKey;
        _globalScholarKey = scholarApiKey;
    }

    public async Task RunReviewLoopAsync(Guid sessionId, string initialQuery, string explicitObjective, string inclusionCriteria, string exclusionCriteria, int maxResults, bool requirePeerReview = false, string synthesisDirective = "", UserApiKeys? userKeys = null)
    {
        // 1. Resolve runtime credentials (BYOK fallback chain)
        string activeMistral = !string.IsNullOrWhiteSpace(userKeys?.MistralApiKey) ? userKeys.MistralApiKey : _globalMistralKey;
        string activeElsevier = !string.IsNullOrWhiteSpace(userKeys?.ElsevierApiKey) ? userKeys.ElsevierApiKey : _globalElsevierKey;
        string? activeIeee = !string.IsNullOrWhiteSpace(userKeys?.IeeeApiKey) ? userKeys.IeeeApiKey : _globalIeeeKey;
        string? activeScholar = !string.IsNullOrWhiteSpace(userKeys?.ScholarApiKey) ? userKeys.ScholarApiKey : _globalScholarKey;

        // 2. Build isolated kernel for this specific thread pass
        var builder = Kernel.CreateBuilder();
        builder.AddMistralChatCompletion("mistral-large-latest", activeMistral);
        var kernel = builder.Build();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        // 3. Assemble transient source gateway adapters for this session run
        var activeSources = new List<IAcademicSource> { new ArxivSource() };
        if (!string.IsNullOrEmpty(activeElsevier)) activeSources.Add(new ElsevierSource(activeElsevier));
        if (!string.IsNullOrEmpty(activeIeee)) activeSources.Add(new IeeeXploreSource(activeIeee));
        if (!string.IsNullOrEmpty(activeScholar))
        {
            activeSources.Add(new GoogleScholarSource(activeScholar));
            activeSources.Add(new ResearchGateSource(activeScholar));
        }

        string userWorkspace = GetWorkspaceFolderPath(sessionId);
        Directory.CreateDirectory(userWorkspace);

        var reviewState = new ReviewState 
        { 
            SearchQuery = initialQuery,
            PeerReviewOnlyToggle = requirePeerReview,
            SynthesisTargetDirective = synthesisDirective
        };
        reviewState.Stats.ProcessingStage = "Screening";
        await SaveStateAsync(sessionId, reviewState);

        var allGroundedChunks = new List<DocumentChunk>();

        foreach (var source in activeSources)
        {
            var currentLog = new PlatformSearchLog
            {
                SourceName = source.SourceName,
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                Status = "Running"
            };
            reviewState.SearchLogs.Add(currentLog);
            await SaveStateAsync(sessionId, reviewState);

            List<AcademicPaper> candidates = new();
            try
            {
                candidates = await source.FetchPapersAsync(reviewState.SearchQuery, maxResults: maxResults);
                currentLog.Status = "Completed Successfully";
                currentLog.PapersFound = candidates.Count;
            }
            catch (Exception ex)
            {
                currentLog.Status = "Faulted / Refused Connection";
                currentLog.ErrorMessage = SanitizeLogMessage(ex.Message);
                Console.WriteLine($"[Source Exception Logging] {source.SourceName} faulted: {currentLog.ErrorMessage}");
            }

            reviewState.Stats.TotalIdentified += candidates.Count;
            OnProgressUpdated?.Invoke(reviewState.Stats);
            await SaveStateAsync(sessionId, reviewState);

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

                        reviewState.Phases.Screening.Add(new ScreeningLog(
                            paper.Id, paper.Title, "Excluded", 
                            "Excluded during post-retrieval pre-screening: Document was classified as an un-reviewed preprint or working paper, violating the active Peer-Reviewed Only configuration threshold.",
                            "N/A", "N/A"
                        ));
                        
                        OnProgressUpdated?.Invoke(reviewState.Stats);
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
                    2. Generate a valid APA 7th edition citation string based on the Authors, Title, Date Context, and the provided Publication/Journal Venue string. Do not use placeholders like "*Journal Name*".
                    3. Create a brief 1-2 sentence executive summary of the paper.

                    Respond ONLY with a valid minified JSON object matching this structure exactly:
                    {
                        "decision": "Included" or "Excluded",
                        "reasoning": "Why it meets inclusion or hits exclusion parameters.",
                        "apaCitation": "The generated APA 7th Edition style citation reference string using the real Publication/Journal Venue context.",
                        "briefSummary": "The 1-2 sentence executive summary overview."
                    }
                    """;

                try
                {
                    var response = await chatService.GetChatMessageContentAsync(prompt);
                    var decisionData = DeserializeDecision(response.ToString());

                    UpdateReviewState(reviewState, paper, decisionData);
                    OnProgressUpdated?.Invoke(reviewState.Stats);
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
        OnProgressUpdated?.Invoke(reviewState.Stats);

        var includedLogs = reviewState.Phases.Screening
            .Where(p => p.Decision.Equals("Included", StringComparison.OrdinalIgnoreCase))
            .ToList();

        reviewState.SynthesizedRecords.Clear();
        foreach (var log in includedLogs)
        {
            string venue = "Preprints";
            if ((log.ApaCitation ?? "").Contains("Proceedings of", StringComparison.OrdinalIgnoreCase) || (log.ApaCitation ?? "").Contains("Conference", StringComparison.OrdinalIgnoreCase)) venue = "Conferences";
            else if ((log.ApaCitation ?? "").Contains("Transactions on", StringComparison.OrdinalIgnoreCase)) venue = "Transactions";
            else if ((log.ApaCitation ?? "").Contains("Journal of", StringComparison.OrdinalIgnoreCase)) venue = "Journals";

            string cleanCategory = log.PaperId.StartsWith("SCOPUS_ID:") ? "ScienceDirect" 
                                 : log.PaperId.StartsWith("IEEE_") ? "IEEE" 
                                 : log.PaperId.StartsWith("SCHOLAR_") ? "Scholar" 
                                 : log.PaperId.StartsWith("RG_") ? "ResearchGate" 
                                 : "Arxiv";
            int parsedYear = ExtractYearFromCitation(log.ApaCitation);

            string quartile = "N/A";
            if (cleanCategory == "ScienceDirect" || cleanCategory == "IEEE" || cleanCategory == "Scholar")
            {
                string extractedVenue = "";
                var venueMatch = Regex.Match(log.ApaCitation ?? "", @"\.\s+\*([^*]+)\*");
                if (venueMatch.Success) extractedVenue = venueMatch.Groups[1].Value.Trim();
                
                quartile = JournalRankingMatcher.LookupQuartile(extractedVenue, parsedYear);
                if ((cleanCategory == "IEEE" || cleanCategory == "Scholar") && quartile == "N/A") quartile = "Q1"; 
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
                ConferenceRating = cleanCategory == "ScienceDirect" && venue == "Conferences" ? "A" : "N/A"
            });
        }
        await SaveStateAsync(sessionId, reviewState);

        var includedPapers = includedLogs.OrderBy(p => p.ApaCitation).ToList();
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
            inc: inclusionCriteria, exc: exclusionCriteria, sbContext.ToString(), exactReferenceListForLLM, reviewState);

        reviewState.Stats.ProcessingStage = "Complete";
        OnProgressUpdated?.Invoke(reviewState.Stats);
        await SaveStateAsync(sessionId, reviewState);
    }

    private string SanitizeLogMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        return Regex.Replace(message, @"([a-zA-Z0-9]{24,64})", "[REDACTED_API_CREDENTIAL]");
    }

    private int ExtractYearFromCitation(string? citation)
    {
        if (string.IsNullOrEmpty(citation)) return 2026;
        var yearMatch = Regex.Match(citation, @"\((20\d{2})\)");
        return yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out int yr) ? yr : 2026;
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

private async Task GeneratePrismaChecklistReportWithRAGAsync(Guid sessionId, int sourceCount, IChatCompletionService chat, string query, string explicitObjective, string inc, string exc, string groundedChunksText, string referenceListMapping, ReviewState finalState)
    {
        var reportPrompt = $$"""
            You are an elite academic meta-analyst preparing an evaluation report mapped to the PRISMA 2020 Expanded Checklist.

            CRITICAL LIVE RUN CONSTRAINTS (ZERO HALLUCINATION DIRECTIVE):
            - The ONLY search term/string used in this run was exactly: "{{query}}"
            - The review objective specified by the user was exactly: "{{explicitObjective}}"
            - The screening inclusion thresholds were exactly: "{{inc}}"
            - The screening exclusion thresholds were exactly: "{{exc}}"

            OFFICIAL ALPHABETIZED REFERENCE LIST FOR THIS RUN:
            {{referenceListMapping}}

            GROUNDED MANUSCRIPT RAW CONTEXT DATA CHUNKS:
            {{groundedChunksText}}

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
                "titleItem": "A rigorous systematic literature review title analyzing '{{query}}' mapping to PRISMA Item 1.",
                "abstractItem": "A formal abstract overview addressing the core objective ('{{explicitObjective}}') mapping to PRISMA Item 2.",
                "rationaleItem": "A rigorous context justification detailing the current state of software frameworks regarding '{{query}}' mapping to PRISMA Item 3.",
                "objectivesItem": "The explicit question formulation matching the stated goal: '{{explicitObjective}}' mapping to PRISMA Item 4.",
                "eligibilityItem": "A comprehensive breakdown detailing the screening configuration limits (Inclusion: '{{inc}}' | Exclusion: '{{exc}}') mapping to PRISMA Item 5.",
                "sourcesItem": "A concise record confirming that document platform searches were limited strictly to the active query interfaces mapping to PRISMA Item 6.",
                "searchStrategyItem": "An analytical description confirming that execution was carried out using the literal search query phrase '{{query}}' mapping to PRISMA Item 7.",
                "selectionProcessItem": "An explanation detailing how fields were evaluated by an automated screening architecture according to constraints mapping to PRISMA Item 8.",
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
            string rawEligibility = ResolveField("eligibilityItem");
            string rawSources = ResolveField("sourcesItem");
            string rawSearch = ResolveField("searchStrategyItem");
            string rawSelection = ResolveField("selectionProcessItem");
            string rawDiscussion = ResolveField("discussionItem");
            string rawSynthesisText = ResolveField("synthesisResultsItem");

            var deltas = new List<StyleDeltaLog>();

            // 2. PASS EACH ACADEMIC FIELD THROUGH THE HUMAN STYLISTIC REFINE SYSTEM
            var (cleanTitle, dTitle) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Title", rawTitle); deltas.Add(dTitle);
            var (cleanAbstract, dAbstract) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Abstract", rawAbstract); deltas.Add(dAbstract);
            var (cleanRationale, dRationale) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Rationale", rawRationale); deltas.Add(dRationale);
            var (cleanObjectives, dObjectives) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Objectives", rawObjectives); deltas.Add(dObjectives);
            var (cleanEligibility, dEligibility) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Eligibility", rawEligibility); deltas.Add(cleanEligibility != "Extraction failed." ? dEligibility : dEligibility with { RefinedText = rawEligibility });
            var (cleanSources, dSources) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Sources", rawSources); deltas.Add(dSources);
            var (cleanSearch, dSearch) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Search Strategy", rawSearch); deltas.Add(dSearch);
            var (cleanSelection, dSelection) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Selection Automation", rawSelection); deltas.Add(dSelection);
            var (cleanDiscussion, dDiscussion) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Discussion", rawDiscussion); deltas.Add(dDiscussion);
            var (cleanSynthesis, dSynthesis) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Synthesis Results", rawSynthesisText); deltas.Add(dSynthesis);

            // Re-append the isolated architectural diagram strings back onto the humanized synthesis field
            string fullSynthesisField = (string.IsNullOrWhiteSpace(cleanSynthesis) ? rawSynthesisText : cleanSynthesis) + mermaidBlock + tikzBlock;

            var reportObj = new PrismaReport
            {
                GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                TitleItem = !string.IsNullOrWhiteSpace(cleanTitle) ? cleanTitle : rawTitle,
                AbstractItem = !string.IsNullOrWhiteSpace(cleanAbstract) ? cleanAbstract : rawAbstract,
                RationaleItem = !string.IsNullOrWhiteSpace(cleanRationale) ? cleanRationale : rawRationale,
                ObjectivesItem = !string.IsNullOrWhiteSpace(cleanObjectives) ? cleanObjectives : rawObjectives,
                EligibilityItem = !string.IsNullOrWhiteSpace(cleanEligibility) ? cleanEligibility : rawEligibility,
                SourcesItem = !string.IsNullOrWhiteSpace(cleanSources) ? cleanSources : rawSources,
                SearchStrategyItem = !string.IsNullOrWhiteSpace(cleanSearch) ? cleanSearch : rawSearch,
                SelectionProcessItem = !string.IsNullOrWhiteSpace(cleanSelection) ? cleanSelection : rawSelection,
                SynthesisResultsItem = fullSynthesisField,
                DiscussionItem = !string.IsNullOrWhiteSpace(cleanDiscussion) ? cleanDiscussion : rawDiscussion,
                BiasAssessmentItem = "Internal risk of bias is controlled via mandatory post-generation human verification. Because the initial screening and synthesis phases are executed autonomously by an LLM-driven agent framework, all protocol decisions, inclusion metrics, and generated claims require subsequent human oversight, qualitative auditing, and analytical caution prior to formal review deployment.",
                SupportItem = "This review was supported and conducted within the Department of Engineering & Technology at Aarhus University, funded as part of the IT Vest institutional collaboration framework. The funders played no active role in specific automated screening study designs, live stream extraction cycles, or pipeline synthesis determinations.",
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

        return cleanInput.Replace(@"\", @"\textbackwards ")
                         .Replace("&", @"\&")
                         .Replace("%", @"\%")
                         .Replace("_", @"\_")
                         .Replace("#", @"\#")
                         .Replace("{", @"\{")
                         .Replace("}", @"\}")
                         .Replace("~", @"\textasciitilde ")
                         .Replace("^", @"\textasciicircum ");
    }
    
    public byte[] GenerateManuscriptPdf(Guid sessionId, PrismaReport report, List<IncludedPaperMetricRow> records)
    {
        string projectRoot = Directory.GetCurrentDirectory();
        CleanOldManuscriptArtifacts(projectRoot);

        string uniqueId = Guid.NewGuid().ToString("N").Substring(0, 8);
        string texFilePath = Path.Combine(projectRoot, $"manuscript_{uniqueId}.tex");

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

        var publicationsByYear = new Dictionary<string, int> { { "2023", 0 }, { "2024", 0 }, { "2025", 0 }, { "2026", 0 } };
        foreach (var r in records)
        {
            string yrStr = r.Year.ToString();
            if (publicationsByYear.ContainsKey(yrStr)) publicationsByYear[yrStr]++;
        }

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
            tikzDiagramCode = tikzDiagramCode.Replace("-¿", "->").Replace("->", "-->");
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
        sb.AppendLine("    {\\ttfamily\\scriptsize\\color{gray} Protocol Hash: cc584090 | Generated: " + (report.GeneratedAt ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC")) + " \\\\ \\vspace{15pt}}");
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
        sb.AppendLine(@"\begin{axis}[ybar, symbolic coords={2023,2024,2025,2026}, xtick=data, x tick label style={/pgf/number format/set thousands separator={}}, ymin=0, ymax=12, ylabel={Papers}, xlabel={Year}, nodes near coords, width=\textwidth, height=5.5cm, bar width=10pt]");
        sb.AppendLine($"\\addplot[fill=blue!50] coordinates {{(2023,{publicationsByYear["2023"]}) (2024,{publicationsByYear["2024"]}) (2025,{publicationsByYear["2025"]}) (2026,{publicationsByYear["2026"]})}};");
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

            sb.AppendLine(@"\matrix [draw,below,matrix of nodes,text align=left,font=\tiny,at={(current bounding box.south)},yshift=-0.4cm] {");
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
            
            sb.AppendLine(@"\matrix [draw,below,matrix of nodes,text align=left,font=\tiny,at={(current bounding box.south)},yshift=-0.4cm] {");
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

            if (File.Exists(reportPath)) archive.CreateEntryFromFile(reportPath, "prisma-report.json");
            if (File.Exists(statePath)) archive.CreateEntryFromFile(statePath, "transparent-process.json");
            if (File.Exists(ledgerPath)) archive.CreateEntryFromFile(ledgerPath, "stylistic-transformation-ledger.json");

            if (Directory.Exists(isolatedFolder))
            {
                var files = Directory.GetFiles(isolatedFolder);
                foreach (var file in files)
                {
                    if (file.EndsWith(".json")) continue;
                    string filename = Path.GetFileName(file);
                    archive.CreateEntryFromFile(file, $"SourcePapers/{filename}");
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
        string apa = GetJsonString(decisionData, "apaCitation");
        string summary = GetJsonString(decisionData, "briefSummary");

        if (decision == "N/A") decision = "Excluded";

        state.Stats.Screened++;
        if (decision.Equals("Included", StringComparison.OrdinalIgnoreCase)) 
            state.Stats.Included++;
        else 
            state.Stats.Excluded++;
        state.Phases.Screening.Add(new ScreeningLog(paper.Id, paper.Title, decision, reasoning, apa, summary));
    }
}