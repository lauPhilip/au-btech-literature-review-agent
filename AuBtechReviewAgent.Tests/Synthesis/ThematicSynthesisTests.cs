using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Thematic synthesis: coding anchored to verified evidence, the codebook guard, the second coding, the
/// coverage fill pass, the revision guard and the citation repair pass, unit by unit.
/// </summary>
public class ThematicSynthesisTests
{
    private static StudyExtraction Extraction(int reference, params (string Value, string Quote, bool Verified)[] findings) => new()
    {
        ReferenceNumber = reference,
        StudyType = "case study",
        EvidenceBasis = "abstract only",
        KeyFindings = findings.Select(f => new EvidencedValue { Value = f.Value, Quote = f.Quote, QuoteVerified = f.Verified }).ToList(),
    };

    private static ReferencedPaper Paper(int reference, string abstractText) =>
        new(reference, $"P{reference}", $"Paper {reference}", abstractText, Array.Empty<DocumentChunk>());

    [Fact]
    public void OnlyVerifiedFindingsGetIds()
    {
        var e = Extraction(4, ("Loops recover", "quote a", true), ("Invented", "quote b", false), ("not reported", "", false));
        var ids = ThematicSynthesis.VerifiedFindings(e).Select(f => f.Id).ToList();
        Assert.Equal(new[] { "F4.1" }, ids);
        Assert.Empty(ThematicSynthesis.VerifiedFindings(new StudyExtraction { ReferenceNumber = 1, Error = "failed" }));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(70, 8)]
    public void SubsectionLengthGrowsWithTheNumberOfStudies(int studies, int paragraphs) =>
        Assert.Equal(paragraphs, ThematicSynthesis.ParagraphsFor(studies));

    [Fact]
    public void ThemeRangeScalesWithTheReviewAndRespectsTheLimit()
    {
        Assert.Equal((4, 8), ThematicSynthesis.ThemeRange(70, 8));
        Assert.Equal((2, 2), ThematicSynthesis.ThemeRange(2, 8));
        Assert.Equal((4, 5), ThematicSynthesis.ThemeRange(30, 5));
    }

    [Fact]
    public async Task CodesAreAnchoredToVerifiedFindingsOrACheckedQuote()
    {
        var papers = new[] { Paper(1, "Agent loops recover from faults through supervisor checks."), Paper(2, "Governance layers audit agent actions in production.") };
        var extractions = new[] { Extraction(1, ("Loops recover from faults", "Agent loops recover from faults through supervisor checks.", true)), Extraction(2) };
        var chat = new FakeChatService()
            // First answer uses a finding id that belongs to another study: rejected, then corrected.
            .Returns("""{"studies":[{"ref":1,"codes":[{"label":"fault recovery","findings":["F2.1"]}]},{"ref":2,"codes":[]}]}""")
            .Returns("""{"studies":[{"ref":1,"codes":[{"label":"fault recovery by supervisors","findings":["F1.1"]}]},{"ref":2,"codes":[{"label":"auditing agent actions","findings":[],"quote":"Governance layers audit agent actions in production."},{"label":"invented code","findings":[],"quote":"a sentence that is nowhere in the abstract at all"}]}]}""");

        var (codes, uncoded) = await ThematicSynthesis.CodeStudiesAsync(chat, "objective", papers, extractions, batchSize: 6, parallelism: 1);

        Assert.Contains("uses a finding id that is not listed", chat.Prompts[1]);
        Assert.Empty(uncoded);
        Assert.Equal(new[] { "C1", "C2", "C3" }, codes.Select(c => c.Id));
        Assert.Equal("verified finding", codes[0].Anchor);
        Assert.Equal("verified quote", codes[1].Anchor);
        Assert.Equal("unanchored", codes[2].Anchor);
    }

    [Fact]
    public async Task AStudyWithoutRelevantCodesIsRecordedWithItsReason()
    {
        var chat = new FakeChatService().Returns("""{"studies":[{"ref":1,"codes":[],"reason":"About crop yields."}]}""");
        var (codes, uncoded) = await ThematicSynthesis.CodeStudiesAsync(chat, "objective", new[] { Paper(1, "Wheat.") }, new[] { Extraction(1) }, 6, 1);
        Assert.Empty(codes);
        Assert.Equal("About crop yields.", Assert.Single(uncoded).Reason);
    }

