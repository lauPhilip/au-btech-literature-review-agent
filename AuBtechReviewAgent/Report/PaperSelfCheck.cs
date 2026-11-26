using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AuBtechReviewAgent;

/// <summary>What a part of the paper is, so each check knows which parts it reads.</summary>
public enum PaperPartKind
{
    /// <summary>The abstract: may give the run's counts and the result figures.</summary>
    Abstract,
    /// <summary>The rationale and objectives.</summary>
    Introduction,
    /// <summary>The opening text of the results, before the themes.</summary>
    Overview,
    /// <summary>One theme subsection of the results.</summary>
    Theme,
    Discussion,
    /// <summary>The answer to one research question.</summary>
    Answer,
    /// <summary>The limitations of the evidence (written by the model).</summary>
    Limitations,
    Implications,
    FutureResearch,
    Conclusion,
    /// <summary>Text written in code (the review's limitations, the coverage line, the check summary): read for references, not judged.</summary>
    CodeWritten,
}

/// <summary>One part of the paper: its field on the report page (for linking a fault to its sentence), its name and its text.</summary>
public sealed record PaperPart(string Field, string Name, PaperPartKind Kind, string Text);

/// <summary>A count the run made in code, with the words that name it in a sentence ("included", "screened").</summary>
public sealed record PaperFact(string Label, int Value, params string[] Words);

/// <summary>One theme: its name, the sources it rests on and, for a theme on one source, the reason on file.</summary>
public sealed record PaperTheme(string Name, IReadOnlyList<int> Sources, string? SingleSourceReason = null);

/// <summary>One research question and its answer as printed (empty when the paper has none).</summary>
public sealed record PaperQuestion(string Id, string Question, string Answer);

/// <summary>One entry of the reference list, with what a reader looks for in it.</summary>
public sealed record PaperReference(int Number, string Text, int? Year, string? Venue, string? Doi, string? Title = null, int FieldsCompleted = 0);

/// <summary>
/// Everything the self-check reads, in one shape for every kind of review: the paper's parts, its counts, themes,
/// questions, references, the citation check and the reasons a source is not cited. A module fills it from its own
/// files (the systematic review in SystematicSelfCheck); the checks never read a run folder.
/// </summary>
public sealed class PaperCheckInput
{
    public List<PaperPart> Parts { get; init; } = new();
    public List<PaperFact> Facts { get; init; } = new();

    /// <summary>More facts written in code without a label (the evidence base by design), whose numbers a part may use.</summary>
    public string FactsText { get; init; } = "";
    public List<PaperTheme> Themes { get; init; } = new();
    public List<PaperQuestion> Questions { get; init; } = new();
    public List<PaperReference> References { get; init; } = new();

    /// <summary>The included sources, by reference number.</summary>
    public List<int> Included { get; init; } = new();

    /// <summary>Why an included source is not cited in the results or discussion, as the run recorded it.</summary>
    public Dictionary<int, string> NotCitedReasons { get; init; } = new();

    /// <summary>The citation check after repair, and before it; null when the run checked no citation.</summary>
    public CitationSupportSummary? CitationsFinal { get; init; }
    public CitationSupportSummary? CitationsInitial { get; init; }
    public int CitationsRemoved { get; init; }

    /// <summary>
    /// The citation check's counts as the run page shows them (from the ledger, with <c>Removed</c> set); null when the
    /// module keeps none. They should equal the report's (<see cref="CitationsFinal"/>); run 596541db showed 109 and 113.
    /// </summary>
    public CitationSupportSummary? CitationsShown { get; init; }

    /// <summary>The closing parts every paper of this kind has, with the names a reader knows them by.</summary>
    public Dictionary<PaperPartKind, string> ExpectedParts { get; init; } = new()
    {
        [PaperPartKind.Discussion] = "discussion",
        [PaperPartKind.Limitations] = "limitations of the evidence",
        [PaperPartKind.Implications] = "implications",
        [PaperPartKind.FutureResearch] = "directions for future research",
        [PaperPartKind.Conclusion] = "conclusion",
    };

    /// <summary>The words the review is named by: "study" and "studies", or "source" and "sources".</summary>
    public string Source { get; init; } = "study";
    public string Sources { get; init; } = "studies";

    public IEnumerable<PaperPart> Of(params PaperPartKind[] kinds) => Parts.Where(p => kinds.Contains(p.Kind));

