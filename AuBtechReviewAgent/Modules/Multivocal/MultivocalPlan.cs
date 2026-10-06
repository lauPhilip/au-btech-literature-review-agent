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

    /// <summary>A search the module can run (G7). Brave runs only with the reviewer's own key (decision 1).</summary>
    public sealed record GreySearch(string Key, string Label, bool NeedsOwnKey);

    public static readonly IReadOnlyList<GreySearch> GreySearches = new[]
    {
        new GreySearch("stackexchange", "Stack Exchange sites (Stack Overflow and others)", false),
        new GreySearch("github", "GitHub repositories", false),
        new GreySearch("hackernews", "Hacker News", false),
        new GreySearch("devto", "dev.to articles", false),
        new GreySearch("openalex-grey", "Reports, theses and other grey literature in OpenAlex", false),
        new GreySearch("zenodo", "Reports and documents on Zenodo", false),
        new GreySearch("backlinks", "Links in the included grey sources (backlink snowballing)", false),
        new GreySearch("brave", "The general web through Brave Search, with your own API key", true),
    };

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

/// <summary>
/// The planning answers of a multivocal (or grey literature) review, in the order of the guidelines: need and
/// audience (G2), whether to include grey literature (G3), the research questions and their types (G4, G5), and the
/// search and quality settings the protocol must fix before searching (G6–G8, G11).
/// </summary>
public sealed class MultivocalPlan
{
    public const int MaxText = 2000;
    public const int MaxQuestions = 20;

    /// <summary>"multivocal" or "grey": the card the review was started from.</summary>
    public string Card { get; set; } = "multivocal";

    public string Topic { get; set; } = "";
    public string Goal { get; set; } = "";
    public string ExistingReviews { get; set; } = "";
    public ReviewAudience? Audience { get; set; }

    /// <summary>The answers to Table 4, in its order; null is not answered.</summary>
    public List<bool?> IncludeGrey { get; set; } = MultivocalGuidelines.IncludeGreyQuestions.Select(_ => (bool?)null).ToList();

    public List<PlannedQuestion> Questions { get; set; } = new() { new PlannedQuestion() };

    public List<string> GreyTypes { get; set; } = new() { "white-papers", "documentation", "blogs", "qa" };
    public List<string> GreySearches { get; set; } = new() { "stackexchange", "github", "openalex-grey", "zenodo", "backlinks" };
    public string StoppingRule { get; set; } = "effort";

    /// <summary>For the effort-bounded rule: how many top hits of each search string are looked at (the paper uses 100).</summary>
    public int TopHits { get; set; } = 100;

    /// <summary>Grey sources scoring below this many of the 20 quality points are excluded (decision 4; the paper's example uses 10).</summary>
    public int QualityThreshold { get; set; } = 10;

    public bool IsGreyOnly => Card == "grey";

    /// <summary>True when at least one Table 4 answer is "yes", which suggests including grey literature.</summary>
    public bool GreyLiteratureIndicated => IncludeGrey.Any(a => a == true);

    /// <summary>The questions numbered as in the protocol: RQ1, RQ1.1, RQ2 ...</summary>
    public IReadOnlyList<(string Number, PlannedQuestion Question)> Numbered()
    {
        var result = new List<(string, PlannedQuestion)>();
        int main = 0;
        for (int i = 0; i < Questions.Count; i++)
        {
            if (Questions[i].Parent != null) continue;
            main++;
            result.Add(($"RQ{main}", Questions[i]));
            int sub = 0;
            for (int j = 0; j < Questions.Count; j++)
                if (Questions[j].Parent == i) result.Add(($"RQ{main}.{++sub}", Questions[j]));
        }
        return result;
    }

    /// <summary>
    /// A complete example plan, to try the form and the protocol without typing (the "Fill in an example" button on
    /// the preview). It plans a review of context engineering for LLM agents, a topic where much of the knowledge is
    /// in grey literature.
    /// </summary>
    public static MultivocalPlan Example(string card = "multivocal") => new()
    {
        Card = card == "grey" ? "grey" : "multivocal",
        Topic = "Context engineering for LLM agents",
        Goal = "Map the practices that developers and researchers use to decide what goes into the context window of an LLM agent, and how they judge whether a practice works.",
        ExistingReviews = "Surveys of prompt engineering and of retrieval-augmented generation exist, but none maps context engineering practices across practitioner and academic sources.",
        Audience = ReviewAudience.Both,
        IncludeGrey = new List<bool?> { true, true, true, true, false, true, true },
        Questions = new List<PlannedQuestion>
        {
            new() { Text = "Which context engineering practices do practitioners and researchers describe?", Type = "description" },
            new() { Text = "Which tools and frameworks support these practices?", Type = "existence", Parent = 0 },
            new() { Text = "How are the practices usually applied when an agent is built?", Type = "process", Parent = 0 },
            new() { Text = "How often is each practice mentioned in grey and in academic sources?", Type = "frequency" },
            new() { Text = "Is the use of retrieval for context related to fewer reported agent failures?", Type = "relationship" },
        },
        GreyTypes = new List<string> { "white-papers", "documentation", "blogs", "qa", "code" },
        GreySearches = new List<string> { "stackexchange", "github", "hackernews", "devto", "openalex-grey", "zenodo", "backlinks" },
        StoppingRule = "effort",
        TopHits = 100,
        QualityThreshold = 10,
    };

