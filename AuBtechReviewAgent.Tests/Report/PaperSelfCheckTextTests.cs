using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Checks 9 and 10 of the self-check, and the whole check on a hand-built paper.</summary>
public class PaperSelfCheckTextTests
{
    [Fact]
    public void ThemesNamedInTheDiscussionAndQuestionTermsInTheAnswersPass()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input(discussion: "Defect reduction and governance go together [1, 3].",
            answer1: "Agents reduce defects in these studies [1]."), "terms");

        Assert.Equal(CheckOutcome.Pass, check.Outcome);
        Assert.Equal("2 of 2 themes are named again after the results; 1 of 1 question keep their terms in the answers.", check.Summary);
    }

    [Fact]
    public void AThemeNeverNamedAgainIsANoteNeverAFault()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input(discussion: "Defects fall with agents [1].", answer1: "They help [1]."), "terms");

        Assert.Equal(CheckOutcome.Note, check.Outcome);
        Assert.Contains(check.Evidence, e => e.Text == "Governance");
        Assert.Contains(check.Evidence, e => e.Part == "RQ1" && e.Detail.Contains("agents") && e.Detail.Contains("reduce"));
    }

    [Fact]
    public void TheReadingEaseFollowsTheFleschFormula()
    {
        // 2 sentences, 9 words, 10 syllables: 206.835 - 1.015 * 9 / 2 - 84.6 * 10 / 9.
        Assert.Equal(108.3, PaperSelfCheck.FleschReadingEase("The cat sat on the mat [1]. Dogs are happy."));
        Assert.Null(PaperSelfCheck.FleschReadingEase(" "));
        Assert.Equal(3, PaperSelfCheck.Syllables("evidence"));
    }

    [Fact]
    public void ALongSentenceIsANoteAndPlainTextPasses()
    {
        Assert.Equal(CheckOutcome.Pass, SelfCheckPaper.Check(SelfCheckPaper.Input(), "readability").Outcome);

        string longSentence = string.Join(" ", Enumerable.Repeat("word", 45)) + " [1].";
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input(discussion: longSentence), "readability");
        Assert.Equal(CheckOutcome.Note, check.Outcome);
        Assert.Contains(check.Evidence, e => e.Field == "discussionItem" && e.Detail == "A sentence of 45 words.");
    }

    [Fact]
    public void TheReadabilityCheckGivesThePlainEnglishMeasurements()
    {
        var check = SelfCheckPaper.Check(SelfCheckPaper.Input(discussion: "Records were screened twice [1]. We kept the studies on agents [2]."), "readability");

        var part = Assert.Single(check.Evidence, e => e.Field == "discussionItem");
        Assert.EndsWith(" Mean sentence length 5 words; 0% of sentences over 25 words; 50% in the passive voice.", part.Detail);
        Assert.Contains("; mean sentence length ", check.Summary);
        Assert.Contains("% in the passive voice", check.Summary);
    }

    [Fact]
    public void TheWholeCheckRunsTenChecksInOrderAndCountsThem()
    {
        var file = PaperSelfCheck.Run(SelfCheckPaper.Input(), new DateTime(2026, 11, 25, 17, 18, 0, DateTimeKind.Utc));

        Assert.Equal(new[] { "numbers", "answers", "parts", "citation-load", "figures", "coverage", "citation-check", "references", "terms", "readability" },
            file.Checks.Select(c => c.Id));
        Assert.Equal(Enumerable.Range(1, 10), file.Checks.Select(c => c.Number));
        Assert.Equal(10, file.Passed + file.Notes + file.Faults);
        Assert.Equal("2026-11-25 17:18:00 UTC", file.CheckedUtc);
        Assert.All(file.Checks, c => Assert.False(string.IsNullOrWhiteSpace(c.Summary), c.Id));
    }
}
