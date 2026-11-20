using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The extraction and appraisal checks of issues #130 (short values that keep their % signs) and #149 (all five MMAT
/// answers, backed by quotes, not all "can't tell" when the full text was read).
/// </summary>
public class ExtractionChecksTests
{
    private const string Interviews = "Data were collected through semi-structured interviews with 24 engineering students.";
    private const string Coding = "The transcripts were analysed by coding them into themes.";
    private const string Derived = "Each theme is supported by several quotes from the transcripts.";
    private const string Gains = "Students reported improvements of 40-90% over conventional teaching.";
    private const string Approach = "A qualitative approach was chosen to explore how students experience the workshops.";

    private static ReferencedPaper FullText(params string[] passages) =>
        new(1, "p1", "Serious play in engineering education", "", passages.Select((t, i) => new DocumentChunk("p1", i + 1, t)).ToList());

    private static ReferencedPaper AbstractOnly() =>
        new(1, "p1", "Serious play in engineering education", $"{Interviews} {Gains}", Array.Empty<DocumentChunk>());

    private static string Answer(string method = "semi-structured interviews", string methodQuote = Interviews,
        string finding = "improvements of 40-90% over conventional teaching", string appraisal = "") =>
        $$"""{"studyType":"qualitative interview study","method":{"value":"{{method}}","quote":"{{methodQuote}}"},"sample":{"value":"24 engineering students","quote":"{{Interviews}}"},"keyFindings":[{"value":"{{finding}}","quote":"{{Gains}}"}],"limitations":{"value":"not reported","quote":""},"appraisalCategory":"qualitative","appraisal":[{{(appraisal.Length > 0 ? appraisal : AllAnswered)}}]}""";

    private const string AllAnswered =
        $$"""{"id":"1.1","answer":"yes","quote":"{{Approach}}"},{"id":"1.2","answer":"yes","quote":"{{Interviews}}"},{"id":"1.3","answer":"yes","quote":"{{Coding}}"},{"id":"1.4","answer":"yes","quote":"{{Derived}}"},{"id":"1.5","answer":"cant_tell","quote":""}""";

    private const string AllCantTell =
        """{"id":"1.1","answer":"cant_tell","quote":""},{"id":"1.2","answer":"cant_tell","quote":""},{"id":"1.3","answer":"cant_tell","quote":""},{"id":"1.4","answer":"cant_tell","quote":""},{"id":"1.5","answer":"cant_tell","quote":""}""";

    private static ReferencedPaper Paper() => FullText(Approach, Interviews, Coding, Derived, Gains);

    [Fact]
    public async Task AGoodAnswerIsTakenAtOnce()
    {
        var chat = new FakeChatService().Returns(Answer());
        var e = await StudyExtractor.ExtractAsync(chat, Paper());

        Assert.Single(chat.Prompts);
        Assert.Null(e.Note);
        Assert.Equal("semi-structured interviews", e.Method.Value);
        Assert.Equal(4, e.AppraisalYes);
    }

    [Fact]
    public async Task AMethodThatIsTheWholeQuoteIsSentBack()
    {
        var chat = new FakeChatService().Returns(Answer(method: Interviews)).Returns(Answer());
        var e = await StudyExtractor.ExtractAsync(chat, Paper());

        Assert.Equal(2, chat.Prompts.Count);
        Assert.Contains("method.value", chat.Prompts[1]);
        Assert.Equal("semi-structured interviews", e.Method.Value);
        Assert.Null(e.Note);
    }

    [Fact]
    public async Task AFindingThatLosesItsPercentSignIsSentBack()
    {
        var chat = new FakeChatService().Returns(Answer(finding: "improvements of 40-90 over conventional teaching")).Returns(Answer());
        var e = await StudyExtractor.ExtractAsync(chat, Paper());

        Assert.Contains("90 without the % sign", chat.Prompts[1]);
        Assert.Equal("improvements of 40-90% over conventional teaching", e.KeyFindings.Single().Value);
    }