    [Fact]
    public async Task TheCodingStepGetsTheResearchQuestionsAndWithoutThemItsPromptIsAsBefore()
    {
        // The audit of two real runs: codes were dropped as "not bearing on the objective" though they answered a question.
        var withQuestions = new FakeChatService().Returns("""{"studies":[{"ref":1,"codes":[],"reason":"About crop yields."}]}""");
        await ThematicSynthesis.CodeStudiesAsync(withQuestions, "objective", new[] { Paper(1, "Wheat.") }, new[] { Extraction(1) }, 6, 1,
            questions: new[] { "How do agent loops recover?" });
        string prompt = withQuestions.Prompts.Single();
        Assert.Contains(ReviewWriter.AimBlock("objective", new[] { "How do agent loops recover?" }), prompt);
        Assert.Contains("bears on the review objective or on any of the research questions.", prompt);

        var without = new FakeChatService().Returns("""{"studies":[{"ref":1,"codes":[],"reason":"About crop yields."}]}""");
        await ThematicSynthesis.CodeStudiesAsync(without, "objective", new[] { Paper(1, "Wheat.") }, new[] { Extraction(1) }, 6, 1);
        Assert.Contains("REVIEW OBJECTIVE: \"objective\"\n", without.Prompts.Single().Replace("\r\n", "\n"));
        Assert.DoesNotContain("RESEARCH QUESTIONS", without.Prompts.Single());
        Assert.Contains("that bears on the review objective. Codes should", without.Prompts.Single());
    }

    [Fact]
    public async Task CodebookIsRejectedUntilEveryCodedStudyIsPlacedOrExplained()
    {
        var codes = new List<ThematicCode>
        {
            new() { Id = "C1", Reference = 1, Label = "fault recovery" },
            new() { Id = "C2", Reference = 2, Label = "auditing" },
            new() { Id = "C3", Reference = 3, Label = "unrelated" },
        };
        var chat = new FakeChatService()
            .Returns("""{"themes":[{"name":"Recovery","description":"d","codes":["C1"]}],"unassigned":[]}""")
            .Returns("""{"themes":[{"name":"Recovery","description":"d","codes":["C1"]},{"name":"Oversight","description":"d","codes":["C2","C1"]}],"unassigned":[{"code":"C3","reason":"Not about agents."}]}""");

        var (themes, unplaced) = await ThematicSynthesis.BuildCodebookAsync(chat, "objective", codes, 8);

        Assert.Contains("studies 2, 3 are in no theme", chat.Prompts[1]);
        Assert.Equal(new[] { "T1", "T2" }, themes.Select(t => t.Id));
        Assert.Equal(new[] { 1, 2 }, themes[1].Studies); // derived from the codes, not from the model
        Assert.Equal(3, Assert.Single(unplaced).Reference);
        Assert.Contains("Not about agents", unplaced[0].Reason);
    }

    [Fact]
    public void SecondCodingAgreementIsKappaOverStudyThemeCells()
    {
        var themes = new List<Theme>
        {
            new() { Id = "T1", Name = "A", Studies = new() { 1, 2 } },
            new() { Id = "T2", Name = "B", Studies = new() { 3 } },
        };
        var same = ThematicSynthesis.CompareCodings(themes, new Dictionary<int, HashSet<string>>
        {
            [1] = new() { "T1" }, [2] = new() { "T1" }, [3] = new() { "T2" },
        });
        Assert.Equal(1.0, same.Kappa!.Value, 3);
        Assert.Empty(same.Disagreements);

        var different = ThematicSynthesis.CompareCodings(themes, new Dictionary<int, HashSet<string>>
        {
            [1] = new() { "T1" }, [2] = new() { "T2" }, [3] = new() { "T2" },
        });
        Assert.Equal(6, different.CellsCompared);
        Assert.Equal(2, different.Disagreements.Count);
        Assert.True(different.Kappa < 1.0);
    }

    [Fact]
    public void ARevisionMayNotDropACitationOrCiteOutsideTheList()
    {
        Assert.Null(ThematicSynthesis.RevisionProblem("A [1]. B [2].", "A and B agree [1, 2]. C adds [3].", 3));
        Assert.Contains("dropped the citation of [2]", ThematicSynthesis.RevisionProblem("A [1]. B [2].", "A [1].", 3));
        Assert.Contains("outside the reference list", ThematicSynthesis.RevisionProblem("A [1].", "A [1]. B [9].", 3));
        Assert.Contains("empty", ThematicSynthesis.RevisionProblem("A [1].", " ", 3));
    }

    [Theory]
    [InlineData("Loops recover [1, 2].", 1, "Loops recover [2].")]
    [InlineData("Loops recover [1].", 1, "Loops recover.")]
    [InlineData("Loops recover [1-3] and fail [1].", 1, "Loops recover [2, 3] and fail.")]
    public void RemoveReferenceDropsOnlyThatNumber(string sentence, int reference, string expected) =>
        Assert.Equal(expected, PrismaReviewEngine.RemoveReference(sentence, reference));

