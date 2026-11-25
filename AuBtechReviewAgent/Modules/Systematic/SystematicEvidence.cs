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
        IReadOnlyList<ReferencedPaper> papers, IReadOnlyList<StudyExtraction> extractions, IReadOnlyList<string>? questions = null)
    {
        questions ??= System.Array.Empty<string>();
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
                Weak = WeakBecause(e),
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
                Weak = WeakBecause(e),
            };
        }));
        var themes = book.Themes.Select(t => new EvidenceTheme
        {
            Id = t.Id,
            Name = t.Name,
            Description = t.Description,
            Sources = t.Studies.ToList(),
            Codes = t.Studies.ToDictionary(r => r, r => (IReadOnlyList<string>)book.Codes.Where(c => c.Reference == r && t.CodeIds.Contains(c.Id)).Select(c => c.Label).ToList()),
            Facts = ThemeFacts(t.Studies, byExtraction),
            // The questions the codebook says the theme answers, with their text (issue #124).
            Question = string.Join("; ", questions.Select((q, i) => (Id: ReviewWriter.QuestionId(i), Text: q.Trim()))
                .Where(q => t.Questions?.Contains(q.Id) == true).Select(q => $"{q.Id}: {q.Text}")),
        }).ToList();
        return new ReviewEvidence { Sources = sources, Themes = themes, BaseFacts = BaseFacts(extractions), LeftOut = book.Uncoded.OrderBy(u => u.Reference).ToList() };
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

    /// <summary>
    /// Facts about one theme's studies (issue #146): how many were read in full text and how many from the abstract
    /// only, and their MMAT categories, each with the studies it counts, so a theme's subsection can say "three of the
    /// four studies were read from the abstract" only when it is so.
    /// </summary>
    public static string ThemeFacts(IReadOnlyCollection<int> studies, IReadOnlyDictionary<int, StudyExtraction> byExtraction)
    {
        if (studies.Count == 0) return "";
        var ordered = studies.Distinct().OrderBy(r => r).ToList();
        StudyExtraction? Of(int r) => byExtraction.GetValueOrDefault(r);
        static string Refs(IEnumerable<int> refs) => string.Join(", ", refs.Select(r => $"[{r}]"));
        static string Counted(List<int> refs) => refs.Count == 0 ? "0" : $"{refs.Count} ({Refs(refs)})";
        var fullText = ordered.Where(r => Of(r)?.EvidenceBasis == "full text").ToList();
        var abstractOnly = ordered.Where(r => Of(r)?.EvidenceBasis == "abstract only").ToList();
        var noText = ordered.Where(r => Of(r)?.EvidenceBasis == StudyExtractor.NoText).ToList();
        var unknown = ordered.Except(fullText).Except(abstractOnly).Except(noText).ToList();
        string read = $"Of the {ordered.Count} {(ordered.Count == 1 ? "study" : "studies")} in this theme, full text was read for {Counted(fullText)} and only the abstract for {Counted(abstractOnly)}" +
                      (noText.Count > 0 ? $"; {Counted(noText)} had neither an abstract nor a full text" : "") +
                      (unknown.Count > 0 ? $"; what {Counted(unknown)} was read from is not on file" : "") + ".";
        var categorised = ordered.Where(r => Of(r) is { Error: null } e && !string.IsNullOrWhiteSpace(e.AppraisalCategory)).ToList();
        var groups = categorised.GroupBy(r => CategoryLabel(Of(r)!.AppraisalCategory))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, System.StringComparer.Ordinal)
            .Select(g => $"{g.Key} ({g.Count()}: {Refs(g)})");
        var uncategorised = ordered.Except(categorised).ToList();
        string designs = categorised.Count == 0 ? " No MMAT category was extracted for them."
            : $" By MMAT category: {string.Join(", ", groups)}" + (uncategorised.Count > 0 ? $"; no category for {Counted(uncategorised)}." : ".");
        return read + designs;
    }

    /// <summary>
    /// Why a study is weak evidence on its own, from its extraction and appraisal, or null: nothing was extracted, it had
    /// no text, it is not empirical, or it fails more than half of the MMAT criteria it was appraised on ("can't tell", which mostly
    /// means the abstract does not say, is not a failure). A study without an extraction is not judged. Used so the rationale does not open on such a study alone as established fact.
    /// </summary>
    public static string? WeakBecause(StudyExtraction? e)
    {
        if (e == null) return null;
        if (e.Error != null) return "nothing was extracted from it";
        if (e.AppraisalCategory == StudyExtractor.NotAppraised) return "it had neither an abstract nor a full text";
        if (e.AppraisalCategory == "not_empirical") return "it is not empirical (a position paper, system description or review)";
        int no = e.Appraisal.Count(a => a.Answer == "no");
        if (e.Appraisal.Count > 0 && no * 2 > e.Appraisal.Count) return $"it fails {no} of the {e.Appraisal.Count} MMAT criteria";
        return null;
    }

    /// <summary>The included studies that are weak evidence on their own (<see cref="WeakBecause"/>), by reference number.</summary>
    public static IReadOnlyDictionary<int, string> WeakSources(IEnumerable<StudyExtraction> extractions) =>
        extractions.Select(e => (e.ReferenceNumber, Why: WeakBecause(e))).Where(x => x.Why != null)
            .GroupBy(x => x.ReferenceNumber).ToDictionary(g => g.Key, g => g.First().Why!);

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
