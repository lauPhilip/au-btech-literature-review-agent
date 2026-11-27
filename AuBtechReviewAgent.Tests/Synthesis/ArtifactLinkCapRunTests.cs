using System.IO.Compression;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issues #680 and #695 in a full fake run: a concept map whose links are all the review's own reading keeps three of
/// them, the other links are on file with the reason, the methods and the note under the figure still tell the truth, and
/// the self-check notes the share and counts the map as a place after the results.
/// </summary>
public class ArtifactLinkCapRunTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "linkcap-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    // The boxes cite different studies and one cites none, so no study can state any of the five links.
    private const string Map = """
        {"format":"concept-map","title":"How safety mechanisms relate","caption":"Concepts from the themes.",
         "nodes":[{"id":"A","label":"Supervisor checks recover from faults [1]"},{"id":"B","label":"Governance layers audit actions [2]"},{"id":"C","label":"Safe orchestration"}],
         "edges":[{"from":"A","to":"C","label":"enables"},{"from":"B","to":"C","label":"constrains"},{"from":"A","to":"B","label":"informs"},
                  {"from":"C","to":"A","label":"depends on"},{"from":"C","to":"B","label":"limits"}]}
        """;

    private static string Respond(string prompt)
    {
        if (prompt.Contains("You are turning the results of a systematic literature review")) return Map;
        if (prompt.Contains("Results & Synthesis section (Item 20a)"))
            return """{"synthesis":"### Findings\nAgent loops recover from faults through supervisor checks [1].\n\n- Governance layers audit agent actions [2]","discussion":"Loops and audits complement each other [1, 2]."}""";
        return PipelineTests.Respond(prompt);
    }

    [Fact]
    public async Task AMapOfOnlyReadingLinksKeepsThreeRecordsTheRestAndTheSelfCheckNotesIt()
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(Respond),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
            SelectedSources: new[] { "arxiv" }, SynthesisDirective: "Show how the safety mechanisms relate", ArtifactKind: ArtifactKinds.ConceptMap));

        string folder = Path.Join(_root, runId.ToString("N"));
        var report = JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(folder, "prisma-report.json")))!;
        var art = report.Artifact!;
        Assert.Equal(new[] { "enables", "constrains", "informs" }, art.Edges.Select(e => e.Label));
        Assert.Equal(2, art.LinksDropped);
        Assert.Equal(3, art.Nodes.Count);
        Assert.DoesNotContain("depends on", art.Mermaid);
        Assert.Contains("Each of its 5 links was checked", report.SynthesisMethodsItem);
        Assert.Contains("the other 2 were left out of the diagram", report.SynthesisMethodsItem);
        Assert.Equal("Dashed links are the review's own reading: no single included study states them.", ArtifactLinks.DashedNote(art));

        var decisions = JsonSerializer.Deserialize<List<ArtifactLinkCheck>>(File.ReadAllText(Path.Join(folder, ArtifactLinks.FileName)))!;
        Assert.Equal(5, decisions.Count);
        Assert.Equal(new[] { "depends on", "limits" }, decisions.Where(d => d.Dropped).Select(d => d.Label));
        Assert.All(decisions.Where(d => d.Dropped), d => Assert.Contains("at most 3", d.DroppedReason));

        var selfCheck = JsonSerializer.Deserialize<PaperSelfCheckFile>(File.ReadAllText(Path.Join(folder, PaperSelfCheckFile.FileName)))!;
        var citations = selfCheck.Checks.Single(c => c.Id == "citation-check");
        Assert.Contains(citations.Evidence, e => e.Part == "Diagram links" && e.Text == "5 of 5 links are the review's own reading");
        Assert.NotEqual(CheckOutcome.Pass, citations.Outcome);

        using var archive = new ZipArchive(new MemoryStream(engine.GenerateWorkspaceArchiveFromDisk(runId)));
        using var tex = new StreamReader(archive.GetEntry("main.tex")!.Open());
        string latex = tex.ReadToEnd();
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(latex, @"\\draw\[->, thick, dashed\]").Count);
    }
}
