using System.Text;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The evidence a review writes from, in one shape for every kind of review (module design M7). The systematic
/// review fills it from its extraction and codebook, and what its writers are shown must not change: the source
/// blocks, the reference lines and the facts about the evidence base are compared with the systematic review's own
/// text from before the shape existed.
/// </summary>
public class ReviewEvidenceTests
{
    private static ThematicCodebook Book() => new()
    {
        Codes =
        {
            new ThematicCode { Id = "C1", Reference = 1, Label = "summaries keep context short" },
            new ThematicCode { Id = "C2", Reference = 1, Label = "retrieval on demand" },
            new ThematicCode { Id = "C3", Reference = 2, Label = "summaries lose detail" },
        },
        Themes =
        {
            new Theme { Id = "T1", Name = "Summaries", Description = "Keeping the context short.", CodeIds = { "C1", "C3" }, Studies = { 1, 2 } },
            new Theme { Id = "T2", Name = "Retrieval", Description = "Fetching what is needed.", CodeIds = { "C2" }, Studies = { 1 } },
        },
    };

    private static readonly List<StudyExtraction> Extractions = new()
    {
        new StudyExtraction
        {
            ReferenceNumber = 1, StudyType = "experiment", EvidenceBasis = "full text", Method = new EvidencedValue { Value = "benchmark" },
            KeyFindings =
            {
                new EvidencedValue { Value = "Summaries halve tokens", Quote = "summaries cut tokens by half", QuoteVerified = true },
                new EvidencedValue { Value = "Unverified", Quote = "not in the paper", QuoteVerified = false },
            },
        },
        new StudyExtraction { ReferenceNumber = 2, Error = "failed", AppraisalCategory = "not_empirical" },
    };

    private static readonly List<ReferencedPaper> Papers = new()
    {
        new ReferencedPaper(1, "p1", "Short contexts", "An abstract.", new[] { new DocumentChunk("p1", 3, "summaries keep the context short and cut tokens by half") }),
        new ReferencedPaper(2, "p2", "Long contexts", "Summaries lose detail in long tasks.", Array.Empty<DocumentChunk>()),
    };

    private static readonly List<IncludedPaperMetricRow> Records = new()
    {
        new IncludedPaperMetricRow { ReferenceNumber = 1, ApaCitation = "Ana, A. (2025). Short contexts." },
        new IncludedPaperMetricRow { ReferenceNumber = 2, ApaCitation = "Bo, B. (2024). Long contexts." },
    };

    // The systematic review's own source block, as PrismaReviewEngine.Thematic wrote it before M7.
    private static string Before(int reference, Theme theme, ThematicCodebook book, int excerpts)
    {
        var sb = new StringBuilder();
        var paper = Papers.FirstOrDefault(p => p.ReferenceNumber == reference);
        var e = Extractions.FirstOrDefault(x => x.ReferenceNumber == reference);
        sb.AppendLine($"STUDY [{reference}] {paper?.Title}");
        if (e != null && e.Error == null)
            sb.AppendLine($"Study type: {e.StudyType}; evidence: {e.EvidenceBasis}; method: {e.Method.Value}");
        var codes = book.Codes.Where(c => c.Reference == reference && theme.CodeIds.Contains(c.Id)).ToList();
        if (codes.Count > 0) sb.AppendLine($"Codes in this theme: {string.Join("; ", codes.Select(c => c.Label))}");
        var findings = ThematicSynthesis.VerifiedFindings(e);
        foreach (var (id, f) in findings) sb.AppendLine($"Finding {id}: {f.Value} (verbatim: \"{f.Quote}\")");
        if (findings.Count == 0 && paper != null && !string.IsNullOrWhiteSpace(paper.Abstract))
            sb.AppendLine($"Abstract: {paper.Abstract.Trim()}");
        if (paper != null && excerpts > 0)
        {
            var terms = TextRelevance.Terms($"{theme.Name} {theme.Description} {string.Join(" ", codes.Select(c => c.Label))}");
            foreach (var chunk in GroundingContextBuilder.SelectChunks(paper.Chunks, terms, excerpts))
                sb.AppendLine($"[{reference}, page {chunk.PageNumber}]: {chunk.Text}");
        }
        return sb.ToString();
    }

