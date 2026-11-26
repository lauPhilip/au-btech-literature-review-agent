using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

// The three questions put to the model for each research question (the themes, the placement of the value groups,
// the tensions in a theme) and the answers they must give, each checked in code.
public sealed partial class MultivocalSynthesiser
{
    /// <summary>Step 1: the themes of a question, named and described, from its value groups.</summary>
    public static async Task<ModelThemeList> AskThemesAsync(IChatCompletionService chat, string topic, string question, string questionText, IReadOnlyList<ValueGroup> groups)
    {
        string list = string.Join("\n", groups.Select(g => $"{g.Attribute}: {g.Value} ({g.Sources} source{(g.Sources == 1 ? "" : "s")})"));
        var prompt = $$"""
            You synthesise the findings of a multivocal literature review for one research question (Garousi et al. 2019, G13), by qualitative coding.
            Topic: {{PromptSafety.Wrap(topic, "review topic")}}
            Research question {{question}}: {{PromptSafety.Wrap(questionText, "research question")}}

            {{PromptSafety.ReviewerInputNotice}}
            {{PromptSafety.DataOnlyNotice}}

            WHAT THE SOURCES SAY (attribute: value, and how many sources say it):
            {{PromptSafety.Wrap(list, "values")}}

            Name one to {{MaxThemesPerQuestion}} themes that together answer the question from these values. Name each theme in a few plain words and describe in one sentence what it covers. Do not say how many sources support a theme, or whether it is true: that is counted and judged separately.

            Respond ONLY with a valid minified JSON object of this shape:
            {"themes":[{"name":"Theme name","description":"One sentence."}]}
            """;
        return await LlmJson.GetAsync<ModelThemeList>(chat, prompt, LlmJson.JsonMode(0.2), ModelThemeList.Problem, repairAttempts: 2);
    }

