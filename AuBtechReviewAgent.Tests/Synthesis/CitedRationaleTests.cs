using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #248: two sentences of a live run's rationale had no citation and came from the model's general knowledge.
/// The prompt asks for cited sentences only, and code leaves out every uncited sentence of the rationale and records
/// it in the writing ledger, in the systematic review and in the grey literature review.
/// </summary>
public class CitedRationaleTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "rationale-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private const string General = "High-stakes applications make context failures risk serious harm.";
    private const string Urgent = "There is an urgent need for systematic reviews.";

    [Fact]
    public void OnlyTheCitedSentencesAreKeptInTheirParagraphs()
    {
        string rationale = $"{General} Agents keep a running summary [1].\n\nRetrieval brings in only what is needed [2]. {Urgent}";

        string kept = ReviewWriter.CitedSentencesOnly(rationale, out var log);

        Assert.Equal("Agents keep a running summary [1].\n\nRetrieval brings in only what is needed [2].", kept);
        Assert.NotNull(log);
        Assert.Equal(ReviewWriter.RationaleCitedOnlyField, log!.FieldName);
        Assert.Equal((rationale, kept, true), (log.OriginalText, log.RefinedText, log.Applied));
        Assert.Equal($"Left out in code, because they carry no citation: \"{General}\" \"{Urgent}\"", log.Note);
    }

    [Fact]
    public void ARationaleThatIsCitedThroughoutIsLeftAlone()
    {
        const string rationale = "Agents keep a running summary [1]. Retrieval brings in only what is needed [2].";
        Assert.Same(rationale, ReviewWriter.CitedSentencesOnly(rationale, out var log));
        Assert.Null(log);
    }

    [Fact]
    public async Task AFrontMatterWhoseRationaleHasNoCitationIsSentBack()
    {
        static string Front(string rationale) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            { ["title"] = "Agent memory: a systematic review", ["abstract"] = "We read the studies.", ["rationale"] = rationale, ["objectives"] = "To map agent memory." });
        var model = new FakeChatService().Returns(Front(General)).Returns(Front("Agents keep a running summary [1]."));

        var front = await ReviewWriter.WriteFrontMatterAsync(model, WritingProfile.Systematic, "agent memory", "Map agent memory", "Studies: 2.",
            new[] { new SynthesisSection { Field = "synthesis-1", Heading = "Memory", Text = "Agents keep a running summary [1]." } }, "", "[1] A. [2] B.", 2);

        Assert.Equal("Agents keep a running summary [1].", front!.Rationale);
        Assert.Contains("YOUR PREVIOUS ANSWER WAS REJECTED: the rationale has no citation", model.Prompts[1]);
        Assert.Contains("leave out a sentence you cannot cite, since code removes every rationale sentence without a citation", model.Prompts[0]);
    }

    [Fact]
    public async Task TheSystematicReviewLeavesOutTheUncitedSentencesAndRecordsThem()
    {
        var model = new FakeChatService().RespondsWith(p => p.Contains("You are writing the title, abstract, rationale and objectives")
            ? ReviewWriterTests.Respond(p).Replace("\"rationale\":\"", $"\"rationale\":\"{General} ")
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

        string folder = Path.Join(_root, runId.ToString("N"));
        var report = System.Text.Json.JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(folder, "prisma-report.json")))!;
        Assert.Equal("Agent loops recover from faults through supervisor checks [1].", report.RationaleItem);
        var ledger = System.Text.Json.JsonSerializer.Deserialize<List<StyleDeltaLog>>(File.ReadAllText(Path.Join(folder, "stylistic-transformation-ledger.json")))!;
        var dropped = Assert.Single(ledger, d => d.FieldName == ReviewWriter.RationaleCitedOnlyField);
        Assert.Contains(General, dropped.Note);
    }

    [Fact]
    public async Task TheGreyLiteratureReviewLeavesOutTheUncitedSentencesAndRecordsThem()
    {
        var input = MultivocalPaperTests.Input();
        var model = new FakeChatService().RespondsWith(p => p.Contains("You are writing the title, abstract, rationale and objectives")
            ? MultivocalWritingTests.Respond(p).Replace("\"rationale\":\"", $"\"rationale\":\"{Urgent} ")
            : MultivocalWritingTests.Respond(p));

        var (paper, _) = await MultivocalWriter.WritePaperAsync(model, input, _ => "We keep a running summary of the conversation.", "fake-model", parallelism: 1);

        Assert.Equal("Agents keep a running summary of the conversation [1].", paper.Rationale);
        var dropped = Assert.Single(paper.Style, d => d.FieldName == ReviewWriter.RationaleCitedOnlyField);
        Assert.Contains(Urgent, dropped.Note);
    }
}
