using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>An instruction-like phrase found in one of the reviewer's input fields.</summary>
public record InputFlag(string Field, string Pattern, string Excerpt)
{
    public override string ToString() => $"{Field}: {ReviewInputGuard.Describe(Pattern)} (\"{Excerpt}\")";
}

/// <summary>The cleaned request, plus what is wrong with it (errors) and what looks suspicious (flags).</summary>
public sealed record InputCheck(ReviewRequest Request, IReadOnlyList<string> Errors, IReadOnlyList<InputFlag> Flags)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Checks what a reviewer types into the configuration panel before it goes into any prompt. The query,
/// objective and criteria are pasted into the screening, search and report prompts, so on a public
/// deployment they are a way to steer the model (or to use the server's key for something else). The
/// guard works in four steps, and none of them rewrites the reviewer's words silently:
///  1. normalise: Unicode NFKC, remove invisible and control characters (zero-width, bidirectional
///     overrides) that can hide text from a reader but not from the model, defuse the markers the prompts
///     use for third-party text, and tidy whitespace;
///  2. limit the length of every field (an error, not a silent cut);
///  3. scan for instruction-like phrases with the same patterns used for paper text, minus the ones that
///     are normal in eligibility criteria ("should be included if ..."); a hit is a flag the reviewer
///     must confirm, and it is written to protocol.md and the ledger;
///  4. the prompts carry <see cref="PromptSafety.ReviewerInputNotice"/>, and every model answer is still
///     checked against a fixed structure in code, which is what actually bounds what a prompt can do.
/// </summary>
public static class ReviewInputGuard
{
    public const int MaxQuery = 300;
    public const int MaxObjective = 1500;
    public const int MaxCriteria = 2500;
    public const int MaxDirective = 1500;
    public const int MaxSearchString = 200;
    /// <summary>Extra search strings besides the primary query.</summary>
    public const int MaxSearchStrings = 4;

    // Patterns that are ordinary in eligibility criteria and would flag almost every honest input.
    private static readonly HashSet<string> NotFlaggedForReviewers = new() { "demands-inclusion", "claims-criteria-met" };

    /// <summary>Normalises one field: NFKC, no invisible or control characters, no prompt markers, tidy whitespace.</summary>
    public static string Normalize(string? text, bool singleLine)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string s = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '\n' or '\t') { sb.Append(singleLine ? ' ' : c); continue; }
            if (c == '\r') continue;
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            // Control characters and format characters (zero-width space and joiners, bidirectional
            // overrides, BOM) are invisible on screen but visible to the model.
            if (category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            sb.Append(c);
        }
        s = sb.ToString()
            .Replace("<<<", "‹‹‹", StringComparison.Ordinal)
            .Replace(">>>", "›››", StringComparison.Ordinal);
        s = Regex.Replace(s, @"[ \t]{2,}", " ");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>Cleans the request's text fields and reports length errors and instruction-like phrases.</summary>
    public static InputCheck Check(ReviewRequest request)
    {
        var errors = new List<string>();
        var flags = new List<InputFlag>();

        string Field(string name, string? value, int max, bool singleLine, bool required)
        {
            string clean = Normalize(value, singleLine);
            if (required && clean.Length == 0) errors.Add($"{name} is empty.");
            if (clean.Length > max) errors.Add($"{name} is {clean.Length} characters long; the limit is {max}.");
            foreach (var pattern in PromptSafety.Scan(clean).Where(p => !NotFlaggedForReviewers.Contains(p)))
                flags.Add(new InputFlag(name, pattern, ExcerptFor(pattern, clean)));
            return clean;
        }

        var cleaned = request with
        {
            Query = Field("Search query", request.Query, MaxQuery, singleLine: true, required: true),
            Objective = Field("Review objective", request.Objective, MaxObjective, singleLine: false, required: false),
            Inclusion = Field("Inclusion criteria", request.Inclusion, MaxCriteria, singleLine: false, required: true),
            Exclusion = Field("Exclusion criteria", request.Exclusion, MaxCriteria, singleLine: false, required: false),
            SynthesisDirective = Field("Artifact target", request.SynthesisDirective, MaxDirective, singleLine: false, required: false),
        };
        // Only review methods the engine supports can be run; "coming soon" methods are shown but refused here too.
        if (ReviewMethods.Find(request.Method) is not { Available: true })
            errors.Add($"The review type \"{request.Method}\" is not available yet.");

        if (request.SearchStrings != null)
        {
            var strings = request.SearchStrings
                .Select((s, i) => Field($"Search string {i + 2}", s, MaxSearchString, singleLine: true, required: false))
                .Where(s => s.Length > 0 && !s.Equals(cleaned.Query, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (strings.Count > MaxSearchStrings) errors.Add($"There are {strings.Count} extra search strings; the limit is {MaxSearchStrings}.");
            cleaned = cleaned with { SearchStrings = strings };
        }
        return new InputCheck(cleaned, errors, flags);
    }

    /// <summary>The matched phrase with a little context, for the warning and the protocol.</summary>
    private static string ExcerptFor(string pattern, string text)
    {
        var match = PromptSafety.FirstMatch(pattern, text);
        if (match == null) return "";
        int start = Math.Max(0, match.Index - 20), end = Math.Min(text.Length, match.Index + match.Length + 20);
        return (start > 0 ? "…" : "") + text[start..end].Replace('\n', ' ') + (end < text.Length ? "…" : "");
    }

    /// <summary>A plain description of a scan pattern for the reviewer.</summary>
    public static string Describe(string pattern) => pattern switch
    {
        "ignore-instructions" => "asks to ignore instructions or rules",
        "addresses-the-model" => "addresses the model or gives it a role",
        "mentions-system-prompt" => "mentions the system or developer prompt",
        "chat-markup" => "contains chat-template markup",
        "output-format-command" => "tells the model how to answer",
        "reveal-instructions" => "asks the model to reveal its instructions",
        "role-change" => "tries to give the model new instructions",
        _ => pattern,
    };
}
