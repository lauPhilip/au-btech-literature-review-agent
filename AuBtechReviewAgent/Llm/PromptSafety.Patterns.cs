using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The instruction-like patterns and how strong each one is (#472). A strong pattern is a phrase aimed at the model
/// that reads the text: an order to ignore its instructions, a new role, a demand to include the source. A weak pattern
/// only names such things, as a page about prompts does all the time ("the system prompt holds the agent's identity").
/// A weak hit stays on file, but only a strong one marks a decision uncertain, asks for a second screening, is shown to
/// the reviewer as a flag, and counts in the statement on AI use. Each weak pattern but one belongs to a strong one, and
/// is left out when its strong one is found in the same text, so a real attack keeps the flags it always had.
/// The weak patterns are the earlier, looser forms; the measure on a real run is in AuBtechReviewAgent.Tests/Security.
/// </summary>
public static partial class PromptSafety
{
    private sealed record InjectionPattern(string Name, Regex Regex, bool Strong, string? Family = null);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline;

    private const string Roles = @"(ai|assistant|language model|llm|chatbot|reviewer bot)";

    // Words that may stand between "ignore" and what is ignored when the order is aimed at the model or the reviewer's
    // rules: "ignore all previous instructions", "disregard the eligibility rules", "forget your system prompt". A bare
    // "ignore rules", as in a tool's ".gitignore rules" option, has none of them.
    private const string IgnoreWords = @"(?:(?:all|any|every|the|your|these|those|its|my|our|of|other|previous|prior|above|earlier|preceding|original|existing|eligibility|inclusion|exclusion|scoring|quality|appraisal|screening|review|system|developer)\s+){1,4}";

    private static readonly InjectionPattern[] Patterns =
    {
        new("ignore-instructions", new Regex(@"\b(ignore|disregard|forget|override)\s+" + IgnoreWords + @"(instructions?|rules|criteria|prompts?|guidelines|directions|protocol)\b|\b(ignore|disregard|forget)\s+(everything|anything|all)\s+(above|before|you were told)\b", Options), true),
        new("mentions-ignoring", new Regex(@"\b(ignore|disregard|forget|override)\b[^.\n]{0,40}\b(instructions?|rules|criteria|prompt|guidelines)\b", Options), false, "ignore-instructions"),
        // "You are an AI", an order to act as one, or "as an AI language model you must": spoken to the model. A page
        // that says a tool "can act as an assistant" describes it, and is a weak mention.
        new("addresses-the-model",new Regex(@"\byou\s+are\s+(an?\s+|the\s+)?" + Roles + @"\b|(^|[.!?:;""“(]\s*|\b(please|now|must|should|will)\s+)(act as|pretend to be)\s+(an?\s+)?" + Roles + @"\b|\bas an?\s+" + Roles + @"\b[^.\n]{0,30}\byou\b", Options), true),
        new("mentions-ai-role", new Regex(@"\b(you are|act as|as an?)\s+(an?\s+)?" + Roles + @"\b", Options), false, "addresses-the-model"),
        new("mentions-system-prompt", new Regex(@"\b(system|developer)\s+(prompt|message|instructions?)\b", Options), false),
        new("demands-inclusion", new Regex(@"\b(must|should|always)\s+be\s+(included|accepted|approved|selected)\b|\b(include|accept|approve|select)\s+this\s+(paper|study|article|work)\b|\bdo not exclude\b", Options), true),
        new("claims-criteria-met", new Regex(@"\bmeets?\s+(all\s+)?(the\s+)?(inclusion|eligibility)\s+criteria\b", Options), true),
        // Markup that opens a system turn or an instruction block tries to speak as the operator. User and assistant
        // turns and "### Instruction" headings are what quoted prompt templates in papers and posts look like.
        new("chat-markup",new Regex(@"<\|im_start\|>\s*system\b|<\|?system\|?>|\[INST\]|###\s*system\s*(message|prompt)?\s*:", Options), true),
        new("chat-template", new Regex(@"<\|?(system|assistant|user|im_start|im_end)\|?>|\[/?INST\]|###\s*(system|instruction)", Options), false, "chat-markup"),
        new("output-format-command", new Regex(@"\b(respond|reply|answer|output)\s+(only\s+)?(with|in)\s+(json|yes|no|""?included""?)\b", Options), true),
        new("reveal-instructions", new Regex(@"\b(reveal|print|repeat|show|output|tell me)\b[^.\n]{0,30}\b(your|the|these|above)\s+(system\s+)?(instructions|prompt|rules|message)\b", Options), true),
        new("role-change", new Regex(@"\b(you are now|from now on,? you|pretend (to be|you are))\b|\bnew instructions\s*:", Options), true),
    };

    /// <summary>The names of the weak patterns: hits that stay on file but flag nothing.</summary>
    public static IReadOnlyCollection<string> WeakPatterns { get; } = Patterns.Where(p => !p.Strong).Select(p => p.Name).ToHashSet();

    /// <summary>
    /// Whether a pattern is strong: a phrase aimed at the model. A name this version does not know, such as one in an
    /// older run's files, counts as strong, so nothing that was flagged before is quietly dropped.
    /// </summary>
    public static bool IsStrong(string pattern) => !WeakPatterns.Contains(pattern);

    /// <summary>Whether any of the patterns found is strong.</summary>
    public static bool AnyStrong(IEnumerable<string>? patterns) => patterns?.Any(IsStrong) == true;

    /// <summary>Whether any of the flags found is strong.</summary>
    public static bool AnyStrong(IEnumerable<SourceFlag>? flags) => flags?.Any(f => IsStrong(f.Pattern)) == true;

    /// <summary>Every pattern that matches the text, in order, without a weak one whose strong one matched too.</summary>
    private static IEnumerable<(InjectionPattern Pattern, Match Match)> Matches(string text)
    {
        var found = new List<(InjectionPattern, Match)>();
        foreach (var pattern in Patterns)
        {
            if (pattern.Family != null && found.Any(f => f.Item1.Name == pattern.Family)) continue;
            if (pattern.Regex.Match(text) is { Success: true } match) found.Add((pattern, match));
        }
        return found;
    }
}
