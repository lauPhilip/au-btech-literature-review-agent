using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #723: in run 34d1af9a the discussion failed its checks, and the run threw away its three checked theme
/// subsections with it, so Results had only an overview and the paper no conclusion. A whole offline systematic review
/// whose discussion never passes now keeps its themes, says in the paper that the discussion is missing, and keeps a note.
/// </summary>
public class DiscussionUnwrittenRunTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "nodiscussion-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static string Respond(string prompt) =>
        prompt.Contains("You are writing the Discussion section") ? """{"text":""}""" : ReviewWriterTests.Respond(prompt);

    [Fact]
    public async Task AFailedDiscussionKeepsTheThemeSubsections()
    {
        var model = new FakeChatService().RespondsWith(Respond);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));

        var state = engine.LoadState(runId)!;
        var report = System.Text.Json.JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json")))!;
        List<string> prompts;
        lock (model.Prompts) prompts = model.Prompts.ToList();

        Assert.Null(state.ThematicSynthesis!.FallbackReason);
        Assert.NotEmpty(report.SynthesisSections);
        Assert.Equal(PrismaReviewEngine.DiscussionUnwrittenText, report.DiscussionItem);
        Assert.Contains(state.ThematicSynthesis.WritingNotes!, n => n.StartsWith("The discussion was not written"));
        Assert.DoesNotContain(prompts, p => p.Contains("[discussion] Discussion"));
    }
}
