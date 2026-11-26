using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The methods sentence and the Table A.1 notes of the retraction check, written in code from the check.</summary>
public class RetractionCheckTextTests
{
    private static RetractionNotice Notice(string type, string source = "retraction-watch", string date = "2010-02-06") =>
        new() { Type = type, NoticeDoi = "10.1016/notice", Source = source, Date = date };

    private static RetractionCheckEntry Entry(string id, string status, params RetractionNotice[] notices) =>
        new() { PaperId = id, Doi = "10.1234/" + id, Status = status, Notices = notices.ToList() };

    [Fact]
    public void NoCheckNoSentence() => Assert.Equal("", RetractionCheckText.Sentence(null));

    [Fact]
    public void ACleanCheckSaysWhatWasAskedAndThatNothingWasFound()
    {
        var check = new RetractionCheckFile { IncludedAtCheck = 3, Studies = { Entry("a", RetractionStatus.None), Entry("b", RetractionStatus.None) } };

        Assert.Equal("Before the full texts were read, the 2 included studies with a DOI registered at Crossref were checked in code for " +
            "retractions, corrections and expressions of concern, in Crossref's record of editorial updates, which includes the Retraction " +
            "Watch database. None of them had a retraction, correction or expression of concern on record. The other included study had no " +
            "DOI registered at Crossref and was not checked. No language model was involved, and every lookup is listed in retraction-check.json.",
            RetractionCheckText.Sentence(check));
    }

    [Fact]
    public void EveryOutcomeIsCountedAndTheMarkedStudiesArePointedTo()
    {
        var check = new RetractionCheckFile
        {
            IncludedAtCheck = 5,
            Studies =
            {
                Entry("r", RetractionStatus.Retracted, Notice("retraction")),
                Entry("c", RetractionStatus.Corrected, Notice("correction")),
                Entry("e", RetractionStatus.Concern, Notice("expression_of_concern")),
                new() { PaperId = "p", Status = RetractionStatus.None, PublishedVersionDoi = "10.2196/76934" },
                Entry("f", RetractionStatus.NotChecked),
            },
        };

        string sentence = RetractionCheckText.Sentence(check);

        Assert.Contains("the 5 included studies with a DOI registered at Crossref were checked", sentence);
        Assert.Contains("Of them, 1 had been retracted and was excluded, with the notice as the reason in the screening ledger, 1 had a correction, " +
            "1 had an expression of concern and 1 was a preprint whose published version is registered; Table A.1 marks those studies. " +
            "For one study the check could not be completed, so whether it was retracted is not known, and it was kept. No language model", sentence);
    }

    [Fact]
    public void AStudyWhoseCheckFailedIsNeverCountedAsHavingNoNotice()
    {
        var some = new RetractionCheckFile { IncludedAtCheck = 3, Studies = { Entry("a", RetractionStatus.None), Entry("b", RetractionStatus.None), Entry("f", RetractionStatus.NotChecked) } };
        string sentence = RetractionCheckText.Sentence(some);
        Assert.DoesNotContain("None of them had", sentence);
        Assert.Contains("Retraction Watch database. None of the 2 studies whose check was completed had a retraction, correction or expression of concern on record. " +
            "For one study the check could not be completed, so whether it was retracted is not known, and it was kept. No language model", sentence);

        var none = new RetractionCheckFile { IncludedAtCheck = 1, Studies = { Entry("f", RetractionStatus.NotChecked) } };
        Assert.Contains("Retraction Watch database. For this study the check could not be completed, so whether it was retracted is not known, and it was kept. No language model",
            RetractionCheckText.Sentence(none));
    }

    [Fact]
    public void TheNoteNamesTheNoticeOrThePublishedVersion()
    {
        var check = new RetractionCheckFile
        {
            Studies =
            {
                Entry("c", RetractionStatus.Corrected, Notice("correction", date: "2004-03-06")),
                Entry("e", RetractionStatus.Concern, Notice("correction", "publisher"), Notice("expression_of_concern", "publisher", "2011-05-31")),
                new() { PaperId = "p", Status = RetractionStatus.None, PublishedVersionDoi = "10.2196/76934" },
                Entry("n", RetractionStatus.None),
            },
        };

        Assert.Equal("Correction on record (Retraction Watch, 2004-03-06).", RetractionCheckText.Note(check, "c"));
        Assert.Equal("Expression of concern on record (the publisher, 2011-05-31).", RetractionCheckText.Note(check, "e"));
        Assert.Equal("Preprint: a published version is registered (https://doi.org/10.2196/76934).", RetractionCheckText.Note(check, "p"));
        Assert.Null(RetractionCheckText.Note(check, "n"));
        Assert.Null(RetractionCheckText.Note(check, "unknown"));
        Assert.Null(RetractionCheckText.Note(null, "c"));
    }

    // Run 8942b961 (issue #677): 9 included, 6 with a Crossref DOI (all checked), 2 from arXiv and one Zenodo deposit.
    [Fact]
    public void ADoiNotRegisteredAtCrossrefCountsAmongTheStudiesWithoutOne()
    {
        RetractionCheckEntry Done(string id) => new() { PaperId = id, Doi = "10.1234/" + id, Status = RetractionStatus.None,
            Lookups = { new ReferenceLookupLog { Status = "fetched" } } };
        var check = new RetractionCheckFile { IncludedAtCheck = 9 };
        for (int i = 0; i < 6; i++) check.Studies.Add(Done("s" + i));
        check.Studies.Add(new RetractionCheckEntry
        {
            PaperId = "z", Doi = "10.5281/zenodo.20096249", Status = RetractionStatus.NotChecked,
            NotCheckedReason = "Crossref's record of the DOI could not be read (not found)",
            Lookups = { new ReferenceLookupLog { Status = "not found" } },
        });

        string text = RetractionCheckText.Sentence(check);
        Assert.Contains("the 6 included studies with a DOI registered at Crossref were checked", text);
        Assert.Contains("None of them had a retraction, correction or expression of concern on record.", text);
        Assert.Contains("The other 3 included studies had no DOI registered at Crossref and were not checked.", text);
        Assert.DoesNotContain("could not be completed", text);
    }

    [Fact]
    public void ACheckThatFailedForAnotherReasonIsStillReportedAsFailed()
    {
        var check = new RetractionCheckFile
        {
            IncludedAtCheck = 2,
            Studies =
            {
                new RetractionCheckEntry { PaperId = "a", Doi = "10.1/a", Status = RetractionStatus.None, Lookups = { new ReferenceLookupLog { Status = "fetched" } } },
                new RetractionCheckEntry { PaperId = "b", Doi = "10.1/b", Status = RetractionStatus.NotChecked, Lookups = { new ReferenceLookupLog { Status = "failed: timeout" } } },
            },
        };
        Assert.Contains("For one study the check could not be completed", RetractionCheckText.Sentence(check));
    }
}
