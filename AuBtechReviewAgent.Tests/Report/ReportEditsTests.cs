using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>How the reviewer's sentence edits are put into a report in code, and how their history is read.</summary>
public class ReportEditsTests
{
    private static ReportEdit Edit(int number, string field, int index, string before, string after, string action = ReportEdit.Edited,
        string verdict = CitationSupportChecker.Supported) =>
        new(number, action, field, index, before, after, DateTime.UtcNow, "run owner (edit key)",
            CitationSupportChecker.ReferencesIn(after).Select(r => new CitationSupportResult { Field = field, SentenceIndex = index, Sentence = after, Reference = r, Verdict = verdict }).ToList(),
            "fast-model", "1.0.0");

    [Theory]
    [InlineData("One claim [1]. Two claims [2, 3].\n\nA new paragraph (with brackets) [4]! Is it? \"Quoted\" end.")]
    [InlineData("  Leading space. trailing lower case stays joined. Last one  ")]
    [InlineData("Single sentence without stop")]
    public void SentenceSpansAreTheSentencesTheCheckSplits(string text)
    {
        var spans = ReportEdits.SentenceSpans(text);
        Assert.Equal(CitationSupportChecker.SplitSentences(text), spans.Select(s => text.Substring(s.Start, s.Length)).ToList());
    }

    [Fact]
    public void AnEditReplacesOneSentenceAndKeepsTheParagraphs()
    {
        string text = "First claim [1].\n\nSecond claim [2]. Third claim [3].";
        Assert.Equal("First claim [1].\n\nA better claim [2]. Third claim [3].", ReportEdits.Apply(text, new Dictionary<int, string> { [1] = "A better claim [2]." }));
    }

    [Fact]
    public void PrepareCleansTheTextAndRefusesWhatWouldMoveOtherSentences()
    {
        string text = "First claim [1]. Second claim [2].";
        Assert.Equal("A clean claim [1].", ReportEdits.Prepare(text, 0, "A  clean​ claim\n[1].", 3).Sentence);
        Assert.Contains("Write one sentence", ReportEdits.Prepare(text, 0, "no capital and no stop [1]", 3).Error);
        Assert.Contains("Write one sentence", ReportEdits.Prepare(text, 1, "lower case start [2].", 3).Error);
        Assert.Contains("not in the reference list", ReportEdits.Prepare(text, 0, "Claim [0].", 3).Error);
        Assert.Contains("at most", ReportEdits.Prepare(text, 0, new string('a', ReportEdits.MaxSentenceLength + 1), 3).Error);
        Assert.Contains("empty", ReportEdits.Prepare(text, 0, "   ", 3).Error);
    }

    [Fact]
    public void OnlyTheWrittenProseIsEditable()
    {
        foreach (var field in new[] { "rationaleItem", "synthesisResultsItem", "synthesis-3", "discussionItem", "conclusionItem", "researchAnswer2", "futureResearchItem" })
            Assert.True(ReportEdits.IsEditable(field), field);
        foreach (var field in new[] { "titleItem", "abstractItem", "eligibilityItem", "sourcesItem", "searchStrategyItem", "selectionProcessItem",
                     "biasAssessmentItem", "synthesisMethodsItem", "reviewLimitationsItem", "coverageSummary", "citationCheckSummary", "supportItem", "artifact-1", "synthesis-x" })
            Assert.False(ReportEdits.IsEditable(field), field);
    }

    [Fact]
    public void AReportWithoutEditsIsTheSameObject()
    {
        var report = new PrismaReport { DiscussionItem = "A [1]." };
        Assert.Same(report, ReportEdits.Apply(report, null));
        Assert.Same(report, ReportEdits.Apply(report, new ReportEditLog()));
        var undone = new ReportEditLog { Edits = { Edit(1, "discussionItem", 0, "A [1].", "B [1]."), Edit(2, "discussionItem", 0, "B [1].", "A [1].", ReportEdit.Undone) } };
        Assert.Same(report, ReportEdits.Apply(report, undone));
    }

    [Fact]
    public void EditsReachEveryKindOfFieldAndTheOverviewKeepsItsDiagram()
    {
        var report = new PrismaReport
        {
            SynthesisResultsItem = "Overview [1]. More [2].[MERMAID_START]graph TD; A-->B[MERMAID_END]",
            SynthesisSections = { new SynthesisSection { Field = "synthesis-1", Text = "Theme [1]." } },
            ResearchAnswers = { new ResearchAnswer { Number = 1, Question = "Q?", Answer = "Answer [2]." } },
            CitationCheckSummary = "Checked.",
        };
        var log = new ReportEditLog { Edits =
        {
            Edit(1, "synthesisResultsItem", 1, "More [2].", "Much more [2]."),
            Edit(2, "synthesis-1", 0, "Theme [1].", "Better theme [1]."),
            Edit(3, "researchAnswer1", 0, "Answer [2].", "Better answer [2].", verdict: CitationSupportChecker.Partial),
        } };
        var edited = ReportEdits.Apply(report, log);

        Assert.Equal("Overview [1]. Much more [2].[MERMAID_START]graph TD; A-->B[MERMAID_END]", edited.SynthesisResultsItem);
        Assert.Equal("Better theme [1].", edited.SynthesisSections[0].Text);
        Assert.Equal("Better answer [2].", edited.ResearchAnswers[0].Answer);
        Assert.StartsWith("Checked. After the run, the reviewer edited 3 sentences", edited.CitationCheckSummary);
        Assert.Contains("2 supported, 1 partly supported and 0 not supported", edited.CitationCheckSummary);
        Assert.Equal("Theme [1].", report.SynthesisSections[0].Text); // the run's own report is not changed
    }

    [Fact]
    public void UndoWalksBackThroughTheEditsAndTheVerdictsFollow()
    {
        var log = new ReportEditLog { Edits =
        {
            Edit(1, "discussionItem", 0, "A [1].", "B [1]."),
            Edit(2, "discussionItem", 0, "B [1].", "C [2].", verdict: CitationSupportChecker.NotSupported),
        } };
        var audit = new List<CitationSupportResult>
        {
            new() { Field = "discussionItem", SentenceIndex = 0, Reference = 1, Verdict = CitationSupportChecker.Partial },
            new() { Field = "discussionItem", SentenceIndex = 1, Reference = 3, Verdict = CitationSupportChecker.Supported },
        };

        var now = ReportEdits.Checks(audit, log);
        Assert.Equal(new[] { (0, 2, CitationSupportChecker.NotSupported), (1, 3, CitationSupportChecker.Supported) },
            now.Select(c => (c.SentenceIndex, c.Reference, c.Verdict)));

        log.Edits.Add(Edit(3, "discussionItem", 0, "C [2].", "B [1].", ReportEdit.Undone));
        Assert.Equal("B [1].", log.Current()[("discussionItem", 0)].After);
        log.Edits.Add(Edit(4, "discussionItem", 0, "B [1].", "A [1].", ReportEdit.Undone));
        Assert.Empty(log.Current());
        Assert.Equal(audit.Select(c => c.Verdict), ReportEdits.Checks(audit, log).Select(c => c.Verdict));
        Assert.Equal("", log.Sentence());
    }
}
