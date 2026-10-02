using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Thematic synthesis: coding anchored to verified evidence, the codebook guard, the second coding, the
/// coverage fill pass, the revision guard and the citation repair pass, unit by unit and in a full offline run.
/// </summary>
public class ThematicSynthesisTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "thematic-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    private static StudyExtraction Extraction(int reference, params (string Value, string Quote, bool Verified)[] findings) => new()
    {
        ReferenceNumber = reference,
        StudyType = "case study",
        EvidenceBasis = "abstract only",
        KeyFindings = findings.Select(f => new EvidencedValue { Value = f.Value, Quote = f.Quote, QuoteVerified = f.Verified }).ToList(),
    };

    private static ReferencedPaper Paper(int reference, string abstractText) =>
        new(reference, $"P{reference}", $"Paper {reference}", abstractText, Array.Empty<DocumentChunk>());

    [Fact]
    public void OnlyVerifiedFindingsGetIds()
    {
        var e = Extraction(4, ("Loops recover", "quote a", true), ("Invented", "quote b", false), ("not reported", "", false));
        var ids = ThematicSynthesis.VerifiedFindings(e).Select(f => f.Id).ToList();
        Assert.Equal(new[] { "F4.1" }, ids);
        Assert.Empty(ThematicSynthesis.VerifiedFindings(new StudyExtraction { ReferenceNumber = 1, Error = "failed" }));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(70, 8)]
    public void SubsectionLengthGrowsWithTheNumberOfStudies(int studies, int paragraphs) =>
        Assert.Equal(paragraphs, ThematicSynthesis.ParagraphsFor(studies));

    [Fact]
    public void ThemeRangeScalesWithTheReviewAndRespectsTheLimit()
    {
        Assert.Equal((4, 8), ThematicSynthesis.ThemeRange(70, 8));
        Assert.Equal((2, 2), ThematicSynthesis.ThemeRange(2, 8));
        Assert.Equal((4, 5), ThematicSynthesis.ThemeRange(30, 5));
    }

    [Fact]
    public async Task CodesAreAnchoredToVerifiedFindingsOrACheckedQuote()
    {
        var papers = new[] { Paper(1, "Agent loops recover from faults through supervisor checks."), Paper(2, "Governance layers audit agent actions in production.") };
        var extractions = new[] { Extraction(1, ("Loops recover from faults", "Agent loops recover from faults through supervisor checks.", true)), Extraction(2) };
        var chat = new FakeChatService()
            // First answer uses a finding id that belongs to another study: rejected, then corrected.
            .Returns("""{"studies":[{"ref":1,"codes":[{"label":"fault recovery","findings":["F2.1"]}]},{"ref":2,"codes":[]}]}""")
            .Returns("""{"studies":[{"ref":1,"codes":[{"label":"fault recovery by supervisors","findings":["F1.1"]}]},{"ref":2,"codes":[{"label":"auditing agent actions","findings":[],"quote":"Governance layers audit agent actions in production."},{"label":"invented code","findings":[],"quote":"a sentence that is nowhere in the abstract at all"}]}]}""");

        var (codes, uncoded) = await ThematicSynthesis.CodeStudiesAsync(chat, "objective", papers, extractions, batchSize: 6, parallelism: 1);

        Assert.Contains("uses a finding id that is not listed", chat.Prompts[1]);
        Assert.Empty(uncoded);
        Assert.Equal(new[] { "C1", "C2", "C3" }, codes.Select(c => c.Id));
        Assert.Equal("verified finding", codes[0].Anchor);
        Assert.Equal("verified quote", codes[1].Anchor);
        Assert.Equal("unanchored", codes[2].Anchor);
    }

    [Fact]
    public async Task AStudyWithoutRelevantCodesIsRecordedWithItsReason()
    {
        var chat = new FakeChatService().Returns("""{"studies":[{"ref":1,"codes":[],"reason":"About crop yields."}]}""");
        var (codes, uncoded) = await ThematicSynthesis.CodeStudiesAsync(chat, "objective", new[] { Paper(1, "Wheat.") }, new[] { Extraction(1) }, 6, 1);
        Assert.Empty(codes);
        Assert.Equal("About crop yields.", Assert.Single(uncoded).Reason);
    }

    [Fact]
    public async Task CodebookIsRejectedUntilEveryCodedStudyIsPlacedOrExplained()
    {
        var codes = new List<ThematicCode>
        {
            new() { Id = "C1", Reference = 1, Label = "fault recovery" },
            new() { Id = "C2", Reference = 2, Label = "auditing" },
            new() { Id = "C3", Reference = 3, Label = "unrelated" },
        };
        var chat = new FakeChatService()
            .Returns("""{"themes":[{"name":"Recovery","description":"d","codes":["C1"]}],"unassigned":[]}""")
            .Returns("""{"themes":[{"name":"Recovery","description":"d","codes":["C1"]},{"name":"Oversight","description":"d","codes":["C2","C1"]}],"unassigned":[{"code":"C3","reason":"Not about agents."}]}""");

        var (themes, unplaced) = await ThematicSynthesis.BuildCodebookAsync(chat, "objective", codes, 8);

        Assert.Contains("studies 2, 3 are in no theme", chat.Prompts[1]);
        Assert.Equal(new[] { "T1", "T2" }, themes.Select(t => t.Id));
        Assert.Equal(new[] { 1, 2 }, themes[1].Studies); // derived from the codes, not from the model
        Assert.Equal(3, Assert.Single(unplaced).Reference);
        Assert.Contains("Not about agents", unplaced[0].Reason);
    }

    [Fact]
    public void SecondCodingAgreementIsKappaOverStudyThemeCells()
    {
        var themes = new List<Theme>
        {
            new() { Id = "T1", Name = "A", Studies = new() { 1, 2 } },
            new() { Id = "T2", Name = "B", Studies = new() { 3 } },
        };
        var same = ThematicSynthesis.CompareCodings(themes, new Dictionary<int, HashSet<string>>
        {
            [1] = new() { "T1" }, [2] = new() { "T1" }, [3] = new() { "T2" },
        });
        Assert.Equal(1.0, same.Kappa!.Value, 3);
        Assert.Empty(same.Disagreements);

        var different = ThematicSynthesis.CompareCodings(themes, new Dictionary<int, HashSet<string>>
        {
            [1] = new() { "T1" }, [2] = new() { "T2" }, [3] = new() { "T2" },
        });
        Assert.Equal(6, different.CellsCompared);
        Assert.Equal(2, different.Disagreements.Count);
        Assert.True(different.Kappa < 1.0);
    }

    [Fact]
    public void ARevisionMayNotDropACitationOrCiteOutsideTheList()
    {
        Assert.Null(ThematicSynthesis.RevisionProblem("A [1]. B [2].", "A and B agree [1, 2]. C adds [3].", 3));
        Assert.Contains("dropped the citation of [2]", ThematicSynthesis.RevisionProblem("A [1]. B [2].", "A [1].", 3));
        Assert.Contains("outside the reference list", ThematicSynthesis.RevisionProblem("A [1].", "A [1]. B [9].", 3));
        Assert.Contains("empty", ThematicSynthesis.RevisionProblem("A [1].", " ", 3));
    }

    [Theory]
    [InlineData("Loops recover [1, 2].", 1, "Loops recover [2].")]
    [InlineData("Loops recover [1].", 1, "Loops recover.")]
    [InlineData("Loops recover [1-3] and fail [1].", 1, "Loops recover [2, 3] and fail.")]
    public void RemoveReferenceDropsOnlyThatNumber(string sentence, int reference, string expected) =>
        Assert.Equal(expected, PrismaReviewEngine.RemoveReference(sentence, reference));

    [Fact]
    public void MethodsTextDescribesWhatTheRunDid()
    {
        var book = new ThematicCodebook
        {
            Codes = new() { new() { Id = "C1", Reference = 1, Anchor = "verified finding" }, new() { Id = "C2", Reference = 2, Anchor = "unanchored" } },
            Themes = new() { new() { Id = "T1", Name = "Recovery", Studies = new() { 1, 2 } } },
            SecondCoding = new SecondCoding { Kappa = 0.8, CellsCompared = 2 },
        };
        string text = ThematicSynthesis.MethodsText(book, "fake-model", repairEnabled: true);
        Assert.Contains("Thomas and Harden (2008)", text);
        Assert.Contains("1 of 2 codes were anchored", text);
        Assert.Contains("Cohen's kappa = 0.80", text);
        Assert.Contains("rewritten once", text);

        string fallback = ThematicSynthesis.MethodsText(new ThematicCodebook { FallbackReason = "no study could be coded" }, null, false);
        Assert.Contains("synthesised narratively", fallback);
        Assert.Contains("no study could be coded", fallback);

        Assert.Contains("grouped into 1 theme: Recovery (2 studies)", ThematicSynthesis.Overview(book, 3));
        Assert.Equal("The synthesis and discussion cite 2 of the 3 included studies. Not cited: [3] not coded.",
            ThematicSynthesis.CoverageSentence(2, 3, new[] { new UncodedStudy(3, "not coded.") }));
    }

    // ─── Full offline run ───────────────────────────────────────────────────────────────────────────────

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
            return """{"themes":[{"name":"Fault recovery and oversight","description":"How agents recover and are overseen.","codes":["C1","C2"]},{"name":"Governance of agent actions","description":"Auditing what agents do.","codes":["C2"]}],"unassigned":[]}""";
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
}
