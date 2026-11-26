using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>
/// One source behind an answer, as the certainty rule sees it. A module fills it from its own appraisal: the
/// systematic review from the extraction and MMAT (<see cref="SystematicCertainty"/>); a multivocal review can map its
/// quality tiers onto the same flags later.
/// </summary>
/// <param name="Reference">The source's reference number.</param>
/// <param name="Empirical">True when the source collects or analyses data (MMAT: any category but not empirical).</param>
/// <param name="AbstractOnly">True when the source was not read in full text.</param>
/// <param name="FailsAppraisal">True when it fails more than half of the appraisal criteria it was appraised on ("can't tell" is not a failure).</param>
/// <param name="Questions">The question ids its codes bear on ("RQ1"); null when no code of it names a question.</param>
public sealed record CertaintySource(int Reference, bool Empirical, bool AbstractOnly, bool FailsAppraisal, IReadOnlyCollection<string>? Questions);

/// <summary>A theme the codebook maps to a question, with its sources.</summary>
public sealed record CertaintyTheme(string Id, string Name, IReadOnlyList<int> Sources);

/// <summary>
/// One answer to a research question with the evidence behind it: the sources it cites and the themes mapped to its
/// question. <paramref name="NotRated"/> says why an answer gets no rating (no evidence bears on the question).
/// <paramref name="Contested"/> lists the sources whose place in a theme of the question two independent codings of
/// their quotes disagree on; it is null when the run has no second coding, so coherence cannot be rated.
/// </summary>
public sealed record CertaintyQuestion(int Number, string Id, string Question, string Answer, IReadOnlyList<int> Cited,
    IReadOnlyList<CertaintyTheme> Themes, string? NotRated = null, IReadOnlyList<int>? Contested = null);

/// <summary>The levels, from GRADE: high, moderate, low and very low.</summary>
public enum CertaintyLevel { VeryLow = 1, Low = 2, Moderate = 3, High = 4 }

/// <summary>
/// The thresholds of the certainty rule, in one place and on file in certainty.json, so a reader sees exactly
/// what lowered a rating.
/// </summary>
public sealed class CertaintyRule
{
    /// <summary>A domain is a serious concern when more than this share of the sources behind an answer is affected.</summary>
    public double ConcernShare { get; init; } = 0.5;

    /// <summary>An answer that rests on fewer sources than this has a serious concern about the adequacy of its data.</summary>
    public int FewestSources { get; init; } = 2;

    /// <summary>The rating starts at moderate, not high, when at least this share (but not all) of the sources is empirical; below it, at low.</summary>
    public double ModerateStartShare { get; init; } = 0.5;

    /// <summary>A source fails its appraisal when it fails more than this share of its criteria.</summary>
    public double FailShare { get; init; } = 0.5;

    /// <summary>Levels lowered for each serious concern.</summary>
    public int LevelsPerConcern { get; init; } = 1;

    public static CertaintyRule Default { get; } = new();
}

/// <summary>One domain of a rating: its concern ("no serious concern", "serious concern" or "not assessed"), why, and the sources it names.</summary>
public sealed class CertaintyDomain
{
    public string Domain { get; init; } = "";
    public string Concern { get; init; } = "";
    public string Reason { get; init; } = "";
    public List<int> Sources { get; init; } = new();
}

/// <summary>What leaving out one kind of source does to an answer's support.</summary>
public sealed class SensitivityCheck
{
    public string LeavesOut { get; init; } = "";
    public List<int> Removed { get; init; } = new();
    public List<int> Remaining { get; init; } = new();

    /// <summary>The themes mapped to the question that would keep no source.</summary>
    public List<string> ThemesWithout { get; init; } = new();
}

/// <summary>The certainty of one answer, written in code.</summary>
public sealed class CertaintyAssessment
{
    public int Number { get; init; }
    public string Question { get; init; } = "";

    /// <summary>The answer's first sentence, for the table.</summary>
    public string AnswerInBrief { get; init; } = "";
    public List<int> Sources { get; init; } = new();

    /// <summary>"high", "moderate", "low" or "very low"; null when the answer is not rated.</summary>
    public string? Start { get; init; }
    public string? StartReason { get; init; }
    public List<CertaintyDomain> Domains { get; init; } = new();
    public string? Overall { get; init; }

    /// <summary>Why the answer is not rated; null when it is.</summary>
    public string? NotRated { get; init; }

    /// <summary>The phrase printed after the answer, e.g. "(low certainty: 3 studies, two read from the abstract only)".</summary>
    public string Phrase { get; init; } = "";
    public List<SensitivityCheck> Sensitivity { get; init; } = new();
    public string SensitivitySentence { get; init; } = "";
}

/// <summary>certainty.json: the method, the rule's thresholds and every answer's rating.</summary>
public sealed class CertaintyFile
{
    public string Method { get; init; } = "";
    public CertaintyRule Rule { get; init; } = CertaintyRule.Default;
    public List<CertaintyAssessment> Answers { get; init; } = new();

    public const string FileName = "certainty.json";
}

/// <summary>The words a review uses in the certainty text (the systematic review's are the defaults).</summary>
public sealed record CertaintyWords(string Source = "study", string Sources = "studies", string Appraisal = "MMAT")
{
    public static CertaintyWords Systematic { get; } = new();
}
