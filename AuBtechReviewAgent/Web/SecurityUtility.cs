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
    /// True for the values appsettings.json ships with ("YOUR_MISTRAL_API_KEY_PLACEHOLDER" and the like).
    /// Matches only that pattern, so a real key that happens to contain the word is not rejected.
    /// </summary>
    public static bool IsPlaceholderKey(string? value) =>
        value != null && Regex.IsMatch(value.Trim(), @"^YOUR_.*_PLACEHOLDER$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The key the server was configured with, cleaned, or empty when none is usable: nothing set, the
    /// shipped placeholder, or a value that is empty after <see cref="SanitizeApiKey"/>. Program.cs refuses to
    /// start on an empty Mistral key and treats an empty optional key as "source not configured".
    /// </summary>
    public static string ConfiguredServerKey(string? configured) =>
        IsPlaceholderKey(configured) ? string.Empty : SanitizeApiKey(configured);
}
