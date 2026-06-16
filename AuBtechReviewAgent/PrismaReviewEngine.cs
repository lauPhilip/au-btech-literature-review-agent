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
        var reviewState = new ReviewState { SearchQuery = initialQuery };
        await SaveStateAsync(reviewState);

        foreach (var source in _sources)
        {
            // Now passing the dynamic user search query live into the source fetching client
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

                    TASK:
                    1. Determine if it should be Included or Excluded.
                    2. Generate a valid APA 7th edition citation string based on the Authors, Title, and Date Context.
                    3. Create a brief 1-2 sentence executive summary of the paper.

                    Respond ONLY with a valid minified JSON block matching this object structure:
                    {
                        "decision": "Included" or "Excluded",
                        "reasoning": "Why it meets inclusion or hits exclusion parameters.",
                        "apaCitation": "The generated APA 7th Edition style citation reference string.",
                        "Publication": "Date Context",
                        "briefSummary": "The 2-3 sentence executive summary overview."
                    }
                    """;

                var response = await chatService.GetChatMessageContentAsync(prompt);
                var decisionData = DeserializeDecision(response.ToString());

                UpdateReviewState(reviewState, paper, decisionData);

                OnProgressUpdated?.Invoke(reviewState.Stats);

                
                await SaveStateAsync(reviewState);
            }
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
        string decision = decisionData.GetProperty("decision").GetString() ?? "Excluded";
        string reasoning = decisionData.GetProperty("reasoning").GetString() ?? "";
        string apa = decisionData.GetProperty("apaCitation").GetString() ?? "N/A";
        string Publication = decisionData.GetProperty("Publication").GetString() ?? "0000";
        string summary = decisionData.GetProperty("briefSummary").GetString() ?? "N/A";

        state.Stats.Screened++;
        if (decision == "Included") state.Stats.Included++; else state.Stats.Excluded++;

        state.Phases.Screening.Add(new ScreeningLog(paper.Id, paper.Title, decision, reasoning, apa, Publication, summary));
    }
}