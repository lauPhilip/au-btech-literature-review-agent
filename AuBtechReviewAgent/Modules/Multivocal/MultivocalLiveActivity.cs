using System;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The live line of a running multivocal review (<see cref="LiveLine"/>), from the runner's status: the step it runs
/// (<see cref="MultivocalRunner.StepFor"/>) and the progress line that step reports ("12 of 30 sources screened",
/// <c>3 of 8 searches · GitHub: "agent runtime"</c>). Nothing here calls a model. Search strings and research questions
/// are the reviewer's text and are shown as text, cut to <see cref="LiveActivity.MaxSubject"/> characters.
/// </summary>
public static class MultivocalLiveActivity
{
    private static readonly Regex SearchLine = new("searches · (?<subject>.+)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex QuestionLine = new("^(?<question>.+?): (?<rest>(placing values|second placement|\\d+ findings in).*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex WritingLine = new("writing steps · (?<what>.+)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex RangeLine = new(@"values (\d+)–(\d+) of (\d+)", RegexOptions.CultureInvariant);

    /// <summary>The line for a multivocal run; null when it is not running.</summary>
    public static LiveLine? For(MultivocalRunStatus? status, string? found = null)
    {
        if (status is not { Running: true }) return null;
        string progress = status.Progress ?? "";
        var of = LiveActivity.Of(progress);
        switch (status.Step)
        {
            case "Searching":
                if (SearchLine.Match(progress) is { Success: true } s && LiveActivity.SearchFor(s.Groups["subject"].Value) is { } searching)
                    return LiveActivity.Line("search", searching, of is { } n ? $"search {n.Done} of {n.Total}" : found);
                return LiveActivity.Line("search", "Searching the chosen sources", found);
            case "Screening":
                if (progress.Contains("second screenings", StringComparison.Ordinal) && of is { } two)
                    return LiveActivity.Line("screen", $"Screening source {LiveActivity.Next(two)} of {two.Total} a second time", found);
                return LiveActivity.Line("screen", of is { } one ? $"Screening source {LiveActivity.Next(one)} of {one.Total}" : "Screening the sources", found);
            case "Scoring the quality":
                return LiveActivity.Line("quality", of is { } q ? $"Scoring the quality of source {LiveActivity.Next(q)} of {q.Total}" : "Reading the pages of the included sources", found);
            case "Proposing the map":
                return LiveActivity.Line("map", "Proposing the systematic map from the research questions", found);
            case "Fixing the map":
                return LiveActivity.Line("map", "Fixing the systematic map", found);
            case "Extracting":
                return LiveActivity.Line("extract", of is { } x ? $"Extracting source {LiveActivity.Next(x)} of {x.Total}" : "Extracting what each source says", found);
            case "Synthesising":
                return Synthesising(progress, found);
            case "Writing the paper":
                return Writing(progress, of, found);
            case "Checking the citations":
                if (progress.StartsWith("Repairing", StringComparison.Ordinal)) return LiveActivity.Line("repair", "Repairing the citations that failed the check", found);
                if (progress.StartsWith("Second check", StringComparison.Ordinal) && of is { } sc)
                    return LiveActivity.Line("check-second", $"Checking the citations again: source {LiveActivity.Next(sc)} of {sc.Total}", found);
                return LiveActivity.Line("check", of is { } c ? $"Checking the citations: source {LiveActivity.Next(c)} of {c.Total}" : "Checking every citation against its kept page", found);
            default:
                return LiveActivity.Line("protocol", "Starting the review", found);
        }
    }

    private static LiveLine Synthesising(string progress, string? found)
    {
        if (progress.StartsWith("Checking that each", StringComparison.Ordinal))
            return LiveActivity.Line("synthesis", "Checking that each quote supports its value", found);
        if (QuestionLine.Match(progress) is { Success: true } m)
        {
            string question = LiveActivity.Cut(m.Groups["question"].Value);
            string rest = m.Groups["rest"].Value;
            if (rest.StartsWith("second placement", StringComparison.Ordinal))
                return LiveActivity.Line("synthesis", $"Placing the values of “{question}” a second time", found);
            if (RangeLine.Match(rest) is { Success: true } r)
                return LiveActivity.Line("synthesis", $"Placing values {r.Groups[1].Value}–{r.Groups[2].Value} of {r.Groups[3].Value} for “{question}”", found);
            return LiveActivity.Line("synthesis", $"Naming the themes for “{question}”", found);
        }
        return LiveActivity.Line("synthesis", "Finding the themes in what the sources say", found);
    }

    private static LiveLine Writing(string progress, (int Done, int Total)? of, string? found)
    {
        var m = WritingLine.Match(progress);
        if (!m.Success) return LiveActivity.Line("writing", "Writing the paper", found);
        string what = m.Groups["what"].Value;
        if (what.StartsWith("theme sections:", StringComparison.Ordinal) && LiveActivity.Of(what) is { } t)
            return LiveActivity.Line("writing", $"Writing theme section {LiveActivity.Next(t)} of {t.Total}", found);
        if (what.Contains("peer review", StringComparison.Ordinal))
            return LiveActivity.Line("peer-review", "Reading the draft as a peer reviewer would", found);
        return LiveActivity.Line("writing", $"Writing {LiveActivity.Cut(what)}", of is { } w ? $"writing step {w.Done} of {w.Total}" : found);
    }

    /// <summary>"312 found · 41 included": the sources on file so far, left out while there are none.</summary>
    public static string? Counts(MultivocalLedger? ledger, MultivocalScreeningFile? screening)
    {
        if (ledger == null || ledger.Sources.Count == 0) return null;
        string count = $"{ledger.Sources.Count} found";
        return screening == null ? count : $"{count} · {screening.Count("Included")} included";
    }
}
