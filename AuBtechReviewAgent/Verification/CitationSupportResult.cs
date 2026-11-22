using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>A sentence in the synthesis or discussion together with the reference numbers it cites.</summary>
public record CitedSentence(string Field, int SentenceIndex, string Sentence, IReadOnlyList<int> References);

/// <summary>The verdict on one (sentence, reference) pair, as written to citation-audit.json.</summary>
public class CitationSupportResult
{
    public string Field { get; set; } = "";
    public int SentenceIndex { get; set; }
    public string Sentence { get; set; } = "";
    public int Reference { get; set; }

    /// <summary>supported, partially_supported, not_supported, or unverifiable (no text available / check failed).</summary>
    public string Verdict { get; set; } = "";

    /// <summary>"full text" or "abstract only": what the check could look at.</summary>
    public string EvidenceBasis { get; set; } = "";
    public string? EvidenceLocation { get; set; }
    public string? Quote { get; set; }

    /// <summary>
    /// True when the quoted passage really occurs in the excerpt the model pointed to. A "supported" verdict
    /// whose quote cannot be found is downgraded, because the model may have invented the evidence.
    /// </summary>
    public bool QuoteVerified { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>Set when a theme subsection cites a study that was not coded under that theme.</summary>
    public string? ThemeNote { get; set; }

    /// <summary>The part of the sentence attributed to this reference: what the check judged.</summary>
    public string AttributedText { get; set; } = "";

    /// <summary>The first check's verdict, before the second check (equal to Verdict when no second check ran).</summary>
    public string FirstVerdict { get; set; } = "";

    /// <summary>The independent second check, run on partly supported and not supported citations.</summary>
    public string? SecondVerdict { get; set; }
    public string? SecondQuote { get; set; }
    public string? SecondEvidenceLocation { get; set; }
    public string? SecondReason { get; set; }

    /// <summary>"first check" or "second check": whose verdict is the final one.</summary>
    public string ResolvedBy { get; set; } = "first check";
}

public class CitationSupportSummary
{
    public int Checked { get; set; }
    public int Supported { get; set; }
    public int PartiallySupported { get; set; }
    public int NotSupported { get; set; }
    public int Unverifiable { get; set; }
    public int InsufficientEvidence { get; set; }
    public int SecondChecked { get; set; }
    public int SecondCheckAgreed { get; set; }
    public int UpgradedBySecondCheck { get; set; }

    public static CitationSupportSummary From(IEnumerable<CitationSupportResult> results)
    {
        var list = results.ToList();
        return new CitationSupportSummary
        {
            Checked = list.Count,
            Supported = list.Count(r => r.Verdict == CitationSupportChecker.Supported),
            PartiallySupported = list.Count(r => r.Verdict == CitationSupportChecker.Partial),
            NotSupported = list.Count(r => r.Verdict == CitationSupportChecker.NotSupported),
            Unverifiable = list.Count(r => r.Verdict == CitationSupportChecker.Unverifiable),
            InsufficientEvidence = list.Count(r => r.Verdict == CitationSupportChecker.InsufficientEvidence),
            SecondChecked = list.Count(r => r.SecondVerdict != null),
            SecondCheckAgreed = list.Count(r => r.SecondVerdict != null && r.SecondVerdict == r.FirstVerdict),
            UpgradedBySecondCheck = list.Count(r => r.ResolvedBy == "second check"),
        };
    }

    /// <summary>One sentence for the report, e.g. "Of 14 citations checked, 11 were supported, ...".</summary>
    public string ToSentence()
    {
        if (Checked == 0) return "No inline citations were available for the automated support check.";
        string text = $"An automated check compared the part of each cited sentence attributed to a paper with the text of that paper. Of {Checked} citation{(Checked == 1 ? "" : "s")} checked, " +
               $"{Supported} {(Supported == 1 ? "was" : "were")} judged supported, {PartiallySupported} partially supported, {NotSupported} not supported, " +
               (InsufficientEvidence > 0 ? $"{InsufficientEvidence} could not be judged because only the abstract was available and it does not mention the claim, " : "") +
               $"and {Unverifiable} could not be verified.";
        if (SecondChecked > 0)
            text += $" {SecondChecked} citation{(SecondChecked == 1 ? " that was" : "s that were")} not fully supported went through a second, independent check with more of the paper's text; it agreed with the first check on {SecondCheckAgreed} and found verbatim support that raised the verdict for {UpgradedBySecondCheck}.";
        return text + " Every verdict, with the quoted evidence, is listed in citation-audit.json; these are model judgements and should be spot-checked by a human reviewer.";
    }
}


/// <summary>A cache for the citation check's answers, and the model they came from (part of the key).</summary>
public sealed record CheckCache(ReviewCache Cache, string Model);
