using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// One limit on the calls in flight to one model with one key, shared by every run on the server. The stage widths
/// (Llm:QualityParallelism, Llm:FastParallelism) count items in one run; this caps the calls all runs make together,
/// so several runs at once cannot add up past what the model's rate limit was sized for.
/// </summary>
public static class ModelCallLimiter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>
    /// The calls in flight that one model may have on the server: the stage width times the runs that may run at once,
    /// and never less than two widths, because one run can make two calls per item (the two screenings, dual coding).
    /// </summary>
    public static int CapacityFor(int width, int concurrentRuns) => Math.Max(1, width) * Math.Max(2, concurrentRuns);

    /// <summary>The shared gate of an endpoint, model and key. The key is only hashed, never kept.</summary>
    public static SemaphoreSlim GateFor(string endpoint, string model, string apiKey, int capacity) =>
        Gates.GetOrAdd($"{endpoint}|{model}|{RecordingChatCompletionService.Sha256(apiKey ?? "")}|{capacity}",
            _ => new SemaphoreSlim(Math.Max(1, capacity)));
}

/// <summary>
/// Holds a place in a <see cref="ModelCallLimiter"/> gate for the length of one call. A retry waits outside the gate
/// (the retry is in <see cref="RecordingChatCompletionService"/>), so a call that waits out a 429 does not block others.
/// </summary>
public sealed class LimitedChatService : IChatCompletionService
{
    private readonly IChatCompletionService _inner;
    private readonly SemaphoreSlim _gate;

    public LimitedChatService(IChatCompletionService inner, SemaphoreSlim gate)
    {
        _inner = inner;
        _gate = gate;
    }

    public IReadOnlyDictionary<string, object?> Attributes => _inner.Attributes;

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _inner.GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var chunk in _inner.GetStreamingChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken).ConfigureAwait(false))
                yield return chunk;
        }
        finally { _gate.Release(); }
    }
}
