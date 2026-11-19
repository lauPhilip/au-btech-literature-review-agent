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

    /// <summary>
    /// How many model calls one run makes at the same time (screening, extraction, coding, checks). Reasoning models
    /// take 30 to 110 seconds a call, so the calls are bound by waiting, not by the server's throughput.
    /// </summary>
    public int ScreeningParallelism { get; set; } = 8;

    /// <summary>
    /// Mistral only: the reasoning_effort sent with every call ("low", "medium" or "high"). Reasoning models such as
    /// Mistral Large 4 always reason and use "high" when the parameter is left out, which made a review several
    /// times slower than with the earlier non-reasoning model. Empty: the parameter is not sent. A model that does
    /// not accept it is asked again without it.
    /// </summary>
    public string ReasoningEffort { get; set; } = "low";

    /// <summary>
    /// Optional second model for the narrow, high-volume checks of a systematic review (<see cref="FastStages"/>):
    /// the peer-review filter, both screenings and the citation checks. Those calls answer a short structured
    /// question, so a smaller model without reasoning is several times faster there; everything that writes or
    /// reasons (search strings, extraction, coding, writing, repair, peer review) stays on <see cref="Model"/>.
    /// Empty: <see cref="Model"/> does everything.
    /// </summary>
    public string FastModel { get; set; } = "";

    /// <summary>The reasoning_effort sent to <see cref="FastModel"/> ("none" switches reasoning off on Mistral Small 4).</summary>
    public string FastReasoningEffort { get; set; } = "none";

    /// <summary>The stages (LlmStage names) that go to <see cref="FastModel"/> when one is set.</summary>
    public static readonly IReadOnlySet<string> FastStages = new HashSet<string>(StringComparer.Ordinal)
    {
        "peer-review-filter", "screening", "screening-second", "citation-check", "citation-recheck",
    };

    public bool HasFastModel => !string.IsNullOrWhiteSpace(FastModel) && !FastModel.Trim().Equals(Model.Trim(), StringComparison.Ordinal);

    /// <summary>The model a call of this stage is sent to.</summary>
    public string ModelFor(string stage) => HasFastModel && FastStages.Contains(stage) ? FastModel.Trim() : Model;

    /// <summary>A readable name of the model a stage uses, for the methods text.</summary>
    public string DisplayNameFor(string stage) => NameOf(ModelFor(stage));

    public bool IsOpenAICompatible => Provider.Equals("OpenAICompatible", StringComparison.OrdinalIgnoreCase);

    /// <summary>A readable name for the methods section, e.g. "mistral-large-latest (Mistral API)".</summary>
    public string DisplayName => NameOf(Model);

    /// <summary>The same settings with another model and effort, for the client of <see cref="FastModel"/>.</summary>
    public LlmOptions ForFastModel() => new()
    {
        Provider = Provider, Model = FastModel.Trim(), BaseUrl = BaseUrl, ApiKey = ApiKey,
        ScreeningParallelism = ScreeningParallelism, ReasoningEffort = FastReasoningEffort,
    };

    private string NameOf(string model) => IsOpenAICompatible
        ? $"{model} (OpenAI-compatible endpoint{(string.IsNullOrWhiteSpace(BaseUrl) ? "" : " " + SafeHost(BaseUrl))})"
        : $"{model} (Mistral API)";

    private static string SafeHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
}

