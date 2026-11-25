using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Checks 2, 3 and 6: every research question has an answer, the closing parts are there, and the included studies are
/// cited. Run 50e9a8e3 had no discussion, no limitations and no conclusion because the discussion failed a check, and
/// nothing in the paper said so; a reader saw only a gap.
/// </summary>
public static partial class PaperSelfCheck
{
    // An answer that says plainly why the evidence cannot answer the question (the code-written one says "cannot answer it").
    private static readonly Regex SaysWhyUnanswered = new(
        @"\b(?:can ?not|could not|does not|do not|did not)\s+(?:be\s+)?answer|\bnone of the\b.*\bbears?\b|\bno (?:included )?(?:study|studies|source|sources|finding|findings|evidence)\b.*\b(?:bears?|address|answer|report)|\b(?:not enough|insufficient) evidence\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A sentence by which the paper owns up to a part it lacks.
    private static readonly Regex SaysPartFailed = new(@"\b(?:could not be written|was not written|were not written|is missing|are missing)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool IsEmpty(string? text) => string.IsNullOrWhiteSpace(text) || text.Trim() == "Pending...";

    /// <summary>Check 2: every research question has an answer, and the answer cites a source or says plainly why it cannot answer.</summary>
    internal static PaperCheck AnswersCheck(PaperCheckInput input)
    {
        var check = new PaperCheck
        {
            Id = "answers",
            Title = "Every research question answered",
            Explanation = $"Each research question should have its own answer that cites at least one {input.Source}. " +
                "When the evidence cannot answer a question, the answer should say so plainly rather than be left out.",
        };
        if (input.Questions.Count == 0)
        {
            check.Summary = "The review set no research questions, so there is nothing to answer.";
            return check;
        }
        var outcomes = new List<string>();
        foreach (var q in input.Questions)
        {
            string field = input.Of(PaperPartKind.Answer).FirstOrDefault(p => p.Text == q.Answer)?.Field ?? "";
            if (IsEmpty(q.Answer))
            {
                outcomes.Add(CheckOutcome.Fault);
                check.Evidence.Add(new(q.Id, field, Short(q.Question), "The paper gives no answer to this question."));
            }
            else if (ThematicSynthesis.CitedIn(q.Answer).Count > 0)
                outcomes.Add(CheckOutcome.Pass);
            else if (SaysWhyUnanswered.IsMatch(q.Answer))
            {
                outcomes.Add(CheckOutcome.Note);
                check.Evidence.Add(new(q.Id, field, Short(q.Answer), "The evidence does not answer this question, and the answer says so."));
            }
            else
            {
                outcomes.Add(CheckOutcome.Fault);
                check.Evidence.Add(new(q.Id, field, Short(q.Answer), $"The answer cites no {input.Source} and does not say why."));
            }
        }
        check.Outcome = Worst(outcomes.ToArray());
        int answered = outcomes.Count(o => o == CheckOutcome.Pass);
        check.Summary = check.Outcome == CheckOutcome.Pass
            ? $"All {Plural(input.Questions.Count, "question has", "questions have")} a cited answer."
            : $"{answered} of {Plural(input.Questions.Count, "question has", "questions have")} a cited answer.";
        return check;
    }

    /// <summary>Check 3: the discussion and the closing parts exist and are not empty; a part that is missing is a fault unless the paper says so.</summary>
    internal static PaperCheck PartsCheck(PaperCheckInput input)
    {
        var check = new PaperCheck
        {
            Id = "parts",
            Title = "Discussion and closing parts present",
            Explanation = "A review needs its discussion, the limitations of the evidence, the implications, the directions for future research and a conclusion. " +
                "If one of them could not be written, the paper should say so, so the reader does not take the gap for the review's view.",
        };
        bool owned = input.Parts.Any(p => SaysPartFailed.IsMatch(p.Text ?? ""));
        var outcomes = new List<string>();
        foreach (var (kind, name) in input.ExpectedParts)
        {
            var parts = input.Of(kind).ToList();
            if (parts.Count == 0 || parts.All(p => IsEmpty(p.Text)))
            {
                outcomes.Add(owned ? CheckOutcome.Note : CheckOutcome.Fault);
                check.Evidence.Add(new(char.ToUpperInvariant(name[0]) + name[1..], parts.FirstOrDefault()?.Field ?? "", "",
                    owned ? $"The {name} is missing, and the paper says a part could not be written." : $"The {name} is missing, and the paper does not say so."));
                continue;
            }
            int words = parts.Sum(p => Regex.Matches(p.Text, @"\S+").Count);
            if (words < 15)
            {
                outcomes.Add(CheckOutcome.Note);
                check.Evidence.Add(new(parts[0].Name, parts[0].Field, Short(parts[0].Text), $"The {name} has only {Plural(words, "word", "words")}."));
            }
        }
        check.Outcome = Worst(outcomes.ToArray());
        int missing = check.Evidence.Count(e => e.Detail.Contains("is missing"));
        check.Summary = missing > 0 ? $"{Plural(missing, "part is", "parts are")} missing."
            : check.Evidence.Count > 0 ? "All parts are there, but some are very short." : $"All {input.ExpectedParts.Count} parts are there.";
        return check;
    }

    /// <summary>Check 6: the included sources the results and discussion never cite, and the themes that rest on one source, each with its reason.</summary>
    internal static PaperCheck CoverageCheck(PaperCheckInput input)
    {
        var check = new PaperCheck
        {
            Id = "coverage",
            Title = $"Included {input.Sources} cited",
            Explanation = $"Every included {input.Source} should be cited somewhere in the results or the discussion, or the paper should say why not. " +
                $"A theme that rests on a single {input.Source} should have a reason on file, because one {input.Source} is thin ground for a theme.",
        };
        var cited = input.Of(PaperPartKind.Overview, PaperPartKind.Theme, PaperPartKind.Discussion).SelectMany(p => ThematicSynthesis.CitedIn(p.Text)).ToHashSet();
        var uncited = input.Included.Where(r => !cited.Contains(r)).OrderBy(r => r).ToList();
        var outcomes = new List<string>();
        foreach (var r in uncited)
        {
            bool reason = input.NotCitedReasons.TryGetValue(r, out var why) && !string.IsNullOrWhiteSpace(why);
            outcomes.Add(reason ? CheckOutcome.Note : CheckOutcome.Fault);
            check.Evidence.Add(new("Results and discussion", "", $"[{r}]", reason ? $"Not cited: {why}" : "Not cited, and no reason is on file."));
        }
        foreach (var theme in input.Themes.Where(t => t.Sources.Distinct().Count() == 1))
        {
            bool reason = !string.IsNullOrWhiteSpace(theme.SingleSourceReason);
            outcomes.Add(reason ? CheckOutcome.Note : CheckOutcome.Fault);
            check.Evidence.Add(new("Theme: " + theme.Name, "", $"[{theme.Sources[0]}]",
                reason ? $"Rests on one {input.Source}: {theme.SingleSourceReason}" : $"Rests on one {input.Source}, and no reason is on file."));
        }
        check.Outcome = Worst(outcomes.ToArray());
        int single = input.Themes.Count(t => t.Sources.Distinct().Count() == 1);
        check.Summary = $"{input.Included.Count - uncited.Count} of {Plural(input.Included.Count, input.Source, input.Sources)} cited" +
            (uncited.Count > 0 ? $"; {RefList(uncited)} not" : "") +
            (single > 0 ? $"; {Plural(single, "theme rests", "themes rest")} on one {input.Source}." : ".");
        return check;
    }
}
