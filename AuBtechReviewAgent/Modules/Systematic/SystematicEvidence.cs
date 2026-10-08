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

    /// <summary>Facts about the evidence base for the limitations paragraph, from the extraction (not the model).</summary>
    public static string BaseFacts(IReadOnlyList<StudyExtraction> extractions)
    {
        if (extractions.Count == 0) return "(no extraction data)";
        int fullText = extractions.Count(e => e.EvidenceBasis == "full text");
        int notEmpirical = extractions.Count(e => e.AppraisalCategory == "not_empirical");
        var types = extractions.Where(e => e.Error == null && !string.IsNullOrWhiteSpace(e.StudyType))
            .GroupBy(e => e.StudyType.ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(6)
            .Select(g => $"{g.Key} ({g.Count()})");
        return $"{extractions.Count} studies; full text was read for {fullText} and only the abstract for {extractions.Count - fullText}; " +
               $"{notEmpirical} are not empirical (position papers, system descriptions or reviews). Most common study types: {string.Join(", ", types)}.";
    }
}
