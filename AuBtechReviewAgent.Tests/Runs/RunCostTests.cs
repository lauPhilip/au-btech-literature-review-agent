using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The effort figures of a run (issue #414): tokens read from llm-calls.json whether the usage is stored once or twice
/// encoded, with or without reasoning tokens, and summed by stage and by model with the retries, failures and call time.
/// </summary>
public class RunCostTests
{
    private const string Plain = """{"prompt_tokens":372,"total_tokens":1259,"completion_tokens":887,"prompt_tokens_details":{"cached_tokens":100},"completion_tokens_details":{"reasoning_tokens":860},"service_tier":"standard"}""";

    // As the Mistral connector's usage lands in llm-calls.json: the object serialised to a string, and that string serialised again.
    private static string Twice(string json) => JsonSerializer.Serialize(json);

    [Fact]
    public void UsageIsReadWhenStoredOnce()
    {
        var u = LlmUsage.Parse(Plain);
        Assert.Equal((372L, 887L, 860L, 100L, 1259L, true), (u.Input, u.Output, u.Reasoning, u.CachedInput, u.Total, u.Known));
    }

    [Fact]
    public void UsageIsReadWhenDoubleEncodedAsInARealRun()
    {
        // Copied from a real systematic run's llm-calls.json (19 Nov 2026).
        const string real = "\"{\\u0022prompt_tokens\\u0022:372,\\u0022total_tokens\\u0022:1259,\\u0022completion_tokens\\u0022:887,\\u0022prompt_tokens_details\\u0022:{\\u0022cached_tokens\\u0022:0},\\u0022completion_tokens_details\\u0022:{\\u0022reasoning_tokens\\u0022:860},\\u0022service_tier\\u0022:\\u0022standard\\u0022}\"";

        Assert.Equal(LlmUsage.Parse(Plain) with { CachedInput = 0 }, LlmUsage.Parse(real));
        Assert.Equal(LlmUsage.Parse(Plain), LlmUsage.Parse(Twice(Plain)));
        Assert.Equal(1259, RunMetrics.TokensIn(Twice(Plain))); // the old total read 0 here
    }

    [Theory]
    [InlineData("""{"prompt_tokens":10,"completion_tokens":5}""", 10, 5, 0, 15)]
    [InlineData("""{"PromptTokens":100,"CompletionTokens":20,"TotalTokens":120}""", 100, 20, 0, 120)]
    [InlineData("""{"Details":{"InputTokenCount":7,"OutputTokenCount":3}}""", 7, 3, 0, 10)]
    public void ReasoningTokensAreZeroWhenTheProviderSendsNone(string usage, long input, long output, long reasoning, long total)
    {
        var u = LlmUsage.Parse(usage);
        Assert.Equal((input, output, reasoning, total), (u.Input, u.Output, u.Reasoning, u.Total));
        Assert.True(u.Known);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"\\\"not json either\\\"\"")]
    public void UnreadableUsageCountsNothing(string? usage) => Assert.Equal(LlmUsage.None, LlmUsage.Parse(usage));

    private static List<LlmCallRecord> Calls() => new()
    {
        new() { Stage = "screening", ModelRequested = "small", Attempts = 3, Outcome = "ok", DurationMs = 30_000, Usage = Twice(Plain) },
        new() { Stage = "screening", ModelRequested = "small", Attempts = 1, Outcome = "ok", DurationMs = 6_000, Usage = """{"prompt_tokens":10,"completion_tokens":5}""" },
        new() { Stage = "outline", ModelRequested = "large", ModelReported = "large-2411", Attempts = 4, Outcome = "failed: HttpOperationException", DurationMs = 24_000 },
    };

    [Fact]
    public void TheRunsEffortIsSummedByStageAndByModel()
    {
        var m = new RunMetrics();
        m.AddCost(Calls());

        Assert.Equal((3, 5, 1, 1), (m.LlmCalls, m.LlmRetries, m.LlmFailures, m.CallsWithoutUsage));
        Assert.Equal((382L, 100L, 892L, 860L, 1274L), (m.InputTokens, m.CachedInputTokens, m.OutputTokens, m.ReasoningTokens, m.Tokens));
        Assert.Equal(1.0, m.LlmMinutes);

        Assert.Equal(new[] { "outline", "screening" }, m.CostByStage.Keys);
        var screening = m.CostByStage["screening"];
        Assert.Equal((2, 36.0, 382L, 892L, 860L, 2, 0), (screening.Calls, screening.Seconds, screening.InputTokens, screening.OutputTokens, screening.ReasoningTokens, screening.Retries, screening.Failures));
        var outline = m.CostByStage["outline"];
        Assert.Equal((1, 0L, 3, 1, 1), (outline.Calls, outline.Tokens, outline.Retries, outline.Failures, outline.CallsWithoutUsage));

        // By the model the call was sent to, not the dated name the provider reported.
        Assert.Equal(new[] { "large", "small" }, m.CostByModel!.Keys);
        Assert.Equal(1274L, m.CostByModel["small"].Tokens);
    }

    [Fact]
    public void AnOldMetricsLineStillLoadsAndLeavesTheNewFiguresEmpty()
    {
        const string old = """{"SchemaVersion":2,"RunId":"r","CompletedUtc":"2026-11-01T10:00:00Z","LlmCalls":4,"Tokens":0,"CostByStage":{"screening":{"Calls":4,"Seconds":12.5,"Tokens":0}}}""";

        var m = JsonSerializer.Deserialize<RunMetrics>(old)!;

        Assert.Equal(4, m.LlmCalls);
        Assert.Null(m.InputTokens);
        Assert.Null(m.ReasoningTokens);
        Assert.Null(m.LlmMinutes);
        Assert.Null(m.CostByModel);
        Assert.Null(m.CostByStage["screening"].OutputTokens);
        Assert.Equal(12.5, m.CostByStage["screening"].Seconds);
        Assert.Equal("systematic", m.Type);
    }
}