    /// <summary>Adds a main question at the end, or a sub-question right after its main question's other sub-questions.</summary>
    public void AddQuestion(int? parent)
    {
        if (Questions.Count >= MaxQuestions) return;
        var question = new PlannedQuestion { Parent = parent };
        if (parent is int p && p >= 0 && p < Questions.Count && Questions[p].Parent == null)
        {
            int at = p + 1;
            while (at < Questions.Count && Questions[at].Parent == p) at++;
            ShiftParents(at, +1);
            Questions.Insert(at, question);
        }
        else
        {
            question.Parent = null;
            Questions.Add(question);
        }
    }

    /// <summary>Removes a question; removing a main question removes its sub-questions too. One empty question always remains.</summary>
    public void RemoveQuestion(int index)
    {
        if (index < 0 || index >= Questions.Count) return;
        var remove = Questions.Select((q, i) => (q, i)).Where(x => x.i == index || x.q.Parent == index).Select(x => x.i).OrderByDescending(i => i).ToList();
        foreach (int i in remove)
        {
            Questions.RemoveAt(i);
            ShiftParents(i, -1);
        }
        if (Questions.Count == 0) Questions.Add(new PlannedQuestion());
    }

    private void ShiftParents(int from, int by)
    {
        foreach (var q in Questions)
            if (q.Parent is int p && p >= from) q.Parent = p + by;
    }

    /// <summary>Cleans every text the reviewer typed (the same rules as the systematic review's form).</summary>
    public MultivocalPlan Normalized()
    {
        string Clean(string? s) => ReviewInputGuard.Normalize(s ?? "", singleLine: false);
        Topic = Clean(Topic);
        Goal = Clean(Goal);
        ExistingReviews = Clean(ExistingReviews);
        foreach (var q in Questions) q.Text = Clean(q.Text);
        return this;
    }

    /// <summary>What must be fixed before the protocol can be written; empty when the plan is complete.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Card is not ("multivocal" or "grey")) problems.Add("The kind of review is unknown.");
        if (Topic.Length == 0) problems.Add("Give the topic of the review.");
        if (Goal.Length == 0) problems.Add("Say what the review is for (its goal).");
        if (Audience == null) problems.Add("Choose who the review is for.");
        if (new[] { Topic, Goal, ExistingReviews }.Any(t => t.Length > MaxText) || Questions.Any(q => q.Text.Length > MaxText))
            problems.Add($"Keep every answer under {MaxText} characters.");
        if (IncludeGrey.Count != MultivocalGuidelines.IncludeGreyQuestions.Count || IncludeGrey.Any(a => a == null))
            problems.Add("Answer all seven questions on including grey literature.");

        var numbered = Numbered();
        if (numbered.Count == 0 || numbered.Any(q => q.Question.Text.Length == 0))
            problems.Add("Write at least one research question, and fill in or remove empty ones.");
        if (Questions.Count > MaxQuestions) problems.Add($"Use at most {MaxQuestions} research questions.");
        if (Questions.Any(q => MultivocalGuidelines.FindType(q.Type) == null))
            problems.Add("Choose a type for every research question.");
        if (Questions.Any(q => q.Parent is int p && (p < 0 || p >= Questions.Count || Questions[p].Parent != null)))
            problems.Add("A sub-question must belong to a main question.");

        if (GreyTypes.Count == 0 || GreyTypes.Any(t => MultivocalGuidelines.GreyTypes.All(g => g.Key != t)))
            problems.Add("Choose at least one kind of grey literature.");
        if (GreySearches.Count == 0 || GreySearches.Any(s => MultivocalGuidelines.GreySearches.All(g => g.Key != s)))
            problems.Add("Choose at least one place to search.");
        if (MultivocalGuidelines.StoppingRules.All(r => r.Key != StoppingRule)) problems.Add("Choose a stopping rule.");
        if (StoppingRule == "effort" && TopHits is < 10 or > 500) problems.Add("Look at between 10 and 500 top hits per search string.");
        if (QualityThreshold is < 1 or > MultivocalGuidelines.QualityPointsMax)
            problems.Add($"Set the quality threshold between 1 and {MultivocalGuidelines.QualityPointsMax} points.");
        return problems;
    }
}
