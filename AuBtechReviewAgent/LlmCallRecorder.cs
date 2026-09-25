using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>One call to the language model, as written to llm-calls.json.</summary>
public class LlmCallRecord
{
    public int Sequence { get; set; }
    public string Stage { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public long DurationMs { get; set; }
    public string ModelRequested { get; set; } = "";
    public string? ModelReported { get; set; }
    public double? Temperature { get; set; }
    public int Attempts { get; set; }
    public string Outcome { get; set; } = "";
    public string PromptSha256 { get; set; } = "";
    public int PromptChars { get; set; }
    public string? ResponseSha256 { get; set; }
    public int ResponseChars { get; set; }
    public string? Usage { get; set; }
}

/// <summary>What a run was produced with, so it can be described and repeated (stored in the run ledger).</summary>
public class RunSettingsRecord
{
    public string Model { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public Dictionary<string, double> StageTemperatures { get; set; } = new();
    public int LlmCalls { get; set; }
    public int LlmRetries { get; set; }
    public int LlmFailures { get; set; }

    /// <summary>Hash over the hashes of every prompt sent, in order. Two runs with the same value sent identical prompts.</summary>
    public string PromptSequenceSha256 { get; set; } = "";
}

/// <summary>
/// Labels the model calls made inside a using-block with a pipeline stage ("screening", "outline", ...),
/// so the call log can say which stage every call belonged to.
/// </summary>
public static class LlmStage
{
    private static readonly AsyncLocal<string?> Current = new();
    public static string Name => Current.Value ?? "unlabelled";

    public static IDisposable Begin(string stage)
    {
        string? previous = Current.Value;
        Current.Value = stage;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly string? _previous;
        public Restore(string? previous) => _previous = previous;
        public void Dispose() => Current.Value = _previous;
    }
}

/// <summary>
/// Wraps the Mistral chat service for one run. It (1) retries rate-limit (429) and temporary server errors
/// with exponential backoff, so a busy moment does not break a run halfway, and (2) records every call:
/// stage, model, temperature, duration, token usage and SHA-256 hashes of the prompt and the response.
/// Prompts themselves are not stored (they contain whole paper excerpts), only their fingerprints.
/// </summary>
public class RecordingChatCompletionService : IChatCompletionService
{
    private readonly IChatCompletionService _inner;
    private readonly string _model;
    private readonly int _maxAttempts;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly List<LlmCallRecord> _calls = new();
    private readonly object _lock = new();

    public RecordingChatCompletionService(IChatCompletionService inner, string model, int maxAttempts = 4, Func<int, TimeSpan>? backoff = null)
    {
        _inner = inner;
        _model = model;
        _maxAttempts = Math.Max(1, maxAttempts);
        _backoff = backoff ?? (attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)));
    }

    public IReadOnlyDictionary<string, object?> Attributes => _inner.Attributes;

    public IReadOnlyList<LlmCallRecord> Calls
    {
        get { lock (_lock) return _calls.ToList(); }
    }

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        string prompt = string.Join("\n", chatHistory.Select(m => m.Content));
        var record = new LlmCallRecord
        {
            Stage = LlmStage.Name,
            StartedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff UTC"),
            ModelRequested = _model,
            Temperature = ReadTemperature(executionSettings),
            PromptSha256 = Sha256(prompt),
            PromptChars = prompt.Length,
        };
        var stopwatch = Stopwatch.StartNew();

        for (int attempt = 1; ; attempt++)
        {
            record.Attempts = attempt;
            try
            {
                var result = await _inner.GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken).ConfigureAwait(false);
                string response = string.Join("\n", result.Select(r => r.Content));
                record.Outcome = "ok";
                record.ResponseSha256 = Sha256(response);
                record.ResponseChars = response.Length;
                record.ModelReported = result.FirstOrDefault()?.ModelId;
                record.Usage = ReadUsage(result.FirstOrDefault());
                Finish(record, stopwatch);
                return result;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < _maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                record.Outcome = $"failed: {ex.GetType().Name}";
                Finish(record, stopwatch);
                throw;
            }
        }
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default) =>
        _inner.GetStreamingChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);

    public RunSettingsRecord Summarize(string appVersion)
    {
        var calls = Calls;
        var settings = new RunSettingsRecord
        {
            Model = _model,
            AppVersion = appVersion,
            LlmCalls = calls.Count,
            LlmRetries = calls.Sum(c => Math.Max(0, c.Attempts - 1)),
            LlmFailures = calls.Count(c => c.Outcome != "ok"),
            PromptSequenceSha256 = Sha256(string.Join("|", calls.Select(c => c.PromptSha256))),
        };
        foreach (var group in calls.Where(c => c.Temperature.HasValue).GroupBy(c => c.Stage))
            settings.StageTemperatures[group.Key] = group.First().Temperature!.Value;
        return settings;
    }

    /// <summary>429 (rate limit), 408, 5xx and network timeouts are worth retrying; everything else is not.</summary>
    public static bool IsTransient(Exception ex)
    {
        HttpStatusCode? status = ex switch
        {
            HttpOperationException hoe => hoe.StatusCode,
            System.Net.Http.HttpRequestException hre => hre.StatusCode,
            _ => null
        };
        if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout) return true;
        if (status is { } s && (int)s >= 500) return true;
        if (status == null && ex is System.Net.Http.HttpRequestException or TaskCanceledException { InnerException: TimeoutException }) return true;
        return ex.InnerException != null && IsTransient(ex.InnerException);
    }

    public static string AppVersion =>
        typeof(RecordingChatCompletionService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(RecordingChatCompletionService).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private void Finish(LlmCallRecord record, Stopwatch stopwatch)
    {
        record.DurationMs = stopwatch.ElapsedMilliseconds;
        lock (_lock)
        {
            record.Sequence = _calls.Count + 1;
            _calls.Add(record);
        }
    }

    private static double? ReadTemperature(PromptExecutionSettings? settings)
    {
        if (settings == null) return null;
        // MistralAIPromptExecutionSettings.Temperature; read by name so this class does not depend on the connector type.
        var prop = settings.GetType().GetProperty("Temperature");
        return prop?.GetValue(settings) switch
        {
            double d => d,
            float f => f,
            _ => null
        };
    }

    private static string? ReadUsage(ChatMessageContent? content)
    {
        if (content?.Metadata == null || !content.Metadata.TryGetValue("Usage", out var usage) || usage == null) return null;
        try { return JsonSerializer.Serialize(usage); } catch { return usage.ToString(); }
    }

    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
