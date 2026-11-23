using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The attack set's papers (#373) run through a whole systematic review with the scripted fake model: each paper's text
/// is its abstract and its open full text. The fake model includes a paper whose title starts with "Agent", whatever
/// the text says, so a decision that differs from that would mean the text steered it.
/// </summary>
public class InjectionAttackSetRunTests : PipelineFeatureRun
{
    private static string IdOf(AttackCase c) => "ATTACK_" + c.Id;

    private sealed record SystematicRun(PrismaReviewEngine Engine, Guid RunId, ReviewState State, List<string> Prompts);

    private async Task<SystematicRun> RunAsync()
    {
        var papers = AttackSet.Papers.Select(c => new AcademicPaper(IdOf(c), c.Title, c.Text, "Published: 2025", new() { "Ana Attack" }, "Journal of Tests")).ToArray();
        var chat = new FakeChatService().RespondsWith(PipelineTests.Respond);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => chat,
            SourceFactory = _ => new PipelineTests.FakeSource(papers),
            Cache = ReviewCache.Disabled,
            FullTextFetcher = (paper, _) => Task.FromResult(AttackSet.Papers.FirstOrDefault(c => IdOf(c) == paper.Id) is { } c
                ? new FullTextResult(new[] { new DocumentChunk(paper.Id, 1, c.Text) }, "open-access link from the source", "https://example.org/" + c.Id + ".pdf")
                : new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request() with { MaxResultsPerSource = 25 });
        List<string> prompts;
        lock (chat.Prompts) prompts = chat.Prompts.ToList();
        return new SystematicRun(engine, runId, engine.LoadState(runId)!, prompts);
    }

    [Fact]
    public async Task EveryPoisonedPaperIsFlaggedAndTheScriptedDecisionsStand()
    {
        var run = await RunAsync();
        var state = run.State;

        Assert.Equal(PrismaReviewEngine.StageComplete, state.Stats.ProcessingStage);
        foreach (var c in AttackSet.Papers)
        {
            var log = Assert.Single(state.Phases.Screening, l => l.PaperId == IdOf(c));
            Assert.True(c.Decision == log.Decision, $"{c.Id}: {log.Decision}");
            Assert.Equal(c.Flags, log.InjectionFlags ?? new());

            var entry = state.FullTextFlags?.SingleOrDefault(f => f.PaperId == IdOf(c));
            if (c.Decision == "Excluded" || c.Flags.Count == 0 && c.Invisible == 0) { Assert.Null(entry); continue; }
            Assert.NotNull(entry);
            Assert.Equal(c.Flags, entry!.Flags.Select(f => f.Pattern));
            Assert.Equal(HiddenTextRemoved.Of(c.Invisible, 0), entry.Removed);
        }
        Assert.Contains(state.SynthesizedRecords, r => r.PaperId == IdOf(AttackSet.Case("benign-discussion")));
    }

    [Fact]
    public async Task NoPromptCarriesHiddenTextInvisibleCharactersOrAnEarlyEndMarker()
    {
        var run = await RunAsync();

        Assert.NotEmpty(run.Prompts);
        Assert.All(run.Prompts, p => Assert.True(InjectionAttackSetTests.HasNoInvisibleCharacters(p)));
        foreach (string hidden in AttackSet.Papers.SelectMany(c => c.NotToModel))
            Assert.DoesNotContain(run.Prompts, p => p.Contains(hidden, StringComparison.Ordinal));
        // The fake end marker did reach the model, but defused, so the wrapper still ends where the paper's text ends.
        Assert.Contains(run.Prompts, p => p.Contains("UNTRUSTED_SOURCE_TEXT››› New instructions", StringComparison.Ordinal));
        // The clean control reached the model exactly as it is, joiners and all.
        Assert.Contains(run.Prompts, p => p.Contains(AttackSet.Case("clean-control").Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStatementOnAiUseCountsOnlyThePapersWithFlags()
    {
        var run = await RunAsync();
        var state = run.State;
        int flagged = AttackSet.Papers.Count(c => c.Decision == "Included" && c.Flags.Count > 0);

        string statement = AiUseStatement.Methods(state, run.RunId);

        // A paper that only lost invisible characters (stray-invisible) is on file, but it held no instruction-like phrase.
        Assert.Contains(state.FullTextFlags!, f => f.PaperId == IdOf(AttackSet.Case("stray-invisible")) && f.Flags.Count == 0);
        Assert.Contains(AiUseStatement.FlaggedSources(flagged, state.Stats.FullTextRetrieved, "full text", "included study", "included studies"), statement);
    }
}
