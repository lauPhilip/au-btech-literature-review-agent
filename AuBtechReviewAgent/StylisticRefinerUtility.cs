using System;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

public static class StylisticRefinerUtility
{
    public static async Task<(string RefinedText, StyleDeltaLog Delta)> RefineAcademicProseAsync(
        IChatCompletionService chatService, 
        string fieldName, 
        string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) 
            return (string.Empty, new StyleDeltaLog(fieldName, string.Empty, string.Empty));

        var prompt = $$"""
            You are a rigorous academic copyeditor enforcing strict human stylistic variation guidelines on a draft manuscript.

            CRITICAL STYLISTIC SANITIZATION CONSTRAINTS:
            1. Ban Empty Parallelisms: Eliminate structural clichés like "not only..., but also..." or "not X, but Y". Replace them with direct, simple connections.
            2. Remove Artificial Signposting: Delete canned transitional phrases (e.g., "Crucially," "Importantly," "It is worth noting that," "Moreover," "Furthermore").
            3. Restore Basic Copulatives: Do not avoid simple verbs like "is" or "are" in favor of elegant variation or passive filler loops.
            4. Lower Lexical Density: Replace bloated, repetitive model buzzwords (e.g., "landscape," "testament," "pivotal," "beacon," "delve," "orchestration") with exact, understated domain language.
            5. Vary Sentence Cadence: Break up monotone, perfectly symmetrical structures. Mix short, direct assertions with complex academic clauses.

            ORIGINAL DRAFT:
            {{rawText}}

            TASK: Reword the draft above to read as if written by a clear, direct human researcher. Retain all factual source references, citations, and specific data points exactly.

            Respond ONLY with the clean, refined academic text paragraph. Do not add introductions, conversational remarks, or markdown code blocks.
            """;

        var response = await chatService.GetChatMessageContentAsync(prompt);
        string refinedText = response.ToString().Trim();

        return (refinedText, new StyleDeltaLog(fieldName, rawText, refinedText));
    }
}