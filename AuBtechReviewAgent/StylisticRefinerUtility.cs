using System;
using System.Linq;
using System.Text.RegularExpressions;
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

        string refinedText;
        try
        {
            var response = await chatService.GetChatMessageContentAsync(prompt);
            refinedText = response.ToString().Trim();
        }
        catch (Exception ex)
        {
            return (rawText, new StyleDeltaLog(fieldName, rawText, rawText, Applied: false, Note: $"Refiner call failed: {ex.GetType().Name}"));
        }

        string? rejection = CheckRewrite(rawText, refinedText);
        if (rejection != null)
        {
            // Keep the original and record why, so the ledger shows the rewrite was attempted and refused.
            return (rawText, new StyleDeltaLog(fieldName, rawText, refinedText, Applied: false, Note: rejection));
        }

        return (refinedText, new StyleDeltaLog(fieldName, rawText, refinedText));
    }

    /// <summary>
    /// A copy-edit should not change what the text says. Reject rewrites that are far longer or shorter
    /// than the original, or that drop or add a number or an inline [n] citation. Returns null when the
    /// rewrite is acceptable, otherwise the reason it was rejected.
    /// </summary>
    public static string? CheckRewrite(string original, string rewrite)
    {
        if (string.IsNullOrWhiteSpace(rewrite)) return "Rewrite was empty.";

        double ratio = rewrite.Length / (double)Math.Max(1, original.Length);
        if (ratio > 1.6) return $"Rewrite is {ratio:0.0}x the original length; a copy-edit should not add content.";
        if (ratio < 0.4) return $"Rewrite is {ratio:0.0}x the original length; a copy-edit should not remove content.";

        var citesBefore = Regex.Matches(original, @"\[\d+(?:\s*[,\u2013-]\s*\d+)*\]").Select(m => m.Value).OrderBy(x => x).ToList();
        var citesAfter = Regex.Matches(rewrite, @"\[\d+(?:\s*[,\u2013-]\s*\d+)*\]").Select(m => m.Value).OrderBy(x => x).ToList();
        if (!citesBefore.SequenceEqual(citesAfter)) return "Rewrite changed the inline [n] citations.";

        // Numbers written as digits (counts, years, percentages) must survive unchanged.
        var numsBefore = Regex.Matches(Regex.Replace(original, @"\[[^\]]*\]", ""), @"\d+(?:[.,]\d+)?").Select(m => m.Value).OrderBy(x => x).ToList();
        var numsAfter = Regex.Matches(Regex.Replace(rewrite, @"\[[^\]]*\]", ""), @"\d+(?:[.,]\d+)?").Select(m => m.Value).OrderBy(x => x).ToList();
        if (!numsBefore.SequenceEqual(numsAfter)) return "Rewrite added, removed or changed a number.";

        return null;
    }
}