    [Fact]
    public async Task AFullTextStudyWithOnlyCantTellIsSentBack()
    {
        var chat = new FakeChatService().Returns(Answer(appraisal: AllCantTell)).Returns(Answer());
        var e = await StudyExtractor.ExtractAsync(chat, Paper());

        Assert.Contains("every criterion is cant_tell", chat.Prompts[1]);
        Assert.Equal(4, e.AppraisalYes);
        Assert.All(e.Appraisal.Where(a => a.Answer == "yes"), a => Assert.True(a.QuoteVerified));
    }

    [Fact]
    public async Task AnAbstractOnlyStudyMayAnswerCantTellToAll()
    {
        var chat = new FakeChatService().Returns(Answer(appraisal: AllCantTell));
        var e = await StudyExtractor.ExtractAsync(chat, AbstractOnly());

        Assert.Single(chat.Prompts);
        Assert.Equal(5, e.Appraisal.Count(a => a.Answer == "cant_tell"));
    }

    [Fact]
    public async Task MissingCriteriaAndInventedQuotesAreSentBack()
    {
        string twoOnly = $$"""{"id":"1.1","answer":"yes","quote":"{{Approach}}"},{"id":"1.2","answer":"yes","quote":"{{Interviews}}"}""";
        string invented = AllAnswered.Replace(Coding, "The analysis was rigorous and complete.");
        var chat = new FakeChatService().Returns(Answer(appraisal: twoOnly)).Returns(Answer(appraisal: invented)).Returns(Answer());
        var e = await StudyExtractor.ExtractAsync(chat, Paper());

        Assert.Contains("missing: 1.3, 1.4, 1.5", chat.Prompts[1]);
        // The second answer was still wrong (an invented quote), so the full checks gave up and the answer was asked for again.
        Assert.Equal(3, chat.Prompts.Count);
        Assert.Equal(4, e.AppraisalYes);
        Assert.Null(e.Note); // the answer that was kept passes every check
    }

    [Fact]
    public async Task AProblemTheModelKeepsIsKeptWithANote()
    {
        var chat = new FakeChatService().RespondsWith(_ => Answer(method: Interviews));
        var e = await StudyExtractor.ExtractAsync(chat, Paper());

        Assert.Equal(3, chat.Prompts.Count);
        Assert.Null(e.Error);
        Assert.Equal(Interviews, e.Method.Value);
        Assert.StartsWith("Kept after a second try", e.Note);
        Assert.Contains("method.value repeats the quote", e.Note);
    }

    [Fact]
    public async Task CriterionIdsWithTheirTextAndCantTellWithAnApostropheAreRead()
    {
        string wordy = AllAnswered.Replace("\"id\":\"1.", "\"id\":\"Criterion 1.").Replace("\"answer\":\"cant_tell\"", "\"answer\":\"Can't tell\"");
        var chat = new FakeChatService().Returns(Answer(appraisal: wordy));
        var e = await StudyExtractor.ExtractAsync(chat, Paper());

        Assert.Single(chat.Prompts);
        Assert.Equal(new[] { "yes", "yes", "yes", "yes", "cant_tell" }, e.Appraisal.Select(a => a.Answer));
    }

    [Fact]
    public async Task TheAppraisalGetsPassagesAboutDataCollectionAndAnalysis()
    {
        // Eight passages that the extraction terms rank first, and one about the analysis that they would leave out.
        var filler = Enumerable.Range(1, 10).Select(i => $"The results and findings show the method evaluation of case {i}.").ToList();
        var paper = FullText(filler.Append(Coding).ToArray());
        var chat = new FakeChatService().RespondsWith(_ => Answer());
        await StudyExtractor.ExtractAsync(chat, paper);

        Assert.Contains(Coding, chat.Prompts[0]);
    }
}
