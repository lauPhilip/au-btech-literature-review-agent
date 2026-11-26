using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

// Screening one source once or twice, with the systematic review's steps and answer format, and applying the answers.
public sealed partial class MultivocalScreener
{
    /// <summary>
    /// Part of the cache key of an academic record (block I-3): raise it whenever its prompt changes. v2: targeted second
    /// screenings. v3: the first screening looks for positive evidence (<see cref="FirstProcedure"/>).
    /// </summary>
    public const string FormalPromptVersion = "formal-screening-v3";

    /// <summary>
    /// The first screening's steps in the multivocal review: the systematic review's steps
    /// (<see cref="PrismaReviewEngine.ScreeningProcedure"/>) with a step for positive evidence, as its second screener
    /// has, and a rule for a record with no summary. A live run with broad criteria included 65% of its sources, and a
    /// source with no summary was decided on its title alone. The systematic review's steps are not changed. A source
    /// excluded here for too little information has low confidence, so it is screened a second time and marked uncertain
    /// (<see cref="MultivocalSecondScreening.TargetReason"/>).
    /// </summary>
    public const string FirstProcedure = """
        TASK:
        1. Look for positive evidence: what in the record shows that the source's main subject meets the inclusion thresholds. A passing mention of the topic, or a source that only might be relevant, is not positive evidence.
        2. Decide Included only when there is positive evidence and no exclusion threshold applies. Otherwise decide Excluded.
        3. If the record has no summary and the title alone gives no positive evidence, decide Excluded with the reason "too-little-information" and confidence "low".
        4. Create a brief 1-2 sentence executive summary of the source.
        5. Say how confident you are: "high", "medium" or "low" (low when the record is too thin to judge).
        6. If Excluded, name the main reason: "off-topic" (not about the review's subject), "inclusion-not-met" (on topic but misses an inclusion threshold), "exclusion-matched" (an exclusion threshold applies) or "too-little-information" (the record is too thin to show it qualifies).
        """;

    /// <summary>
    /// The stages that label the two screenings' calls, as in the systematic review, so a chat service routed by stage
    /// (<see cref="StageRoutedChatService"/>) sends them to the fast model, inside the runner or outside it.
    /// </summary>
    public const string FirstStage = "screening";
    public const string SecondStage = "screening-second";

    /// <summary>One screening of one source: its answer, or the error that stopped it, and the flags of its text.</summary>
    private sealed record Screened(ScreeningAnswer? Answer, bool FromCache, IReadOnlyList<string> Flags, string? Error);

    private Task<Screened> ScreenFirstAsync(IChatCompletionService chat, MultivocalPlan plan, GreyRecord record) =>
        ScreenOnceAsync(chat, plan, record, secondScreener: false);

    private Task<Screened> ScreenSecondAsync(IChatCompletionService chat, MultivocalPlan plan, GreyRecord record) =>
        ScreenOnceAsync(chat, plan, record, secondScreener: true);

