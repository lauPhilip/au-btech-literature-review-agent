using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;

/// <summary>
/// Which language model the review uses, bound from the "Llm" section of appsettings.json.
/// Provider "Mistral" (default) uses the Mistral API. Provider "OpenAICompatible" talks to any server that
/// implements the OpenAI chat completions API: a local Ollama (BaseUrl http://localhost:11434/v1), LM Studio,
/// vLLM, llama.cpp server, or a hosted service. With a local model the whole review can run without sending
/// anything to a commercial provider.
/// </summary>
public class LlmOptions
{
    public string Provider { get; set; } = "Mistral";
    public string Model { get; set; } = "mistral-large-latest";

    /// <summary>OpenAICompatible only: base URL ending before /chat/completions, e.g. http://localhost:11434/v1.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>OpenAICompatible only: API key if the server needs one (Ollama does not). Keep it in user-secrets.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>How many records are screened at the same time within one run.</summary>
    public int ScreeningParallelism { get; set; } = 4;

    public bool IsOpenAICompatible => Provider.Equals("OpenAICompatible", StringComparison.OrdinalIgnoreCase);

    /// <summary>A readable name for the methods section, e.g. "mistral-large-latest (Mistral API)".</summary>
    public string DisplayName => IsOpenAICompatible
        ? $"{Model} (OpenAI-compatible endpoint{(string.IsNullOrWhiteSpace(BaseUrl) ? "" : " " + SafeHost(BaseUrl))})"
        : $"{Model} (Mistral API)";

    private static string SafeHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
}

/// <summary>
/// A minimal chat client for OpenAI-compatible servers. It only needs what the review uses: one user
/// prompt in, one text answer out, with temperature and JSON mode. The engine writes its settings as
/// MistralAIPromptExecutionSettings; temperature and response_format are read from those, so no prompt code
/// has to know which provider is behind it.
/// </summary>
public class OpenAICompatibleChatService : IChatCompletionService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly Uri _endpoint;
    private readonly string _apiKey;

    public OpenAICompatibleChatService(string baseUrl, string model, string apiKey = "", HttpClient? http = null)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/chat/completions", UriKind.Absolute, out var endpoint))
            throw new ArgumentException($"Llm:BaseUrl '{baseUrl}' is not a valid URL.");
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; // local models can be slow
    }

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        string body = BuildRequestBody(_model, chatHistory, executionSettings);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(_apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpOperationException(response.StatusCode, json, $"The model server answered {(int)response.StatusCode}.", null);

        var (text, model, usage) = ParseResponse(json);
        var content = new ChatMessageContent(AuthorRole.Assistant, text) { ModelId = model ?? _model };
        if (usage != null) content.Metadata = new Dictionary<string, object?> { ["Usage"] = usage };
        return new[] { content };
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The review never streams; answer with the whole message as one chunk.
        var all = await GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
        foreach (var m in all) yield return new StreamingChatMessageContent(m.Role, m.Content) { ModelId = m.ModelId };
    }

    /// <summary>The JSON request body. Public for unit tests.</summary>
    public static string BuildRequestBody(string model, ChatHistory history, PromptExecutionSettings? settings)
    {
        var messages = new JsonArray();
        foreach (var m in history)
            messages.Add(new JsonObject { ["role"] = m.Role.Label, ["content"] = m.Content ?? "" });

        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["stream"] = false };
        if (settings is MistralAIPromptExecutionSettings mistral && mistral.Temperature is double t) body["temperature"] = t;
        else if (settings?.ExtensionData?.TryGetValue("temperature", out var temp) == true && temp is double t2) body["temperature"] = t2;

        if (settings?.ExtensionData?.ContainsKey("response_format") == true)
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        return body.ToJsonString();
    }

    /// <summary>Text, model name and usage from a chat completions response. Public for unit tests.</summary>
    public static (string Text, string? Model, string? Usage) ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string text = "";
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            text = c.GetString() ?? "";
        string? model = root.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() : null;
        string? usage = root.TryGetProperty("usage", out var u) ? u.GetRawText() : null;
        return (text, model, usage);
    }
}

/// <summary>Builds the chat service for a run from the configured provider.</summary>
public static class LlmFactory
{
    public static IChatCompletionService Create(LlmOptions options, string mistralApiKey)
    {
        if (options.IsOpenAICompatible)
            return new OpenAICompatibleChatService(options.BaseUrl, options.Model, options.ApiKey);

        var builder = Kernel.CreateBuilder();
        builder.AddMistralChatCompletion(options.Model, mistralApiKey);
        return builder.Build().GetRequiredService<IChatCompletionService>();
    }
}
