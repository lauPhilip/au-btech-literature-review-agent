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
}