    [Fact]
    public void TheSystematicReviewsWritersSeeTheSameEvidenceAsBefore()
    {
        var book = Book();
        var evidence = SystematicEvidence.Build(book, Records, Papers, Extractions);

        for (int t = 0; t < book.Themes.Count; t++)
            foreach (int reference in book.Themes[t].Studies)
                foreach (int excerpts in new[] { 0, 2 })
                    Assert.Equal(Before(reference, book.Themes[t], book, excerpts), evidence.SourceBlock(reference, evidence.Themes[t], excerpts));

        Assert.Equal("[1] - Ana, A. (2025). Short contexts.\n[2] - Bo, B. (2024). Long contexts.", evidence.ReferenceLines());
        Assert.Equal("[2] - Bo, B. (2024). Long contexts.\n[9] - (citation unavailable)", evidence.ReferenceLines(new[] { 9, 2 }));
        Assert.Equal("2 studies; full text was read for 1 and only the abstract for 1; 1 are not empirical (position papers, system descriptions or reviews). " +
                     "Study designs by MMAT category, the complete list from the data extraction (describe the designs only by these categories and counts): none was extracted. " +
                     "Each study's design as its authors name it, its MMAT category and what it was read from (name the design of a cited study, or count the studies read from the abstract only, only as given here): [1] experiment, full text; [2] nothing extracted.", evidence.BaseFacts);
    }

    [Fact]
    public void TheFactsGroupTheDesignsByMmatCategory()
    {
        // Issue #147: four names for mixed methods are one category, and every category is listed (issue #115).
        (string Type, string Category)[] studies =
        {
            ("case study with mixed methods", "mixed_methods"), ("mixed methods case study", "mixed_methods"), ("mixed methods intervention study", "mixed_methods"),
            ("mixed methods with experimental and control groups", "mixed_methods"), ("case study", "qualitative"), ("interview study", "qualitative"),
            ("cluster-randomized controlled trial", "quantitative_randomized"), ("survey", "quantitative_descriptive"), ("position paper", "not_empirical"),
        };
        var extractions = studies.Select((s, i) => new StudyExtraction { ReferenceNumber = i + 1, StudyType = s.Type, AppraisalCategory = s.Category, EvidenceBasis = "full text" })
            .Append(new StudyExtraction { ReferenceNumber = 99, Error = "failed" }).ToList();

        string facts = SystematicEvidence.BaseFacts(extractions);

        Assert.Contains("Study designs by MMAT category, the complete list from the data extraction (describe the designs only by these categories and counts): mixed methods (4), qualitative (2), not empirical (1), " +
                        "quantitative descriptive (1), quantitative randomized (1); no category was extracted for 1.", facts);
        Assert.StartsWith("10 studies; full text was read for 9 and only the abstract for 1; 1 are not empirical", facts);
        Assert.DoesNotContain("mixed methods case study (1)", facts);
        Assert.EndsWith("none was extracted. Each study's design as its authors name it, its MMAT category and what it was read from (name the design of a cited study, or count the studies read from the abstract only, only as given here): [1] nothing extracted.",
            SystematicEvidence.BaseFacts(new[] { new StudyExtraction { ReferenceNumber = 1, Error = "failed" } }));
    }

    [Fact]
    public void TheFactsNameEachStudysDesignAndWhatItWasReadFrom()
    {
        // Issue #146: the writer called [7, 8] one trial and miscounted the abstract-only studies; both are known in code.
        var extractions = new[]
        {
            new StudyExtraction { ReferenceNumber = 8, StudyType = "mixed methods case study ", AppraisalCategory = "mixed_methods", EvidenceBasis = "abstract only" },
            new StudyExtraction { ReferenceNumber = 7, StudyType = "cluster-randomized controlled trial", AppraisalCategory = "quantitative_randomized", EvidenceBasis = "abstract only" },
            new StudyExtraction { ReferenceNumber = 6, StudyType = "", AppraisalCategory = "qualitative", EvidenceBasis = "full text" },
        };

        Assert.EndsWith("(name the design of a cited study, or count the studies read from the abstract only, only as given here): [6] design not reported, qualitative, full text; " +
                        "[7] cluster-randomized controlled trial, quantitative randomized, abstract only; [8] mixed methods case study, mixed methods, abstract only.",
            SystematicEvidence.BaseFacts(extractions));
    }