    /// <summary>Step 2: one batch of value groups placed in the themes; every group in a theme or none, with a reason.</summary>
    public static async Task<ModelPlacement> AskPlacementAsync(IChatCompletionService chat, string question, string questionText, ModelThemeList themes, IReadOnlyList<ValueGroup> batch)
    {
        string themeList = string.Join("\n", themes.Themes.Select(t => $"- {t.Name.Trim()}: {t.Description.Trim()}"));
        string list = string.Join("\n", batch.Select(g => $"{g.Id} {g.Attribute}: {g.Value} ({g.Sources} source{(g.Sources == 1 ? "" : "s")}); for example {string.Join(" / ", g.Samples)}"));
        var prompt = $$"""
            You place the findings of a multivocal literature review in its themes, for research question {{question}}: {{PromptSafety.Wrap(questionText, "research question")}}

            THEMES:
            {{PromptSafety.Wrap(themeList, "themes")}}

            {{PromptSafety.ReviewerInputNotice}}
            {{PromptSafety.DataOnlyNotice}}

            VALUES TO PLACE (id, attribute: value, sources, example quotes):
            {{PromptSafety.Wrap(list, "values")}}

            Place every value above: give the names of the themes it belongs to (one or more, written exactly as above), or no theme with the reason it fits none.

            Respond ONLY with a valid minified JSON object of this shape, one entry per value:
            {"placements":[{"group":"V1","themes":["Theme name"],"reason":""},{"group":"V2","themes":[],"reason":"Why it fits no theme."}]}
            """;
        var ids = batch.Select(g => g.Id).ToHashSet();
        var names = themes.Themes.Select(t => t.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await LlmJson.GetAsync<ModelPlacement>(chat, prompt, LlmJson.JsonMode(0.0), a => ModelPlacement.Problem(a, ids, names), repairAttempts: 2);
    }

    /// <summary>Step 3: the tensions in one theme, between values of different sources, from sample quotes.</summary>
    public static async Task<ModelTensions> AskTensionsAsync(IChatCompletionService chat, string question, string questionText, string theme,
        IReadOnlyList<ValueGroup> groups, IReadOnlyList<SynthesisFinding> findings)
    {
        // With formal findings (block I-5) each value says which pools its sources are in, so a disagreement between
        // the state of the art and the state of the practice can be named; a grey literature review's prompt is unchanged.
        bool pools = findings.Any(f => f.Literature == "formal");
        string list = string.Join("\n", groups.Select(g => $"{g.Id} {g.Attribute}: {g.Value} ({g.Sources} source{(g.Sources == 1 ? "" : "s")}{(pools ? PoolsOf(g, findings) : "")}); for example {string.Join(" / ", g.Samples)}"));
        string poolNote = pools
            ? " Academic sources show the state of the art and grey sources the state of the practice; where they disagree, name it, as it matters most."
            : "";
        var prompt = $$"""
            In a multivocal literature review, research question {{question}}: {{PromptSafety.Wrap(questionText, "research question")}}
            Theme: {{PromptSafety.Wrap(theme, "theme")}}

            {{PromptSafety.ReviewerInputNotice}}
            {{PromptSafety.DataOnlyNotice}}

            VALUES IN THIS THEME (id, attribute: value, sources, example quotes):
            {{PromptSafety.Wrap(list, "values")}}

            Name the tensions: pairs of values whose sources disagree or contradict each other about how to do something or what works, with one sentence on how, using the values' names, not their ids. Values that are merely different, or different options, are not tensions. Only real disagreements; none is fine.{{poolNote}}

            Respond ONLY with a valid minified JSON object of this shape:
            {"tensions":[{"groupA":"V1","groupB":"V4","description":"One sentence."}]}
            """;
        return await LlmJson.GetAsync<ModelTensions>(chat, prompt, LlmJson.JsonMode(0.2), ModelTensions.Problem);
    }

    /// <summary>", 1 academic, 2 grey": how many of a value's sources are in each pool.</summary>
    private static string PoolsOf(ValueGroup group, IReadOnlyList<SynthesisFinding> findings)
    {
        var sources = findings.Where(f => group.Findings.Contains(f.Id)).GroupBy(f => f.Address).Select(g => g.First().Literature).ToList();
        return $", {sources.Count(l => l == "formal")} academic, {sources.Count(l => l == "grey")} grey";
    }

    /// <summary>
    /// The model's tensions as pairs of findings from different sources, one from each value; a tension that names a
    /// value outside the theme, or whose two values come from the same single source, is left out with a note.
    /// </summary>
    public static List<SynthesisTension> TensionsFor(ModelTensions answer, IReadOnlyList<ValueGroup> groups, IReadOnlyList<SynthesisFinding> findings, out List<string> notes)
    {
        notes = new List<string>();
        var byId = groups.ToDictionary(g => g.Id);
        var byFinding = findings.ToDictionary(f => f.Id);
        var tensions = new List<SynthesisTension>();
        int outside = 0, oneSource = 0;
        foreach (var t in answer.Tensions)
        {
            if (!byId.TryGetValue(t.GroupA.Trim(), out var a) || !byId.TryGetValue(t.GroupB.Trim(), out var b) || a == b)
            {
                outside++;
                continue;
            }
            // A pair across the pools comes first, so a tension between the state of the art and of the practice is shown as one.
            var pair = a.Findings.SelectMany(fa => b.Findings.Select(fb => (A: byFinding[fa], B: byFinding[fb])))
                .Where(x => x.A.Address != x.B.Address).OrderBy(x => x.A.Literature == x.B.Literature ? 1 : 0).FirstOrDefault();
            if (pair.A == null)
            {
                oneSource++;
                continue;
            }
            // The model sees the values by id; the reader sees their names.
            string description = ValueId.Replace(t.Description.Trim(), m => byId.TryGetValue(m.Value, out var g) ? $"\"{g.Value}\"" : "another value");
            tensions.Add(new SynthesisTension
            {
                FindingA = pair.A.Id,
                FindingB = pair.B.Id,
                Description = $"{a.Value} against {b.Value}. {description}",
                Between = !findings.Any(f => f.Literature == "formal") ? null
                    : pair.A.Literature != pair.B.Literature ? SynthesisTension.ArtAgainstPractice
                    : pair.A.Literature == "formal" ? "within the state of the art" : "within the state of the practice",
            });
        }
        if (outside > 0) notes.Add($"{outside} tension(s) named a value outside the theme; left out.");
        if (oneSource > 0) notes.Add($"{oneSource} tension(s) were between values of one source only; left out.");
        return tensions;
    }
}

/// <summary>The findings of one question with the same attribute and value, grouped in code.</summary>
public sealed class ValueGroup
{
    public string Id { get; set; } = "";
    public string Attribute { get; set; } = "";
    public string Value { get; set; } = "";
    public List<string> Findings { get; set; } = new();
    public int Sources { get; set; }
    public List<string> Samples { get; set; } = new();

    public string Label => $"{Attribute}: {Value} ({Findings.Count} finding{(Findings.Count == 1 ? "" : "s")})";
}

public sealed class ModelThemeList
{
    public List<ModelThemeName> Themes { get; set; } = new();

