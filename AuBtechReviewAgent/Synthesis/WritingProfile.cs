using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>
/// How the writing prompts name a kind of review and its parts. Everything else in the prompts, and every check of
/// the answers, is the same for every kind of review. The defaults are the systematic review's words, so its prompts
/// stay exactly as they were.
/// </summary>
public sealed record WritingProfile
{
    /// <summary>The systematic review's words.</summary>
    public static readonly WritingProfile Systematic = new();

    /// <summary>The review, with its article, e.g. "a systematic literature review".</summary>
    public string Review { get; init; } = "a systematic literature review";

    /// <summary>The review in short, with its article, e.g. "a systematic review".</summary>
    public string ReviewShort { get; init; } = "a systematic review";

    /// <summary>Where a theme subsection goes, e.g. "the Results & Synthesis section (PRISMA 2020 Item 20a)".</summary>
    public string ResultsSection { get; init; } = "the Results & Synthesis section (PRISMA 2020 Item 20a)";

    /// <summary>The discussion, e.g. "the Discussion section (PRISMA 2020 Item 23a)".</summary>
    public string DiscussionSection { get; init; } = "the Discussion section (PRISMA 2020 Item 23a)";

    /// <summary>What the review calls one source and several, e.g. "study" and "studies".</summary>
    public string Source { get; init; } = "study";
    public string Sources { get; init; } = "studies";

    /// <summary>Where the facts about the evidence base come from, e.g. "from the data extraction".</summary>
    public string BaseFactsFrom { get; init; } = "from the data extraction";

    /// <summary>What the excerpts of the sources' texts are, e.g. "excerpts from the included papers".</summary>
    public string SourceTexts { get; init; } = "excerpts from the included papers";

    /// <summary>
    /// What the discussion's paragraphs cover, in order. The systematic review's discussion is PRISMA item 23a only: the
    /// limitations, implications, future research and conclusion are written after it by <see cref="ReviewWriter.WriteClosingAsync"/>.
    /// </summary>
    public string DiscussionParts { get; init; } =
        "(a) what the themes together say about the objective, (b) where the themes connect, reinforce or contradict each other, (c) how strong the evidence behind the main findings is. Do not write the limitations, the implications, the directions for future research or a conclusion: they are separate sections written after this one.";

    /// <summary>
    /// Whether the theme prompts get lean source blocks (<see cref="ReviewEvidence.SourceBlock"/>): only the findings
    /// placed in the theme, without a codes line that repeats them. Off by default, so the systematic review's prompts
    /// stay exactly as they were; the multivocal review turns it on to cut the tokens it sends.
    /// </summary>
    public bool LeanEvidence { get; init; }

    /// <summary>
    /// The ids of the research questions, in order, when the review numbers them its own way (the multivocal review's
    /// "RQ1.1"); null: RQ1, RQ2 ... (<see cref="ReviewWriter.QuestionId(int)"/>), as the systematic review numbers them.
    /// </summary>
    public IReadOnlyList<string>? QuestionIds { get; init; }

    /// <summary>What the closing step's three paragraphs are, as the review's guidelines name them; the defaults are PRISMA 2020's.</summary>
    public string LimitationsPart { get; init; } = "the limitations of the evidence base (PRISMA 2020 item 23b)";
    public string ImplicationsPart { get; init; } = "the implications for practice and policy (item 23d)";
    public string FutureResearchPart { get; init; } = "the directions for future research (item 23d)";
}
