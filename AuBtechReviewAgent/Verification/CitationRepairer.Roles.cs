using System;

namespace AuBtechReviewAgent;

public static partial class CitationRepairer
{
    /// <summary>
    /// What a section of the paper is for, as the repair prompt names it, or null for a part without a fixed role. The
    /// audit of two real runs found that the repair, not knowing the role, rewrote a direction for future research and
    /// the readings in the discussion into restatements of findings; with the role it can keep the sentence and drop the
    /// citation instead. Fields of the systematic review ("discussionItem") and of the multivocal review ("discussion").
    /// </summary>
    public static string? SectionRole(string field)
    {
        string name = field.EndsWith("Item", StringComparison.Ordinal) ? field[..^"Item".Length] : field;
        if (name.StartsWith("researchAnswer", StringComparison.Ordinal)) return "Answer to a research question: answers it from the findings";
        if (name.StartsWith("synthesis", StringComparison.Ordinal) || name.StartsWith("theme", StringComparison.Ordinal)) return "Results: reports what the sources found";
        return name switch
        {
            "discussion" => "Discussion: interprets the findings",
            "futureResearch" => "Future research: proposes a study",
            "implications" => "Implications: says what to do",
            "evidenceLimitations" or "limitations" => "Limitations: says what limits the evidence",
            "rationale" => "Rationale: states the problem and the gap",
            "conclusion" => "Conclusion: says what the findings mean together",
            "summary" => "Summary for practitioners: says what to do or check",
            _ => null,
        };
    }

    /// <summary>The part of the repair prompt that keeps a sentence's role, with the section's role when it has one.</summary>
    public static string RoleRule(string field) =>
        (SectionRole(field) is { } role ? $"SECTION ROLE: {role}.\n" : "") +
        "Keep each sentence's role in its section. If the evidence supports only the finding behind a sentence, not the sentence's reading, proposal or recommendation, prefer \"drop_citation\", so the sentence keeps its role without the citation. Never turn a proposal or a recommendation into a restatement of a finding.";
}
