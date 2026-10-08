using System.Net;
using System.Text;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The synthesis of a multivocal run (MLR block F, G13), with a fake web and model: themes per research question, what
/// each theme rests on counted in code, and every quote checked against the kept page text, never the live page.
/// </summary>
public class MultivocalSynthesisTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrsynth-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static SynthesisFinding Finding(string id, string address, string kind, double points) => new()
    {
        Id = id, Address = address, Kind = kind, Tier = MultivocalSynthesiser.TierOf(kind), QualityPoints = points, Question = "RQ1",
    };

    [Fact]
    public void WhatAThemeRestsOnIsCountedInCode()
    {
        var findings = new List<SynthesisFinding>
        {
            Finding("F1.1", "a/1", "blogs", 11), Finding("F1.2", "a/1", "blogs", 11),
            Finding("F2.1", "b/2", "code", 13), Finding("F3.1", "c/3", "white-papers", 18),
        };

        var thirdTierOnly = MultivocalSynthesiser.Count(new SynthesisTheme { Findings = { "F1.1", "F1.2", "F2.1" } }, findings);
        Assert.Equal(2, thirdTierOnly.Sources); // two findings of one source count once
        Assert.Equal(2, thirdTierOnly.ThirdTier);
        Assert.Equal(12, thirdTierOnly.MeanQuality);
        Assert.Equal("3rd-tier grey literature only", thirdTierOnly.EvidenceLabel);

        var mixed = MultivocalSynthesiser.Count(new SynthesisTheme { Findings = { "F1.1", "F3.1" } }, findings);
        Assert.Equal((1, 0, 1), (mixed.FirstTier, mixed.SecondTier, mixed.ThirdTier));
        Assert.Equal("grey literature only", mixed.EvidenceLabel);

        findings[3].Literature = "formal";
        Assert.Equal("grey and formal literature", MultivocalSynthesiser.Count(new SynthesisTheme { Findings = { "F1.1", "F3.1" } }, findings).EvidenceLabel);
        Assert.Equal((1, 2, 3), (MultivocalSynthesiser.TierOf("theses"), MultivocalSynthesiser.TierOf("qa"), MultivocalSynthesiser.TierOf("blogs")));
    }

    [Fact]
    public void TheModelsThemesMustUseTheFindingsAndPlaceEveryOne()
    {
        var sources = new Dictionary<string, string> { ["F1.1"] = "a", ["F1.2"] = "a", ["F2.1"] = "b" };
        ModelSynthesis Answer(params ModelTheme[] themes) => new() { Themes = themes.ToList() };
        ModelTheme Theme(string name, params string[] findings) => new() { Name = name, Description = "d", Findings = findings.ToList() };

        Assert.Null(ModelSynthesis.Problem(Answer(Theme("Summaries", "F1.1", "F2.1"), Theme("Notes", "F1.2")), sources));
        Assert.Contains("is not one of the findings", ModelSynthesis.Problem(Answer(Theme("X", "F9.9", "F1.1", "F1.2", "F2.1")), sources));
        Assert.Contains("F1.2 are in no theme", ModelSynthesis.Problem(Answer(Theme("X", "F1.1", "F2.1")), sources));

        var withUnassigned = Answer(Theme("X", "F1.1", "F2.1"));
        withUnassigned.Unassigned.Add(new UnassignedFinding { Finding = "F1.2", Reason = "Off the question." });
        Assert.Null(ModelSynthesis.Problem(withUnassigned, sources));

        var sameSource = Theme("X", "F1.1", "F1.2", "F2.1");
        sameSource.Tensions.Add(new SynthesisTension { FindingA = "F1.1", FindingB = "F1.2", Description = "d" });
        Assert.Contains("same source", ModelSynthesis.Problem(Answer(sameSource), sources));
        sameSource.Tensions[0] = new SynthesisTension { FindingA = "F1.1", FindingB = "F2.1", Description = "They disagree." };
        Assert.Null(ModelSynthesis.Problem(Answer(sameSource), sources));
    }

    private const string KeptWords = "We keep a running summary of the conversation. Retrieval brings in only the documents the agent needs.";

    private sealed class ChangingWeb : HttpMessageHandler
    {
        public string Words { get; set; } = KeptWords;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request.RequestUri!.AbsolutePath == "/post" ? 200 : 404, $"<html><body><main><p>{Words}</p></main></body></html>"));

        // The HttpClient that called the handler owns and disposes the response.
        private static HttpResponseMessage Respond(int status, string body) =>
            new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
    }

    private sealed class Source : IGreySource
    {
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(new List<GreyRecord>
        {
            new("x:1", "Managing agent context", "A post.", "https://blog.example.org/post", "Ana", "blog.example.org", "blogs", null, new Dictionary<string, long>()),
        });
    }

    [Fact]
    public async Task TheFindingsAreGroupedPerQuestionAndCheckedAgainstTheKeptPageNotTheLiveOne()
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new Source());
        await searcher.SearchAsync(runId, key);
        var web = new ChangingWeb();
        var pages = new MultivocalPages(store, searcher, new PageFetcher(web, _ => Task.CompletedTask));
        await pages.KeepAsync(runId, key, "https://blog.example.org/post");

        // The live page changes after its text was kept: a quote only the live page has must fail.
        web.Words = "Agents should never summarise. " + KeptWords;
        Assert.Equal("found", pages.CheckQuote(runId, "blog.example.org/post", "Retrieval brings in only the documents the agent needs."));
        Assert.Equal("not found", pages.CheckQuote(runId, "blog.example.org/post", "Agents should never summarise."));
        Assert.Equal("no kept page", pages.CheckQuote(runId, "other.example.org/x", "Retrieval brings in only the documents the agent needs."));

        string folder = store.FolderOf(runId);
        var map = new MapVersion
        {
            Number = 1,
            Attributes =
            {
                new MapAttribute { Name = "Practice", Question = "RQ1", Values = { "Retrieval", "Summarisation", "Other" }, Multiple = true },
                new MapAttribute { Name = "Tool or framework", Question = "RQ1.1", Open = true },
            },
        };
        File.WriteAllText(Path.Join(folder, MultivocalMapper.MapFile), JsonSerializer.Serialize(new MultivocalMapFile { RunId = runId, Versions = { map }, FixedVersion = 1 }));
        File.WriteAllText(Path.Join(folder, MultivocalQualityAssessor.QualityFile), JsonSerializer.Serialize(new MultivocalQualityFile
        {
            RunId = runId, Sources = { new GreyQuality { Address = "blog.example.org/post", Points = 12, Outcome = "Passed" } },
        }));
        File.WriteAllText(Path.Join(folder, MultivocalExtractor.ExtractionFile), JsonSerializer.Serialize(new MultivocalExtractionFile
        {
            RunId = runId,
            MapVersion = 1,
            Sources =
            {
                new GreyExtraction
                {
                    Address = "blog.example.org/post", Title = "Managing agent context", Url = "https://blog.example.org/post",
                    Attributes =
                    {
                        new ExtractedAttribute
                        {
                            Attribute = "Practice",
                            Values =
                            {
                                new ExtractedValue { Value = "Summarisation", Quote = "We keep a running summary of the conversation." },
                                new ExtractedValue { Value = "Retrieval", Quote = "Agents should never summarise." }, // only on the live page
                            },
                        },
                        new ExtractedAttribute { Attribute = "Tool or framework" },
                    },
                },
            },
        }));
        await RunHeader.WriteAsync(folder, store.LoadHeader(runId)! with { Stage = MultivocalExtractor.StageExtracted });

        var model = new FakeChatService().Returns("""{"themes":[{"name":"Summaries keep the context short","description":"Sources keep running summaries.","findings":["F1.1"],"tensions":[]}],"unassigned":[]}""");
        var screener = new MultivocalScreener(store, planner, searcher, () => model);
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, () => model);
        var mapper = new MultivocalMapper(store, planner, searcher, assessor, () => model);
        var extractor = new MultivocalExtractor(store, planner, searcher, assessor, mapper, pages, () => model);
        var synthesiser = new MultivocalSynthesiser(store, planner, searcher, assessor, mapper, extractor, pages, () => model, "fake-model");

        await Assert.ThrowsAsync<InvalidOperationException>(() => synthesiser.SynthesiseAsync(runId, "wrong-key"));
        var file = await synthesiser.SynthesiseAsync(runId, key);

        var finding = Assert.Single(file.Findings); // the value quoted only from the live page is left out
        Assert.Equal("Summarisation", finding.Value);
        Assert.Equal("page text", finding.EvidenceBasis);
        Assert.Contains(file.Notes, n => n.Contains("1 extracted value(s) were left out"));
        Assert.Single(model.Prompts); // RQ1 only: the other questions have no findings
        Assert.Equal(new[] { "RQ1.1", "RQ1.2", "RQ2", "RQ3" }, file.Unanswered);

        var theme = Assert.Single(file.Themes);
        Assert.Equal("RQ1", theme.Question);
        Assert.Equal(1, theme.Sources);
        Assert.Equal("3rd-tier grey literature only", theme.EvidenceLabel); // one blog post
        Assert.Equal(12, theme.MeanQuality);
        Assert.Equal(MultivocalSynthesiser.StageSynthesised, store.LoadHeader(runId)!.Stage);

        var zip = RunArchive.Build(runId, folder, Array.Empty<(string, byte[])>(), ReviewModules.Find("multivocal")!.ArchiveFiles, null, null);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        Assert.Contains(archive.Entries, e => e.FullName == MultivocalSynthesiser.SynthesisFile);
    }
}