    private async Task<Screened> ScreenOnceAsync(IChatCompletionService chat, MultivocalPlan plan, GreyRecord record, bool secondScreener)
    {
        var flags = PromptSafety.Scan(record.Title, record.Summary, record.Producer);
        try
        {
            var (answer, hit) = await ScreenCachedAsync(chat, plan, record, secondScreener);
            return new Screened(answer, hit, flags, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // One failed source does not stop the screening; it is logged as not screened.
            string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            _log.LogWarning("Grey screening failed for '{Title}': {Message}", record.Title.Replace('\r', ' ').Replace('\n', ' '), message);
            return new Screened(null, false, flags, message);
        }
    }

    private async Task<(ScreeningAnswer Answer, bool FromCache)> ScreenCachedAsync(IChatCompletionService chat, MultivocalPlan plan, GreyRecord r, bool secondScreener)
    {
        string version = r.Kind == MultivocalSearcher.FormalKind ? FormalPromptVersion : PromptVersion;
        string stage = secondScreener ? SecondStage : FirstStage;
        // Keyed on the model that answers this stage, so a decision of the main model is not reused as the fast model's.
        string key = ReviewCache.Key(version, TemplateSha256(r.Kind == MultivocalSearcher.FormalKind, secondScreener), secondScreener ? "second" : "first", _modelFor(stage), plan.InclusionCriteria, plan.ExclusionCriteria,
            r.Title, r.Summary, r.Site, r.Kind, r.Producer, r.Published?.ToString("yyyy-MM-dd"));
        if (_cache.TryGet<ScreeningAnswer>("grey-screening", key, out var cached) && cached != null && ScreeningAnswer.Validate(cached) == null)
            return (cached.Normalized(), true);
        ScreeningAnswer answer;
        using (LlmStage.Begin(stage))
            answer = await ScreenGreyAsync(chat, r, plan.InclusionCriteria, plan.ExclusionCriteria, secondScreener);
        _cache.Set("grey-screening", key, answer);
        return (answer, false);
    }

    /// <summary>
    /// The screening prompt for one source: the systematic review's answer format and second screener's steps, the
    /// multivocal review's own first steps (<see cref="FirstProcedure"/>), with the record described as what it is. It is judged on its subject against the criteria, not on being grey literature; an academic
    /// record of the formal pool (block I-3) gets the same criteria, steps and answer format, introduced as academic.
    /// </summary>
    public static string Prompt(GreyRecord r, string inclusion, string exclusion, bool secondScreener)
    {
        bool formal = r.Kind == MultivocalSearcher.FormalKind;
        string kind = formal ? "Academic literature, from a bibliographic database"
            : MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == r.Kind).Label ?? r.Kind;
        string intro = formal
            ? """
              Evaluate the following academic source (a journal or conference paper, a preprint or a book chapter found in a bibliographic database) against the reviewer's selection criteria for a multivocal literature review.
              Judge it by its subject against the criteria, exactly as a grey literature source would be judged. Being a preprint or not peer-reviewed is not a reason to exclude it; its quality is appraised in a later step.
              """
            : """
              Evaluate the following grey literature source (a blog post, Q&A thread, code repository, report, thesis or similar) against the reviewer's selection criteria for a multivocal literature review.
              Judge it by its subject against the criteria, exactly as an academic paper would be judged. Being grey literature, informal or not peer-reviewed is not a reason to exclude it; its quality is assessed in a later step.
              """;
        string record = $"""
            - Title: {r.Title}
            - Kind of source: {kind}
            - Published on: {r.Site}
            - By: {(r.Producer.Length > 0 ? r.Producer : "not given")}
            - Date: {(r.Published is DateTime d ? d.ToString("yyyy-MM-dd") : "not given")}
            - Abstract or summary: {(r.Summary.Length > 0 ? r.Summary : "none")}
            """;

        return $$"""
            {{intro}}

            CRITERIA DIRECTIVES (from the reviewer):
            - Inclusion Thresholds: {{inclusion}}
            - Exclusion Thresholds: {{(exclusion.Length > 0 ? exclusion : "none")}}

            {{PromptSafety.ReviewerInputNotice}}

            {{PromptSafety.DataOnlyNotice}}

            SOURCE TARGET DATA:
            {{PromptSafety.Wrap(record, formal ? "academic record metadata and abstract" : "grey source metadata and summary")}}

            {{(secondScreener ? PrismaReviewEngine.ScreeningProcedure(true) : FirstProcedure)}}

            {{PrismaReviewEngine.ScreeningAnswerFormat}}
            """;
    }

    private static readonly Lazy<string>[] Templates = { new(() => Hash(false, false)), new(() => Hash(false, true)), new(() => Hash(true, false)), new(() => Hash(true, true)) };

    /// <summary>
    /// The fingerprint of the fixed text of one screening prompt (grey or formal, first or second screener), part of the
    /// cache key, so an edit to it, or to the steps shared with the systematic review, is never answered from the cache.
    /// </summary>
    public static string TemplateSha256(bool formal, bool secondScreener) => Templates[(formal ? 2 : 0) + (secondScreener ? 1 : 0)].Value;

