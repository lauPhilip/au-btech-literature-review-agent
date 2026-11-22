using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The systematic report without the thematic synthesis: the first PRISMA draft, the single citation-mandatory pass
/// for the synthesis and discussion, and its peer review with one revision. The golden prompts cover only the
/// thematic path, so these pin down what this path sends and writes, and what a failed report leaves behind.
/// </summary>
public class ReportFallbackTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "report-fallback-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private const string Revised = "Agent loops recover from faults through supervisor checks [1], while governance layers audit agent actions [2].";

    private static string Respond(string prompt)
    {
        if (prompt.Contains("RESULTS AND SYNTHESIS (Item 20a):"))
            return """{"comments":[{"section":"synthesis","severity":"high","issue":"Thin.","suggestion":"Compare."}]}""";
        if (prompt.Contains("revising two sections of a systematic review in response to peer-review"))
            return $$"""{"synthesis":"{{Revised}}","discussion":""}""";
        return PipelineTests.Respond(prompt);
    }

    private async Task<(List<string> Prompts, ReviewState State, PrismaReport Report, string Folder)> RunAsync(Func<string, string> respond, bool thematic)
    {
        var model = new FakeChatService().RespondsWith(respond);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
            Synthesis = new SynthesisOptions { ThematicSynthesis = thematic },
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));
        string folder = Path.Join(_root, runId.ToString("N"));
        var report = JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(folder, "prisma-report.json")))!;
        List<string> prompts;
        lock (model.Prompts) prompts = model.Prompts.Select(p => p.Replace("\r\n", "\n")).ToList();
        return (prompts, engine.LoadState(runId)!, report, folder);
    }

    [Fact]
    public async Task WithoutTheThematicSynthesisTheDraftAndOneCitedPassAreWrittenAndPeerReviewed()
    {
        var (prompts, state, report, folder) = await RunAsync(Respond, thematic: false);

        string outline = File.ReadAllText(Path.Join(folder, "grounded-outline.txt"));
        Assert.False(string.IsNullOrWhiteSpace(outline));

        string draft = Assert.Single(prompts, p => p.Contains("elite academic meta-analyst"));
        Assert.Contains("- The primary search query for this run was exactly: \"agent loops\"\n", draft);
        string supplementary = state.SearchPerspectives.Count > 1 ? string.Join(" | ", state.SearchPerspectives.Skip(1)) : "(none - single literal query only)";
        Assert.Contains($"these supplementary phrasings: {supplementary}\n", draft);
        Assert.Contains("- The screening inclusion thresholds were exactly: \"Agent architecture\"\n", draft);
        Assert.Contains("- The screening exclusion thresholds were exactly: \"Agronomy\"\n", draft);
        var venues = state.SynthesizedRecords.GroupBy(r => r.VenueType).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}").ToList();
        Assert.Contains($"- Included set: {state.SynthesizedRecords.Count} included record(s) ({string.Join(", ", venues)}); the peer-reviewed-only filter was OFF. Do not describe", draft);
        Assert.Contains("- Screening: done by a language model only; do not claim that any human screened, verified or reviewed records during the run.\n", draft);
        Assert.Contains("ground synthesisResultsItem and discussionItem on these claims):\n" + outline + "\n", draft);
        Assert.Contains("\"titleItem\": \"A single-line systematic review title (at most 25 words, no full stop) on 'agent loops' mapping to PRISMA Item 1.\",", draft);
        Assert.Contains("\"objectivesItem\": \"The explicit question formulation matching the stated goal: 'Map agent loop safety' mapping to PRISMA Item 4.\",", draft);

        string cited = Assert.Single(prompts, p => p.Contains("You are an expert systematic-review author writing two sections"));
        Assert.Contains("For the synthesis, write about 3 substantial paragraphs that group findings", cited);
        Assert.Contains("\n   3 paragraphs interpreting what the findings mean", cited);
        Assert.Contains("(each claim already mapped to supporting reference numbers):\n" + outline + "\n", cited);
        Assert.Contains("REVIEW OBJECTIVE: \"Map agent loop safety\"\nPRIMARY TOPIC: \"agent loops\"\n", cited);

        string critique = Assert.Single(prompts, p => p.Contains("RESULTS AND SYNTHESIS (Item 20a):"));
        Assert.Contains("RESULTS AND SYNTHESIS (Item 20a):\nAgent loops recover from faults through supervisor checks [1]. Governance layers audit agent actions [2].\n\nDISCUSSION (Item 23a):\nLoops and audits complement each other [1, 2].\n", critique);
        string revise = Assert.Single(prompts, p => p.Contains("revising two sections of a systematic review"));
        Assert.Contains("PEER-REVIEW FEEDBACK:\n- [high] (synthesis) Thin. -> Compare.\n", revise);

        Assert.Equal(Revised, report.SynthesisResultsItem);
        Assert.Equal("Loops and audits complement each other [1, 2].", report.DiscussionItem);
        Assert.Empty(report.SynthesisSections ?? new List<SynthesisSection>());
        Assert.Equal("Agent Loops: A Systematic Review", report.TitleItem); // the front matter failed, so the draft's title is kept
        Assert.Equal("We review 3 papers on agent loops.", report.AbstractItem);
        Assert.Equal("", report.ConclusionItem);
        Assert.Null(state.ThematicSynthesis);
        Assert.Equal(2, state.Stats.StudiesCitedInSynthesis);
        Assert.False(string.IsNullOrWhiteSpace(report.CitationCheckSummary));

        var peer = JsonDocument.Parse(File.ReadAllText(Path.Join(folder, "peer-review-feedback.json"))).RootElement;
        Assert.Equal("Revisions applied in response to 1 reviewer comment(s)", peer.GetProperty("Verdict").GetString());
        var title = JsonDocument.Parse(File.ReadAllText(Path.Join(folder, "stylistic-transformation-ledger.json"))).RootElement[0];
        Assert.Equal("Title", title.GetProperty("FieldName").GetString());
        Assert.Contains("The front-matter step failed; the first draft's text was used.", title.GetProperty("Note").GetString());
        var audit = JsonDocument.Parse(File.ReadAllText(Path.Join(folder, "citation-audit.json"))).RootElement;
        Assert.Equal(2, audit.GetProperty("ReferenceListSize").GetInt32());
        Assert.Equal(2, audit.GetProperty("StudiesCited").GetInt32());
    }

    [Fact]
    public async Task AFailedReportIsWrittenAsAFailureAndTheRunSaysWhy()
    {
        string Failing(string prompt) => prompt.Contains("elite academic meta-analyst")
            ? throw new InvalidOperationException("draft down")
            : ReviewWriterTests.Respond(prompt);

        var (_, state, report, _) = await RunAsync(Failing, thematic: true);

        Assert.Equal("Checklist Synthesis generation completely faulted: draft down", report.TitleItem);
        Assert.Equal("The report could not be generated: draft down", state.FailureMessage);
    }
}
