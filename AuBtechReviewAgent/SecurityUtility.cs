using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

public static class SecurityUtility
{
    /// <summary>
    /// Cleans a pasted API key: removes whitespace and invisible characters. Returns empty when what is left
    /// cannot be a key (anything other than letters, digits and . _ - :), so a mistyped or malicious value
    /// is never sent to a provider or stored in the browser.
    /// </summary>
    public static string SanitizeApiKey(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string key = new string(input.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c) &&
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
        return key.Length <= 512 && Regex.IsMatch(key, @"^[A-Za-z0-9._\-:]+$") ? key : string.Empty;
    }

    /// <summary>
    /// Strips HTML tags and a few known prompt-injection phrases. No longer used for the review form: it
    /// rewrote the reviewer's criteria silently (and "&lt; 5 years" lost its text), so the form now goes
    /// through <see cref="ReviewInputGuard"/>, which flags instead of rewriting. Kept for other callers.
    /// </summary>
    public static string SanitizeInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string clean = input;

        // 1. Script Injection Countermeasure: Strip out all HTML tags and script elements
        clean = Regex.Replace(clean, @"<[^>]*>", string.Empty);

        // 2. Prompt Injection Countermeasure: Neutralize adversarial directive overrides
        string[] maliciousPhrases = new[] 
        {
            "ignore previous instructions",
            "ignore above instructions",
            "system override",
            "you are now an adversarial",
            "forget your goals",
            "stop executing the systematic review"
        };

        foreach (var phrase in maliciousPhrases)
        {
            clean = Regex.Replace(clean, phrase, "[DIRECTIVE_REMOVED_FOR_COMPLIANCE]", RegexOptions.IgnoreCase);
        }

        return clean.Trim();
    }
}