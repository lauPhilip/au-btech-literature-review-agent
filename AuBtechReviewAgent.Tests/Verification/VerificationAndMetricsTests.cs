using System.IO.Compression;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Clause-level citation checks, the verdict rubric (insufficient evidence for abstracts), the second check,
/// the repair targets, and the run metrics with their store.
/// </summary>
public class VerificationAndMetricsTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "verify-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    private static ReferencedPaper Paper(int n, string abstractText, params string[] chunks) =>
        new(n, $"P{n}", $"Paper {n}", abstractText, chunks.Select((c, i) => new DocumentChunk($"P{n}", i + 1, c)).ToList());

    [Theory]
    [InlineData("Digital twins reduce workflow delays via edge offloading [19], while policy-aware controllers enforce governance rules [24], yet neither addresses safety.", 19,
        "Digital twins reduce workflow delays via edge offloading")]
    [InlineData("Digital twins reduce workflow delays via edge offloading [19], while policy-aware controllers enforce governance rules [24], yet neither addresses safety.", 24,
        "policy-aware controllers enforce governance rules")]
    [InlineData("Agent loops recover from faults through supervisor checks [1, 2].", 2, "Agent loops recover from faults through supervisor checks")]
    [InlineData("Studies [3] show that agents fail silently.", 3, "Studies show that agents fail silently.")]
    public void TheCheckJudgesOnlyThePartAttributedToTheReference(string sentence, int reference, string expected) =>
        Assert.Equal(expected, CitationSupportChecker.AttributedClause(sentence, reference));

    [Fact]
    public void TextAfterTheLastMarkerIsTheReviewsOwnInterpretation()
    {
        Assert.Equal("yet neither addresses safety", CitationSupportChecker.UnattributedTail("A does x [1], yet neither addresses safety."));
        Assert.Null(CitationSupportChecker.UnattributedTail("A does x [1]."));
    }

    [Fact]
    public async Task ThePromptShowsTheAttributedPartAndTheRubric()
    {
        var chat = new FakeChatService().Returns("""{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"Agent loops recover from faults through supervisor checks.","reason":"ok"}]}""");
        var cited = CitationSupportChecker.ExtractCitedSentences("Agent loops recover from faults [1], while governance layers audit actions [2].", "synthesis-1")
            .Select(s => s with { References = new[] { 1 } }).ToList();
        await CitationSupportChecker.CheckAsync(chat, cited, new[] { Paper(1, "Agent loops recover from faults through supervisor checks.") });
        Assert.Contains("Attributed to [1]: \"Agent loops recover from faults\"", chat.Prompts[0]);
        Assert.Contains("do not downgrade only because the wording differs", chat.Prompts[0]);
        Assert.Contains("not_in_excerpts", chat.Prompts[0]);
    }

    [Fact]
    public async Task AClaimAnAbstractDoesNotMentionIsInsufficientEvidenceNotAFailure()
    {
        var cited = CitationSupportChecker.ExtractCitedSentences("Agents audit tools [1]. Agents audit tools [2].", "discussionItem");
        var papers = new[] { Paper(1, "An abstract about loops."), Paper(2, "An abstract about loops.", "A full-text passage about loops.") };
        var chat = new FakeChatService()
            .Returns("""{"results":[{"sentence":1,"verdict":"not_in_excerpts","reason":"no mention"}]}""")
            .Returns("""{"results":[{"sentence":1,"verdict":"not_in_excerpts","reason":"no mention"}]}""");
        var results = await CitationSupportChecker.CheckAsync(chat, cited, papers);
        Assert.Equal(CitationSupportChecker.InsufficientEvidence, results.Single(r => r.Reference == 1).Verdict);
        Assert.Equal(CitationSupportChecker.NotSupported, results.Single(r => r.Reference == 2).Verdict);
        var summary = CitationSupportSummary.From(results);
        Assert.Equal(1, summary.InsufficientEvidence);
        Assert.Contains("only the abstract was available", summary.ToSentence());
    }

    [Fact]
    public async Task TheSecondCheckRaisesAVerdictOnlyWithAVerifiedQuote()
    {
        const string evidence = "Supervisor checks let agent loops recover from tool faults in production.";
        var cited = CitationSupportChecker.ExtractCitedSentences("Loops recover from tool faults [1]. Loops never fail [1].", "synthesis-1");
        var papers = new[] { Paper(1, "Agents in production.", "Unrelated passage.", evidence) };
        var chat = new FakeChatService()
            // First check: both partly supported (the quote is real, from E1).
            .Returns("""{"results":[{"sentence":1,"verdict":"partially_supported","excerpt":"E1","quote":"Agents in production.","reason":"loose"},{"sentence":2,"verdict":"partially_supported","excerpt":"E1","quote":"Agents in production.","reason":"loose"}]}""")
            // Second check: sentence 1 supported with a real quote; sentence 2 "supported" with an invented quote.
            .Returns(prompt =>
            {
                string text = string.Join("\n", prompt.Select(m => m.Content));
                Assert.Contains("independent second reviewer", text);
                Assert.DoesNotContain("partially_supported\",\"excerpt", text); // the first verdict is not shown
                string label = System.Text.RegularExpressions.Regex.Match(text, @"^(E\d+): Supervisor checks", System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value;
                return $$"""{"results":[{"sentence":1,"verdict":"supported","excerpt":"{{label}}","quote":"{{evidence}}","reason":"found"},{"sentence":2,"verdict":"supported","excerpt":"{{label}}","quote":"Loops never fail under any circumstances at all.","reason":"x"}]}""";
            });

        var results = await CitationSupportChecker.CheckAsync(chat, cited, papers, secondCheck: true);

        Assert.Equal(2, chat.Prompts.Count);
        var raised = results.Single(r => r.SentenceIndex == 0);
        Assert.Equal(CitationSupportChecker.Supported, raised.Verdict);
        Assert.Equal(CitationSupportChecker.Partial, raised.FirstVerdict);
        Assert.Equal("second check", raised.ResolvedBy);
        Assert.Equal(evidence, raised.Quote);

        var kept = results.Single(r => r.SentenceIndex == 1);
        Assert.Equal(CitationSupportChecker.Partial, kept.Verdict);
        Assert.Equal(CitationSupportChecker.Unverifiable, kept.SecondVerdict); // its quote was not in the paper
        Assert.Equal("first check", kept.ResolvedBy);

        var summary = CitationSupportSummary.From(results);
        Assert.Equal(2, summary.SecondChecked);
        Assert.Equal(1, summary.UpgradedBySecondCheck);
    }

    [Fact]
    public async Task NoSecondCheckUnlessAskedOrWhenEverythingIsSupported()
    {
        var cited = CitationSupportChecker.ExtractCitedSentences("Loops recover [1].", "synthesis-1");
        var papers = new[] { Paper(1, "Loops recover from faults through supervisor checks.") };
        var chat = new FakeChatService().Returns("""{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"Loops recover from faults through supervisor checks.","reason":"ok"}]}""");
        var r = Assert.Single(await CitationSupportChecker.CheckAsync(chat, cited, papers, secondCheck: true));
        Assert.Single(chat.Prompts);
        Assert.Null(r.SecondVerdict);
        Assert.Equal(CitationSupportChecker.Supported, r.FirstVerdict);
    }

    [Fact]
    public void RepairTargetsPartlyAndNotSupportedButNotAbstractOnlyGaps()
    {
        Assert.True(PrismaReviewEngine.NeedsRepair(new CitationSupportResult { Verdict = CitationSupportChecker.Partial }));
        Assert.True(PrismaReviewEngine.NeedsRepair(new CitationSupportResult { Verdict = CitationSupportChecker.NotSupported }));
        Assert.False(PrismaReviewEngine.NeedsRepair(new CitationSupportResult { Verdict = CitationSupportChecker.InsufficientEvidence }));
        Assert.False(PrismaReviewEngine.NeedsRepair(new CitationSupportResult { Verdict = CitationSupportChecker.Supported }));
    }

    [Theory]
    [InlineData("""{"PromptTokens":100,"CompletionTokens":20,"TotalTokens":120}""", 120)]
    [InlineData("""{"prompt_tokens":100,"completion_tokens":20}""", 120)]
    [InlineData("""{"Details":{"InputTokenCount":7,"OutputTokenCount":3}}""", 10)]
    [InlineData("not json", 0)]
    [InlineData(null, 0)]
    public void TokensAreReadFromEitherProvidersUsage(string? usage, long expected) =>
        Assert.Equal(expected, RunMetrics.TokensIn(usage));

    [Fact]
    public void MetricsStoreAppendsAndReadsBackAndSkipsBrokenLines()
    {
        var store = new RunMetricsStore(new MetricsOptions { Folder = _root }, _root);
        store.Append(new RunMetrics { RunId = "b", CompletedUtc = "2026-10-02T10:00:00Z", CitationsFinal = new VerdictCounts { Total = 4, Supported = 3 } });
        store.Append(new RunMetrics { RunId = "a", CompletedUtc = "2026-10-01T10:00:00Z" });
        File.AppendAllText(store.FilePath, "{broken\n");
        var all = store.ReadAll();
        Assert.Equal(new[] { "a", "b" }, all.Select(m => m.RunId));
        Assert.Equal(0.75, all[1].CitationsFinal.SupportedShare);

        RunMetricsStore.Disabled.Append(new RunMetrics());
        Assert.Empty(RunMetricsStore.Disabled.ReadAll());
        Assert.Equal("1.0.0 (abcdef1)", new RunMetrics { AppVersion = "1.0.0+abcdef1234" }.ShortVersion);
    }

    [Fact]
    public async Task ACompletedRunWritesItsMetricsToTheRunFolderTheArchiveAndTheStore()
    {
        var store = new RunMetricsStore(new MetricsOptions { Folder = Path.Join(_root, "metrics") }, _root);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = Path.Join(_root, "runs"),
            ChatFactory = _ => new FakeChatService().RespondsWith(PipelineTests.Respond),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
            MetricsStore = store,
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));

        var m = Assert.Single(store.ReadAll());
        Assert.Equal(runId.ToString("N"), m.RunId);
        Assert.Equal(2, m.Included);
        Assert.Equal(4, m.CitationsFinal.Total);
        Assert.Equal(4, m.CitationsFinal.Supported);
        Assert.Equal(2, m.StudiesCited);
        Assert.Equal(1.0, m.CoverageShare);
        Assert.False(m.ThematicSynthesisUsed);
        Assert.Equal("no study could be coded", m.FallbackReason);
        Assert.True(m.LlmCalls > 5);
        Assert.Contains("citation-check", m.CostByStage.Keys);
        Assert.Equal(12, m.ConfigFingerprint.Length);
        Assert.DoesNotContain("agent loops", JsonSerializer.Serialize(m)); // no query text in the store

        byte[] zip = engine.GenerateWorkspaceArchiveFromDisk(runId);
        using var archive = new ZipArchive(new MemoryStream(zip));
        Assert.NotNull(archive.GetEntry("run-metrics.json"));
    }
}
