using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #452: a result figure appears in its theme, the abstract and one place after the results; the future research
/// and the conclusion use none. The guard sends an answer back at most twice, then keeps it, and both are on file.
/// </summary>
public class FigureGuardTests
{
    private const string Results = "Testing agents found 34 % more rare scenarios [1]. Coverage grew by 40-90 % [2].";

    private static ReviewEvidence Evidence() => new()
    {
        Sources = Enumerable.Range(1, 2).Select(r => new EvidenceSource { Reference = r, ReferenceLine = $"Author {r} (2025). Study {r}." }).ToList(),
        BaseFacts = "2 studies.",
    };

    private static readonly List<SynthesisSection> Sections = new()
    {
        new SynthesisSection { Field = "synthesis-1", ThemeId = "T1", Heading = "Testing", Text = Results },
    };

    [Fact]
    public void ResultFiguresLeaveOutYearsNamesCountsAndMarkers()
    {
        var figures = ReviewWriter.ResultFigures("Since 2023, GPT-4 and Llama3 agents found 34 % and 0.82 more faults in 2 studies [12].", "2 studies.");
        Assert.Equal(new[] { "0.82", "34" }, figures.OrderBy(f => f));
    }

    [Fact]
    public void AFigureMayAppearInOnePlaceAfterTheResults()
    {
        Assert.Null(ReviewWriter.FigurePlacementProblem(Results, "", new[] { ("answer 1", "Agents find 34 % more [1]."), ("the discussion", "Coverage matters.") }));
        Assert.StartsWith("the discussion uses 34, a result figure answer 1 already gives",
            ReviewWriter.FigurePlacementProblem(Results, "", new[] { ("answer 1", "Agents find 34 % more [1]."), ("the discussion", "The 34 % gain matters.") }));
        Assert.StartsWith("answer 2 uses 40",
            ReviewWriter.FigurePlacementProblem(Results, "", new[] { ("answer 1", "Coverage grew 40-90 % [2]."), ("answer 2", "It grew 40 % [2].") }));
        Assert.StartsWith("the conclusion uses 90, a result figure",
            ReviewWriter.FigurePlacementProblem(Results, "", new[] { ("the conclusion", "Gains of up to 90 % make testing worth it.") }, null, new[] { "the conclusion" }));
        Assert.StartsWith("the implications uses 34, a result figure an earlier section already gives",
            ReviewWriter.FigurePlacementProblem(Results, "", new[] { ("the implications", "Teams can expect 34 % more [1].") }, new[] { "Agents find 34 % more [1]." }));
        Assert.Null(ReviewWriter.FigurePlacementProblem(Results, "", new[] { ("the conclusion", "Teams in 2026 should test agents.") }, null, new[] { "the conclusion" }));
    }

    private static string Json(string text, params string[] answers) => System.Text.Json.JsonSerializer.Serialize(new { answers, text });

    [Fact]
    public async Task TheDiscussionIsSentBackTwiceThenKeptAndBothAreOnFile()
    {
        const string repeating = "Agents find 34 % more rare scenarios than manual testing [1].";
        var model = new FakeChatService()
            .Returns(Json(repeating, "Agents find 34 % more rare scenarios [1]."))
            .Returns(Json(repeating, "Agents find 34 % more rare scenarios [1]."))
            .Returns(Json(repeating, "Agents find 34 % more rare scenarios [1]."));
        var notes = new List<string>();

        var result = await ReviewWriter.WriteDiscussionAndAnswersAsync(model, WritingProfile.Systematic, "agent testing", "Map agent testing", Sections, Evidence(), 2,
            notes.Add, new[] { "What do testing agents find?" }, null);

        Assert.Contains("A figure from the results (a number, percentage or effect size) is given in at most one answer", model.Prompts[0]);
        Assert.Equal(3, model.Prompts.Count);
        Assert.Contains("the discussion uses 34, a result figure answer 1 already gives", model.Prompts[1]);
        Assert.Equal(repeating, result.Text);
        Assert.Equal(3, notes.Count);
        Assert.StartsWith("The discussion call was sent back for a repeated result figure (1 of 2)", notes[0]);
        Assert.StartsWith("The discussion call was kept after 2 send-backs with a repeated result figure", notes[2]);
    }
}
