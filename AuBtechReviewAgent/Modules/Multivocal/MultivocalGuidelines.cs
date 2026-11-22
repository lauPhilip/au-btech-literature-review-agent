using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The fixed parts of the Garousi, Felderer and Mäntylä (2019) guidelines that the planning form asks about: the
/// seven questions of Table 4 (G3), the question types of Table 6 (G5), the kinds of grey literature (G6), the
/// searches the module can run (G7, decision 1 in docs/roadmap/mlr-todo.md) and the stopping rules (G8).
/// </summary>
public static class MultivocalGuidelines
{
    public const string Reference = "Garousi, Felderer & Mäntylä (2019), Guidelines for including grey literature and conducting multivocal literature reviews, Information and Software Technology 106, 101–121";

    /// <summary>Table 4: one or more "yes" answers suggest including grey literature.</summary>
    public static readonly IReadOnlyList<string> IncludeGreyQuestions = new[]
    {
        "Is the subject \"complex\" and not solvable by considering only the formal literature?",
        "Is there a lack of volume or quality of evidence, or a lack of consensus of outcome measurement in the formal literature?",
        "Is the contextual information important to the subject under study?",
        "Is it the goal to validate or corroborate scientific outcomes with practical experiences?",
        "Is it the goal to challenge assumptions or falsify results from practice using academic research or vice versa?",
        "Would a synthesis of insights and evidence from the industrial and academic community be useful to one or even both communities?",
        "Is there a large volume of practitioner sources indicating high practitioner interest in a topic?",
    };

    /// <summary>A type of research question from Table 6 (after Easterbrook et al.).</summary>
    /// <param name="HardForSources">Primary sources often cannot answer this type; the form warns about it (p. 110).</param>
    public sealed record QuestionType(string Key, string Category, string Name, string Pattern, bool HardForSources);

    public static readonly IReadOnlyList<QuestionType> QuestionTypes = new[]
    {
        new QuestionType("existence", "Exploratory", "Existence", "Does X exist?", false),
        new QuestionType("description", "Exploratory", "Description and classification", "What is X like?", false),
        new QuestionType("descriptive-comparative", "Exploratory", "Descriptive comparison", "How does X differ from Y?", false),
        new QuestionType("frequency", "Base-rate", "Frequency distribution", "How often does X occur?", false),
        new QuestionType("process", "Base-rate", "Descriptive process", "How does X normally work?", false),
        new QuestionType("relationship", "Relationship", "Relationship", "Are X and Y related?", true),
        new QuestionType("causality", "Causality", "Causality", "Does X cause (or prevent) Y?", true),
        new QuestionType("causality-comparative", "Causality", "Causal comparison", "Does X cause more Y than does Z?", true),
        new QuestionType("causality-interaction", "Causality", "Causal comparison with interaction", "Does X or Z cause more Y under one condition but not others?", true),
        new QuestionType("design", "Design", "Design", "What is an effective way to achieve X?", true),
    };

    public static QuestionType? FindType(string? key) => QuestionTypes.FirstOrDefault(t => t.Key == key);

    /// <summary>Kinds of grey literature the review can cover (G6, after the paper's Fig. 1).</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> GreyTypes = new[]
    {
        ("white-papers", "White papers and technical reports"),
        ("documentation", "Product and project documentation"),
        ("blogs", "Blog posts and practitioner articles"),
        ("qa", "Questions and answers on Q&A sites"),
        ("code", "Code repositories and their README files"),
        ("news", "News and magazine articles"),
        ("theses", "Theses and dissertations"),
        ("talks", "Talks and videos, only with a published transcript"),
    };

    /// <summary>
    /// A search the module can run (G7). There is no general web search engine among them (decision 1): none is both
    /// free and allows its results to be kept, and Brave's terms forbid storing them.
    /// </summary>
    public sealed record GreySearch(string Key, string Label);

    public static readonly IReadOnlyList<GreySearch> GreySearches = new[]
    {
        new GreySearch("stackexchange", "Stack Exchange sites (Stack Overflow and others)"),
        new GreySearch("github", "GitHub repositories"),
        new GreySearch("hackernews", "Hacker News"),
        new GreySearch("openalex-grey", "Reports, theses and standards in OpenAlex"),
        new GreySearch("zenodo", "Reports and documents on Zenodo"),
        new GreySearch("backlinks", "Links in the included grey sources (backlink snowballing)"),
    };

    /// <summary>The label of a search, or its key when the search is no longer offered (a plan made before it was dropped).</summary>
    public static string SearchLabel(string key) => GreySearches.FirstOrDefault(s => s.Key == key)?.Label ?? key;

    /// <summary>The three stopping rules of G8.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> StoppingRules = new[]
    {
        ("effort", "Effort bounded: the top hits of each search string"),
        ("saturation", "Theoretical saturation: stop when new sources add nothing new"),
        ("exhaustion", "Evidence exhaustion: every hit of every search string"),
    };

    /// <summary>The quality checklist (Table 7) gives at most this many points.</summary>
    public const int QualityPointsMax = 20;
}

/// <summary>Who the review is for (G2).</summary>
public enum ReviewAudience { Researchers, Practitioners, Both }

/// <summary>One research question (G4) with its type (G5); a sub-question names its parent's position.</summary>
public sealed class PlannedQuestion
{
    public string Text { get; set; } = "";
    public string Type { get; set; } = "";

    /// <summary>The index of the main question this one belongs to, or null for a main question.</summary>
    public int? Parent { get; set; }
}
