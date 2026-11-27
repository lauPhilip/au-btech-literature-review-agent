using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>A chat service that can say which model a stage's calls go to, so the call log records it.</summary>
public interface IStageModels
{
    string ModelFor(string stage);
}

/// <summary>
/// Sends each call to the fast model when its stage (<see cref="LlmStage.Name"/>, set around every call) is one of
/// <see cref="LlmOptions.FastStages"/>, and to the main model otherwise. No prompt code has to know about it.
/// </summary>
public sealed class StageRoutedChatService : IChatCompletionService, IStageModels
{
    private readonly IChatCompletionService _main;
    private readonly IChatCompletionService _fast;
    private readonly LlmOptions _options;

    public StageRoutedChatService(IChatCompletionService main, IChatCompletionService fast, LlmOptions options)
    {
        _main = main;
        _fast = fast;
        _options = options;
    }

    public IReadOnlyDictionary<string, object?> Attributes => _main.Attributes;

    public string ModelFor(string stage) => _options.ModelFor(stage);

    private IChatCompletionService For(string stage) => _options.HasFastModel && LlmOptions.FastStages.Contains(stage) ? _fast : _main;

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default) =>
        For(LlmStage.Name).GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default) =>
        For(LlmStage.Name).GetStreamingChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
}

public static class LlmFactory
{
    /// <summary>
    /// The chat service of a systematic review: <see cref="Create"/> for the main model, routed to a second client for
    /// the checks when <see cref="LlmOptions.FastModel"/> is set. Each model's calls pass a gate shared by every run on
    /// the server (<see cref="ModelCallLimiter"/>), sized from its stage width and <paramref name="concurrentRuns"/>.
    /// </summary>
    public static IChatCompletionService CreateForReview(LlmOptions options, string mistralApiKey, int concurrentRuns = 3) => options.HasFastModel
        ? new StageRoutedChatService(Limited(options, mistralApiKey, concurrentRuns), Limited(options.ForFastModel(), mistralApiKey, concurrentRuns), options)
        : Limited(options, mistralApiKey, concurrentRuns);

    private static IChatCompletionService Limited(LlmOptions options, string mistralApiKey, int concurrentRuns)
    {
        string endpoint = options.IsOpenAICompatible ? options.BaseUrl : MistralEndpoint(options);
        string key = options.IsOpenAICompatible ? options.ApiKey : mistralApiKey;
        var gate = ModelCallLimiter.GateFor(endpoint, options.Model, key, ModelCallLimiter.CapacityFor(options.QualityParallelism, concurrentRuns));
        return new LimitedChatService(Create(options, mistralApiKey), gate);
    }

    /// <summary>
    /// The Mistral chat completions API, which follows the OpenAI format, at its EU regional endpoint: requests sent
    /// there are processed in the EU. Mistral bills regional calls at 1.1 times its list price.
    /// </summary>
    public const string MistralBaseUrl = "https://api.eu.mistral.ai/v1";

    /// <summary>Mistral's global endpoint, which names no region. Only for a model the EU endpoint does not serve.</summary>
    public const string MistralGlobalUrl = "https://api.mistral.ai/v1";

    /// <summary>
    /// The Mistral address a run calls: <see cref="LlmOptions.MistralBaseUrl"/> when it is an https address, else the EU
    /// endpoint, so a mistyped setting never sends a key in clear text or to a relative path.
    /// </summary>
    public static string MistralEndpoint(LlmOptions options) =>
        Uri.TryCreate(options.MistralBaseUrl?.Trim(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? url.ToString().TrimEnd('/')
            : MistralBaseUrl;

    // One client for every run, so connections are reused instead of opened per run.
    private static readonly HttpClient MistralHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// Mistral is called on its chat completions API directly rather than through the Semantic Kernel connector.
    /// The connector has no thinking chunk, so a reasoning model's whole chunk list reached the JSON reader, which
    /// took the thinking chunk for the answer: every structured answer failed validation and was asked for twice.
    /// </summary>
    public static IChatCompletionService Create(LlmOptions options, string mistralApiKey)
    {
        if (options.IsOpenAICompatible)
            return new OpenAICompatibleChatService(options.BaseUrl, options.Model, options.ApiKey);

        return new OpenAICompatibleChatService(MistralEndpoint(options), options.Model, mistralApiKey, MistralHttp, options.ReasoningEffort);
    }
}
