using AuBtechReviewAgent;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The stage widths and the limit on calls in flight that all runs share (#385).</summary>
public class ModelCallLimiterTests
{
    /// <summary>A model that counts the calls in flight and remembers the most it saw at once.</summary>
    private sealed class CountingChat : IChatCompletionService
    {
        private readonly object _peakLock = new();
        private int _inFlight;
        public int Peak;
        public int Calls;
        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        {
            int now = Interlocked.Increment(ref _inFlight);
            lock (_peakLock) Peak = Math.Max(Peak, now);
            await Task.Delay(5, cancellationToken);
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Increment(ref Calls);
            return new[] { new ChatMessageContent(AuthorRole.Assistant, "ok") };
        }

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task TwoRunsTogetherNeverExceedTheSharedLimit()
    {
        var model = new CountingChat();
        string name = "limit-test-" + Guid.NewGuid();
        // Two runs, each with its own client and recorder, as the engine makes them; the gate is the shared one.
        var gateA = ModelCallLimiter.GateFor("http://model.test", name, "key", 3);
        var gateB = ModelCallLimiter.GateFor("http://model.test", name, "key", 3);
        Assert.Same(gateA, gateB);
        var runA = new RecordingChatCompletionService(new LimitedChatService(model, gateA), name);
        var runB = new RecordingChatCompletionService(new LimitedChatService(model, gateB), name);

        var calls = Enumerable.Range(0, 40).Select(i => (i % 2 == 0 ? runA : runB).GetChatMessageContentAsync("p" + i));
        await Task.WhenAll(calls);

        Assert.Equal(40, model.Calls);
        Assert.InRange(model.Peak, 1, 3);
    }

    [Fact]
    public void AnotherKeyOrModelHasItsOwnGate()
    {
        string name = "gate-test-" + Guid.NewGuid();
        var gate = ModelCallLimiter.GateFor("http://model.test", name, "key-a", 4);
        Assert.NotSame(gate, ModelCallLimiter.GateFor("http://model.test", name, "key-b", 4));
        Assert.NotSame(gate, ModelCallLimiter.GateFor("http://model.test", name + "-small", "key-a", 4));
    }

    [Fact]
    public void TheCapacityIsTheWidthTimesTheRunsAndAtLeastTwoWidths()
    {
        Assert.Equal(24, ModelCallLimiter.CapacityFor(8, 3));
        Assert.Equal(48, ModelCallLimiter.CapacityFor(16, 3));
        Assert.Equal(16, ModelCallLimiter.CapacityFor(8, 1)); // the two screenings of a record still run at once
    }

    [Fact]
    public void FastStagesRunWiderOnlyWhenAFastModelIsSet()
    {
        var one = new LlmOptions();
        Assert.Equal(8, one.ParallelismFor("screening"));
        Assert.Equal(8, one.ParallelismFor("extraction"));

        var two = new LlmOptions { FastModel = "mistral-small-latest" };
        Assert.Equal(16, two.ParallelismFor("screening"));
        Assert.Equal(16, two.ParallelismFor("citation-check"));
        Assert.Equal(8, two.ParallelismFor("extraction"));
        Assert.Equal(16, two.ForFastModel().QualityParallelism); // the fast client's gate is sized from the fast width
    }

    [Fact]
    public void QualityParallelismFallsBackToTheEarlierName()
    {
        LlmOptions Bind(params (string Key, string Value)[] values) => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>("Llm:" + v.Key, v.Value))).Build()
            .GetSection("Llm").Get<LlmOptions>()!;

        Assert.Equal(5, Bind(("ScreeningParallelism", "5")).QualityParallelism);
        var both = Bind(("ScreeningParallelism", "5"), ("QualityParallelism", "6"), ("FastParallelism", "20"), ("FastModel", "small"));
        Assert.Equal(6, both.QualityParallelism);
        Assert.Equal(20, both.ParallelismFor("screening"));
    }
}
