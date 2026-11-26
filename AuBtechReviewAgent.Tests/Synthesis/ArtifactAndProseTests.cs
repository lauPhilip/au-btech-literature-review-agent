using System.IO.Compression;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Plain prose in the report, the artifact built from the findings, and progress that never moves back.</summary>
public class ArtifactAndProseTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "artifact-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    [Fact]
    public void MarkdownHeadingsListsAndTablesBecomePlainProse()
    {
        const string text = """
            The synthesis reveals persistent challenges [2].

            ### Technical Challenges
            Scalability remains a barrier [28]. Hybrid systems are unproven [16].

            ### Research Agenda
            1. Technical: Prioritize uncertainty quantification [13].
            2. **Operational**: Develop domain-aware plugins [16, 22]

            Table 1. Challenges in Agentic AI Systems
            | Category | Key Challenges |
            |----------|----------------|
            | Technical | Safety gaps [16] |
            See `code` and [the site](https://example.org).
            """;
        var (clean, changes) = ProseCleaner.Clean(text, "synthesis-1");

        Assert.Equal("""
            The synthesis reveals persistent challenges [2].

            Scalability remains a barrier [28]. Hybrid systems are unproven [16].

            Technical: Prioritize uncertainty quantification [13]. Operational: Develop domain-aware plugins [16, 22].

            See code and the site.
            """.Replace("\r\n", "\n"), clean);
        Assert.DoesNotContain("#", clean);
        Assert.DoesNotContain("|", clean);
        Assert.Equal(2, changes.Count(c => c.Kind == "heading removed"));
        var table = Assert.Single(changes, c => c.Kind == "table removed");
        Assert.StartsWith("Table 1. Challenges", table.Text);
        Assert.Contains(changes, c => c.Kind == "list turned into prose");
    }

    [Fact]
    public void PlainProseIsLeftAlone()
    {
        var (clean, changes) = ProseCleaner.Clean("Loops recover from faults [1].\n\nAudits help [2, 3].", "x");
        Assert.Equal("Loops recover from faults [1].\n\nAudits help [2, 3].", clean);
        Assert.Empty(changes);
    }

    [Fact]
    public void ASentenceThatNamesAStudyDoesNotCiteItAgainAtTheEnd()
    {
        // Issue #125, from run 959b875d: "[5] warns ... [5]" and "Study [6] reports ... [6]".
        Assert.Equal("[1] and [11] stress provenance, whereas [5] warns that governance can fail.",
            ProseCleaner.DropRepeatedCitations("[1] and [11] stress provenance, whereas [5] warns that governance can fail [5]."));
        Assert.Equal("Study [6] reports gains of 40% over manual testing.",
            ProseCleaner.DropRepeatedCitations("Study [6] reports gains of 40% over manual testing [6]."));
        Assert.Equal("[8] calls for shared benchmarks [2].", ProseCleaner.DropRepeatedCitations("[8] calls for shared benchmarks [2, 8]."));

        // A marker that ends a clause carries a claim of its own, and "[3] and" does not name [3] as the subject.
        Assert.Equal("Loops recover [3]; audits help [3].", ProseCleaner.DropRepeatedCitations("Loops recover [3]; audits help [3]."));
        Assert.Equal("Studies [3] and [7] show recovery, and logging helps [3].",
            ProseCleaner.DropRepeatedCitations("Studies [3] and [7] show recovery, and logging helps [3]."));
        Assert.Equal("[5] warns [5] that governance fails.", ProseCleaner.DropRepeatedCitations("[5] warns [5] that governance fails."));

        var (clean, changes) = ProseCleaner.Clean("Audits help [2].\n\n[5] warns that governance can fail [5]. Loops recover [1].", "synthesis-2");
        Assert.Equal("Audits help [2].\n\n[5] warns that governance can fail. Loops recover [1].", clean);
        var change = Assert.Single(changes);
        Assert.Equal("repeated citation removed", change.Kind);
        Assert.Equal("[5] warns that governance can fail [5].", change.Text);
    }

    [Fact]
    public void ASentenceThatNamesAStudyDoesNotCiteItAgainAtTheEndOfTheClause()
    {
        // Issue #568, from run 289a0d3e: "[4] identifies ... [4], while [1] highlights ..." and "with [1] describing ... [1] and [5] detailing ...".
        Assert.Equal("Reasoning loops support fault tolerance: [4] identifies scenario generation and ReAct-style loops, while [1] highlights secure pipelines [2].",
            ProseCleaner.DropRepeatedCitations("Reasoning loops support fault tolerance: [4] identifies scenario generation and ReAct-style loops [4], while [1] highlights secure pipelines [2]."));
        Assert.Equal("Governance is a layer, with [1] describing secure pipelines and [5] detailing a control plane.",
            ProseCleaner.DropRepeatedCitations("Governance is a layer, with [1] describing secure pipelines [1] and [5] detailing a control plane [5]."));

        // A repeat that does not close the clause, or that follows another study's marker, stays.
        Assert.Equal("[4] finds that loops [4] recover, while [1] logs [2].", ProseCleaner.DropRepeatedCitations("[4] finds that loops [4] recover, while [1] logs [2]."));
        Assert.Equal("[4] and [1] find loops [4], while [2] logs [3].", ProseCleaner.DropRepeatedCitations("[4] and [1] find loops [4], while [2] logs [3]."));
    }

    private static string DiagramJson(string extraNodeRef = "1") => $$"""
        {"format":"concept-map","title":"How safety mechanisms relate","caption":"Concepts from the themes.",
         "nodes":[{"id":"A","label":"Supervisor checks recover from faults [{{extraNodeRef}}]"},{"id":"B","label":"Governance layers audit actions [2]"},{"id":"C","label":"Safe orchestration"}],
         "edges":[{"from":"A","to":"C","label":"enables"},{"from":"B","to":"C","label":"constrains"}]}
        """;

    [Fact]
    public async Task ADiagramIsValidatedAndDrawnInCode()
    {
        var chat = new FakeChatService()
            .Returns(DiagramJson("9"))   // cites a number outside the list: rejected
            .Returns(DiagramJson());
        var art = await ArtifactBuilder.BuildAsync(chat, ArtifactKinds.ConceptMap, "", "objective", "results [1] [2]", "[1] - A\n[2] - B", 2);

        Assert.Null(art.Error);
        Assert.Contains("citation numbers 9 are not in the reference list", chat.Prompts[1]);
        Assert.Contains(PromptSafety.ReviewerInputNotice, chat.Prompts[0]);
        Assert.Equal(new[] { "n1", "n2", "n3" }, art.Nodes.Select(n => n.Id));
        string mermaid = ArtifactBuilder.ToMermaid(art);
        Assert.StartsWith("flowchart LR", mermaid);
        Assert.Contains("n1[\"Supervisor checks recover from faults [1]\"]", mermaid);
        Assert.Contains("n2 -->|\"constrains\"| n3", mermaid);
        string tikz = ArtifactBuilder.ToTikz(art, t => t);
        Assert.Contains(@"\node[box] (n3) at (4.4,", tikz); // the shared target sits one layer to the right
        Assert.Contains(@"\draw[->, thick] (n1) --", tikz);
        Assert.Equal(new[] { "artifact-n1", "artifact-n2", "artifact-n3" }, art.CitableTexts().Select(t => t.Field));
    }

    [Fact]
    public async Task ACustomRequestPicksATableAndRowsMustMatchTheColumns()
    {
        var chat = new FakeChatService()
            .Returns("""{"format":"table","title":"T","caption":"c","columns":["Approach","Finding"],"rows":[["Loops"]]}""")
            .Returns("""{"format":"table","title":"Approaches compared","caption":"c","columns":["Approach","Finding"],"rows":[["Loops","Recover from faults [1]"],["Audits","Constrain actions [2]"]]}""");
        var art = await ArtifactBuilder.BuildAsync(chat, ArtifactKinds.Custom, "compare the approaches", "objective", "results", "[1] - A\n[2] - B", 2);

        Assert.Contains("every row must have exactly 2 cells", chat.Prompts[1]);
        Assert.Contains("\"compare the approaches\"", chat.Prompts[0]);
        Assert.Equal(ArtifactKinds.Table, art.Kind);
        Assert.Equal("artifact-r2c2", art.CitableTexts().Last().Field);
        art.SetText("artifact-r2c2", "");
        art.Compact();
        Assert.Equal("–", art.Rows[1][1]);
    }

    [Fact]
    public async Task AnArtifactThatCannotBeBuiltSaysSo()
    {
        var chat = new FakeChatService().Returns("{}").Returns("{}");
        var art = await ArtifactBuilder.BuildAsync(chat, ArtifactKinds.Bullets, "", "objective", "results", "[1] - A", 1);
        Assert.NotNull(art.Error);
        Assert.Empty(art.Bullets);
    }

    [Fact]
    public void ADeletedBoxTakesItsArrowsWithIt()
    {
        var art = new ReviewArtifact
        {
            Kind = ArtifactKinds.Flowchart,
            Nodes = new() { new() { Id = "n1", Label = "a" }, new() { Id = "n2", Label = "b" } },
            Edges = new() { new() { From = "n1", To = "n2" } },
            Bullets = new() { "x", "" },
        };
        art.SetText("artifact-n2", "");
        art.Compact();
        Assert.Single(art.Nodes);
        Assert.Empty(art.Edges);
        Assert.Equal(new[] { "x" }, art.Bullets);
    }

    [Fact]
    public void TheDashboardKeepsTheFurthestProgressOfTheSameRun()
    {
        var started = DateTime.UtcNow;
        var plan = RunProgress.Plan(false, false);
        var live = new ReviewStats { RunStartedUtc = started, ProgressPlan = plan, ProgressStep = RunProgress.Extraction, ProgressFraction = 0.5 };
        var stale = new ReviewStats { RunStartedUtc = started, ProgressPlan = plan, ProgressStep = RunProgress.FullText, ProgressFraction = 1 };
        var kept = RunProgress.KeepFurthest(live, stale);
        Assert.Equal(RunProgress.Extraction, kept.ProgressStep);
        Assert.Equal(0.5, kept.ProgressFraction);

        var newer = new ReviewStats { RunStartedUtc = started, ProgressPlan = plan, ProgressStep = RunProgress.Synthesis, ProgressFraction = 0.1 };
        Assert.Equal(RunProgress.Synthesis, RunProgress.KeepFurthest(live, newer).ProgressStep);

        var otherRun = new ReviewStats { RunStartedUtc = started.AddMinutes(5), ProgressPlan = plan, ProgressStep = RunProgress.Search };
        Assert.Equal(RunProgress.Search, RunProgress.KeepFurthest(live, otherRun).ProgressStep);
    }

    private static string Respond(string prompt)
    {
        if (prompt.Contains("You are turning the results of a systematic literature review"))
            return DiagramJson();
        if (prompt.Contains("Results & Synthesis section (Item 20a)"))
            return """{"synthesis":"### Findings\nAgent loops recover from faults through supervisor checks [1].\n\n- Governance layers audit agent actions [2]","discussion":"Loops and audits complement each other [1, 2]."}""";
        return PipelineTests.Respond(prompt);
    }

    [Fact]
    public async Task AFullRunBuildsTheRequestedArtifactChecksItsCitationsAndCleansMarkdown()
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
        Assert.Null(art.Error);
        Assert.Equal(ArtifactKinds.ConceptMap, art.Kind);
        Assert.StartsWith("flowchart LR", art.Mermaid);
        Assert.DoesNotContain("[MERMAID_START]", report.SynthesisResultsItem);

        // The synthesis lost its Markdown, and the change is in the audit.
        Assert.DoesNotContain("###", report.SynthesisResultsItem);
        Assert.Contains("Governance layers audit agent actions [2].", report.SynthesisResultsItem);
        string audit = File.ReadAllText(Path.Join(folder, "citation-audit.json"));
        Assert.Contains("heading removed", audit);

        // The cited boxes of the diagram went through the citation check.
        using (var doc = JsonDocument.Parse(audit))
        {
            var fields = doc.RootElement.GetProperty("SupportChecks").EnumerateArray().Select(c => c.GetProperty("Field").GetString()).ToList();
            Assert.Contains("artifact-n1", fields);
            Assert.Contains("artifact-n2", fields);
        }

        // The box "Safe orchestration" cites nothing, so no study can state either link: both are the review's reading (#571).
        Assert.All(art.Edges, e => Assert.Equal(ArtifactLinks.Reading, e.Check));
        Assert.Contains("-.->", art.Mermaid);
        Assert.Contains("Each of its 2 links was checked", report.SynthesisMethodsItem);
        Assert.Contains("No study is cited by both boxes", File.ReadAllText(Path.Join(folder, ArtifactLinks.FileName)));

        var state = engine.LoadState(runId)!;
        Assert.Equal(ArtifactKinds.ConceptMap, state.ArtifactKind);
        Assert.Equal("Map agent loop safety", state.ReviewObjective);
        Assert.Contains("concept map of the themes", report.SynthesisMethodsItem);
        Assert.Contains("Concept map of the themes", engine.ReadProtocol(runId));
        Assert.Contains("artifact", state.RunSettings!.StageTemperatures.Keys);

        byte[] zip = engine.GenerateWorkspaceArchiveFromDisk(runId);
        using var archive = new ZipArchive(new MemoryStream(zip));
        using var tex = new StreamReader(archive.GetEntry("main.tex")!.Open());
        string latex = tex.ReadToEnd();
        Assert.Contains(@"\node[box] (n1)", latex);
        Assert.Contains(@"\draw[->, thick, dashed]", latex);
        Assert.NotNull(archive.GetEntry(ArtifactLinks.FileName));
        Assert.Contains("How safety mechanisms relate", latex);
    }

    [Fact]
    public async Task NoArtifactWhenNoneIsAskedFor()
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
            SelectedSources: new[] { "arxiv" }, ArtifactKind: ArtifactKinds.None));
        var report = JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json")))!;
        Assert.Null(report.Artifact);
        Assert.Contains("No artifact was requested", engine.ReadProtocol(runId));
    }
}
