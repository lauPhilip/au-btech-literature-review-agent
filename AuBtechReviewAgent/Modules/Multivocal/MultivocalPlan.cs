using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

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
    public List<string> GreySearches { get; set; } = new() { "stackexchange", "github", "openalex-grey", "zenodo" };

    /// <summary>
    /// The search strings sent to the grey literature searches, in this order. Grey literature uses its own words
    /// (G7: run an informal pre-search for synonyms), so these are separate from the formal search strings. Empty
    /// means the topic is used; older plans have none.
    /// </summary>
    public List<string> GreySearchStrings { get; set; } = new();

    public const int MaxSearchStrings = 5;
    public const int MaxSearchStringLength = 200;

    /// <summary>The strings the searches use: the ones given, or the topic when none are given.</summary>
    public IReadOnlyList<string> EffectiveGreySearchStrings =>
        GreySearchStrings.Any(s => !string.IsNullOrWhiteSpace(s))
            ? GreySearchStrings.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList()
            : new[] { Topic.Trim() };
    public string StoppingRule { get; set; } = "effort";

    /// <summary>
    /// The bibliographic databases the formal pool is searched in (keys of <see cref="SourceCatalog"/>), in the
    /// catalogue's order. Scopus and IEEE Xplore need a key, which is never written to the plan (decision 7). Empty
    /// means no formal pool: every plan made before block I has none, and stays a grey literature review.
    /// </summary>
    public List<string> FormalDatabases { get; set; } = new();

    /// <summary>
    /// The search strings sent to the formal databases, in this order. Formal literature has its own words, so these
    /// are separate from the grey strings (p. 111). Empty means the topic is used.
    /// </summary>
    public List<string> FormalSearchStrings { get; set; } = new();

    /// <summary>
    /// The strings the language model proposed for the formal databases when the reviewer last asked, kept so the
    /// protocol can say which strings the model proposed and the reviewer accepted, and which were not used.
    /// </summary>
    public List<string> FormalStringsProposed { get; set; } = new();

    /// <summary>Phrases in the plan that read like instructions to the model, confirmed by the reviewer before the protocol was written (#365).</summary>
    public List<string> InputFlags { get; set; } = new();

    /// <summary>The databases a multivocal plan starts with: the ones that need no key.</summary>
    public static IReadOnlyList<string> DefaultFormalDatabases =>
        SourceCatalog.All.Where(s => s.KeyKind == SourceKeyKind.None).Select(s => s.Key).ToList();

    /// <summary>The strings the formal databases are searched with: the ones given, or the topic when none are given.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EffectiveFormalSearchStrings =>
        FormalSearchStrings.Any(s => !string.IsNullOrWhiteSpace(s))
            ? FormalSearchStrings.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList()
            : new[] { Topic.Trim() };

    /// <summary>Whether a formal search string was proposed by the language model (and so accepted by the reviewer).</summary>
    public bool WasProposed(string searchString) =>
        FormalStringsProposed.Any(p => p.Trim().Equals(searchString.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The selection criteria (G9): what a source must be about to be included, and what excludes it. Formal and grey
    /// sources are screened with the same criteria (G10). Older plans have none, and cannot be screened.
    /// </summary>
    public string InclusionCriteria { get; set; } = "";
    public string ExclusionCriteria { get; set; } = "";

    /// <summary>True when the plan has the criteria screening needs.</summary>
    public bool HasCriteria => InclusionCriteria.Trim().Length > 0;

    /// <summary>
    /// For the effort-bounded rule: how many top hits of each search string are looked at. The paper uses 100; a plan
    /// starts at 20, since every hit is screened twice by a model, and the reviewer can raise it on the form.
    /// </summary>
    public int TopHits { get; set; } = DefaultTopHits;

    /// <summary>The top hits a new plan starts with.</summary>
    public const int DefaultTopHits = 20;

    /// <summary>Grey sources scoring below this many of the 20 quality points are excluded (decision 4; the paper's example uses 10).</summary>
    public int QualityThreshold { get; set; } = 10;

    public bool IsGreyOnly => Card == "grey";

    /// <summary>
    /// Whether this review searches and uses formal (academic) literature too: a plan from the Multivocal card that
    /// names formal databases (block I). The planning page plans from that card only where it is on or in preview
    /// (Development), so production makes grey literature reviews until the card is switched on. A plan without formal
    /// databases, which every plan made before block I is, stays a grey literature review.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesFormalPool => !IsGreyOnly && FormalDatabases.Count > 0;

    /// <summary>What the review is, in lower case: "multivocal literature review" with both pools, otherwise "grey literature review".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ReviewKind => UsesFormalPool ? "multivocal literature review" : "grey literature review";

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
    /// in grey literature. It is kept small (two grey strings, four grey searches, 20 top hits), so trying it is cheap;
    /// the form adds <see cref="ExampleFormalDatabases"/> to a multivocal plan. Its criteria are narrow on purpose: with
    /// broader ones a live run included 282 of 431 sources, and every included source costs a page, a quality call and
    /// an extraction.
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
        GreySearches = new List<string> { "stackexchange", "github", "openalex-grey" },
        GreySearchStrings = new List<string> { "context engineering", "LLM agent context window" },
        InclusionCriteria = "Sources whose main subject is how the context of an LLM agent is built, selected or managed, meaning what goes into its context window and why. A source must explain at least one such practice, or evaluate one, in enough detail that a developer could apply it.",
        ExclusionCriteria = "Sources that mention an agent's context only in passing; generic tutorials on retrieval-augmented generation or vector databases; release notes and changelogs; code repositories whose README does not explain a practice; marketing pages without technical content; sources only about the wording of prompts for single chat requests; sources not in English.",
        StoppingRule = "effort",
        TopHits = DefaultTopHits,
        QualityThreshold = 10,
    };

    /// <summary>The academic databases the example searches: only ones that need no key, so it runs on any server.</summary>
    public static readonly IReadOnlyList<string> ExampleFormalDatabases = new[] { "openalex", "semanticscholar", "arxiv" };

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
        InclusionCriteria = Clean(InclusionCriteria);
        ExclusionCriteria = Clean(ExclusionCriteria);
        foreach (var q in Questions) q.Text = Clean(q.Text);
        // A search that is not built yet (backlink snowballing, #474) is not planned, so the protocol never promises it.
        GreySearches = GreySearches.Where(GreySourceCatalog.IsBuilt).ToList();
        GreySearchStrings = GreySearchStrings.Select(t => ReviewInputGuard.Normalize(t ?? "", singleLine: true)).Where(t => t.Length > 0).ToList();
        FormalSearchStrings = Distinct(FormalSearchStrings.Select(t => ReviewInputGuard.Normalize(t ?? "", singleLine: true)));
        FormalStringsProposed = Distinct(FormalStringsProposed.Select(t => ReviewInputGuard.Normalize(t ?? "", singleLine: true)));
        // The catalogue's order, so the protocol lists the databases the same way every time.
        FormalDatabases = SourceCatalog.AllKeys.Where(k => FormalDatabases.Contains(k, StringComparer.OrdinalIgnoreCase))
            .Concat(FormalDatabases.Where(k => SourceCatalog.Find(k) == null)).ToList();
        return this;
    }

    private static List<string> Distinct(IEnumerable<string> strings)
    {
        var result = new List<string>();
        foreach (var t in strings)
            if (t.Length > 0 && !result.Contains(t, StringComparer.OrdinalIgnoreCase)) result.Add(t);
        return result;
    }

    /// <summary>What must be fixed before the protocol can be written; empty when the plan is complete.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Card is not ("multivocal" or "grey")) problems.Add("The kind of review is unknown.");
        if (Topic.Length == 0) problems.Add("Give the topic of the review.");
        if (Goal.Length == 0) problems.Add("Say what the review is for (its goal).");
        if (Audience == null) problems.Add("Choose who the review is for.");
        if (new[] { Topic, Goal, ExistingReviews, InclusionCriteria, ExclusionCriteria }.Any(t => t.Length > MaxText) || Questions.Any(q => q.Text.Length > MaxText))
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
        foreach (var key in GreySearches.Where(k => MultivocalGuidelines.GreySearches.Any(g => g.Key == k) && !GreySourceCatalog.IsBuilt(k)))
            problems.Add($"{MultivocalGuidelines.SearchLabel(key)} is not available yet; leave it out.");
        if (MultivocalGuidelines.StoppingRules.All(r => r.Key != StoppingRule)) problems.Add("Choose a stopping rule.");
        if (!HasCriteria) problems.Add("Say what a source must be about to be included (the inclusion criteria).");
        if (GreySearchStrings.Count > MaxSearchStrings) problems.Add($"Use at most {MaxSearchStrings} search strings.");
        if (GreySearchStrings.Any(t => t.Length > MaxSearchStringLength)) problems.Add($"Keep every search string under {MaxSearchStringLength} characters.");
        if (FormalDatabases.Any(k => SourceCatalog.Find(k) == null)) problems.Add("Choose the academic databases from the list.");
        if (FormalSearchStrings.Count > MaxSearchStrings) problems.Add($"Use at most {MaxSearchStrings} search strings for academic databases.");
        if (FormalSearchStrings.Any(t => t.Length > MaxSearchStringLength)) problems.Add($"Keep every search string for academic databases under {MaxSearchStringLength} characters.");
        if (StoppingRule == "effort" && TopHits is < 10 or > 500) problems.Add("Look at between 10 and 500 top hits per search string.");
        if (QualityThreshold is < 1 or > MultivocalGuidelines.QualityPointsMax)
            problems.Add($"Set the quality threshold between 1 and {MultivocalGuidelines.QualityPointsMax} points.");
        return problems;
    }
}
