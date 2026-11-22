using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;

public partial class PrismaReviewEngine
{
    /// <summary>The primary query followed by the reviewer's extra strings, without repeats, at most MaxSearchStrings in all.</summary>
    internal static List<string> WithPrimaryQuery(string query, IEnumerable<string> extra)
    {
        var all = new List<string> { query };
        foreach (var s in extra)
        {
            string clean = s.Trim();
            if (clean.Length > 0 && !all.Any(a => a.Equals(clean, StringComparison.OrdinalIgnoreCase))) all.Add(clean);
        }
        return all.Take(ReviewInputGuard.MaxSearchStrings + 1).ToList();
    }

    /// <summary>
    /// Proposes the extra search strings before a run, so the reviewer can see and edit them on the form. One small
    /// model call with the key the run would use; the caller limits how often it is asked.
    /// Returns only the extra strings (not the primary query).
    /// </summary>
    public async Task<List<string>> PreviewSearchStringsAsync(string query, string objective, string inclusion, UserApiKeys? userKeys)
    {
        var (activeMistral, _, _, _) = ResolveKeys(userKeys);
        IChatCompletionService chat = ChatFactory != null ? ChatFactory(activeMistral) : LlmFactory.Create(Llm, activeMistral);
        string cleanQuery = ReviewInputGuard.Normalize(query, singleLine: true);
        var all = await GenerateSearchPerspectivesAsync(chat, cleanQuery,
            ReviewInputGuard.Normalize(objective, singleLine: false), ReviewInputGuard.Normalize(inclusion, singleLine: false));
        return all.Skip(1).ToList();
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

            {{PromptSafety.ReviewerInputNotice}}

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

    internal static MistralAIPromptExecutionSettings JsonMode(double temperature) => LlmJson.JsonMode(temperature);
}
