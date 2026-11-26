using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The citation check as one sequence of counts, in the order the steps ran: the first check, the second check on what
/// was not fully supported, the repair and the removal of what was still not supported, and what is left. The counts
/// are taken from the checks as they stood after each step, so they add up: the citations first checked, less those
/// dropped or removed, are the citations in the final text. They are written to citation-audit.json (CheckSteps), and
/// the methods text and the paper's self-check read them from there.
/// </summary>
public sealed record CitationCheckSteps
{
    /// <summary>Citations judged by the first check, one per cited sentence and reference.</summary>
    public int FirstChecked { get; init; }
    public int FirstSupported { get; init; }
    public int FirstPartiallySupported { get; init; }
    public int FirstNotSupported { get; init; }
    public int FirstInsufficientEvidence { get; init; }
    public int FirstUnverifiable { get; init; }

    /// <summary>Citations the second, independent check looked at, and those whose verdict it raised.</summary>
    public int SecondChecked { get; init; }
    public int SecondRaised { get; init; }

    /// <summary>Citations still not fully supported after both checks, which the repair looked at.</summary>
    public int ToRepair { get; init; }
    public bool RepairRan { get; init; }

    /// <summary>Cited sentences the repair rewrote, citations it dropped (a marker or a whole sentence), and citations still not supported that were removed.</summary>
    public int Rewritten { get; init; }
    public int Dropped { get; init; }
    public int Removed { get; init; }

    /// <summary>Citations that left the text in the sense check of the repaired paragraphs (none in practice).</summary>
    public int LostInSenseCheck { get; init; }

    /// <summary>Citations in the final text.</summary>
    public int FinalChecked { get; init; }

    /// <summary>The first and second check, from the checks as they stand straight after them.</summary>
    public static CitationCheckSteps AfterChecks(IReadOnlyCollection<CitationSupportResult> checks) => new()
    {
        FirstChecked = checks.Count,
        FirstSupported = checks.Count(r => r.FirstVerdict == CitationSupportChecker.Supported),
        FirstPartiallySupported = checks.Count(r => r.FirstVerdict == CitationSupportChecker.Partial),
        FirstNotSupported = checks.Count(r => r.FirstVerdict == CitationSupportChecker.NotSupported),
        FirstInsufficientEvidence = checks.Count(r => r.FirstVerdict == CitationSupportChecker.InsufficientEvidence),
        FirstUnverifiable = checks.Count(r => r.FirstVerdict == CitationSupportChecker.Unverifiable),
        SecondChecked = checks.Count(r => r.SecondVerdict != null),
        SecondRaised = checks.Count(r => r.ResolvedBy == "second check"),
        ToRepair = checks.Count(CitationRepairer.NeedsRepair),
        FinalChecked = checks.Count,
    };

    /// <summary>
    /// The repair and what it left, from the citation counts after each stage and the repair log. The stages are the
    /// first check, the first repair, the second pass and the sense check.
    /// </summary>
    public CitationCheckSteps WithRepair(bool ran, int afterRepair, int afterSecondPass, int final, IEnumerable<CitationRepair> repairs) => this with
    {
        RepairRan = ran,
        Rewritten = repairs.Count(r => r.Action == "rewrite"),
        Dropped = FirstChecked - afterRepair,
        Removed = afterRepair - afterSecondPass,
        LostInSenseCheck = afterSecondPass - final,
        FinalChecked = final,
    };

    private static string Citations(int n) => $"{n} citation{(n == 1 ? "" : "s")}";
    private static string Was(int n) => n == 1 ? "was" : "were";

    /// <summary>
    /// The paragraph of the methods that reports the check, in the order the steps ran. Every number is in
    /// citation-audit.json; <paramref name="final"/> is the summary of the final checks.
    /// </summary>
    public string Paragraph(CitationSupportSummary final)
    {
        if (FirstChecked == 0 && Removed == 0) return "No inline citations were available for the automated support check.";
        var text = new List<string>
        {
            "An automated check compared the part of each cited sentence attributed to a paper with the text of that paper.",
            $"The first check judged {Citations(FirstChecked)}: {FirstSupported} {Was(FirstSupported)} supported, {FirstPartiallySupported} partly supported, {FirstNotSupported} not supported, " +
                (FirstInsufficientEvidence > 0 ? $"{FirstInsufficientEvidence} could not be judged because only the abstract was available and it does not mention the claim, " : "") +
                $"and {FirstUnverifiable} could not be verified.",
        };
        if (SecondChecked > 0)
            text.Add($"The {SecondChecked} that {(SecondChecked == 1 ? "was" : "were")} not fully supported went through a second, independent check with more of the paper's text, which raised the verdict for {SecondRaised} of them.");
        if (RepairRan)
        {
            text.Add($"After both checks, {Citations(ToRepair)} {Was(ToRepair)} still not fully supported and went to the repair.");
            var done = new List<string>();
            if (Rewritten > 0) done.Add($"rewrote {Rewritten} cited sentence{(Rewritten == 1 ? "" : "s")}");
            if (Dropped > 0) done.Add($"dropped {Citations(Dropped)}, with the sentence when nothing else backed it");
            text.Add(done.Count == 0 ? "The repair changed no sentence." : $"The repair {string.Join(" and ", done)}, and the changed sentences were checked again.");
            if (Removed > 0)
                text.Add($"{Citations(Removed)} that {Was(Removed)} still not supported after that {Was(Removed)} removed, with the claim when no other citation backed it.");
            int left = FirstChecked - FinalChecked;
            if (left > 0) text.Add($"So {left} of the {FirstChecked} citations first checked left the text, and {FinalChecked} remain.");
        }
        text.Add(final.FinalSentence());
        text.Add("Every verdict, with the quoted evidence, every repair and every removed citation is listed in citation-audit.json; these are model judgements and should be spot-checked by a human reviewer.");
        return string.Join(" ", text);
    }
}
