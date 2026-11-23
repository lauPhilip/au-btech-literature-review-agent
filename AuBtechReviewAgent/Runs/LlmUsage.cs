using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>
/// The tokens of one model call, read from the usage the provider reported (<see cref="LlmCallRecord.Usage"/>). The
/// recorder stores the usage as JSON text, and for the Mistral connector that text is itself a JSON string holding the
/// object (double-encoded), so up to three levels of string are unwrapped. Names are read in Mistral and OpenAI style
/// (prompt_tokens) and in .NET style (PromptTokens). Reasoning tokens are a part of the output tokens, and cached
/// tokens a part of the input tokens, as the providers count them.
/// </summary>
public readonly record struct LlmUsage(long Input, long Output, long Reasoning, long CachedInput, long Total, bool Known)
{
    public static readonly LlmUsage None = new(0, 0, 0, 0, 0, false);

    public static LlmUsage Parse(string? usageJson)
    {
        if (string.IsNullOrWhiteSpace(usageJson)) return None;
        try
        {
            string text = usageJson;
            for (int level = 0; level < 3; level++)
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.String) { text = root.GetString() ?? ""; continue; }
                return root.ValueKind == JsonValueKind.Object ? Read(root) : None;
            }
            return None;
        }
        catch (JsonException) { return None; }
    }

    private static LlmUsage Read(JsonElement root)
    {
        long input = 0, output = 0, reasoning = 0, cached = 0, total = 0;
        bool known = false;
        void Walk(JsonElement e)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Object) { Walk(p.Value); continue; }
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt64(out long n)) continue;
                switch (p.Name.Replace("_", "").ToLowerInvariant())
                {
                    case "prompttokens" or "inputtokens" or "inputtokencount": input = n; known = true; break;
                    case "completiontokens" or "outputtokens" or "outputtokencount": output = n; known = true; break;
                    case "reasoningtokens": reasoning = n; break;
                    case "cachedtokens": cached = n; break;
                    case "totaltokens" or "totaltokencount": total = n; known = true; break;
                }
            }
        }
        Walk(root);
        return new LlmUsage(input, output, reasoning, cached, total > 0 ? total : input + output, known);
    }
}

/// <summary>
/// The effort of a group of model calls (one stage, one model or the whole run): calls, tokens, retries, failures and
/// the summed time of the calls. Calls, Seconds and Tokens were the only fields before issue #414; the others are
/// null in older metrics lines, where they were never counted.
/// </summary>
public class StageCost
{
    public int Calls { get; set; }

    /// <summary>The summed duration of the calls, retries and waits included, in seconds. Calls run side by side, so this can exceed the run's minutes.</summary>
    public double Seconds { get; set; }

    /// <summary>Input plus output tokens.</summary>
    public long Tokens { get; set; }
    public long? InputTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? OutputTokens { get; set; }

    /// <summary>The part of <see cref="OutputTokens"/> the model spent reasoning.</summary>
    public long? ReasoningTokens { get; set; }
    public int? Retries { get; set; }
    public int? Failures { get; set; }

    /// <summary>Calls with no usage reported (failed calls, or a provider that sends none); their tokens are not counted.</summary>
    public int? CallsWithoutUsage { get; set; }

    public static StageCost Of(IEnumerable<LlmCallRecord> calls)
    {
        var list = calls.ToList();
        var usage = list.Select(c => LlmUsage.Parse(c.Usage)).ToList();
        return new StageCost
        {
            Calls = list.Count,
            Seconds = Math.Round(list.Sum(c => c.DurationMs) / 1000.0, 1),
            Tokens = usage.Sum(u => u.Total),
            InputTokens = usage.Sum(u => u.Input),
            CachedInputTokens = usage.Sum(u => u.CachedInput),
            OutputTokens = usage.Sum(u => u.Output),
            ReasoningTokens = usage.Sum(u => u.Reasoning),
            Retries = list.Sum(c => Math.Max(0, c.Attempts - 1)),
            Failures = list.Count(c => c.Outcome != "ok"),
            CallsWithoutUsage = usage.Count(u => !u.Known),
        };
    }
}
