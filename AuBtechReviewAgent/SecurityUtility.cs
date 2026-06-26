using System;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

public static class SecurityUtility
{
    /// <summary>
    /// Sanitizes text input fields to prevent both Script Injection (XSS) and common Prompt Injection patterns.
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