    [Theory]
    [InlineData("Loops recover [1].", new[] { 1 }, true)]
    [InlineData("Loops recover [1], but fail [2].", new[] { 1 }, true)]
    [InlineData("Sources [1, 2] show that loops recover.", new[] { 1 }, true)]           // [2] stays and carries the sentence
    [InlineData("Sources [1, 2] show that loops recover.", new[] { 1, 2 }, true)]        // "Sources show that loops recover." still reads
    [InlineData("Loops recover [1] and fail.", new[] { 1 }, true)]
    [InlineData("Whereas [6] and [14] prioritise gating, [3] does not.", new[] { 14 }, false)] // "[6] and prioritise"
    [InlineData("Neither [6] nor [14] examines it.", new[] { 6 }, false)]
    [InlineData("[14]'s harness reduced the rate.", new[] { 14 }, false)]
    public void ACitationTheSentenceReadsThroughIsNotDropped(string sentence, int[] references, bool canDrop) =>
        Assert.Equal(canDrop, CitationRepairer.CanDrop(sentence, references));

    [Fact]
    public void ARevisionNamesOtherSectionsByTheirHeading()
    {
        var headings = new Dictionary<string, string> { ["synthesis-2"] = "Memory tiers", ["synthesis-12"] = "Costs" };
        Assert.Equal("As discussed in the section \u201cMemory tiers\u201d, tiers help [1].", ReviewWriter.NameSections("As discussed in synthesis-2, tiers help [1].", headings));
        Assert.Equal("See the section \u201cCosts\u201d.", ReviewWriter.NameSections("See [synthesis-12].", headings));
        Assert.Equal("Nothing to name [1].", ReviewWriter.NameSections("Nothing to name [1].", headings));
    }

    [Fact]
    public void MethodsTextDescribesWhatTheRunDid()
    {
        var book = new ThematicCodebook
        {
            Codes = new() { new() { Id = "C1", Reference = 1, Anchor = "verified finding" }, new() { Id = "C2", Reference = 2, Anchor = "unanchored" } },
            Themes = new() { new() { Id = "T1", Name = "Recovery", Studies = new() { 1, 2 } } },
            SecondCoding = new SecondCoding { Kappa = 0.8, CellsCompared = 2 },
        };
        string text = ThematicSynthesis.MethodsText(book, "fake-model", repairEnabled: true);
        Assert.Contains("Thomas and Harden (2008)", text);
        Assert.Contains("1 of 2 codes were anchored", text);
        Assert.Contains("Cohen's kappa = 0.80", text);
        Assert.Contains("rewritten once", text);

        string fallback = ThematicSynthesis.MethodsText(new ThematicCodebook { FallbackReason = "no study could be coded" }, null, false);
        Assert.Contains("synthesised narratively", fallback);
        Assert.Contains("no study could be coded", fallback);

        Assert.Contains("grouped into 1 theme: Recovery (2 studies)", ThematicSynthesis.Overview(book, 3));
        Assert.Equal("The synthesis and discussion cite 2 of the 3 included studies. Not cited: [3] not coded.",
            ThematicSynthesis.CoverageSentence(2, 3, new[] { new UncodedStudy(3, "not coded.") }));
    }

    [Fact]
    public void MethodsTextCountsOneDisagreementInTheSingular()
    {
        // Issue #127: "1 disagreements" in section 2.6 of a real run.
        var second = new SecondCoding { Kappa = 0.93, CellsCompared = 30, Disagreements = new() { "[1], \"Recovery\": first coding only" } };
        var book = new ThematicCodebook
        {
            Codes = new() { new() { Id = "C1", Reference = 1, Anchor = "verified finding" } },
            Themes = new() { new() { Id = "T1", Name = "Recovery", Studies = new() { 1 } } },
            SecondCoding = second,
        };
        Assert.Contains("(1 disagreement, listed in thematic-codebook.json)", ThematicSynthesis.MethodsText(book, "fake-model", repairEnabled: false));

        second.Disagreements.Add("[2], \"Recovery\": second coding only");
        Assert.Contains("(2 disagreements, listed", ThematicSynthesis.MethodsText(book, "fake-model", repairEnabled: false));
        second.Disagreements.Clear();
        Assert.Contains("(0 disagreements, listed", ThematicSynthesis.MethodsText(book, "fake-model", repairEnabled: false));
    }
}
