using System.Reflection;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #483: merging the calls of a new start into llm-calls.json dropped each call's reasoning effort. Issue #484:
/// the data appendix named only the main model, while the statement on AI use names both models and what each did.
/// </summary>
public class MultivocalCallLogModelsTests
{
    [Fact]
    public void MergingKeepsEveryFieldOfACallButItsNumber()
    {
        var call = new LlmCallRecord
        {
            Sequence = 7, Stage = "screening", StartedUtc = "2026-11-24T10:00:00Z", DurationMs = 1200, ModelRequested = "small-model",
            ModelReported = "small-model-2611", Temperature = 0, ReasoningEffort = "low", Attempts = 2, Outcome = "ok",
            PromptSha256 = "p", PromptChars = 10, ResponseSha256 = "r", ResponseChars = 20, Usage = "in=1 out=2",
        };
        // Every property is set above, so a field the copy forgets shows up here.
        foreach (var property in typeof(LlmCallRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.NotEqual(property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null, property.GetValue(call));

        var earlier = MultivocalRunLog.Merge(Array.Empty<LlmCallRecord>(), new[] { call });
        var merged = MultivocalRunLog.Merge(earlier, new[] { call });

        Assert.Equal(new[] { 1, 2 }, merged.Select(c => c.Sequence));
        foreach (var copy in merged)
            foreach (var property in typeof(LlmCallRecord).GetProperties().Where(p => p.Name != nameof(LlmCallRecord.Sequence)))
                Assert.Equal(property.GetValue(call), property.GetValue(copy));
        Assert.All(merged, c => Assert.Equal("low", c.ReasoningEffort));
    }

    [Fact]
    public void TheAppendixNamesBothModelsAndWhatEachDidAsTheStatementOnAiUseDoes()
    {
        var input = MultivocalPaperTests.Input();
        input.Screening.Model = "small-model";
        input.Synthesis.Model = "large-model";
        var paper = new MultivocalPaperFile { Model = "large-model", CheckModel = "small-model", WrittenUtc = new DateTime(2026, 11, 23) };

        var meta = MultivocalReporter.Write(input, paper).Meta;

        const string what = "small-model answered both screenings of every source and the citation checks, and large-model did every other step that used a model";
        Assert.Contains($"Language models: {what}", meta);
        Assert.Contains($"Of these, {what}. ", MultivocalPaper.AiUse(input, paper));
    }

    [Fact]
    public void BeforeThePaperIsWrittenTheAppendixNamesTheScreeningModel()
    {
        var input = MultivocalPaperTests.Input();
        input.Screening.Model = "small-model";
        input.Synthesis.Model = "large-model";

        Assert.Contains("Language models: small-model answered both screenings of every source, and large-model did every other step that used a model",
            MultivocalReporter.Write(input).Meta);
    }

    [Fact]
    public void WithOneModelTheAppendixLineStaysAsItWas()
    {
        var input = MultivocalPaperTests.Input();
        input.Screening.Model = "large-model";
        input.Synthesis.Model = "large-model";

        var meta = MultivocalReporter.Write(input, new MultivocalPaperFile { Model = "large-model" }).Meta;

        Assert.Contains("Language model: large-model", meta);
        Assert.DoesNotContain(meta, m => m.StartsWith("Language models", StringComparison.Ordinal));
    }
}
