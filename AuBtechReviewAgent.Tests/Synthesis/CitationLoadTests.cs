using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The rule on references per sentence (Lau, 25 November 2026; the citation dumps of issue #464): a written sentence
/// cites at most three references across all its markers, ideally one or two. It is in every writing prompt and checked
/// in code; a sentence that breaks it sends the answer back, a revision may not add one, and the repair never adds a reference.
/// </summary>
public class CitationLoadTests
{
    [Fact]
    public void ThreeReferencesAreAcceptedAndFourAreSentBack()
    {
        Assert.Null(ReviewWriter.CitationLoadProblem("Agents log their tool calls [1, 2, 3]. Loops recover [4]."));
        string? problem = ReviewWriter.CitationLoadProblem("Agents log their tool calls [1, 2, 3, 4]. Loops recover [5].");
        Assert.NotNull(problem);
        Assert.Contains("cites 4 references ([1], [2], [3], [4]); a sentence cites at most 3, ideally one or two", problem);
    }

    [Fact]
    public void EveryMarkerInASentenceCountsAndARangeCountsEachReference()
    {
        Assert.Contains("cites 4 references", ReviewWriter.CitationLoadProblem("One logs calls [1, 2], while another audits them [3] and a third replays them [4].")!);
        Assert.Contains("cites 4 references", ReviewWriter.CitationLoadProblem("Several report audits [2-5].")!);
        // The same reference twice in one sentence counts once.
        Assert.Null(ReviewWriter.CitationLoadProblem("One logs calls [1], audits them [1, 2] and replays them [3]."));
    }

    [Fact]
    public void ASentenceAlreadyInTheTextARevisionStartsFromIsNotHeldAgainstIt()
    {
        const string Before = "Agents log their tool calls [1, 2, 3, 4].";
        Assert.Null(ReviewWriter.CitationLoadProblem(Before + " Loops recover [5].", Before));
        Assert.NotNull(ReviewWriter.CitationLoadProblem("Agents log every call [1, 2, 3, 4].", Before));
    }

    [Fact]
    public void ASoftGuardSendsBackTwiceThenKeepsTheAnswerWithANote()
    {
        var notes = new List<string>();
        var guard = ReviewWriter.WithSoftGuard<string>(_ => null, _ => "it lists", "subsection", "a list", notes.Add);
        Assert.Equal("it lists", guard("a"));
        Assert.Equal("it lists", guard("a"));
        Assert.Null(guard("a"));
        Assert.Equal(new[] { "The subsection was sent back for a list (1 of 2): it lists", "The subsection was sent back for a list (2 of 2): it lists", "The subsection was kept after 2 send-backs with a list: it lists" }, notes);
    }
}
