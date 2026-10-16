using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

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

    /// <summary>Settings for a prompt that must answer with a JSON object.</summary>
    public static MistralAIPromptExecutionSettings JsonMode(double temperature)
    {
        var settings = new MistralAIPromptExecutionSettings { Temperature = temperature };
        settings.ExtensionData ??= new Dictionary<string, object>();
        settings.ExtensionData["response_format"] = new { type = "json_object" };
        return settings;
    }

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
    /// <item>A backslash that does not start a JSON escape (a path or regex the model copied from a page, seen
    /// in a live run: "'a' is an invalid escapable character") is kept as a literal backslash.</item>
    /// <item>A quotation mark inside a string that cannot end it (it is followed by a word, not by a comma, colon or
    /// bracket) is kept as an escaped quotation mark, which is what the model meant.</item>
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
                    case '\\':
                        if (IsEscape(text, i + 1)) { escaped = true; result.Append(c); }
                        else result.Append("\\\\"); // a lone backslash copied from a page: keep it as a backslash
                        break;
                    case '"':
                        if (ClosesString(text, i + 1)) { inString = false; result.Append(c); }
                        else result.Append("\\\""); // a quotation mark inside the text: keep it as one
                        break;
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

    /// <summary>
    /// Whether a quotation mark that ends just before text[at] can close a JSON string. In valid JSON a closing mark is
    /// followed by a comma, a colon, a closing bracket or the end; after a comma or colon comes a value or a name. Any
    /// other mark is a quotation inside the text that the model did not escape (seen in a live run: the "lost in the
    /// middle" effect broke a whole section with "'a' is invalid after a value"), so it is kept as part of the text.
    /// Valid JSON is never changed by this.
    /// </summary>
    private static bool ClosesString(string text, int at)
    {
        int i = SkipSpace(text, at);
        if (i >= text.Length) return true;
        char c = text[i];
        if (c is '}' or ']') return true;
        if (c is not (',' or ':')) return false;
        i = SkipSpace(text, i + 1);
        if (i >= text.Length) return true;
        char next = text[i];
        if (next is '"' or '{' or '[' or '-' || char.IsAsciiDigit(next)) return true;
        return StartsWord(text, i, "true") || StartsWord(text, i, "false") || StartsWord(text, i, "null");
    }

    private static int SkipSpace(string text, int at)
    {
        while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        return at;
    }

    private static bool StartsWord(string text, int at, string word) =>
        string.CompareOrdinal(text, at, word, 0, word.Length) == 0 &&
        (at + word.Length >= text.Length || !char.IsLetterOrDigit(text[at + word.Length]));

    /// <summary>Whether text[at] starts a valid JSON escape after a backslash: one of "\/bfnrt, or u and four hex digits.</summary>
    private static bool IsEscape(string text, int at)
    {
        if (at >= text.Length) return false;
        char c = text[at];
        if (c is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't') return true;
        if (c != 'u' || at + 4 >= text.Length) return false;
        for (int k = 1; k <= 4; k++) if (!Uri.IsHexDigit(text[at + k])) return false;
        return true;
    }

    public static bool OneOf(string? value, params string[] allowed) =>
        value != null && Array.Exists(allowed, a => a.Equals(value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The model's answer could not be turned into a valid structured result, even after a repair attempt.</summary>
public class LlmOutputException : Exception
{
    public LlmOutputException(string problem) : base($"The model's answer failed validation: {problem}") { }
}
