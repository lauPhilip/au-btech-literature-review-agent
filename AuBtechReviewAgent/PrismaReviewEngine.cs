using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public async Task RunReviewLoopAsync(string initialQuery, string inclusionCriteria, string exclusionCriteria)
{
    var chatService = _kernel.GetRequiredService<IChatCompletionService>();
    
    if (File.Exists(_reportFilePath)) File.Delete(_reportFilePath);

    var reviewState = new ReviewState { SearchQuery = initialQuery };
    
    // ─── FORCE EXPLICIT INITIAL INITIALIZATION STATE ──────────────────────
    reviewState.Stats.ProcessingStage = "Screening";
    await SaveStateAsync(reviewState);

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
                - Abstract: {{paper.Abstract}}

                Respond ONLY with a valid minified JSON object matching this structure:
                {
                    "decision": "Included" or "Excluded",
                    "reasoning": "Why it meets inclusion or hits exclusion parameters.",
                    "apaCitation": "The generated APA 7th Edition style citation reference string.",
                    "briefSummary": "The 1-2 sentence executive summary overview."
                }
                """;

            var response = await chatService.GetChatMessageContentAsync(prompt);
            var decisionData = DeserializeDecision(response.ToString());

            // ─── KEEP STAGE ENFORCED AS SCREENING HERE ────────────────────
            reviewState.Stats.ProcessingStage = "Screening";
            UpdateReviewState(reviewState, paper, decisionData);
            
            OnProgressUpdated?.Invoke(reviewState.Stats);
            await SaveStateAsync(reviewState);
        }
    }

    // ─── TRANSITION TO SYNTHESIS AFTER BOTH LOOPS FINISH ─────────────────
    reviewState.Stats.ProcessingStage = "Synthesizing";
    OnProgressUpdated?.Invoke(reviewState.Stats);

    await GeneratePrismaChecklistReportAsync(chatService, initialQuery, inclusionCriteria, exclusionCriteria, reviewState);

    // ─── FINAL SIGN-OFF STAGE COMPLETION ─────────────────────────────────
    reviewState.Stats.ProcessingStage = "Complete";
    OnProgressUpdated?.Invoke(reviewState.Stats);
    await SaveStateAsync(reviewState);
}

private async Task GeneratePrismaChecklistReportAsync(IChatCompletionService chat, string query, string inc, string exc, ReviewState finalState)
{
    // Capture the exact papers processed to hand over as data constraints
    string papersContextJson = JsonSerializer.Serialize(finalState.Phases.Screening);

    var reportPrompt = $$"""
        You are an elite academic meta-analyst preparing an evaluation report mapped to the PRISMA 2020 Expanded Checklist.
        Analyze the literature review run data parameters and the screening logs to fill out the items.

        METADATA CONTEXT:
        - Primary Search Query: {{query}}
        - Inclusion Criteria: {{inc}}
        - Exclusion Criteria: {{exc}}
        - Evaluation Dataset: Total {{finalState.Stats.Screened}} papers (Included: {{finalState.Stats.Included}}, Excluded: {{finalState.Stats.Excluded}})
        
        RAW EXTRACTED DATA MATRIX:
        {{papersContextJson}}

        TASK:
        Generate a fully formed, detailed paragraph for each checklist field below based on the metadata evidence.

        Respond ONLY with a valid minified JSON object matching this structure exactly without any markdown formatting wrappers or backticks:
        {
            "titleItem": "An informative systematic review report title text mapping to PRISMA Item 1.",
            "rationaleItem": "A rigorous justification for this review in the context of open-source agentic engineering validation needs mapping to PRISMA Item 3.",
            "objectivesItem": "The explicit question formulation parameters formulated from the query mapping to PRISMA Item 4.",
            "eligibilityItem": "A summary detailing how inclusion/exclusion parameters filtered candidate eligibility boundaries mapping to PRISMA Item 5.",
            "sourcesItem": "Document platform coverage (arXiv API via export.arxiv.org) and retrieval timelines mapping to PRISMA Item 6.",
            "selectionProcessItem": "Explain how the automated LLM single-screener layer and the Ralph loop runtime verification handled study selection mapping to PRISMA Item 8."
        }
        """;

try
    {
        var response = await chat.GetChatMessageContentAsync(reportPrompt);
        string responseString = response.ToString();

        // Clean out any accidental markdown wrapper backticks
        string cleanJson = responseString
            .Replace("```json", "")
            .Replace("```", "")
            .Trim();

     
        var deserializeOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var reportObj = JsonSerializer.Deserialize<PrismaReport>(cleanJson, deserializeOptions);
     
        
        if (reportObj != null)
        {
            reportObj.GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");
            
            var writeOptions = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(_reportFilePath, JsonSerializer.Serialize(reportObj, writeOptions));
        }
    }
    catch (Exception ex)
    {
        // Document precise pipeline fault on crash
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
    // Helper function to extract properties case-insensitively
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

    // Safely extract values regardless of how Mistral decides to case the keys
    string decision = GetJsonString(decisionData, "decision");
    string reasoning = GetJsonString(decisionData, "reasoning");
    string apa = GetJsonString(decisionData, "apaCitation");
    string summary = GetJsonString(decisionData, "briefSummary");

    // Fallback normalization
    if (decision == "N/A") decision = "Excluded";

    state.Stats.Screened++;
    if (decision.Equals("Included", StringComparison.OrdinalIgnoreCase)) 
        state.Stats.Included++; 
    else 
        state.Stats.Excluded++;

    state.Phases.Screening.Add(new ScreeningLog(
        paper.Id,
        paper.Title,
        decision,
        reasoning,
        apa,
        summary
    ));
}
}