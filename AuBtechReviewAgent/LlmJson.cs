using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// Structured model output with validation. The answer is parsed into a typed record and checked by a
/// validator; if it is malformed or fails the check, the model is asked once more with the exact problem
/// ("decision must be Included or Excluded"). Replaces ad-hoc regex rescue of half-broken JSON, which could
/// silently turn a garbled answer into "Excluded".
/// </summary>
public static class LlmJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static async Task<T> GetAsync<T>(
        IChatCompletionService chat, string prompt, PromptExecutionSettings? settings,
        Func<T, string?> validate, int repairAttempts = 1) where T : class
    {
        string currentPrompt = prompt;
        string lastProblem = "";
        for (int attempt = 0; attempt <= repairAttempts; attempt++)
        {
            var response = await chat.GetChatMessageContentAsync(currentPrompt, settings);
            string raw = response.ToString();
            T? value = null;
            try
            {
                value = JsonSerializer.Deserialize<T>(ExtractObject(raw), Options);
                lastProblem = value == null ? "The answer was empty." : validate(value) ?? "";
            }
            catch (JsonException ex)
            {
                lastProblem = $"The answer was not valid JSON ({ex.Message.Split('.')[0]}).";
            }
            if (value != null && lastProblem.Length == 0) return value;

            currentPrompt = prompt + "\n\nYOUR PREVIOUS ANSWER WAS REJECTED: " + lastProblem +
                            "\nAnswer again with only the JSON object in the required structure.";
        }
        throw new LlmOutputException(lastProblem);
    }

    /// <summary>The first {...} object in a reply, without code fences or surrounding prose.</summary>
    public static string ExtractObject(string raw)
    {
        string text = (raw ?? "").Replace("```json", "").Replace("```", "").Trim();
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text.Substring(start, end - start + 1) : text;
    }

    public static bool OneOf(string? value, params string[] allowed) =>
        value != null && Array.Exists(allowed, a => a.Equals(value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The model's answer could not be turned into a valid structured result, even after a repair attempt.</summary>
public class LlmOutputException : Exception
{
    public LlmOutputException(string problem) : base($"The model's answer failed validation: {problem}") { }
}
