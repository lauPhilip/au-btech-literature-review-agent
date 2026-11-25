using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Thematic synthesis in a full offline run: one cited subsection per theme, coverage, citation repair, and the
/// fallback to a single-pass synthesis when coding fails.
/// </summary>
public class ThematicSynthesisRunTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "thematic-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    private static string Respond(string prompt)
    {
        if (prompt.Contains("You are coding studies for the thematic synthesis"))
        {
            var refs = Regex.Matches(prompt, @"STUDY \[(\d+)\]").Select(m => int.Parse(m.Groups[1].Value));
            var studies = refs.Select(r => prompt.Contains($"F{r}.1:")
                ? (object)new { @ref = r, codes = new[] { new { label = "fault recovery by supervisor checks", findings = new[] { $"F{r}.1" }, quote = "" } } }
                : new { @ref = r, codes = new[] { new { label = "auditing agent actions", findings = Array.Empty<string>(), quote = "Governance layers audit agent actions in production." } } });
            return JsonSerializer.Serialize(new { studies });
        }
        if (prompt.Contains("You are building the codebook"))
            return """{"themes":[{"name":"Fault recovery and oversight","description":"How agents recover and are overseen.","codes":["C1","C2"]},{"name":"Governance of agent actions","description":"Auditing what agents do.","codes":["C2"],"singleStudyReason":"Only this study covers it."}],"unassigned":[]}""";
        if (prompt.Contains("second, independent coder"))
            return """{"studies":[{"ref":1,"themes":["T1"]},{"ref":2,"themes":["T1","T2"]}]}""";
        if (prompt.Contains("You are writing one subsection"))
            return prompt.Contains("THEME: Fault recovery")
                ? """{"heading":"Fault recovery and oversight","text":"Agent loops recover from faults through supervisor checks [1]."}"""
                : """{"heading":"Governance of agent actions","text":"Governance layers audit agent actions [2]."}""";
        if (prompt.Contains("so that it covers every study of the theme"))
            return """{"text":"Agent loops recover from faults through supervisor checks [1]. Governance layers audit agent actions in production [2]."}""";
        if (prompt.Contains("You are writing the Discussion section"))
            return """{"text":"Loops and audits complement each other [1, 2]."}""";
        if (prompt.Contains("You are checking citations") && prompt.Contains("CITED PAPER [1]") && prompt.Contains("complement each other [1, 2]"))
        {
            // Paper 1 does not support the discussion sentence: the repair pass must deal with it.
            int n = Regex.Matches(prompt, @"^S\d+: ", RegexOptions.Multiline).Count;
            var items = Enumerable.Range(1, n).Select(i =>
            {
                bool flagged = Regex.IsMatch(prompt, $@"^S{i}: Loops and audits", RegexOptions.Multiline);
                string e1 = Regex.Match(prompt, @"^E1: (.*)$", RegexOptions.Multiline).Groups[1].Value;
                return flagged
                    ? new { sentence = i, verdict = "not_supported", excerpt = "E1", quote = "", reason = "The paper does not discuss audits." }
                    : new { sentence = i, verdict = "supported", excerpt = "E1", quote = e1[..Math.Min(40, e1.Length)], reason = "ok" };
            });
            return JsonSerializer.Serialize(new { results = items });
        }
        if (prompt.Contains("You are correcting citations"))
            return """{"repairs":[{"id":1,"action":"drop_citation","sentence":"","reason":"Only paper 2 discusses audits."}]}""";
        return PipelineTests.Respond(prompt);
    }

    [Fact]
    public async Task FullRunWritesOneCitedSubsectionPerThemeCoversEveryStudyAndRepairsCitations()
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(Respond),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));

        var state = engine.LoadState(runId)!;
        Assert.Equal(PrismaReviewEngine.StageComplete, state.Stats.ProcessingStage);
        string folder = Path.Join(_root, runId.ToString("N"));
        var report = JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(folder, "prisma-report.json")))!;

        // One subsection per theme, each with its own field name for the citation check.
        Assert.Equal(new[] { "synthesis-1", "synthesis-2" }, report.SynthesisSections.Select(s => s.Field));
        Assert.Equal("Fault recovery and oversight", report.SynthesisSections[0].Heading);
        Assert.Contains("synthesised thematically", report.SynthesisResultsItem);
        Assert.Contains("Thomas and Harden (2008)", report.SynthesisMethodsItem);

        // The codebook: anchors checked in code, study membership derived from codes, coverage filled.
        var book = state.ThematicSynthesis!;
        Assert.Equal(new[] { "verified finding", "verified quote" }, book.Codes.Select(c => c.Anchor));
        Assert.Equal(new[] { 1, 2 }, book.Themes[0].Studies);
        Assert.Equal(new[] { 1 }, book.Coverage[0].CitedAfterWriting);
        Assert.Equal("added 1 of 1 uncited studies", book.Coverage[0].FillPass);
        Assert.Empty(book.Coverage[0].NotCited);
        Assert.Equal(1.0, book.SecondCoding!.Kappa!.Value, 3);
        Assert.Equal(2, state.Stats.ThemesIdentified);
        Assert.Equal(2, state.Stats.StudiesCoded);
        Assert.Equal(2, state.Stats.StudiesCitedInSynthesis);
        Assert.Contains("cite 2 of the 2 included studies", report.CoverageSummary);

        // The unsupported citation was dropped by the repair pass and the changed sentence checked again.
        Assert.Equal("Loops and audits complement each other [2].", report.DiscussionItem);
        Assert.Equal(1, state.Stats.CitationsRepaired);
        Assert.Equal(0, state.Stats.CitationsNotSupported);
        string audit = File.ReadAllText(Path.Join(folder, "citation-audit.json"));
        Assert.Contains("\"Repairs\"", audit);
        Assert.Contains("drop_citation", audit);
        using (var doc = JsonDocument.Parse(audit))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("InitialSupportSummary").GetProperty("NotSupported").GetInt32());
            var fields = doc.RootElement.GetProperty("SupportChecks").EnumerateArray().Select(c => c.GetProperty("Field").GetString()).Distinct().ToList();
            Assert.Contains("synthesis-1", fields);
            Assert.Contains("synthesis-2", fields);
            Assert.Contains("discussionItem", fields);
        }

        // Every new stage is recorded with its temperature.
        foreach (var stage in new[] { "thematic-coding", "thematic-codebook", "thematic-coding-second", "theme-sections", "coverage-fill", "discussion", "citation-repair" })
            Assert.Contains(stage, state.RunSettings!.StageTemperatures.Keys);

        // The archive carries the codebook and main.tex has the subsections and the evidence map.
        byte[] zip = engine.GenerateWorkspaceArchiveFromDisk(runId);
        using var archive = new ZipArchive(new MemoryStream(zip));
        Assert.NotNull(archive.GetEntry("thematic-codebook.json"));
        using var texReader = new StreamReader(archive.GetEntry("main.tex")!.Open());
        string tex = texReader.ReadToEnd();
        Assert.Contains(@"\subsection{Fault recovery and oversight}", tex);
        Assert.Contains("Evidence Map of the Thematic Synthesis", tex);
        Assert.Contains(@"\subsection{Synthesis Methods}", tex);
    }

    [Fact]
    public async Task WhenCodingFailsTheRunFallsBackToTheSinglePassSynthesisAndSaysWhy()
    {
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => new FakeChatService().RespondsWith(PipelineTests.Respond),
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));

        var state = engine.LoadState(runId)!;
        var report = JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json")))!;
        Assert.Empty(report.SynthesisSections);
        Assert.Contains("[1]", report.SynthesisResultsItem);
        Assert.Contains("no study could be coded", state.ThematicSynthesis!.FallbackReason);
        Assert.Contains("synthesised narratively", report.SynthesisMethodsItem);
        Assert.Equal(0, state.Stats.ThemesIdentified);
    }

    [Fact]
    public async Task ASecondRunOnTheSameStudiesReusesTheCodingAndCodebookAndGetsTheSameThemes()
    {
        var cache = new ReviewCache(new CacheOptions { Folder = Path.Join(_root, "cache") }, _root);
        async Task<(ReviewState State, PrismaReport Report, FakeChatService Chat)> Run()
        {
            var chat = new FakeChatService().RespondsWith(Respond);
            var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
            {
                WorkspaceRoot = _root,
                Cache = cache,
                ChatFactory = _ => chat,
                SourceFactory = _ => new PipelineTests.FakeSource(),
                FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
            };
            var runId = Guid.NewGuid();
            await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3, SelectedSources: new[] { "arxiv" }));
            string file = Path.Join(_root, runId.ToString("N"), "prisma-report.json");
            return (engine.LoadState(runId)!, JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(file))!, chat);
        }

        var (first, firstReport, _) = await Run();
        var (second, report, chat) = await Run();

        string prompts = string.Join("\n", chat.Prompts);
        Assert.DoesNotContain("You are coding studies for the thematic synthesis", prompts);
        Assert.DoesNotContain("You are building the codebook", prompts);
        var reuse = second.ThematicSynthesis!.Reuse!;
        Assert.Equal((1, 1, true), (reuse.CodingAnswers, reuse.CodingAnswersReused, reuse.CodebookReused));
        Assert.All(second.ThematicSynthesis.Codes, c => Assert.True(c.FromCache));
        Assert.Equal(first.ThematicSynthesis!.Themes.Select(t => (t.Name, string.Join(",", t.Studies))),
            second.ThematicSynthesis.Themes.Select(t => (t.Name, string.Join(",", t.Studies))));
        Assert.Equal((0, 0, false), (first.ThematicSynthesis.Reuse!.CodingAnswersReused, 0, first.ThematicSynthesis.Reuse.CodebookReused));
        Assert.DoesNotContain("coding answer", firstReport.SynthesisMethodsItem);
        Assert.Contains("1 of 1 coding answer and the codebook were reused from an earlier identical answer", report.SynthesisMethodsItem);
    }
}