    [Fact]
    public void EachThemeGetsTheFactsOfItsOwnStudies()
    {
        // Issue #146: section 4.3 said two of its four studies were abstract-only, but three were.
        var byExtraction = new[]
        {
            new StudyExtraction { ReferenceNumber = 6, StudyType = "interview study", AppraisalCategory = "qualitative", EvidenceBasis = "full text" },
            new StudyExtraction { ReferenceNumber = 7, StudyType = "cluster-randomized controlled trial", AppraisalCategory = "quantitative_randomized", EvidenceBasis = "abstract only" },
            new StudyExtraction { ReferenceNumber = 8, StudyType = "mixed methods case study", AppraisalCategory = "mixed_methods", EvidenceBasis = "abstract only" },
            new StudyExtraction { ReferenceNumber = 11, StudyType = "case study with mixed methods", AppraisalCategory = "mixed_methods", EvidenceBasis = "abstract only" },
            new StudyExtraction { ReferenceNumber = 12, Error = "failed" },
        }.ToDictionary(e => e.ReferenceNumber);

        Assert.Equal("Of the 4 studies in this theme, full text was read for 1 ([6]) and only the abstract for 3 ([7], [8], [11]). " +
                     "By MMAT category: mixed methods (2: [8], [11]), qualitative (1: [6]), quantitative randomized (1: [7]).",
            SystematicEvidence.ThemeFacts(new[] { 11, 8, 7, 6 }, byExtraction));
        Assert.Equal("Of the 1 study in this theme, full text was read for 0 and only the abstract for 0; what 1 ([12]) was read from is not on file. " +
                     "No MMAT category was extracted for them.", SystematicEvidence.ThemeFacts(new[] { 12 }, byExtraction));
        Assert.Equal("", SystematicEvidence.ThemeFacts(Array.Empty<int>(), byExtraction));

        // The systematic review's themes carry their facts; the shared shape leaves them empty for other reviews.
        var evidence = SystematicEvidence.Build(Book(), Records, Papers, Extractions);
        Assert.Equal("Of the 1 study in this theme, full text was read for 1 ([1]) and only the abstract for 0. No MMAT category was extracted for them.", evidence.Themes[1].Facts);
        Assert.Equal("", new EvidenceTheme().Facts);
    }

    [Fact]
    public void StudiesWithNoTextAreCountedApartInTheFacts()
    {
        // Issue #148: a study with only a title is neither abstract-only nor non-empirical.
        var extractions = new[]
        {
            new StudyExtraction { ReferenceNumber = 1, StudyType = "survey", EvidenceBasis = "full text", AppraisalCategory = "quantitative_descriptive" },
            new StudyExtraction { ReferenceNumber = 2, StudyType = "survey", EvidenceBasis = "abstract only", AppraisalCategory = "quantitative_descriptive" },
            new StudyExtraction { ReferenceNumber = 3, EvidenceBasis = StudyExtractor.NoText, AppraisalCategory = StudyExtractor.NotAppraised },
        };

        Assert.Equal("3 studies; full text was read for 1 and only the abstract for 1; 1 had neither an abstract nor a full text, so nothing was extracted from it; " +
                     "0 are not empirical (position papers, system descriptions or reviews). Study designs by MMAT category, the complete list from the data extraction (describe the designs only by these categories and counts): quantitative descriptive (2), not appraised (1). " +
                     "Each study's design as its authors name it, its MMAT category and what it was read from (name the design of a cited study, or count the studies read from the abstract only, only as given here): " +
                     "[1] survey, quantitative descriptive, full text; [2] survey, quantitative descriptive, abstract only; [3] design not reported, not appraised, no text.",
            SystematicEvidence.BaseFacts(extractions));
    }

    [Fact]
    public void TheShapeHoldsOnlyWhatWasCheckedInCode()
    {
        var evidence = SystematicEvidence.Build(Book(), Records, Papers, Extractions);

        var first = evidence.Source(1)!;
        Assert.Equal(new[] { "summaries cut tokens by half" }, first.Findings.Select(f => f.Quote)); // the unverified quote is not evidence
        Assert.Equal("F1.1", first.Findings[0].Id);
        Assert.Equal(new[] { "summaries keep context short", "retrieval on demand" }, first.Codes);
        Assert.Empty(evidence.Source(2)!.Findings); // a failed extraction gives no findings; the writer gets the abstract
        Assert.Contains("Abstract: Summaries lose detail in long tasks.", evidence.SourceBlock(2, evidence.Themes[0], 0));
        Assert.Equal(new[] { 1, 2 }, evidence.Themes[0].Sources);
        Assert.Equal(new[] { "summaries lose detail" }, evidence.Themes[0].Codes[2]);
        Assert.Equal(new[] { "summaries cut tokens by half" }, evidence.VerifiedQuotes()[1]);
        Assert.Empty(evidence.VerifiedQuotes()[2]);
        Assert.Contains("Codes: summaries keep context short; retrieval on demand", evidence.SourceBlock(1, null, 0)); // outside a theme: every code
    }
}
