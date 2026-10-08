using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The writing stages of a review (module design M8): the theme sections with their fill pass, the discussion and
/// the automated peer review with its revision. A whole systematic review runs offline with a fake model that codes
/// the studies, builds the codebook and answers every writing prompt, and the prompts the writing stages sent are
/// compared, word for word, with the prompts the systematic review sent before the stages were shared
/// (Golden/systematic-writing-prompts.txt). Set UPDATE_GOLDEN=1 to write the file again after a deliberate change.
/// </summary>
public class ReviewWriterTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "writer-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static string GoldenPath([CallerFilePath] string here = "") => Path.Join(Path.GetDirectoryName(here)!, "Golden", "systematic-writing-prompts.txt");

    /// <summary>The markers of the prompts the writing stages send.</summary>
    private static readonly string[] WritingMarkers =
    {
        "You are writing one subsection of the Results",
        "You are revising one theme subsection",
        "You are writing the Discussion section",
        "strict but constructive peer reviewer",
        "You are the author revising one section",
    };

    private static string Respond(string prompt)
    {
        if (prompt.Contains("You are coding studies"))
        {
            var studies = Regex.Matches(prompt, @"STUDY \[(\d+)\][^\n]*\n(.*?)(?=STUDY \[|\z)", RegexOptions.Singleline).Select(m =>
            {
                int reference = int.Parse(m.Groups[1].Value);
                string block = m.Groups[2].Value;
                string finding = $"F{reference}.1";
                return block.Contains(finding + ":")
                    ? $$"""{"ref":{{reference}},"codes":[{"label":"fault recovery by supervisors","findings":["{{finding}}"],"quote":""}],"reason":""}"""
                    : $$"""{"ref":{{reference}},"codes":[{"label":"auditing agent actions","findings":[],"quote":"{{Regex.Match(block, @"Abstract: ([^\n]*)").Groups[1].Value.Trim()}}"}],"reason":""}""";
            });
            return $$"""{"studies":[{{string.Join(",", studies)}}]}""";
        }
        if (prompt.Contains("You are building the codebook"))
            return """{"themes":[{"name":"Recovery and oversight","description":"How agent loops recover and are overseen.","codes":["C1","C2"]}],"unassigned":[]}""";
        if (prompt.Contains("second, independent coder")) return """{"studies":[{"ref":1,"themes":["T1"]},{"ref":2,"themes":["T1"]}]}""";
        if (prompt.Contains("You are writing one subsection of the Results"))
            return """{"heading":"Recovery and oversight","text":"Agent loops recover from faults through supervisor checks [1]."}""";
        if (prompt.Contains("You are revising one theme subsection"))
            return """{"text":"Agent loops recover from faults through supervisor checks [1]. Governance layers audit agent actions [2]."}""";
        if (prompt.Contains("You are writing the Discussion section"))
            return """{"text":"Recovery and audits complement each other [1, 2]."}""";
        if (prompt.Contains("strict but constructive peer reviewer"))
            return """{"comments":[{"section":"synthesis-1","severity":"medium","issue":"Thin comparison.","suggestion":"Compare the two mechanisms."},{"section":"discussion","severity":"low","issue":"Short.","suggestion":"Expand."}]}""";
        if (prompt.Contains("You are the author revising one section"))
            return """{"text":"Agent loops recover from faults through supervisor checks [1], while governance layers audit agent actions [2]."}""";
        return PipelineTests.Respond(prompt);
    }

    /// <summary>The prompts of the writing stages from one offline systematic review, in a fixed order.</summary>
    private async Task<(string Prompts, ReviewState State)> RunAsync()
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

        List<string> prompts;
        lock (model.Prompts) prompts = model.Prompts.ToList();
        var sb = new StringBuilder();
        foreach (var marker in WritingMarkers)
            foreach (var prompt in prompts.Where(p => p.Contains(marker)).OrderBy(p => p, StringComparer.Ordinal))
                sb.Append("=== ").AppendLine(marker).AppendLine(prompt.Replace("\r\n", "\n"));
        return (sb.ToString().Replace("\r\n", "\n"), engine.LoadState(runId)!);
    }

    [Fact]
    public async Task TheSystematicReviewSendsTheSameWritingPromptsAsBefore()
    {
        var (prompts, state) = await RunAsync();

        // The run went through every writing stage.
        foreach (var marker in WritingMarkers) Assert.Contains("=== " + marker, prompts);
        Assert.NotNull(state.ThematicSynthesis);
        Assert.Null(state.ThematicSynthesis!.FallbackReason);
        Assert.Equal("added 1 of 1 uncited studies", Assert.Single(state.ThematicSynthesis.Coverage).FillPass);

        string path = GoldenPath();
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, prompts);
        }
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), prompts);
    }

    [Fact]
    public async Task AnotherKindOfReviewGetsItsOwnWordsAndTheSameChecks()
    {
        var evidence = new ReviewEvidence
        {
            SourceLabel = "SOURCE",
            Sources = new[]
            {
                new EvidenceSource { Reference = 1, ReferenceLine = "Ana (2025). Keeping context short. blog.example.org", Findings = new[] { new EvidenceFinding("F1.1", "Summarisation", "We keep a running summary.") } },
                new EvidenceSource { Reference = 2, ReferenceLine = "Bo (2026). Retrieval first. docs.example.org", Findings = new[] { new EvidenceFinding("F2.1", "Retrieval", "Fetch only what is needed.") } },
            },
            Themes = new[] { new EvidenceTheme { Id = "T1", Name = "Short contexts", Description = "Keeping the context small.", Sources = new[] { 1, 2 } } },
        };
        var profile = new WritingProfile { Review = "a grey literature review", ReviewShort = "a grey literature review", ResultsSection = "the results", Source = "source", Sources = "sources" };
        var model = new FakeChatService()
            .Returns("""{"heading":"Short contexts","text":"Sources keep a running summary [1]. One cites a source outside the list [7]."}""") // rejected: [7] is not in the list
            .Returns("""{"heading":"Short contexts","text":"Sources keep a running summary [1]."}""")
            .Returns("""{"text":"Sources keep a running summary [1] and fetch only what is needed [2]."}""");

        var (section, coverage) = await ReviewWriter.WriteThemeAsync(model, profile, "Map the practices", evidence, evidence.Themes[0], 1, referenceCount: 2);

        Assert.Contains("of a grey literature review", model.Prompts[0]);
        Assert.Contains("Cite every one of these sources at least once: [1], [2]", model.Prompts[0]);
        Assert.Contains("SOURCE [1]", model.Prompts[0]);
        Assert.DoesNotContain("PRISMA", model.Prompts[0]);
        Assert.DoesNotContain("studies", model.Prompts[0]);
        Assert.Contains("citation numbers 7 are not in the reference list", model.Prompts[1]);
        Assert.Contains("Integrate the sources [2]", model.Prompts[2]);
        Assert.Equal("Sources keep a running summary [1] and fetch only what is needed [2].", section!.Text);
        Assert.Equal("added 1 of 1 uncited sources", coverage.FillPass);
        Assert.Empty(coverage.NotCited);
    }
}
