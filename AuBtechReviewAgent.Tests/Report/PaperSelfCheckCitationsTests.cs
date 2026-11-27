using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Checks 4, 7 and 8 of the self-check: references per sentence, the citation check and the reference list.</summary>
public class PaperSelfCheckCitationsTests
{
    private static List<PaperReference> Refs(int count) => Enumerable.Range(1, count)
        .Select(n => new PaperReference(n, $"Author {n} (2024). Title {n}.", 2024, "Journal of Tests", $"10.1/{n}", $"A long enough title of work number {n}")).ToList();

    private static PaperCheckInput WithRefs(PaperCheckInput input, List<PaperReference> refs)
    {
        input.References.AddRange(refs);
        return input;
    }

    [Fact]
    public void SentencesCitingOneOrTwoPassAndTheShareIsGiven()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input(), "citation-load");

        Assert.Equal(CheckOutcome.Pass, check.Outcome);
        Assert.Equal("No sentence cites more than 3; 100% of the 4 cited sentences cite one or two.", check.Summary);
    }

    [Fact]
    public void ASentenceCitingFourIsAFaultButCodeWrittenListsAreLeftOut()
    {
        var input = SelfCheckPaper.Input(discussion: "Every study agrees on this point [1, 2, 3, 4].");
        input.Parts.Add(new PaperPart("coverage", "Coverage", PaperPartKind.CodeWritten, "Not cited: [1, 2, 3, 4, 5]."));

        var check = SelfCheckPaper.Check(input, "citation-load");

        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        var e = Assert.Single(check.Evidence);
        Assert.Equal("discussionItem", e.Field);
        Assert.Equal("Cites 4 references.", e.Detail);
    }

    [Fact]
    public void AllCitationsSupportedPasses()
    {
        var input = SelfCheckPaper.Input();
        var check = SelfCheckPaper.Check(new PaperCheckInput { Parts = input.Parts, CitationsFinal = new CitationSupportSummary { Checked = 5, Supported = 5 } }, "citation-check");

        Assert.Equal(CheckOutcome.Pass, check.Outcome);
        Assert.Equal("5 checked: 100% supported, 0% partly, 0% not supported.", check.Summary);
    }

    [Fact]
    public void PartlySupportedOrRemovedIsANoteAndNotSupportedIsAFault()
    {
        var parts = SelfCheckPaper.Input().Parts;
        var partly = SelfCheckPaper.Check(new PaperCheckInput { Parts = parts, CitationsRemoved = 2,
            CitationsFinal = new CitationSupportSummary { Checked = 4, Supported = 3, PartiallySupported = 1 } }, "citation-check");
        Assert.Equal(CheckOutcome.Note, partly.Outcome);
        Assert.EndsWith("; 2 removed.", partly.Summary);

        var not = SelfCheckPaper.Check(new PaperCheckInput { Parts = parts,
            CitationsFinal = new CitationSupportSummary { Checked = 4, Supported = 3, NotSupported = 1 },
            CitationsInitial = new CitationSupportSummary { Checked = 6, NotSupported = 3 } }, "citation-check");
        Assert.Equal(CheckOutcome.Fault, not.Outcome);
        Assert.Contains(not.Evidence, e => e.Text.StartsWith("Before repair: 3 not supported"));
    }

    [Fact]
    public void TheSelfCheckGivesTheFirstCheckAndTheFinalCountOfTheMethodsParagraph()
    {
        var parts = SelfCheckPaper.Input().Parts;
        var steps = new CitationCheckSteps { FirstChecked = 82, FirstPartiallySupported = 9, FirstNotSupported = 3, Dropped = 1, Removed = 1, FinalChecked = 80 };
        var check = SelfCheckPaper.Check(new PaperCheckInput { Parts = parts, CitationSteps = steps, CitationsRemoved = 1,
            CitationsFinal = new CitationSupportSummary { Checked = 80, Supported = 79, PartiallySupported = 1 },
            CitationsInitial = new CitationSupportSummary { Checked = 82, NotSupported = 1, PartiallySupported = 6 } }, "citation-check");

        var evidence = Assert.Single(check.Evidence, e => e.Text.StartsWith("First check:"));
        Assert.Equal("First check: 3 not supported, 9 partly", evidence.Text);
        Assert.Equal("Of 82 citations first checked; 80 are in the final text.", evidence.Detail);
        Assert.DoesNotContain(check.Evidence, e => e.Text.StartsWith("Before repair"));
    }

    [Fact]
    public void CitationsThatWereNeverCheckedAreAFault()
    {
        Assert.Equal(CheckOutcome.Fault, SelfCheckPaper.Check(SelfCheckPaper.Input(), "citation-check").Outcome);
    }

    [Fact]
    public void AMatchingReferenceListPasses()
    {
        var check = SelfCheckPaper.Check(WithRefs(SelfCheckPaper.Input(), Refs(4)), "references");

        Assert.Equal(CheckOutcome.Pass, check.Outcome);
        Assert.Equal("4 entries, all cited and all in the list.", check.Summary);
    }

    [Fact]
    public void ACitedNumberMissingFromTheListOrAListedTwiceWorkIsAFault()
    {
        var refs = Refs(3);
        var missing = SelfCheckPaper.Check(WithRefs(SelfCheckPaper.Input(), refs), "references");
        Assert.Equal(CheckOutcome.Fault, missing.Outcome);
        Assert.Contains(missing.Evidence, e => e.Text == "[4]" && e.Detail.StartsWith("Cited in the text"));

        var twice = Refs(4);
        twice[3] = twice[3] with { Doi = "https://doi.org/10.1/1" };
        var duplicate = SelfCheckPaper.Check(WithRefs(SelfCheckPaper.Input(), twice), "references");
        Assert.Equal(CheckOutcome.Fault, duplicate.Outcome);
        Assert.Contains(duplicate.Evidence, e => e.Text == "[1] and [4]" && e.Detail.Contains("more than once"));
    }

    [Fact]
    public void UncitedEntriesAndMissingDetailsAreNotesAndTheCompletionIsCounted()
    {
        var refs = Refs(5);
        refs[1] = refs[1] with { Doi = null, Year = 0 };
        refs[2] = refs[2] with { FieldsCompleted = 3 };

        var check = SelfCheckPaper.Check(WithRefs(SelfCheckPaper.Input(), refs), "references");

        Assert.Equal(CheckOutcome.Note, check.Outcome);
        Assert.Contains(check.Evidence, e => e.Text == "[5]" && e.Detail.Contains("cited nowhere"));
        Assert.Equal("5 entries; the reference completion filled 3 fields in 1; 1 without a year, 1 without a DOI.", check.Summary);
    }

    [Fact]
    public void AnArxivPreprintIsNotNotedForMissingAVenueButAnotherWorkIs()
    {
        // Run 8942b961: [4] and [5] were arXiv preprints, whose reference names arXiv as the venue (issue #678).
        var refs = Refs(5);
        refs[3] = refs[3] with { Venue = "", Text = "He, L., & Xu, J. (2026). *SAT-edge-agent* (arXiv:2608.03728). arXiv. https://doi.org/10.48550/arXiv.2608.03728" };
        refs[4] = refs[4] with { Venue = "" };

        var check = SelfCheckPaper.Check(WithRefs(SelfCheckPaper.Input(), refs), "references");

        var noVenue = Assert.Single(check.Evidence, e => e.Detail == "No venue.");
        Assert.Equal("[5]", noVenue.Text);
    }

    [Fact]
    public void TheRunPageAgreeingWithTheReportChangesNothing()
    {
        var final = new CitationSupportSummary { Checked = 5, Supported = 5 };
        var check = SelfCheckPaper.Check(new PaperCheckInput { Parts = SelfCheckPaper.Input().Parts, CitationsFinal = final,
            CitationsShown = new CitationSupportSummary { Checked = 5, Supported = 5 } }, "citation-check");

        Assert.Equal(CheckOutcome.Pass, check.Outcome);
        Assert.Empty(check.Evidence);
    }

    [Fact]
    public void ARunPageCountOtherThanTheReportsIsAFaultWithBothNumbers()
    {
        // Run 596541db: the run page said 109 citations checked, the report 113.
        var check = SelfCheckPaper.Check(new PaperCheckInput { Parts = SelfCheckPaper.Input().Parts, CitationsRemoved = 4,
            CitationsFinal = new CitationSupportSummary { Checked = 113, Supported = 113 },
            CitationsShown = new CitationSupportSummary { Checked = 109, Supported = 109, Removed = 4 } }, "citation-check");

        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        Assert.EndsWith("; the run page gives other counts.", check.Summary);
        var shown = check.Evidence.Where(e => e.Part == "Run page").Select(e => e.Text).ToList();
        Assert.Equal(new[] { "109 checked on the run page, 113 in the report", "109 supported on the run page, 113 in the report" }, shown);
    }

    [Fact]
    public void RunPageCountsWithoutACheckInTheReportAreAFault()
    {
        var check = SelfCheckPaper.Check(new PaperCheckInput { CitationsShown = new CitationSupportSummary { Checked = 3, Supported = 3 } }, "citation-check");

        Assert.Equal(CheckOutcome.Fault, check.Outcome);
        Assert.Equal("The run page gives citation counts the report does not have.", check.Summary);
        Assert.Equal(2, check.Evidence.Count);
    }
}
