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

// Screening one source twice, with the systematic review's steps and answer format, and applying the two answers.
public sealed partial class MultivocalScreener
{
    /// <summary>Part of the cache key of an academic record (block I-3): raise it whenever its prompt changes.</summary>
    public const string FormalPromptVersion = "formal-screening-v1";

    private sealed record Screened(ScreeningAnswer? First, ScreeningAnswer? Second, int CacheHits, IReadOnlyList<string> Flags, string? Error);

    private async Task<Screened> ScreenOneAsync(IChatCompletionService chat, MultivocalPlan plan, GreyRecord record)
    {
        var flags = PromptSafety.Scan(record.Title, record.Summary, record.Producer);
        try
        {
            var (first, hit1) = await ScreenCachedAsync(chat, plan, record, secondScreener: false);
            var (second, hit2) = await ScreenCachedAsync(chat, plan, record, secondScreener: true);
            return new Screened(first, second, (hit1 ? 1 : 0) + (hit2 ? 1 : 0), flags, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // One failed source does not stop the screening; it is logged as not screened.
            string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            _log.LogWarning("Grey screening failed for '{Title}': {Message}", record.Title.Replace('\r', ' ').Replace('\n', ' '), message);
            return new Screened(null, null, 0, flags, message);
        }
    }

    private async Task<(ScreeningAnswer Answer, bool FromCache)> ScreenCachedAsync(IChatCompletionService chat, MultivocalPlan plan, GreyRecord r, bool secondScreener)
    {
        string version = r.Kind == MultivocalSearcher.FormalKind ? FormalPromptVersion : PromptVersion;
        string key = ReviewCache.Key(version, secondScreener ? "second" : "first", _model, plan.InclusionCriteria, plan.ExclusionCriteria,
            r.Title, r.Summary, r.Site, r.Kind, r.Producer, r.Published?.ToString("yyyy-MM-dd"));
        if (_cache.TryGet<ScreeningAnswer>("grey-screening", key, out var cached) && cached != null && ScreeningAnswer.Validate(cached) == null)
            return (cached.Normalized(), true);
        var answer = await ScreenGreyAsync(chat, r, plan.InclusionCriteria, plan.ExclusionCriteria, secondScreener);
        _cache.Set("grey-screening", key, answer);
        return (answer, false);
    }

    /// <summary>
    /// The screening prompt for one source: the systematic review's steps and answer format, with the record described
    /// as what it is. It is judged on its subject against the criteria, not on being grey literature; an academic
    /// record of the formal pool (block I-3) gets the same criteria, steps and answer format, introduced as academic.
    /// </summary>
    public static async Task<ScreeningAnswer> ScreenGreyAsync(IChatCompletionService chat, GreyRecord r, string inclusion, string exclusion, bool secondScreener)
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

        var prompt = $$"""
            {{intro}}

            CRITERIA DIRECTIVES (from the reviewer):
            - Inclusion Thresholds: {{inclusion}}
            - Exclusion Thresholds: {{(exclusion.Length > 0 ? exclusion : "none")}}

            {{PromptSafety.ReviewerInputNotice}}

            {{PromptSafety.DataOnlyNotice}}

            SOURCE TARGET DATA:
            {{PromptSafety.Wrap(record, formal ? "academic record metadata and abstract" : "grey source metadata and summary")}}

            {{PrismaReviewEngine.ScreeningProcedure(secondScreener)}}

            {{PrismaReviewEngine.ScreeningAnswerFormat}}
            """;

        var answer = await LlmJson.GetAsync<ScreeningAnswer>(chat, prompt, LlmJson.JsonMode(0.0), ScreeningAnswer.Validate);
        return answer.Normalized();
    }

    /// <summary>The same rules as the systematic review: a disagreement keeps the source and flags it.</summary>
    private static void Apply(GreyScreening entry, Screened s, GreyRecord record)
    {
        entry.InjectionFlags = s.Flags.Count > 0 ? s.Flags.ToList() : null;
        if (s.Error != null || s.First == null || s.Second == null)
        {
            entry.Decision = "Error";
            entry.Reasoning = $"Not screened: the model call failed ({s.Error}).";
            entry.Uncertain = true;
            return;
        }
        var (first, second) = (s.First, s.Second);
        bool agree = first.IsIncluded == second.IsIncluded;
        entry.First = first;
        entry.Second = second;
        entry.FromCache = s.CacheHits == 2;
        entry.Decision = agree ? first.Decision : "Included";
        entry.Reasoning = agree
            ? first.Reasoning
            : $"The two independent screenings disagreed (first: {first.Decision}, second: {second.Decision}), so the source was kept and flagged for the reviewer. First screening: {first.Reasoning}";
        entry.Summary = first.BriefSummary;
        entry.Confidence = new[] { first.Confidence, second.Confidence }.Contains("low") ? "low"
            : new[] { first.Confidence, second.Confidence }.Contains("medium") ? "medium"
            : first.Confidence ?? second.Confidence;
        if (record.Summary.Length == 0)
        {
            entry.Confidence = "low";
            entry.Reasoning = "(No summary was available, so this decision rests on the title and site only.) " + entry.Reasoning;
        }
        entry.Uncertain = !agree || entry.Confidence == "low" || entry.InjectionFlags != null;
        entry.ExclusionReason = entry.Decision == "Excluded" ? first.ExclusionReason ?? second.ExclusionReason ?? ExclusionReasons.Unspecified : null;
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