/// <summary>
/// A minimal chat client for OpenAI-compatible servers, including the Mistral API itself. It only needs what the
/// review uses: one user prompt in, one text answer out, with temperature and JSON mode. The engine writes its
/// settings as MistralAIPromptExecutionSettings; temperature and response_format are read from those, so no prompt
/// code has to know which provider is behind it. A reasoning model's answer is a list of chunks; only the text
/// chunks are the answer, and the thinking is never passed on.
/// </summary>
public class OpenAICompatibleChatService : IChatCompletionService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private volatile string _reasoningEffort;

    public OpenAICompatibleChatService(string baseUrl, string model, string apiKey = "", HttpClient? http = null, string reasoningEffort = "")
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/chat/completions", UriKind.Absolute, out var endpoint))
            throw new ArgumentException($"Llm:BaseUrl '{baseUrl}' is not a valid URL.");
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
        _reasoningEffort = reasoningEffort?.Trim() ?? "";
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; // local models can be slow
    }

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        string json;
        string sentEffort;
        while (true)
        {
            string effort = _reasoningEffort;
            sentEffort = effort;
            string body = BuildRequestBody(_model, chatHistory, executionSettings, effort);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (!string.IsNullOrWhiteSpace(_apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _http.SendAsync(request, cancellationToken);
            json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode) break;
            if (effort.Length > 0 && RejectsReasoningEffort(response.StatusCode, json))
            {
                // A model without adjustable reasoning: ask again without the parameter, and leave it out from now on.
                _reasoningEffort = "";
                continue;
            }
            throw new HttpOperationException(response.StatusCode, json, $"The model server answered {(int)response.StatusCode}{ErrorDetail(json)}.", null);
        }

        var (text, model, usage) = ParseResponse(json);
        var content = new ChatMessageContent(AuthorRole.Assistant, text) { ModelId = model ?? _model };
        // What was sent, so the call log can show it: the effort can be dropped for a model that rejects it.
        var metadata = new Dictionary<string, object?> { ["ReasoningEffort"] = sentEffort.Length > 0 ? sentEffort : null };
        if (usage != null) metadata["Usage"] = usage;
        content.Metadata = metadata;
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
    public static string BuildRequestBody(string model, ChatHistory history, PromptExecutionSettings? settings, string reasoningEffort = "")
    {
        var messages = new JsonArray();
        foreach (var m in history)
            messages.Add(new JsonObject { ["role"] = m.Role.Label, ["content"] = m.Content ?? "" });

        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["stream"] = false };
        if (settings is MistralAIPromptExecutionSettings mistral && mistral.Temperature is double t) body["temperature"] = t;
        else if (settings?.ExtensionData?.TryGetValue("temperature", out var temp) == true && temp is double t2) body["temperature"] = t2;
        // top_p as in the settings (1 by default), which is what the Semantic Kernel connector always sent. Mistral answers
        // 400 "top_p must be 1 when using greedy sampling" to temperature 0 without it, and every decision runs at 0.
        if (settings is MistralAIPromptExecutionSettings withTopP)
            body["top_p"] = withTopP.TopP;

        if (settings?.ExtensionData?.ContainsKey("response_format") == true)
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (!string.IsNullOrWhiteSpace(reasoningEffort)) body["reasoning_effort"] = reasoningEffort.Trim();
        return body.ToJsonString();
    }

    /// <summary>
    /// The server's own reason for an error, for the log and the ledger (": temperature must be ..."), or empty. Only the
    /// "message" or "detail" text of a JSON error body is used, cut to 200 characters, so a page of HTML never lands there.
    /// Public for unit tests.
    /// </summary>
    public static string ErrorDetail(string body)
    {
        string? text = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("message", out var m)) text = m.ValueKind == JsonValueKind.String ? m.GetString() : m.GetRawText();
                else if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String) text = em.GetString();
                else if (root.TryGetProperty("detail", out var d)) text = d.ValueKind == JsonValueKind.String ? d.GetString() : d.GetRawText();
            }
        }
        catch (JsonException)
        {
            text = null; // not JSON: the raw body is used below
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim().TrimEnd('.');
            return ": " + (text.Length > 200 ? text[..200] + "..." : text);
        }
        // Not a JSON error body (a proxy's page, an empty answer): its start, on one line.
        string raw = (body ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return raw.Length == 0 ? " with an empty answer" : ": " + (raw.Length > 200 ? raw[..200] + "..." : raw);
    }

    /// <summary>Whether an error answer says the model does not accept reasoning_effort. Public for unit tests.</summary>
    public static bool RejectsReasoningEffort(System.Net.HttpStatusCode status, string body) =>
        (status is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.UnprocessableEntity) &&
        (body ?? "").Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase);

    /// <summary>Text, model name and usage from a chat completions response. Public for unit tests.</summary>
    public static (string Text, string? Model, string? Usage) ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string text = "";
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
            text = ContentText(c);
        string? model = root.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() : null;
        string? usage = root.TryGetProperty("usage", out var u) ? u.GetRawText() : null;
        return (text, model, usage);
    }

    /// <summary>
    /// The answer text of a message's content: a plain string, or, from a reasoning model, a list of chunks of which
    /// only the "text" chunks are the answer. Thinking chunks are left out, so a JSON object in the model's reasoning
    /// can never be read as its answer. Public for unit tests.
    /// </summary>
    public static string ContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var text = new StringBuilder();
        foreach (var chunk in content.EnumerateArray())
        {
            if (chunk.ValueKind == JsonValueKind.String) { text.Append(chunk.GetString()); continue; }
            if (chunk.ValueKind != JsonValueKind.Object) continue;
            string type = chunk.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "text";
            if (!type.Equals("text", StringComparison.OrdinalIgnoreCase)) continue; // thinking, images, references
            if (chunk.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String) text.Append(value.GetString());
        }
        return text.ToString();
    }
}

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

/// <summary>Builds the chat service for a run from the configured provider.</summary>
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