    /// <summary>The parts the model wrote, in reading order.</summary>
    public IEnumerable<PaperPart> Written => Parts.Where(p => p.Kind != PaperPartKind.CodeWritten);
}

/// <summary>Pass, note or fault. A note is worth a look; a fault is something a reader would notice as wrong.</summary>
public static class CheckOutcome
{
    public const string Pass = "pass";
    public const string Note = "note";
    public const string Fault = "fault";
}

/// <summary>One piece of evidence for a check: where (part and field), what (a sentence or a value) and why.</summary>
public sealed record CheckEvidence(string Part, string Field, string Text, string Detail);

/// <summary>The result of one check, with its evidence and a plain-English explanation of what it looks for.</summary>
public sealed class PaperCheck
{
    public int Number { get; set; }
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Outcome { get; set; } = CheckOutcome.Pass;
    public string Summary { get; set; } = "";
    public string Explanation { get; set; } = "";
    public List<CheckEvidence> Evidence { get; set; } = new();
}

/// <summary>self-check.json: every check of the finished paper, made in code without a model call.</summary>
public sealed class PaperSelfCheckFile
{
    public const string FileName = "self-check.json";
    public int Version { get; set; } = 1;
    public string CheckedUtc { get; set; } = "";
    public int Passed { get; set; }
    public int Notes { get; set; }
    public int Faults { get; set; }
    public List<PaperCheck> Checks { get; set; } = new();

    /// <summary>"8 checks passed, 2 notes, 1 fault".</summary>
    [JsonIgnore] public string SummaryLine => PaperSelfCheck.SummaryLine(Passed, Notes, Faults);
}

/// <summary>
/// The self-check of a finished paper (Lau, 25 November 2026). An editor's review of a real run found faults a reader
/// notices at once: numbers attached to the wrong count, a question with no answer, a missing discussion nobody
/// mentions, a figure repeated in every section. Every finished run now checks its own paper in code, after the report
/// is complete, and records each check with a pass, note or fault, its evidence and what it looks for. The checks
/// change nothing in the paper; they are shown to the reader and the owner as they are.
/// </summary>
public static partial class PaperSelfCheck
{
    /// <summary>Runs every check on <paramref name="input"/>, in order.</summary>
    public static PaperSelfCheckFile Run(PaperCheckInput input, DateTime? now = null)
    {
        var checks = new List<Func<PaperCheckInput, PaperCheck>>
        {
            NumbersCheck, AnswersCheck, PartsCheck, CitationLoadCheck, FiguresCheck, CoverageCheck, CitationCheckCheck, ReferencesCheck, TermsCheck, ReadabilityCheck,
        };
        var results = checks.Select((check, i) =>
        {
            var r = check(input);
            r.Number = i + 1;
            return r;
        }).ToList();
        return new PaperSelfCheckFile
        {
            CheckedUtc = (now ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture),
            Passed = results.Count(r => r.Outcome == CheckOutcome.Pass),
            Notes = results.Count(r => r.Outcome == CheckOutcome.Note),
            Faults = results.Count(r => r.Outcome == CheckOutcome.Fault),
            Checks = results,
        };
    }

    /// <summary>"8 checks passed, 2 notes, 1 fault"; the notes and faults are left out when there are none.</summary>
    public static string SummaryLine(int passed, int notes, int faults)
    {
        var parts = new List<string> { $"{passed} {(passed == 1 ? "check" : "checks")} passed" };
        if (notes > 0) parts.Add($"{notes} {(notes == 1 ? "note" : "notes")}");
        if (faults > 0) parts.Add($"{faults} {(faults == 1 ? "fault" : "faults")}");
        return string.Join(", ", parts);
    }

    /// <summary>The worst of the outcomes given: a fault over a note over a pass.</summary>
    internal static string Worst(params string[] outcomes) =>
        outcomes.Contains(CheckOutcome.Fault) ? CheckOutcome.Fault : outcomes.Contains(CheckOutcome.Note) ? CheckOutcome.Note : CheckOutcome.Pass;

    /// <summary>A sentence cut to a length a table cell can show.</summary>
    internal static string Short(string text, int max = 220)
    {
        string t = (text ?? "").Trim();
        return t.Length <= max ? t : t[..(max - 3)].TrimEnd() + "...";
    }

    /// <summary>"[1], [3] and [5]".</summary>
    internal static string RefList(IEnumerable<int> refs)
    {
        var list = refs.Select(r => $"[{r}]").ToList();
        return list.Count <= 1 ? string.Join("", list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    internal static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
