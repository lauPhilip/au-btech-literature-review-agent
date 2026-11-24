using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #248: in a live run the discussion's last two paragraphs both described the limitations. The prompt asks for
/// each part once, the grey literature review asks for its limitations in one paragraph, and code merges a
/// discussion that still describes them twice, on file, for both kinds of review.
/// </summary>
public class DiscussionPartsTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "discussion-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private const string Findings = "Summaries keep contexts short [1, 2].";
    private const string Comparison = "White papers and blog posts agree on retrieval [2].";
    private const string EvidenceLimits = "The evidence has limitations: the sources are grey literature that is not peer reviewed [1].";
    private const string ReviewLimits = "This review also has limitations. The sources are grey literature that is not peer reviewed [1]. A language model screened and coded the sources.";
    private static readonly string Repeating = string.Join("\n\n", Findings, Comparison, EvidenceLimits, ReviewLimits);

    private static ReviewEvidence Evidence() => new()
    {
        Sources = Enumerable.Range(1, 3).Select(r => new EvidenceSource { Reference = r, ReferenceLine = $"Author {r} (2025). Source {r}." }).ToList(),
        BaseFacts = "3 sources.",
    };

    private static readonly List<SynthesisSection> Sections = new()
    {
        new SynthesisSection { Field = "synthesis-1", Heading = "Short contexts", Text = "Sources keep running summaries [1, 2]." },
    };

    private static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(new { text });

    public static TheoryData<string> Profiles => new() { "systematic", "grey" };

    private static WritingProfile ProfileOf(string kind) =>
        kind == "systematic" ? WritingProfile.Systematic : MultivocalWriter.Profile(MultivocalPlan.Example("grey"));

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task ThePromptAsksForEachPartOnce(string kind)
    {
        var profile = ProfileOf(kind);
        var model = new FakeChatService().Returns(Json(Findings));

        await ReviewWriter.WriteDiscussionAsync(model, profile, "agent memory", "Map agent memory", Sections, Evidence(), 3);

        string rule = Assert.Single(model.Prompts.Single().Split('\n'), l => l.TrimStart().StartsWith("1. Write about"));
        foreach (var part in new[] { "(a)", "(b)", "(c)", "(d)", "(e)" }.Where(profile.DiscussionParts.Contains))
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(rule, System.Text.RegularExpressions.Regex.Escape(part)));
        Assert.EndsWith(ReviewWriter.EachPartOnceRule, rule.TrimEnd());
        // The grey literature review's limitations and implications are written after the discussion, as the systematic review's are (issue #461).
        if (kind == "grey") Assert.Contains("Do not write the limitations, the implications", rule);
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task AnAnswerThatRepeatsTheLimitationsIsMergedIntoOneParagraphOnFile(string kind)
    {
        var notes = new List<string>();
        var model = new FakeChatService().Returns(Json(Repeating));

        string discussion = await ReviewWriter.WriteDiscussionAsync(model, ProfileOf(kind), "agent memory", "Map agent memory", Sections, Evidence(), 3, notes.Add);

        Assert.Equal(string.Join("\n\n", Findings, Comparison,
            EvidenceLimits + " This review also has limitations. A language model screened and coded the sources."), discussion);
        string note = Assert.Single(notes);
        Assert.StartsWith("The discussion described the limitations in 2 paragraphs; they were merged into one in code, and 1 sentence", note);
        Assert.EndsWith("\"The sources are grey literature that is not peer reviewed [1].\"", note);
    }

    [Fact]
    public void ARepeatThatAddsACitationIsKeptAndOneLimitationsParagraphIsLeftAlone()
    {
        string adds = string.Join("\n\n", Findings, EvidenceLimits, "Limitations remain. The sources are grey literature that is not peer reviewed [1, 3].");
        Assert.Equal(string.Join("\n\n", Findings, EvidenceLimits + " Limitations remain. The sources are grey literature that is not peer reviewed [1, 3]."),
            ReviewWriter.OneLimitationsParagraph(adds, out var note));
        Assert.Contains("merged into one in code.", note);

        string once = string.Join("\n\n", Findings, Comparison, EvidenceLimits);
        Assert.Same(once, ReviewWriter.OneLimitationsParagraph(once, out var none));
        Assert.Null(none);
    }

    [Fact]
    public async Task APeerReviewRevisionThatSplitsTheLimitationsAgainIsRejected()
    {
        string merged = ReviewWriter.OneLimitationsParagraph(Repeating, out _);
        var model = new FakeChatService().RespondsWith(p => p.Contains("strict but constructive peer reviewer")
            ? """{"comments":[{"section":"discussion","severity":"medium","issue":"Thin.","suggestion":"Expand."}]}"""
            : Json(Repeating));

        var (log, discussion) = await ReviewWriter.PeerReviewAsync(model, WritingProfile.Systematic,
            Sections.Select(s => new SynthesisSection { Field = s.Field, Heading = s.Heading, Text = s.Text }).ToList(), merged, Evidence(), 3, 1);

        Assert.Equal(merged, discussion);
        var revision = Assert.Single(log.SectionRevisions);
        Assert.False(revision.Applied);
        Assert.Contains("describes the limitations in more than one paragraph", revision.Note);
    }

    [Fact]
    public async Task TheSystematicReviewKeepsTheMergeInItsCodebook()
    {
        var model = new FakeChatService().RespondsWith(p => p.Contains("You are writing the Discussion section")
            ? Json("Recovery and audits complement each other [1, 2].\n\nThe evidence has limitations: the studies are small [1].\n\nLimitations also apply to the review. The studies are small [1].")
            : ReviewWriterTests.Respond(p));
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));

        var notes = engine.LoadState(runId)!.ThematicSynthesis!.WritingNotes;
        Assert.Contains(notes!, n => n.StartsWith("The discussion described the limitations in 2 paragraphs") && n.Contains("\"The studies are small [1].\""));
    }
}
