using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

// The support check: whether each extracted value says what its quote says, before it becomes a finding.
public sealed partial class MultivocalSynthesiser
{
    private sealed class SupportAnswer { public List<SupportVerdict>? Verdicts { get; set; } }
    private sealed class SupportVerdict { public string Finding { get; set; } = ""; public string Verdict { get; set; } = ""; public string? Reason { get; set; } }

    /// <summary>How many sources' values are checked at the same time.</summary>
    public const int SupportParallelism = 4;

    /// <summary>
    /// Checks every extracted value against its own quote, one call per source: "supported", "partially_supported" or
    /// "not_supported". Values judged not supported are returned apart, with the reason; when a source's answer fails
    /// the check, its values are kept and counted as unchecked. Values of an open attribute (a tool's name, say) are
    /// names the source mentions, so only that the quote names them is checked.
    /// </summary>
    public static async Task<(List<SynthesisFinding> Supported, List<UnsupportedValue> Unsupported, int Unchecked)> CheckSupportAsync(
        IChatCompletionService chat, IReadOnlyList<SynthesisFinding> findings, IReadOnlySet<string> openAttributes, ReviewCache? cache = null, string model = "")
    {
        using var throttle = new System.Threading.SemaphoreSlim(SupportParallelism);
        var work = findings.GroupBy(f => f.Address).Select(source => Task.Run(async () =>
        {
            await throttle.WaitAsync();
            try { return (Findings: source.ToList(), Verdicts: await AskSupportAsync(chat, source.ToList(), openAttributes, cache ?? ReviewCache.Disabled, model)); }
            finally { throttle.Release(); }
        })).ToList();

        var supported = new List<SynthesisFinding>();
        var unsupported = new List<UnsupportedValue>();
        int unchecked_ = 0;
        foreach (var task in work)
        {
            var (sourceFindings, verdicts) = await task;
            foreach (var f in sourceFindings)
            {
                if (verdicts == null) { unchecked_++; supported.Add(f); continue; }
                var v = verdicts[f.Id];
                if (v.Verdict == CitationSupportChecker.NotSupported)
                    unsupported.Add(new UnsupportedValue { Finding = f.Id, Attribute = f.Attribute, Value = f.Value, Quote = f.Quote, Reason = (v.Reason ?? "").Trim() });
                else supported.Add(f);
            }
        }
        // Keep the findings in their own order (by source, then by id), whatever order the calls finished in.
        var order = findings.Select((f, i) => (f.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return (supported.OrderBy(f => order[f.Id]).ToList(), unsupported.OrderBy(u => order[u.Finding]).ToList(), unchecked_);
    }

    /// <summary>Part of the cache key of the support check: raise it whenever its prompt changes.</summary>
    public const string SupportPromptVersion = "grey-support-v1";

    private static async Task<Dictionary<string, SupportVerdict>?> AskSupportAsync(IChatCompletionService chat, List<SynthesisFinding> findings, IReadOnlySet<string> openAttributes,
        ReviewCache cache, string model)
    {
        string list = string.Join("\n", findings.Select(f =>
            $"{f.Id} | {f.Attribute}{(openAttributes.Contains(f.Attribute) ? " (a name the source mentions)" : "")}: {f.Value} | quote: \"{f.Quote}\""));
        var prompt = $$"""
            You are checking the data extraction of a grey literature review: does each quote, taken from the source's own page, support the value it was extracted for?

            Source: {{PromptSafety.Wrap(findings[0].Title, "source title")}}

            {{PromptSafety.DataOnlyNotice}}

            VALUES AND THEIR QUOTES (id | attribute: value | quote):
            {{PromptSafety.Wrap(list, "extracted values")}}

            For each value give a verdict: "supported" when the quote says what the value claims, "partially_supported" when it says part of it or only implies it, "not_supported" when it is about something else. For a name the source mentions, "supported" means the quote names it. Give a one-sentence reason for every verdict that is not "supported".

            Respond ONLY with a valid minified JSON object, one entry per value:
            {"verdicts":[{"finding":"F1.1","verdict":"supported","reason":""}]}
            """;
        var ids = findings.Select(f => f.Id).ToHashSet();
        // The same values with the same quotes of the same source get the verdicts they got before: the key is the whole
        // prompt, so a changed value, quote, title or prompt asks again. A cached answer is checked like a new one.
        string key = ReviewCache.Key(SupportPromptVersion, model, prompt);
        string? Problem(SupportAnswer a)
        {
            if (a.Verdicts == null) return "verdicts must be a list.";
            var unknown = a.Verdicts.Select(v => v.Finding.Trim()).FirstOrDefault(id => !ids.Contains(id));
            if (unknown != null) return $"\"{unknown}\" is not one of the values listed.";
            var missing = ids.Where(id => a.Verdicts.All(v => v.Finding.Trim() != id)).ToList();
            if (missing.Count > 0) return $"values {string.Join(", ", missing)} have no verdict; give one for every value.";
            var bad = a.Verdicts.FirstOrDefault(v => v.Verdict.Trim() is not (CitationSupportChecker.Supported or CitationSupportChecker.Partial or CitationSupportChecker.NotSupported));
            if (bad != null) return $"\"{bad.Verdict}\" is not a verdict; use supported, partially_supported or not_supported.";
            return a.Verdicts.Any(v => v.Verdict.Trim() == CitationSupportChecker.NotSupported && string.IsNullOrWhiteSpace(v.Reason)) ? "a value that is not supported needs the reason." : null;
        }
        try
        {
            using (LlmStage.Begin("extraction-support"))
            {
                SupportAnswer answer;
                if (cache.TryGet<SupportAnswer>("grey-support", key, out var cached) && cached != null && Problem(cached) == null)
                    answer = cached;
                else
                {
                    answer = await LlmJson.GetAsync<SupportAnswer>(chat, prompt, LlmJson.JsonMode(0.0), Problem, repairAttempts: 2);
                    cache.Set("grey-support", key, answer);
                }
                return answer.Verdicts!.GroupBy(v => v.Finding.Trim()).ToDictionary(g => g.Key, g => new SupportVerdict { Finding = g.Key, Verdict = g.First().Verdict.Trim(), Reason = g.First().Reason });
            }
        }
        catch (LlmOutputException)
        {
            return null;
        }
    }
}
