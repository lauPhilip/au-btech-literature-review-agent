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

    public PrismaReviewEngine(string mistralApiKey)
    {
        var builder = Kernel.CreateBuilder();
        builder.AddMistralChatCompletion("mistral-large-latest", mistralApiKey);
        _kernel = builder.Build();

        _sources = new List<IAcademicSource> { new ArxivSource() };
    }

    public async Task RunReviewLoopAsync(string initialQuery, string explicitObjective, string inclusionCriteria, string exclusionCriteria)
    {
        var chatService = _kernel.GetRequiredService<IChatCompletionService>();
        
        var reviewState = new ReviewState { SearchQuery = initialQuery };
        reviewState.Stats.ProcessingStage = "Screening";
        await SaveStateAsync(reviewState);

        var allGroundedChunks = new List<DocumentChunk>();

        foreach (var source in _sources)
        {
            var candidates = await source.FetchPapersAsync(reviewState.SearchQuery, maxResults: 10);
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
        }

        reviewState.Stats.ProcessingStage = "Synthesizing";
        OnProgressUpdated?.Invoke(reviewState.Stats);

        // Generate clean alphabetized references for Mistral context alignment
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
            sbContext.AppendLine($"[Source Context Anchor {chunkIdx}]: (Origin Paper ID: {chunk.SourceId}, Page: {chunk.PageNumber})");
            sbContext.AppendLine($"Content text: {chunk.Text}");
            sbContext.AppendLine("---");
            chunkIdx++;
        }

        await GeneratePrismaChecklistReportWithRAGAsync(chatService, initialQuery, explicitObjective, 
            inc: inclusionCriteria, exc: exclusionCriteria, sbContext.ToString(), exactReferenceListForLLM, reviewState);

        reviewState.Stats.ProcessingStage = "Complete";
        OnProgressUpdated?.Invoke(reviewState.Stats);
        await SaveStateAsync(reviewState);
    }

    private async Task GeneratePrismaChecklistReportWithRAGAsync(IChatCompletionService chat, string query, string explicitObjective, string inc, string exc, string groundedChunksText, string referenceListMapping, ReviewState finalState)
    {
        var reportPrompt = $$"""
            You are an elite academic meta-analyst preparing an evaluation report mapped to the PRISMA 2020 Expanded Checklist.

            OFFICIAL ALPHABETIZED REFERENCE LIST FOR THIS RUN:
            {{referenceListMapping}}

            GROUNDED MANUSCRIPT RAW CONTEXT DATA CHUNKS:
            {{groundedChunksText}}

            TASK:
            Generate a fully formed, detailed academic paragraph for each checklist field below.
            
            STRICT CITATION PRINCIPLES:
            1. You must cross-reference the "Origin Paper ID" inside the DATA CHUNKS with the "Paper ID Token" provided in the OFFICIAL REFERENCE LIST.
            2. When weaving citations into text sentences, look up the exact matching reference number (e.g., [1], [2], [3], [4]) and append it to the claim. 
            3. CRITICAL: Distribute your citations naturally. Do not attribute every single chunk or paragraph to [1]. Use the actual matching source reference index from the reference list provided above.
            4. Never output text strings like "[Source Context Anchor n]". Only use standard numbers like "[1]" or "[2]".
            5. The "titleItem" must be pure text. Do not append citations to the title.

            Respond ONLY with a valid minified JSON object matching this structure exactly without markdown wrappers:
            {
                "titleItem": "An informative systematic review report title text mapping to PRISMA Item 1.",
                "abstractItem": "A concise overview mapping to PRISMA Item 2.",
                "rationaleItem": "A rigorous justification mapping to PRISMA Item 3.",
                "objectivesItem": "The explicit question formulation mapping to PRISMA Item 4.",
                "eligibilityItem": "A summary detailing boundaries mapping to PRISMA Item 5.",
                "sourcesItem": "Document platform coverage mapping to PRISMA Item 6.",
                "searchStrategyItem": "Present the sequence of terms mapping to PRISMA Item 7.",
                "selectionProcessItem": "Explain selection automation layers mapping to PRISMA Item 8.",
                "biasAssessmentItem": "Specify risk of bias methods mapping to PRISMA Item 11.",
                "synthesisResultsItem": "Summarize synthesis outcome logs with accurate multi-source citations mapping to PRISMA Item 20a.",
                "discussionItem": "Provide a general interpretation with multi-source citations mapping to PRISMA Item 23a.",
                "supportItem": "Describe sources of institutional support mapping to PRISMA Item 25.",
                "availabilityItem": "Document public repository accessibility details mapping to PRISMA Item 27."
            }
            """;

        try
        {
            var response = await chat.GetChatMessageContentAsync(reportPrompt);
            string responseString = response.ToString();
            string cleanJson = responseString.Replace("```json", "").Replace("```", "").Trim();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var reportObj = JsonSerializer.Deserialize<PrismaReport>(cleanJson, options);
            
            if (reportObj != null)
            {
                reportObj.GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");
                await File.WriteAllTextAsync(_reportFilePath, JsonSerializer.Serialize(reportObj, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            var failureReport = new PrismaReport
            {
                GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                TitleItem = $"Checklist Synthesis generation faulted: {ex.Message}"
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