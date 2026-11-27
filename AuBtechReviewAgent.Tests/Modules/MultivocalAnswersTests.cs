using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issues #460 and #461, from a real multivocal run: the paper never answered its research questions in one place, and
/// when the discussion failed its checks the paper had no discussion, limitations or conclusion and did not say so,
/// while the statement on AI use still said the model wrote the discussion.
/// </summary>
public class MultivocalAnswersTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlranswers-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static string? Kept(PageSnapshot page) => page.TextFile switch
    {
        "a.txt" => "We keep a running summary of the conversation.\n\nOur agents are built with LangGraph, which keeps state between steps.",
        "b.txt" => "Retrieval brings in only the documents the agent needs. We also summarise old turns.",
        _ => null,
    };

    [Fact]
    public async Task ADiscussionThatFailsEveryTryIsSaidInCodeAndNotClaimedAsWritten()
    {
        var input = MultivocalPaperTests.Input();
        var model = new FakeChatService().RespondsWith(p => p.Contains("You are writing the Discussion section") ? "not an answer" : MultivocalWritingTests.Respond(p));

        var (paper, _) = await MultivocalWriter.WritePaperAsync(model, input, Kept, "fake-model", parallelism: 1);

        // Tried again once, each try with the shared repairs (one more than the soft budget, issue #723), before the paper says so.
        Assert.Equal(MultivocalWriter.DiscussionTries * (ReviewWriter.FigureSendBacks + 2), model.Prompts.Count(p => p.Contains("You are writing the Discussion section")));
        Assert.Contains(paper.Notes, n => n.Contains("written again, once"));
        Assert.Equal("", paper.Discussion);
        Assert.Null(paper.PeerReview);
        Assert.DoesNotContain(model.Prompts, p => p.Contains("You are writing the sections that follow the discussion of"));
        Assert.Equal("", paper.Conclusion);

        // Every question still has its answer in one place: RQ1's in code, saying why.
        Assert.Equal(MultivocalWriter.AnswerUnwrittenText, paper.Answers[0].Text);
        Assert.All(paper.Answers, a => Assert.Equal("code", a.Kind));

        paper.CheckedUtc = DateTime.UtcNow;
        var built = MultivocalPaper.Build(input, paper, null);
        var interpretation = Assert.Single(built.DiscussionNotes, n => n.Heading == "Interpretation");
        Assert.Equal(MultivocalWriter.DiscussionUnwrittenText, interpretation.Text);
        Assert.Contains(built.DiscussionNotes, n => n.Heading == MultivocalPaper.ReviewLimitationsHeading);
        Assert.Null(built.Conclusion);
        string ai = built.Declarations[0].Text;
        Assert.DoesNotContain("the discussion", ai);
        Assert.DoesNotContain("conclusion", ai);
        Assert.Contains("writing the theme sections, the summary for practitioners and the title, abstract and introduction", ai);
        Assert.Contains("The discussion could not be written", PaperLatex.Build(built));
    }

    [Fact]
    public async Task ADiscussionThatFailsOnceIsWrittenOnTheSecondTry()
    {
        var input = MultivocalPaperTests.Input();
        int calls = 0;
        var model = new FakeChatService().RespondsWith(p => p.Contains("You are writing the Discussion section") && ++calls <= 3 ? "not an answer" : MultivocalWritingTests.Respond(p));

        var (paper, _) = await MultivocalWriter.WritePaperAsync(model, input, Kept, "fake-model", parallelism: 1);

        Assert.StartsWith("Summaries and retrieval keep contexts short", paper.Discussion);
        Assert.Equal("written", paper.Answers[0].Kind);
        Assert.NotEmpty(paper.Conclusion);
    }

    [Fact]
    public void APaperWrittenBeforeTheAnswersKeepsItsObjectivesAndSaysItsDiscussionIsMissing()
    {
        // The real run's file: objectives by the model, no answers, an empty discussion.
        var input = MultivocalPaperTests.Input();
        var old = new MultivocalPaperFile { Objectives = "To map the practices.", Model = "m", WrittenUtc = new DateTime(2026, 11, 23) };

        var built = MultivocalPaper.Build(input, old, null);

        Assert.StartsWith("To map the practices. The research questions were RQ1: ", built.Objectives);
        Assert.Equal("Interpretation", built.DiscussionNotes[0].Heading);
        Assert.Equal(MultivocalWriter.DiscussionUnwrittenText, built.DiscussionNotes[0].Text);
    }

    [Fact]
    public async Task TheLimitationsOfTheReviewSayWhatTheAcademicSourcesWereReadFrom()
    {
        // The reading-basis rule sent back a correct sentence on abstract-only sources, because the multivocal paper had no
        // other place that said it (issue #460). It is written in code now, where the prompts say it is.
        var run = await FormalKit.RunAsync(_root);
        var input = run.Reporter.LoadInput(run.RunId)!;
        var formal = MultivocalPools.For(true, input.Ledger, input.Screening, input.Quality, input.Extraction);
        Assert.True(formal.AbstractOnly > 0);

        string text = MultivocalPaper.ReviewLimitations(input);

        Assert.Contains($"Of the {formal.Extracted} academic sources extracted, {formal.AbstractOnly} were read from the abstract alone", text);
        Assert.Contains("not peer reviewed", text);
        Assert.DoesNotContain("abstract", MultivocalPaper.ReviewLimitations(MultivocalPaperTests.Input()));
    }
}
