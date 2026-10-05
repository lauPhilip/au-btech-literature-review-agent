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

    /// <summary>
    /// The first complete {...} object in a reply, without code fences or surrounding prose, made parseable:
    /// <list type="bullet">
    /// <item>Models often put real line breaks and tabs inside JSON strings (long prose fields), which JSON does
    /// not allow ("'0x0A' is invalid within a JSON string"). Inside strings they are written as \n, \t etc.,
    /// which is exactly what the model meant, so nothing is guessed.</item>
    /// <item>The object ends at its matching closing brace (braces inside strings do not count), so a second
    /// object or text after the first is ignored ("'{' is invalid after a single JSON value").</item>
    /// </list>
    /// When the braces never balance (a cut-off answer), the text up to the last "}" is returned and the
    /// parser reports the problem as before.
    /// </summary>
    public static string ExtractObject(string raw)
    {
        string text = raw ?? "";
        int start = text.IndexOf('{');
        if (start < 0) return text.Trim();

        var result = new System.Text.StringBuilder(text.Length - start + 16);
        int depth = 0;
        bool inString = false, escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) { escaped = false; result.Append(c); continue; }
                switch (c)
                {
                    case '\\': escaped = true; result.Append(c); break;
                    case '"': inString = false; result.Append(c); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (c < ' ') result.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                        else result.Append(c);
                        break;
                }
                continue;
            }
            result.Append(c);
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return result.ToString();
        }

        // Unbalanced: keep the old behaviour (up to the last brace) so the error message stays meaningful.
        string escapedText = result.ToString();
        int end = escapedText.LastIndexOf('}');
        return end > 0 ? escapedText.Substring(0, end + 1) : escapedText;
    }

    public static bool OneOf(string? value, params string[] allowed) =>
        value != null && Array.Exists(allowed, a => a.Equals(value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The model's answer could not be turned into a valid structured result, even after a repair attempt.</summary>
public class LlmOutputException : Exception
{
    public LlmOutputException(string problem) : base($"The model's answer failed validation: {problem}") { }
}
