using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The systematic review's evidence in the shared shape (<see cref="ReviewEvidence"/>): every synthesised record
/// with its APA reference line, its study type and method, its verified key findings and its paper text, and the
/// themes of the codebook with the codes that put each study in them.
/// </summary>
public static class SystematicEvidence
{
    public static ReviewEvidence Build(ThematicCodebook book, IReadOnlyList<IncludedPaperMetricRow> records,
        IReadOnlyList<ReferencedPaper> papers, IReadOnlyList<StudyExtraction> extractions)
    {
        var byPaper = papers.ToDictionary(p => p.ReferenceNumber);
        var byExtraction = extractions.ToDictionary(e => e.ReferenceNumber);
        var sources = records.Select(r =>
        {
            byExtraction.TryGetValue(r.ReferenceNumber, out var e);
            return new EvidenceSource
            {
                Reference = r.ReferenceNumber,
                Title = byPaper.TryGetValue(r.ReferenceNumber, out var paper) ? paper.Title : "",
                ReferenceLine = r.ApaCitation,
                Profile = e != null && e.Error == null ? $"Study type: {e.StudyType}; evidence: {e.EvidenceBasis}; method: {e.Method.Value}" : null,
                Findings = ThematicSynthesis.VerifiedFindings(e).Select(f => new EvidenceFinding(f.Id, f.Finding.Value, f.Finding.Quote)).ToList(),
                Codes = book.Codes.Where(c => c.Reference == r.ReferenceNumber).Select(c => c.Label).ToList(),
                Text = paper,
            };
        }).ToList();
        // A study with a paper but no record of its own still gets a source, so its evidence is never lost.
        sources.AddRange(papers.Where(p => records.All(r => r.ReferenceNumber != p.ReferenceNumber)).Select(p =>
        {
            byExtraction.TryGetValue(p.ReferenceNumber, out var e);
            return new EvidenceSource
            {
                Reference = p.ReferenceNumber,
                Title = p.Title,
                Profile = e != null && e.Error == null ? $"Study type: {e.StudyType}; evidence: {e.EvidenceBasis}; method: {e.Method.Value}" : null,
                Findings = ThematicSynthesis.VerifiedFindings(e).Select(f => new EvidenceFinding(f.Id, f.Finding.Value, f.Finding.Quote)).ToList(),
                Codes = book.Codes.Where(c => c.Reference == p.ReferenceNumber).Select(c => c.Label).ToList(),
                Text = p,
            };
        }));
        var themes = book.Themes.Select(t => new EvidenceTheme
        {
            Id = t.Id,
            Name = t.Name,
            Description = t.Description,
            Sources = t.Studies.ToList(),
            Codes = t.Studies.ToDictionary(r => r, r => (IReadOnlyList<string>)book.Codes.Where(c => c.Reference == r && t.CodeIds.Contains(c.Id)).Select(c => c.Label).ToList()),
        }).ToList();
        return new ReviewEvidence { Sources = sources, Themes = themes, BaseFacts = BaseFacts(extractions) };
    }

    /// <summary>
    /// Facts about the evidence base for the discussion, the limitations and the front matter, from the extraction (not
    /// the model). The designs are grouped by their MMAT category, one of a few fixed values, so a summary of the designs
    /// cannot list one design under several free-text names (issue #147); every category is listed with its count, so
    /// a summary can only repeat the extraction (issue #115). Then each study is listed with its design as its authors
    /// name it, its category and what it was read from, so a sentence that names the design of a cited study or counts
    /// the abstract-only studies can be written from facts (issue #146). A study whose extraction failed is named as such.
    /// </summary>
    public static string BaseFacts(IReadOnlyList<StudyExtraction> extractions)
    {
        if (extractions.Count == 0) return "(no extraction data)";
        int fullText = extractions.Count(e => e.EvidenceBasis == "full text");
        int notEmpirical = extractions.Count(e => e.AppraisalCategory == "not_empirical");
        // Studies with neither an abstract nor a full text (issue #148) are counted on their own, not as abstract-only.
        int noText = extractions.Count(e => e.AppraisalCategory == StudyExtractor.NotAppraised);
        var withCategory = extractions.Where(e => e.Error == null && !string.IsNullOrWhiteSpace(e.AppraisalCategory)).ToList();
        var categories = withCategory
            .GroupBy(e => CategoryLabel(e.AppraisalCategory)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, System.StringComparer.Ordinal)
            .Select(g => $"{g.Key} ({g.Count()})");
        int noCategory = extractions.Count - withCategory.Count;
        string categoryList = withCategory.Count == 0 ? "none was extracted" : string.Join(", ", categories);
        string perStudy = string.Join("; ", extractions.OrderBy(e => e.ReferenceNumber).Select(StudyFact));
        return $"{extractions.Count} studies; full text was read for {fullText} and only the abstract for {extractions.Count - fullText - noText}" +
               (noText > 0 ? $"; {noText} had neither an abstract nor a full text, so nothing was extracted from {(noText == 1 ? "it" : "them")}; " : "; ") +
               $"{notEmpirical} are not empirical (position papers, system descriptions or reviews). " +
               $"Study designs by MMAT category, the complete list from the data extraction (describe the designs only by these categories and counts): {categoryList}" +
               (noCategory > 0 && withCategory.Count > 0 ? $"; no category was extracted for {noCategory}." : ".") +
               $" Each study's design as its authors name it, its MMAT category and what it was read from (name the design of a cited study, or count the studies read from the abstract only, only as given here): {perStudy}.";
    }

    /// <summary>An MMAT category as the tables print it, e.g. "quantitative descriptive".</summary>
    public static string CategoryLabel(string category) => category.Trim().Replace('_', ' ');

    /// <summary>One study for the facts: "[3] survey, quantitative descriptive, abstract only".</summary>
    private static string StudyFact(StudyExtraction e)
    {
        if (e.Error != null) return $"[{e.ReferenceNumber}] nothing extracted";
        var parts = new List<string> { string.IsNullOrWhiteSpace(e.StudyType) ? "design not reported" : e.StudyType.Trim() };
        if (!string.IsNullOrWhiteSpace(e.AppraisalCategory)) parts.Add(CategoryLabel(e.AppraisalCategory));
        if (!string.IsNullOrWhiteSpace(e.EvidenceBasis)) parts.Add(e.EvidenceBasis);
        return $"[{e.ReferenceNumber}] {string.Join(", ", parts)}";
    }
}
