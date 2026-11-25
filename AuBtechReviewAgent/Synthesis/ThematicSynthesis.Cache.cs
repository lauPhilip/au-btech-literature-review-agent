using System;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// The review cache and the model the answers of a synthesis step are kept under. Without it (null) nothing is cached
/// and every step asks the model, as before.
/// </summary>
public sealed record SynthesisCache(ReviewCache Cache, string? Model);

/// <summary>
/// Reuse of the coding and codebook answers (issue #445's follow-up). Two runs on the same studies extracted the same
/// values from the cache, but the coding and codebook calls still varied, so the themes differed. Each answer is now
/// kept under its stage, the model and the whole prompt (which holds the findings, the questions and the criteria), and
/// a rerun with the same inputs gets the same codes and themes. Only an answer that passed the step's validator is
/// stored, and a reused one has to pass it again before it is used.
/// </summary>
public static partial class ThematicSynthesis
{
    /// <summary>Raised whenever the coding prompt changes, so a cached coding answer from the old prompt is not reused.</summary>
    public const string CodingPromptVersion = "thematic-coding-v1";

    /// <summary>Raised whenever the codebook prompt changes, so a cached codebook from the old prompt is not reused.</summary>
    public const string CodebookPromptVersion = "thematic-codebook-v1";

    public const string CodingCacheKind = "thematic-coding";
    public const string CodebookCacheKind = "thematic-codebook";

    /// <summary>
    /// The methods sentence on reused coding and codebook answers, written in code; empty when nothing was reused, so the
    /// text of a run without the cache, or of a first run, is as before.
    /// </summary>
    public static string ReuseSentence(SynthesisReuse? reuse)
    {
        if (reuse == null || (reuse.CodingAnswersReused == 0 && !reuse.CodebookReused)) return "";
        string coding = reuse.CodingAnswersReused == 0 ? ""
            : $"{reuse.CodingAnswersReused} of {reuse.CodingAnswers} coding answer{(reuse.CodingAnswers == 1 ? "" : "s")}";
        string what = coding.Length == 0 ? "The codebook was" : reuse.CodebookReused ? $"{coding} and the codebook were" : $"{coding} {(reuse.CodingAnswersReused == 1 ? "was" : "were")}";
        return $"{what} reused from an earlier identical answer (same findings, research questions, criteria, model and prompt version); a reused answer passed the same checks in code again and is marked in thematic-codebook.json.";
    }

    /// <summary>
    /// A validated answer: from the cache when the same model was given the same prompt before and the stored answer
    /// passes <paramref name="validate"/> again, otherwise from the model. Only a validated answer is stored.
    /// </summary>
    private static async Task<(T Answer, bool FromCache)> CachedAnswerAsync<T>(IChatCompletionService chat, string prompt,
        PromptExecutionSettings settings, Func<T, string?> validate, SynthesisCache? cache, string kind, string version) where T : class
    {
        var store = cache?.Cache is { Enabled: true } enabled ? enabled : null;
        string key = ReviewCache.Key(version, kind, cache?.Model, prompt);
        if (store != null && store.TryGet<T>(kind, key, out var cached) && cached != null && validate(cached) == null)
            return (cached, true);
        var answer = await LlmJson.GetAsync(chat, prompt, settings, validate);
        store?.Set(kind, key, answer);
        return (answer, false);
    }
}
