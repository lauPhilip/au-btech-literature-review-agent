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
///     overrides, tag characters; <see cref="HiddenText"/>) that can hide text from a reader but not from the model, defuse the markers the prompts
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
    public const int MaxResearchQuestion = 300;
    /// <summary>Research questions a review may state (published reviews state a median of three).</summary>
    public const int MaxResearchQuestions = 4;

    /// <summary>The research questions in a text box: one per line, numbering such as "RQ1:" or "1." left out, blank lines dropped.</summary>
    public static List<string> ResearchQuestionLines(string? text) => (text ?? "")
        .Split('\n')
        .Select(line => Regex.Replace(line.Trim(), @"^(RQ\s*\d+|\d+)\s*[.:)\-–]\s*", "", RegexOptions.IgnoreCase).Trim())
        .Where(line => line.Length > 0)
        .ToList();

    // Patterns that are ordinary in eligibility criteria and would flag almost every honest input.
    private static readonly HashSet<string> NotFlaggedForReviewers = new() { "demands-inclusion", "claims-criteria-met" };

    /// <summary>Normalises one field: NFKC, no invisible or control characters, no prompt markers, tidy whitespace.</summary>
    public static string Normalize(string? text, bool singleLine) => Clean(text, singleLine).Text;

    /// <summary>
    /// Normalises one field as <see cref="Normalize"/> does, and also returns what the removed tag characters spelled
    /// (<see cref="CleanedText.Hidden"/>), for the scan. Invisible characters are removed by <see cref="HiddenText"/>, the
    /// same way as in source text, so tag characters (U+E0000 to U+E007F) go too, while a joiner between two characters
    /// outside ASCII (Persian, emoji sequences) stays.
    /// </summary>
    public static CleanedText Clean(string? text, bool singleLine)
    {
        if (string.IsNullOrEmpty(text)) return new CleanedText("", 0, 0, "");
        // Control and format characters that HiddenText does not count go first, so its joiner rule judges the text as it ends up (#623).
        var visible = HiddenText.RemoveInvisible(WithoutOtherInvisible(WithoutInvalidCodePoints(text).Normalize(NormalizationForm.FormKC)));
        var sb = new StringBuilder(visible.Text.Length);
        foreach (char c in visible.Text)
        {
            if (c is '\n' or '\t') { sb.Append(singleLine ? ' ' : c); continue; }
            if (c != '\r') sb.Append(c);
        }
        // Taking characters out can leave a base letter beside a combining mark it was not next to before, so the text is normalised again.
        string s = sb.ToString().Normalize(NormalizationForm.FormKC)
            .Replace("<<<", "‹‹‹", StringComparison.Ordinal)
            .Replace(">>>", "›››", StringComparison.Ordinal);
        s = Regex.Replace(s, @"[ \t]{2,}", " ");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        s = TrimEdges(s, out int trimmedJoiners);
        return visible with { Text = s, InvisibleCharacters = visible.InvisibleCharacters + trimmedJoiners };
    }

    /// <summary>
    /// The text without the control and format characters a reader never sees, other than line breaks and tabs and the
    /// ones <see cref="HiddenText.RemoveInvisible"/> counts (invisible characters, tag characters). Astral format characters
    /// count too, so it goes by code point, not by UTF-16 unit; the text must have no lone surrogates.
    /// </summary>
    private static string WithoutOtherInvisible(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            bool counted = rune.Value <= char.MaxValue && HiddenText.IsInvisible((char)rune.Value) || rune.Value is >= 0xE0000 and <= 0xE007F;
            if (category is UnicodeCategory.Control or UnicodeCategory.Format && !counted && rune.Value is not ('\n' or '\t' or '\r' or 0x200C or 0x200D)) continue;
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Trims whitespace like <see cref="string.Trim()"/> and joiners with it, since a joiner at an edge joins nothing and
    /// would be left there once the whitespace beside it is gone (#623). The joiners it drops are counted.
    /// </summary>
    private static string TrimEdges(string text, out int joiners)
    {
        static bool Edge(char c) => char.IsWhiteSpace(c) || c is '\u200C' or '\u200D';
        int start = 0, end = text.Length;
        while (start < end && Edge(text[start])) start++;
        while (end > start && Edge(text[end - 1])) end--;
        joiners = text[..start].Count(c => c is '\u200C' or '\u200D') + text[end..].Count(c => c is '\u200C' or '\u200D');
        return text[start..end];
    }

    /// <summary>
    /// The text without what <see cref="string.Normalize(NormalizationForm)"/> throws on (#485, #623): lone surrogates (half of
    /// a surrogate pair, from a broken paste or a crafted request), which carry no character, and U+FFFE, which is no
    /// character either. Whole pairs stay.
    /// </summary>
    public static string WithoutInvalidCodePoints(string text)
    {
        if (!text.Any(c => char.IsSurrogate(c) || c == '\uFFFE')) return text;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(c).Append(text[++i]); continue; }
            if (!char.IsSurrogate(c) && c != '\uFFFE') sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Cleans the request's text fields and reports length errors and instruction-like phrases.</summary>
    public static InputCheck Check(ReviewRequest request)
    {
        var errors = new List<string>();
        var flags = new List<InputFlag>();

        string Field(string name, string? value, int max, bool singleLine, bool required)
        {
            var clean = Clean(value, singleLine);
            if (required && clean.Text.Length == 0) errors.Add($"{name} is empty.");
            if (clean.Text.Length > max) errors.Add($"{name} is {clean.Text.Length} characters long; the limit is {max}.");
            flags.AddRange(FlagsIn(name, clean.Text, clean.Hidden));
            return clean.Text;
        }

        var cleaned = request with
        {
            Query = Field("Search query", request.Query, MaxQuery, singleLine: true, required: true),
            Objective = Field("Review objective", request.Objective, MaxObjective, singleLine: false, required: false),
            Inclusion = Field("Inclusion criteria", request.Inclusion, MaxCriteria, singleLine: false, required: true),
            Exclusion = Field("Exclusion criteria", request.Exclusion, MaxCriteria, singleLine: false, required: false),
            SynthesisDirective = Field("Artifact target", request.SynthesisDirective, MaxDirective, singleLine: false, required: false),
        };
        // Only review methods this engine supports can be run; "coming soon" methods are shown but refused here too,
        // and so is a card of another module (a grey literature review runs on its own pages, not here).
        if (ReviewMethods.Find(request.Method) is not { } method || !method.Available)
            errors.Add($"The review type \"{request.Method}\" is not available yet.");
        else if (!ReviewMethods.RunsHere(method))
            errors.Add($"The review type \"{method.Name}\" is started from its own page, not from this form.");

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

        if (request.ResearchQuestions != null)
        {
            var questions = request.ResearchQuestions
                .Select((q, i) => Field($"Research question {i + 1}", q, MaxResearchQuestion, singleLine: true, required: false))
                .Where(q => q.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (questions.Count > MaxResearchQuestions) errors.Add($"Research questions: there are {questions.Count}; the limit is {MaxResearchQuestions}.");
            cleaned = cleaned with { ResearchQuestions = questions };
        }
        return new InputCheck(cleaned, errors, flags);
    }

    /// <summary>
    /// The instruction-like phrases in one cleaned field, with the same exceptions as the review form, so every form
    /// that sends a reviewer's text to the model flags the same things. Only strong phrases count (#472): a topic such as
    /// "system prompts in agents" names prompts and is not a request to the model. <paramref name="hidden"/> is what the field's
    /// tag characters spelled (<see cref="Clean"/>); when it reads like instructions, one <see cref="HiddenText.FlagPattern"/>
    /// flag is added last, with what it spells marked as hidden, since the reviewer cannot see it in the field.
    /// </summary>
    public static IReadOnlyList<InputFlag> FlagsIn(string field, string clean, string hidden = "")
    {
        var flags = PromptSafety.Scan(clean).Where(p => PromptSafety.IsStrong(p) && !NotFlaggedForReviewers.Contains(p) && p != HiddenText.FlagPattern)
            .Select(pattern => new InputFlag(field, pattern, ExcerptFor(pattern, clean))).ToList();
        if (PromptSafety.Find(hidden)?.FirstOrDefault(f => PromptSafety.IsStrong(f.Pattern)) is { } found)
            flags.Add(new InputFlag(field, HiddenText.FlagPattern, HiddenMark + found.Excerpt));
        return flags;
    }

    /// <summary>Starts the excerpt of a <see cref="HiddenText.FlagPattern"/> flag: the words after it were invisible in the field.</summary>
    public const string HiddenMark = "hidden, not shown in the field: ";

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
        "mentions-ignoring" => "mentions ignoring rules or instructions",
        "mentions-ai-role" => "mentions an AI or assistant role",
        "chat-markup" => "contains chat markup that opens a system turn",
        "chat-template" => "contains chat-template markup",
        "output-format-command" => "tells the model how to answer",
        "reveal-instructions" => "asks the model to reveal its instructions",
        "role-change" => "tries to give the model new instructions",
        "demands-inclusion" => "demands that it be included",
        "claims-criteria-met" => "claims to meet the inclusion criteria",
        HiddenText.FlagPattern => "hides text from the reader that reads like instructions",
        _ => pattern,
    };
}
