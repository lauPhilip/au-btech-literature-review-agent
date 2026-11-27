using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #742: run 866a897b lost its discussion and conclusion to the rule on references per sentence on every try. The
/// send-back now names every sentence that breaks the rule, and a sentence still over it on the last try is split in code
/// at its clauses; one that cannot be split is kept and noted, never the cause of a lost section.
/// </summary>
public class CitationSplitTests
{
    [Fact]
    public void TheRunsDiscussionSentenceIsSplitAtItsClause()
    {
        var notes = new List<string>();
        string text = ReviewWriter.SplitOverCited(
            "Safety needs layers [1]. Workflow-level safeguards confine unsafe behavior at the action-chain level [2, 12, 9], and orchestration-based redundancy addresses fault tolerance [16].",
            "the discussion", notes);

        Assert.Equal("Safety needs layers [1]. Workflow-level safeguards confine unsafe behavior at the action-chain level [2, 12, 9]. Orchestration-based redundancy addresses fault tolerance [16].", text);
        Assert.Null(ReviewWriter.CitationLoadProblem(text));
        Assert.StartsWith("In the discussion, a sentence that cited 4 references was split in code at its clauses", Assert.Single(notes));
    }

    [Fact]
    public void AContrastKeepsItsMeaningAndASemicolonSplitsToo()
    {
        var notes = new List<string>();
        Assert.Equal("Agents log every call [1, 2]. By contrast, audits replay only failures [3, 4].",
            ReviewWriter.SplitOverCited("Agents log every call [1, 2], whereas audits replay only failures [3, 4].", "the limitations", notes));
        Assert.Equal("Agents log every call [1, 2]. Audits replay only failures [3, 4].",
            ReviewWriter.SplitOverCited("Agents log every call [1, 2]; audits replay only failures [3, 4].", "the limitations", notes));
    }

    [Fact]
    public void ClausesAreJoinedBackWhereTheRuleAllows()
    {
        var notes = new List<string>();
        Assert.Equal("Agents log calls [1], and audits replay them [2]. Loops recover from faults [3, 4].",
            ReviewWriter.SplitOverCited("Agents log calls [1], and audits replay them [2], and loops recover from faults [3, 4].", "the discussion", notes));
    }

    [Fact]
    public void AListOfMarkersOrAnOverCitedClauseIsKeptWithANote()
    {
        var notes = new List<string>();
        const string List = "The excluded records are [4], [6], [13], and [14].";
        const string OneClause = "Agents log their tool calls [1, 2, 3, 4].";

        Assert.Equal(List, ReviewWriter.SplitOverCited(List, "the limitations", notes));
        Assert.Equal(OneClause, ReviewWriter.SplitOverCited(OneClause, "the conclusion", notes));
        Assert.All(notes, n => Assert.Contains("could not be split at its clauses, so it was kept", n));
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void ATextWithinTheRuleIsLeftAsItIs()
    {
        var notes = new List<string>();
        const string Text = "Agents log calls [1, 2, 3], and audits replay them [1].";
        Assert.Equal(Text, ReviewWriter.SplitOverCited(Text, "the discussion", notes));
        Assert.Empty(notes);
    }

    [Fact]
    public void TheSendBackNamesEverySentenceThatBreaksTheRule()
    {
        string problem = ReviewWriter.CitationLoadProblem("Logs help [1, 2, 3, 4]. Audits help [5]. Replays help [1, 2, 3, 5]. Loops help [2, 3, 4, 5].")!;
        Assert.Contains("the sentence \"Logs help [1, 2, 3, 4].\" cites 4 references", problem);
        Assert.Contains("3 sentences break this rule; the others are \"Replays help [1, 2, 3, 5].\" (4 references); \"Loops help [2, 3, 4, 5].\" (4 references).", problem);
    }
}
