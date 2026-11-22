using AuBtechReviewAgent;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A whole offline run with the same fakes as PipelineTests, in a temporary folder that is removed afterwards.
/// </summary>
public abstract class PipelineFeatureRun : IDisposable
{
    protected readonly string _root = Path.Join(Path.GetTempPath(), "features-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestFolders.TryDelete(_root);
    }

    private protected PrismaReviewEngine Engine(Func<string, string>? respond = null, PipelineTests.FakeSource? source = null,
        ICitationGraph? graph = null, ReviewCache? cache = null, FakeChatService? chat = null) =>
        new("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => chat ?? new FakeChatService().RespondsWith(respond ?? PipelineTests.Respond),
            SourceFactory = _ => source ?? new PipelineTests.FakeSource(),
            CitationGraphFactory = graph == null ? null : () => graph,
            Cache = cache ?? ReviewCache.Disabled,
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };

    protected static ReviewRequest Request(bool dual = true, bool chaining = false) => new(
        "agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
        SelectedSources: new[] { "arxiv" }, DualScreening: dual, CitationChaining: chaining);
}
