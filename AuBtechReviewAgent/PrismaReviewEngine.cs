using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;

public class PrismaReviewEngine
{
    private readonly Kernel _kernel;
    private readonly string _stateFilePath = "transparent-process.json";
    private readonly string _reportFilePath = "prisma-report.json";
    private readonly List<IAcademicSource> _sources;

    public event Action<ReviewStats>? OnProgressUpdated;

    public PrismaReviewEngine(string mistralApiKey, string elsevierApiKey)
    {
        var builder = Kernel.CreateBuilder();
        builder.AddMistralChatCompletion("mistral-large-latest", mistralApiKey);
        _kernel = builder.Build();

        _sources = new List<IAcademicSource> { new ArxivSource() };

        if (!string.IsNullOrEmpty(elsevierApiKey))
        {
            _sources.Add(new ElsevierSource(elsevierApiKey));
        }
    }

    public async Task RunReviewLoopAsync(string initialQuery, string explicitObjective, string inclusionCriteria, string exclusionCriteria, int maxResults)
    {
        var chatService = _kernel.GetRequiredService<IChatCompletionService>();
        
        var reviewState = new ReviewState { SearchQuery = initialQuery };
        reviewState.Stats.ProcessingStage = "Screening";
        await SaveStateAsync(reviewState);

        var allGroundedChunks = new List<DocumentChunk>();

        foreach (var source in _sources)
        {
            var currentLog = new PlatformSearchLog
            {
                SourceName = source.SourceName,
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                Status = "Running"
            };
            reviewState.SearchLogs.Add(currentLog);
            await SaveStateAsync(reviewState);

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
                currentLog.ErrorMessage = ex.Message;
                Console.WriteLine($"[Source Exception Logging] {source.SourceName} faulted: {ex.Message}");
            }

            reviewState.Stats.TotalIdentified += candidates.Count;
            OnProgressUpdated?.Invoke(reviewState.Stats);
            await SaveStateAsync(reviewState);

            foreach (var paper in candidates)
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
                    await SaveStateAsync(reviewState);

                    string decision = decisionData.TryGetProperty("decision", out var d) ? d.GetString() ?? "Excluded" : "Excluded";
                    if (decision.Equals("Included", StringComparison.OrdinalIgnoreCase))
                    {
                        var paperChunks = await DocumentRAGUtility.IngestAndChunkPaperAsync(paper.Id, paper.Title);
                        allGroundedChunks.AddRange(paperChunks);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Screening Exception] Problem processing paper '{paper.Title}': {ex.Message}");
                }
            }
        }

        reviewState.Stats.ProcessingStage = "Synthesizing";
        OnProgressUpdated?.Invoke(reviewState.Stats);

        var includedPapers = reviewState.Phases.Screening
            .Where(p => p.Decision.Equals("Included", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.ApaCitation)
            .ToList();

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

        await GeneratePrismaChecklistReportWithRAGAsync(chatService, initialQuery, explicitObjective, 
            inc: inclusionCriteria, exc: exclusionCriteria, sbContext.ToString(), exactReferenceListForLLM, reviewState);

        reviewState.Stats.ProcessingStage = "Complete";
        OnProgressUpdated?.Invoke(reviewState.Stats);
        await SaveStateAsync(reviewState);
    }

    private async Task GeneratePrismaChecklistReportWithRAGAsync(IChatCompletionService chat, string query, string explicitObjective, string inc, string exc, string groundedChunksText, string referenceListMapping, ReviewState finalState)
    {
        string activePlatformsList = string.Join(", ", _sources.Select(s => s.SourceName));

        var reportPrompt = $$"""
            You are an elite academic meta-analyst preparing an evaluation report mapped to the PRISMA 2020 Expanded Checklist.

            CRITICAL LIVE RUN CONSTRAINTS (ZERO HALLUCINATION DIRECTIVE):
            - The ONLY search term/string used in this run was exactly: "{{query}}"
            - The ONLY databases searched were exactly: {{activePlatformsList}}
            - The review objective specified by the user was exactly: "{{explicitObjective}}"
            - The screening inclusion thresholds were exactly: "{{inc}}"
            - The screening exclusion thresholds were exactly: "{{exc}}"

            STRICT COMPOSITION RULES FOR METHODOLOGY ITEMS:
            1. For PRISMA Item 6 (sourcesItem): You must explicitly document that ONLY {{activePlatformsList}} were searched. Do NOT mention external platforms like IEEE Xplore, ACM Digital Library, or Web of Science.
            2. For PRISMA Item 7 (searchStrategyItem): Describe the strategy using exclusively the actual raw query string configuration: "{{query}}". Do NOT invent compound keyword categories or boolean filter lists.
            3. For PRISMA Item 8 (selectionProcessItem): Detail that records were handled by an autonomous multi-source screening engine executing criteria filtering over candidate fields matching thresholds: "{{inc}}" and excluding: "{{exc}}".

            OFFICIAL ALPHABETIZED REFERENCE LIST FOR THIS RUN:
            {{referenceListMapping}}

            GROUNDED MANUSCRIPT RAW CONTEXT DATA CHUNKS:
            {{groundedChunksText}}

            TASK:
            Generate a fully formed, detailed academic paragraph for each checklist field below.

            Respond ONLY with a valid minified JSON object matching this structure exactly without markdown wrappers:
            {
                "titleItem": "A rigorous systematic literature review title analyzing '{{query}}' mapping to PRISMA Item 1.",
                "abstractItem": "A formal abstract overview addressing the core objective ('{{explicitObjective}}') over resources found in {{activePlatformsList}} mapping to PRISMA Item 2.",
                "rationaleItem": "A rigorous context justification detailing the current state of software frameworks regarding '{{query}}' mapping to PRISMA Item 3.",
                "objectivesItem": "The explicit question formulation matching the stated goal: '{{explicitObjective}}' mapping to PRISMA Item 4.",
                "eligibilityItem": "A comprehensive breakdown detailing the screening configuration limits (Inclusion: '{{inc}}' | Exclusion: '{{exc}}') mapping to PRISMA Item 5.",
                "sourcesItem": "A concise record confirming that document platform searches were limited strictly to the active query interfaces of {{activePlatformsList}} mapping to PRISMA Item 6.",
                "searchStrategyItem": "An analytical description confirming that execution was carried out using the literal search query phrase '{{query}}' across the indexing gateways of {{activePlatformsList}} mapping to PRISMA Item 7.",
                "selectionProcessItem": "An explanation detailing how title, author metadata, and abstract fields were evaluated by an automated screening architecture according to constraints mapping to PRISMA Item 8.",
                "biasAssessmentItem": "This field will be post-processed. Output exactly: 'PREDEFINED_METADATA_MARKER'",
                "synthesisResultsItem": "Summarize synthesis outcome logs with accurate multi-source citations mapping to PRISMA Item 20a.",
                "discussionItem": "Provide a general architectural interpretation with multi-source citations mapping to PRISMA Item 23a.",
                "supportItem": "This field will be post-processed. Output exactly: 'PREDEFINED_SUPPORT_MARKER'",
                "availabilityItem": "This field will be post-processed. Output exactly: 'PREDEFINED_REPO_MARKER'"
            }
            """;

        try
        {
            var response = await chat.GetChatMessageContentAsync(reportPrompt);
            string responseString = response.ToString();
            
            string cleanJson = responseString.Replace("```json", "").Replace("```", "").Trim();
            cleanJson = System.Text.RegularExpressions.Regex.Replace(cleanJson, @"\s+", " ");

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var reportObj = JsonSerializer.Deserialize<PrismaReport>(cleanJson, options);
            
            if (reportObj != null)
            {
                reportObj.GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");
                
                // Overwrite structural targets with strict, un-hallucinated institutional parameters
                reportObj.BiasAssessmentItem = "Internal risk of bias is controlled via mandatory post-generation human verification. Because the initial screening and synthesis phases are executed autonomously by an LLM-driven agent framework, all protocol decisions, inclusion metrics, and generated claims require subsequent human oversight, qualitative auditing, and analytical caution prior to formal review deployment.";
                reportObj.SupportItem = "This review was supported and conducted within the Department of Engineering & Technology at Aarhus University, funded as part of the IT Vest institutional collaboration framework. The funders played no active role in specific automated screening study designs, live stream extraction cycles, or pipeline synthesis determinations.";
                reportObj.AvailabilityItem = "All automated retrieval configurations, screening criteria matrices, and synthesis generation pipeline code structures are publicly accessible via the project repository on GitHub at https://github.com/lauPhilip/au-btech-literature-review-agent.";

                await File.WriteAllTextAsync(_reportFilePath, JsonSerializer.Serialize(reportObj, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
        }
        catch (Exception jsonEx)
        {
            Console.WriteLine($"[JSON Extraction Warning] Core serialization faulted, trying string-regex extraction: {jsonEx.Message}");
        }

        try
        {
            var response = await chat.GetChatMessageContentAsync(reportPrompt);
            string raw = response.ToString();
            
            string GetValue(string key)
            {
                var pattern = $"\"{key}\"\\s*:\\s*\"(.*?)\"\\s*(?=[,\\}}])";
                var match = System.Text.RegularExpressions.Regex.Match(raw, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
                return match.Success ? match.Groups[1].Value : "Extraction failed.";
            }

            var fallbackObj = new PrismaReport
            {
                GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                TitleItem = GetValue("titleItem"),
                AbstractItem = GetValue("abstractItem"),
                RationaleItem = GetValue("rationaleItem"),
                ObjectivesItem = GetValue("objectivesItem"),
                EligibilityItem = GetValue("eligibilityItem"),
                SourcesItem = GetValue("sourcesItem"),
                SearchStrategyItem = GetValue("searchStrategyItem"),
                SelectionProcessItem = GetValue("selectionProcessItem"),
                SynthesisResultsItem = GetValue("synthesisResultsItem"),
                DiscussionItem = GetValue("discussionItem"),
                
                BiasAssessmentItem = "Internal risk of bias is controlled via mandatory post-generation human verification. Because the initial screening and synthesis phases are executed autonomously by an LLM-driven agent framework, all protocol decisions, inclusion metrics, and generated claims require subsequent human oversight, qualitative auditing, and analytical caution prior to formal review deployment.",
                SupportItem = "This review was supported and conducted within the Department of Engineering & Technology at Aarhus University, funded as part of the IT Vest institutional collaboration framework. The funders played no active role in specific automated screening study designs, live stream extraction cycles, or pipeline synthesis determinations.",
                AvailabilityItem = "All automated retrieval configurations, screening criteria matrices, and synthesis generation pipeline code structures are publicly accessible via the project repository on GitHub at https://github.com/lauPhilip/au-btech-literature-review-agent."
            };

            await File.WriteAllTextAsync(_reportFilePath, JsonSerializer.Serialize(fallbackObj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception fatalEx)
        {
            var failureReport = new PrismaReport
            {
                GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                TitleItem = $"Checklist Synthesis generation completely faulted: {fatalEx.Message}"
            };
            await File.WriteAllTextAsync(_reportFilePath, JsonSerializer.Serialize(failureReport, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private async Task SaveStateAsync(ReviewState state)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(_stateFilePath, JsonSerializer.Serialize(state, options));
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
                var decisionMatch = System.Text.RegularExpressions.Regex.Match(rawJson, "\"decision\"\\s*:\\s*\"(.*?)\"");
                var reasoningMatch = System.Text.RegularExpressions.Regex.Match(rawJson, "\"reasoning\"\\s*:\\s*\"(.*?)\"");
                var citationMatch = System.Text.RegularExpressions.Regex.Match(rawJson, "\"apaCitation\"\\s*:\\s*\"(.*?)\"");
                var summaryMatch = System.Text.RegularExpressions.Regex.Match(rawJson, "\"briefSummary\"\\s*:\\s*\"(.*?)\"");

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