    /// <summary>The screening prompt written with placeholders in place of the record and the criteria.</summary>
    public static string Template(bool formal, bool secondScreener)
    {
        string S(string name) => PromptTemplate.Slot(name);
        var record = new GreyRecord(S("id"), S("title"), S("summary"), S("url"), S("producer"), S("site"),
            formal ? MultivocalSearcher.FormalKind : S("kind"), new DateTime(2000, 1, 1), new Dictionary<string, long>());
        return Prompt(record, S("inclusion"), S("exclusion"), secondScreener);
    }

    private static string Hash(bool formal, bool secondScreener) => PromptTemplate.Hash(Template(formal, secondScreener));

    /// <summary>Screens one source with <see cref="Prompt"/>, at temperature 0.</summary>
    public static async Task<ScreeningAnswer> ScreenGreyAsync(IChatCompletionService chat, GreyRecord r, string inclusion, string exclusion, bool secondScreener)
    {
        string prompt = Prompt(r, inclusion, exclusion, secondScreener);
        var answer = await LlmJson.GetAsync<ScreeningAnswer>(chat, prompt, LlmJson.JsonMode(0.0), ScreeningAnswer.Validate);
        return answer.Normalized();
    }

    /// <summary>
    /// The same rules as the systematic review: a disagreement keeps the source and flags it. A source screened once
    /// takes its one decision; a failed call, first or second, leaves it not screened.
    /// </summary>
    private static void Apply(GreyScreening entry, Screened s, Screened? again, GreyRecord record)
    {
        entry.InjectionFlags = s.Flags.Count > 0 ? s.Flags.ToList() : null;
        string? error = s.Error ?? again?.Error;
        if (error != null || s.Answer == null || (again != null && again.Answer == null))
        {
            entry.Decision = "Error";
            entry.Reasoning = $"Not screened: the model call failed ({error}).";
            entry.Uncertain = true;
            return;
        }
        var (first, second) = (s.Answer, again?.Answer);
        bool agree = second == null || first.IsIncluded == second.IsIncluded;
        entry.First = first;
        entry.Second = second;
        entry.FromCache = s.FromCache && (again == null || again.FromCache);
        entry.Decision = agree ? first.Decision : "Included";
        entry.Reasoning = agree
            ? first.Reasoning
            : $"The two independent screenings disagreed (first: {first.Decision}, second: {second!.Decision}), so the source was kept and flagged for the reviewer. First screening: {first.Reasoning}";
        entry.Summary = first.BriefSummary;
        var confidences = new[] { first.Confidence, second?.Confidence };
        entry.Confidence = confidences.Contains("low") ? "low" : confidences.Contains("medium") ? "medium" : first.Confidence ?? second?.Confidence;
        if (record.Summary.Length == 0)
        {
            entry.Confidence = "low";
            entry.Reasoning = "(No summary was available, so this decision rests on the title and site only.) " + entry.Reasoning;
        }
        entry.Uncertain = !agree || entry.Confidence == "low" || PromptSafety.AnyStrong(entry.InjectionFlags);
        entry.ExclusionReason = entry.Decision == "Excluded" ? first.ExclusionReason ?? second?.ExclusionReason ?? ExclusionReasons.Unspecified : null;
    }

    /// <summary>How many words a title needs to identify a source on its own, whoever is named as its producer.</summary>
    public const int DistinctTitleWords = 6;

    /// <summary>
    /// What spots the same source under two addresses, in lower case with letters and digits only. A short title needs
    /// its producer too, since two different blogs can share a title such as "Context engineering"; a long title is
    /// enough on its own, since the same report is often listed by two searches with its producer written differently
    /// (seen in a live run: one white paper found at a DOI in OpenAlex and at its record on Zenodo). Empty when the
    /// source has no title.
    /// </summary>
    public static string SameSourceKey(GreyRecord r)
    {
        string title = Plain(r.Title);
        if (title.Length == 0) return "";
        return title.Split(' ').Length >= DistinctTitleWords ? title : $"{title}|{Plain(r.Producer)}";
    }

    private static string Plain(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
}
