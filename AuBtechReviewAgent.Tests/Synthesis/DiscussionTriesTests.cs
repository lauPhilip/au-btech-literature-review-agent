using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #723: in run 34d1af9a the two send-backs of the discussion's soft budget went to retold results, and the third
/// answer cited four references in one sentence. With no try left the discussion failed. The call now has one try more
/// than the soft budget, so a hard problem after both soft send-backs still gets its repair.
/// </summary>
public class DiscussionTriesTests
{
    private const string Results = "Testing agents found 34 % more rare scenarios [1]. Coverage grew by 40-90 % [2].";

    private static ReviewEvidence Evidence() => new()
    {
        Sources = Enumerable.Range(1, 4).Select(r => new EvidenceSource { Reference = r, ReferenceLine = $"Author {r} (2025). Study {r}." }).ToList(),
        BaseFacts = "4 studies.",
    };

    private static readonly List<SynthesisSection> Sections = new()
    {
        new SynthesisSection { Field = "synthesis-1", ThemeId = "T1", Heading = "Testing", Text = Results },
    };

    private static string Json(string text, params string[] answers) => System.Text.Json.JsonSerializer.Serialize(new { answers, text });

    [Fact]
    public async Task AnOverCitedSentenceAfterBothSoftSendBacksIsKeptAndNotLost()
    {
        // Issue #742: the rule on references per sentence is a soft check in the discussion, so a sentence still over it
        // when the budget is spent no longer costs the reader the discussion. This one has no clauses to split at.
        const string repeating = "Agents find 34 % more rare scenarios than manual testing [1].";
        const string overCited = "Agents find more rare scenarios than manual testing [1], [2], [3], [4].";
        const string answer = "Agents find 34 % more rare scenarios [1].";
        var model = new FakeChatService()
            .Returns(Json(repeating, answer))
            .Returns(Json(repeating, answer))
            .Returns(Json(overCited, answer));
        var notes = new List<string>();

        var result = await ReviewWriter.WriteDiscussionAndAnswersAsync(model, WritingProfile.Systematic, "agent testing", "Map agent testing", Sections, Evidence(), 4,
            notes.Add, new[] { "What do testing agents find?" }, null);

        Assert.Equal(3, model.Prompts.Count);
        Assert.Equal(overCited, result.Text);
        Assert.Equal(new[] { answer }, result.Answers);
        Assert.Contains(notes, n => n.StartsWith("The discussion call was kept after 2 send-backs with citing too many references in one sentence"));
        Assert.Contains(notes, n => n.StartsWith("In the discussion, a sentence still cites 4 references after every try, and it could not be split"));
    }

    [Fact]
    public async Task AnOverCitedSentenceWithClausesIsSplitInCodeOnTheLastTry()
    {
        const string repeating = "Agents find 34 % more rare scenarios than manual testing [1].";
        const string overCited = "Workflow-level safeguards confine unsafe behavior at the action-chain level [1, 2, 3], and orchestration-based redundancy addresses fault tolerance [4].";
        var model = new FakeChatService()
            .Returns(Json(repeating))
            .Returns(Json(repeating))
            .Returns(Json(overCited));
        var notes = new List<string>();

        var result = await ReviewWriter.WriteDiscussionAndAnswersAsync(model, WritingProfile.Systematic, "agent testing", "Map agent testing", Sections, Evidence(), 4,
            notes.Add, Array.Empty<string>(), null);

        Assert.Equal("Workflow-level safeguards confine unsafe behavior at the action-chain level [1, 2, 3]. Orchestration-based redundancy addresses fault tolerance [4].", result.Text);
        Assert.Contains(notes, n => n.StartsWith("In the discussion, a sentence that cited 4 references was split in code at its clauses"));
    }

    [Fact]
    public async Task ADiscussionWithNoGoodAnswerAfterEveryTryIsEmpty()
    {
        var model = new FakeChatService();
        for (int i = 0; i < ReviewWriter.FigureSendBacks + 2; i++) model.Returns(Json(""));
        var notes = new List<string>();

        var result = await ReviewWriter.WriteDiscussionAndAnswersAsync(model, WritingProfile.Systematic, "agent testing", "Map agent testing", Sections, Evidence(), 4,
            notes.Add, Array.Empty<string>(), null);

        Assert.Equal(ReviewWriter.FigureSendBacks + 1 + 1, model.Prompts.Count);
        Assert.Equal("", result.Text);
        Assert.Contains(notes, n => n.StartsWith("Discussion could not be written"));
    }
    [Fact]
    public async Task APeerReviewLeavesOutADiscussionThatWasNotWritten()
    {
        var model = new FakeChatService().Returns("""{"comments":[]}""");

        var (log, discussion) = await ReviewWriter.PeerReviewAsync(model, WritingProfile.Systematic, Sections.ToList(), "", Evidence(), 4, 1);

        Assert.Single(model.Prompts);
        Assert.DoesNotContain("[discussion] Discussion", model.Prompts[0]);
        Assert.DoesNotContain("one of: synthesis-1, discussion", model.Prompts[0]);
        Assert.Equal("", discussion);
        Assert.Equal("", log.DiscussionBefore);
    }
}