    /// <summary>Null when there are one to six themes, each named once.</summary>
    public static string? Problem(ModelThemeList a)
    {
        if (a.Themes.Count is 0 or > MultivocalSynthesiser.MaxThemesPerQuestion) return $"give one to {MultivocalSynthesiser.MaxThemesPerQuestion} themes.";
        if (a.Themes.Any(t => string.IsNullOrWhiteSpace(t.Name))) return "every theme needs a name.";
        var repeated = a.Themes.GroupBy(t => t.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (repeated != null) return $"two themes are called \"{repeated.Key}\".";
        var counting = a.Themes.FirstOrDefault(t => Counts.IsMatch(t.Name) || Counts.IsMatch(t.Description));
        return counting != null ? $"the theme \"{counting.Name.Trim()}\" says how many sources support it; describe only what it covers, the sources are counted in code." : null;
    }

    /// <summary>How many sources say something ("the majority of sources", "most practitioners", "12 sources"): counted in code, never by the model.</summary>
    internal static readonly Regex Counts = new(
        @"\b(majority|minority|consensus)\b|\b(most|many|few|several|all|some|\d+)\s+(of\s+the\s+)?(sources|studies|authors|practitioners|papers|posts|repositories|articles)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

public sealed class ModelThemeName
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>The model's placement of one batch of value groups.</summary>
public sealed class ModelPlacement
{
    public List<ModelPlaced> Placements { get; set; } = new();

    /// <summary>Null when every group of the batch is placed once, in named themes only, or in none with a reason.</summary>
    public static string? Problem(ModelPlacement a, IReadOnlySet<string> groups, IReadOnlySet<string> themes)
    {
        var unknown = a.Placements.Select(p => p.Group.Trim()).FirstOrDefault(g => !groups.Contains(g));
        if (unknown != null) return $"\"{unknown}\" is not one of the values to place.";
        var missing = groups.Where(g => a.Placements.All(p => p.Group.Trim() != g)).ToList();
        if (missing.Count > 0) return $"values {string.Join(", ", missing)} are not placed; give every value its themes, or none with the reason.";
        var badTheme = a.Placements.SelectMany(p => p.Themes).Select(t => t.Trim()).FirstOrDefault(t => !themes.Contains(t));
        if (badTheme != null) return $"\"{badTheme}\" is not one of the themes; use the names exactly as given.";
        if (a.Placements.Any(p => p.Themes.Count == 0 && string.IsNullOrWhiteSpace(p.Reason))) return "a value in no theme needs the reason.";
        return null;
    }
}

public sealed class ModelPlaced
{
    public string Group { get; set; } = "";
    public List<string> Themes { get; set; } = new();
    public string? Reason { get; set; }
}

public sealed class ModelTensions
{
    public List<ModelTension> Tensions { get; set; } = new();

    /// <summary>The longest description of a tension, in characters: it is one sentence in the report.</summary>
    public const int MaxDescriptionChars = 300;

    private static readonly Regex SentenceBreak = new(@"[.!?]\s+(?=[A-Z])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Null when every tension names two values and says how they disagree in one short sentence that counts no sources
    /// (the description goes into the report; sources are counted in code, as for the themes).
    /// </summary>
    public static string? Problem(ModelTensions a)
    {
        foreach (var t in a.Tensions)
        {
            if (string.IsNullOrWhiteSpace(t.GroupA) || string.IsNullOrWhiteSpace(t.GroupB)) return "every tension needs the ids of its two values in groupA and groupB.";
            string description = t.Description.Trim();
            if (description.Length == 0) return $"the tension between {t.GroupA.Trim()} and {t.GroupB.Trim()} needs a description.";
            if (description.Length > MaxDescriptionChars) return $"the description of the tension between {t.GroupA.Trim()} and {t.GroupB.Trim()} is too long; use one sentence of at most {MaxDescriptionChars} characters.";
            if (SentenceBreak.IsMatch(description)) return $"the description of the tension between {t.GroupA.Trim()} and {t.GroupB.Trim()} has more than one sentence; use one.";
            if (ModelThemeList.Counts.IsMatch(description)) return $"the tension between {t.GroupA.Trim()} and {t.GroupB.Trim()} says how many sources hold a view; say only how they disagree, the sources are counted in code.";
        }
        return null;
    }
}

public sealed class ModelTension
{
    public string GroupA { get; set; } = "";
    public string GroupB { get; set; } = "";
    public string Description { get; set; } = "";
}
