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
    /// the checks when <see cref="LlmOptions.FastModel"/> is set.
    /// </summary>
    public static IChatCompletionService CreateForReview(LlmOptions options, string mistralApiKey) => options.HasFastModel
        ? new StageRoutedChatService(Create(options, mistralApiKey), Create(options.ForFastModel(), mistralApiKey), options)
        : Create(options, mistralApiKey);

    /// <summary>The Mistral chat completions API, which follows the OpenAI format.</summary>
    public const string MistralBaseUrl = "https://api.mistral.ai/v1";

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

        return new OpenAICompatibleChatService(MistralBaseUrl, options.Model, mistralApiKey, MistralHttp, options.ReasoningEffort);
    }
}
