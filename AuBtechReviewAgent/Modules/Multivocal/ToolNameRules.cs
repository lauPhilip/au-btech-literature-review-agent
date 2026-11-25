using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The rules that turn what the sources name in an open attribute into names that can be counted (#466). In the real
/// run 50e9a8e3 the table of tools counted "Claude Code, OpenCode, Amp, Kiro, Codex, Gemini CLI" as one tool, did not
/// merge "MCP (Model Context Protocol) server" with MCP, and counted GPT-4, Python and RAG as tools. Three rules, in
/// code, so the same extraction always gives the same counts, and every value they touch is on file in the extraction:
/// <list type="number">
/// <item>A value that lists several names is split at commas, semicolons, " and ", " or ", " / ", " + " and " &amp; "
/// outside brackets (<see cref="Split"/>), unless a part reads as a phrase (more than <see cref="MaxNameWords"/> words).</item>
/// <item>For merging, a trailing generic word such as "server", "toolkit", "framework" or "-based" is ignored
/// (<see cref="WithoutGenericTail"/>), so "MCP-based systems" matches MCP; "Playwright MCP server" stays its own tool.</item>
/// <item>For an attribute about tools (<see cref="IsToolAttribute"/>), a value that is a language model, a programming
/// language, a method, a general term or a web address is not counted (<see cref="NotATool"/>), by the short lists below.</item>
/// </list>
/// </summary>
public static class ToolNameRules
{
    /// <summary>The version of these rules, written to the extraction file; raise it when a rule or a list changes.</summary>
    public const string Version = "tool-names-v1";

    /// <summary>A part longer than this reads as a phrase, not a name, so its value is not split.</summary>
    public const int MaxNameWords = 6;

    public const string LanguageModel = "language model", ProgrammingLanguage = "programming language", Method = "method",
        GeneralTerm = "general term", WebAddress = "web address";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly string[] Separators = { ", ", "; ", " and ", " or ", " / ", " + ", " & " };
    private static readonly Regex Filler = new(@"^(etc\.?|and more|and others|others|among others)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex GenericTail = new(@"(?:[\s\-]+(?:based|servers?|toolkits?|frameworks?|librar(?:y|ies)|platforms?|tools?|systems?))+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex TrailingBracket = new(@"\s*\([^()]*\)\s*$", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex ToolWords = new(@"\b(tools?|frameworks?|librar(y|ies)|platforms?|software|products?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex ModelOrLanguageWords = new(@"\b(models?|languages?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    /// <summary>Families of language models, matched as the first word of a name (after a maker's name), with a version glued on or not.</summary>
    private static readonly Regex ModelFamily = new(@"^(gpt|llama|qwen|deepseek|mistral|mixtral|codestral|gemma|gemini|claude|grok|phi|whisper|bert|roberta|clip|t5|falcon|vicuna|o\d)[\d.]*$", RegexOptions.CultureInvariant, Timeout);

    /// <summary>The words that may follow a model family in a model's name: versions, sizes and variants.</summary>
    private static readonly Regex ModelVariant = new(@"^(\d[\w.]*|[a-z]\d[\w.]*|instruct|chat|sonnet|opus|haiku|flash|pro|mini|nano|turbo|small|medium|large|base|vision|embedding|reasoning|fast|latest|preview|models?|continue|pretrained|checkpoints?|chinese|thinking|lite|ultra|plus|max)$", RegexOptions.CultureInvariant, Timeout);

    /// <summary>Makers whose name may come before a model's ("Anthropic Claude", "OpenAI GPT models").</summary>
    public static readonly IReadOnlySet<string> Makers = new HashSet<string>(StringComparer.Ordinal) { "openai", "anthropic", "google", "meta", "mistralai", "alibaba", "microsoft", "xai" };

    /// <summary>Programming and query languages, by their key (letters and digits, lower case).</summary>
    public static readonly IReadOnlySet<string> Languages = new HashSet<string>(StringComparer.Ordinal)
        { "python", "javascript", "typescript", "java", "rust", "golang", "sql", "r", "c", "kotlin", "swift", "ruby", "php", "bash" };

    /// <summary>Methods and techniques, by their key: a way of working, not a tool that supports it.</summary>
    public static readonly IReadOnlySet<string> Methods = new HashSet<string>(StringComparer.Ordinal)
    {
        "rag", "retrievalaugmentedgeneration", "react", "chainofthought", "cot", "mcts", "montecarlotreesearch", "finetuning",
        "promptengineering", "contextengineering", "fewshotprompting", "incontextlearning", "reinforcementlearning", "rlhf",
    };

    /// <summary>General terms, by their key: they name a kind of thing, not one tool.</summary>
    public static readonly IReadOnlySet<string> GeneralTerms = new HashSet<string>(StringComparer.Ordinal)
    {
        "llm", "llms", "largelanguagemodel", "largelanguagemodels", "locallargelanguagemodels", "languagemodel", "languagemodels",
        "ai", "genai", "generativeai", "agent", "agents", "aiagent", "aiagents", "llmagent", "llmagents",
    };

    /// <summary>Whether an open attribute asks for tools, by the words of its name and description (not for models or languages).</summary>
    public static bool IsToolAttribute(MapAttribute attribute) =>
        attribute.Open && ToolWords.IsMatch($"{attribute.Name} {attribute.Description}") && !ModelOrLanguageWords.IsMatch(attribute.Name);

    /// <summary>The names in a value: the value itself, or its parts when it lists several names (rule 1).</summary>
    public static IReadOnlyList<string> Split(string value)
    {
        string text = (value ?? "").Trim();
        var parts = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            string? separator = depth == 0 ? Separators.FirstOrDefault(s => string.CompareOrdinal(text, i, s, 0, s.Length) == 0) : null;
            if (separator != null)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                i += separator.Length - 1;
                continue;
            }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        var names = parts.Select(p => Regex.Replace(p.Trim(), @"^(and|or)\s+", "", RegexOptions.CultureInvariant, Timeout).Trim().TrimEnd('.', ',').Trim())
            .Where(p => p.Length > 0 && !Filler.IsMatch(p)).ToList();
        if (names.Count < 2 || names.Any(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > MaxNameWords)) return new[] { text };
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The name without a trailing generic word (rule 2), for matching only; the name itself when nothing else is left.</summary>
    public static string WithoutGenericTail(string name)
    {
        string stripped = GenericTail.Replace(name ?? "", "").Trim();
        return Key(stripped).Length == 0 ? (name ?? "").Trim() : stripped;
    }

    /// <summary>Why a name is not counted as a tool (rule 3), or null when it is counted.</summary>
    public static string? NotATool(string name)
    {
        string text = (name ?? "").Trim();
        if (Regex.IsMatch(text, @"^(https?://|www\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout)) return WebAddress;
        string bare = TrailingBracket.Replace(text, "").Trim();
        if (bare.Length == 0) bare = text;
        string key = Key(bare);
        if (Languages.Contains(key)) return ProgrammingLanguage;
        if (Methods.Contains(key)) return Method;
        if (GeneralTerms.Contains(key)) return GeneralTerm;
        return IsModel(bare) ? LanguageModel : null;
    }

    /// <summary>A model's name: a family (after a maker's name, if any) followed only by versions, sizes and variants.</summary>
    public static bool IsModel(string name)
    {
        var words = Regex.Split((name ?? "").ToLowerInvariant(), @"[\s\-_:/]+", RegexOptions.CultureInvariant, Timeout).Where(w => w.Length > 0).ToList();
        int at = words.Count > 1 && Makers.Contains(words[0]) ? 1 : 0;
        return at < words.Count && ModelFamily.IsMatch(words[at]) && words.Skip(at + 1).All(ModelVariant.IsMatch);
    }

    /// <summary>A name's key: its letters and digits, lower case.</summary>
    public static string Key(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in ReviewInputGuard.WithoutLoneSurrogates(text ?? "").Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>The rules in plain words, for the method and the data appendix.</summary>
    public static string Described =>
        $"Before counting, a value that listed several names was split at commas, semicolons, \"and\", \"or\", \"/\", \"+\" and \"&\" outside brackets, unless a part read as a phrase (more than {MaxNameWords} words); " +
        "names were matched ignoring case, punctuation, spaces and a trailing generic word (server, toolkit, framework, library, platform, tool, system, -based), and \"X (Y)\" matched both X and Y; " +
        "and for an attribute about tools, a value that is a language model (a known model family followed only by versions, sizes and variants), a programming language, a method, a general term or a web address was not counted, " +
        $"by the short lists in the tool's code (rules {Version}).";
}
