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

    private static SynthesisFinding Coded(string id, string address, string attribute, string value) => new()
    {
        Id = id, Address = address, Attribute = attribute, Value = value, Quote = "Quote " + id, Title = "Page " + address,
        Kind = "blogs", Tier = 3, Question = "RQ1",
    };

    [Fact]
    public void FindingsAreGroupedByAttributeAndValueInCode()
    {
        var findings = new List<SynthesisFinding>
        {
            Coded("F1.1", "a", "Practice", "Retrieval"), Coded("F2.1", "b", "Practice", " retrieval "),
            Coded("F2.2", "b", "Practice", "Retrieval"), Coded("F3.1", "c", "Practice", "Summarisation"),
            Coded("F3.2", "c", "Tool", "Retrieval"),
        };

        var groups = MultivocalSynthesiser.ValueGroups(findings);

        Assert.Equal(new[] { "V1", "V2", "V3" }, groups.Select(g => g.Id));
        var retrieval = groups[0];
        Assert.Equal(("Practice", "Retrieval"), (retrieval.Attribute, retrieval.Value)); // case and spaces do not split a value
        Assert.Equal(new[] { "F1.1", "F2.1", "F2.2" }, retrieval.Findings);
        Assert.Equal(2, retrieval.Sources);
        Assert.Equal(2, retrieval.Samples.Count); // one sample quote per source
        Assert.Equal("Practice: Retrieval (3 findings)", retrieval.Label);
        Assert.Equal("Tool", groups[2].Attribute); // the same value under another attribute is its own group
    }

    [Fact]
    public void TheThemesAndThePlacementAreCheckedInCode()
    {
        ModelThemeList Themes(params string[] names) => new() { Themes = names.Select(n => new ModelThemeName { Name = n, Description = "d" }).ToList() };
        Assert.Null(ModelThemeList.Problem(Themes("Summaries", "Retrieval")));
        Assert.Contains("one to 6 themes", ModelThemeList.Problem(Themes()));
        Assert.Contains("one to 6 themes", ModelThemeList.Problem(Themes("a", "b", "c", "d", "e", "f", "g")));
        Assert.Contains("needs a name", ModelThemeList.Problem(Themes("a", " ")));
        Assert.Contains("two themes are called", ModelThemeList.Problem(Themes("Summaries", "summaries ")));

        // How many sources say something is counted in code (seen in a live run: "The majority of sources report…").
        ModelThemeList Described(string description) => new() { Themes = { new ModelThemeName { Name = "Retrieval", Description = description } } };
        Assert.Null(ModelThemeList.Problem(Described("Retrieval brings documents into the context when the agent needs them.")));
        Assert.Contains("says how many sources", ModelThemeList.Problem(Described("The majority of sources report fewer failures.")));
        Assert.Contains("says how many sources", ModelThemeList.Problem(Described("Most practitioners keep a summary.")));
        Assert.Contains("says how many sources", ModelThemeList.Problem(Described("12 sources describe it.")));

        var groups = new HashSet<string> { "V1", "V2" };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Summaries", "Retrieval" };
        ModelPlacement Placed(params ModelPlaced[] p) => new() { Placements = p.ToList() };
        ModelPlaced In(string group, params string[] themes) => new() { Group = group, Themes = themes.ToList() };

        Assert.Null(ModelPlacement.Problem(Placed(In("V1", "summaries"), In("V2", "Retrieval", "Summaries")), groups, names));
        Assert.Contains("\"V9\" is not one of the values", ModelPlacement.Problem(Placed(In("V1", "Summaries"), In("V2", "Retrieval"), In("V9", "Retrieval")), groups, names));
        Assert.Contains("values V2 are not placed", ModelPlacement.Problem(Placed(In("V1", "Summaries")), groups, names));
        Assert.Contains("\"Memory\" is not one of the themes", ModelPlacement.Problem(Placed(In("V1", "Memory"), In("V2", "Retrieval")), groups, names));
        Assert.Contains("needs the reason", ModelPlacement.Problem(Placed(In("V1", "Summaries"), In("V2")), groups, names));
        var withReason = Placed(In("V1", "Summaries"), In("V2"));
        withReason.Placements[1].Reason = "Off the question.";
        Assert.Null(ModelPlacement.Problem(withReason, groups, names));
    }

    [Fact]
    public void ATensionIsKeptOnlyBetweenTwoSourcesOfTheTheme()
    {
        var findings = new List<SynthesisFinding>
        {
            Coded("F1.1", "a", "Practice", "Summarise"), Coded("F1.2", "a", "Practice", "Never summarise"),
            Coded("F2.1", "b", "Practice", "Never summarise"),
        };
        var groups = MultivocalSynthesiser.ValueGroups(findings);
        var answer = new ModelTensions
        {
            Tensions =
            {
                new ModelTension { GroupA = "V1", GroupB = "V2", Description = "V1 keeps a summary, V2 never does, unlike V9." },
                new ModelTension { GroupA = "V1", GroupB = "V7", Description = "Outside the theme." },
                new ModelTension { GroupA = "V1", GroupB = "V1", Description = "With itself." },
            },
        };

        var tensions = MultivocalSynthesiser.TensionsFor(answer, groups, findings, out var notes);

        var kept = Assert.Single(tensions);
        Assert.Equal(("F1.1", "F2.1"), (kept.FindingA, kept.FindingB)); // F1.2 is from the same source as F1.1, so F2.1 is chosen
        // The reader sees the values' names, never the ids the model was given.
        Assert.Equal("Summarise against Never summarise. \"Summarise\" keeps a summary, \"Never summarise\" never does, unlike another value.", kept.Description);
        Assert.Equal("2 tension(s) named a value outside the theme; left out.", Assert.Single(notes));

        var oneSource = MultivocalSynthesiser.TensionsFor(answer, MultivocalSynthesiser.ValueGroups(findings.Take(2).ToList()), findings.Take(2).ToList(), out var oneNotes);
        Assert.Empty(oneSource);
        Assert.Equal(new[] { "2 tension(s) named a value outside the theme; left out.", "1 tension(s) were between values of one source only; left out." }, oneNotes);
    }

    [Fact]
    public void NotStatedAndOtherValuesAreDecidedInCode()
    {
        Assert.True(MultivocalSynthesiser.IsNotStated("Not addressed."));
        Assert.True(MultivocalSynthesiser.IsNotStated(" not stated"));
        Assert.False(MultivocalSynthesiser.IsNotStated("Retrieval"));
        Assert.True(MultivocalSynthesiser.IsOther(new ValueGroup { Value = "other" }));
        Assert.False(MultivocalSynthesiser.IsOther(new ValueGroup { Value = "Other tools" }));
    }

    [Fact]
    public async Task NamesOfAnOpenAttributeAreNeverAskedAboutTensions()
    {
        var findings = new List<SynthesisFinding> { Coded("F1.1", "a", "Tool", "MemGPT"), Coded("F2.1", "b", "Tool", "Letta") };
        var model = new FakeChatService().RespondsWith(prompt =>
            prompt.Contains("Name one to") ? """{"themes":[{"name":"Memory tools","description":"Tools that keep agent memory."}]}"""
            : prompt.Contains("VALUES TO PLACE") ? """{"placements":[{"group":"V1","themes":["Memory tools"],"reason":""},{"group":"V2","themes":["Memory tools"],"reason":""}]}"""
            : throw new InvalidOperationException("Tensions were asked for tool names."));
        var file = new MultivocalSynthesisFile();

        await MultivocalSynthesiser.SynthesiseQuestionAsync(model, "topic", "RQ1.1", "Which tools?", MultivocalSynthesiser.ValueGroups(findings), findings,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tool" }, file, null);

        Assert.Equal(2, model.Prompts.Count);
        Assert.Empty(Assert.Single(file.Themes).Tensions);
    }

    [Fact]
    public async Task ABatchTheModelCannotPlaceIsKeptAsNotPlacedAndTheRestGoOn()
    {
        // 31 values: the first batch of 30 never passes the check, the last one is placed.
        var findings = Enumerable.Range(1, 31).Select(i => Coded($"F{i}.1", $"s{i}", "Practice", $"Value {i}")).ToList();
        var groups = MultivocalSynthesiser.ValueGroups(findings);
        var model = new FakeChatService().RespondsWith(prompt =>
            prompt.Contains("Name one to") ? """{"themes":[{"name":"Context","description":"How sources keep context."},{"name":"Unused","description":"Nothing."}]}"""
            : prompt.Contains("V31 Practice") ? """{"placements":[{"group":"V31","themes":["Context"],"reason":""}]}"""
            : prompt.Contains("VALUES TO PLACE") ? """{"placements":[{"group":"V1","themes":["Context"],"reason":""}]}"""
            : """{"tensions":[]}""");
        var file = new MultivocalSynthesisFile();

        await MultivocalSynthesiser.SynthesiseQuestionAsync(model, "topic", "RQ1", "How?", groups, findings, new HashSet<string>(), file, null);

        Assert.Equal(30, file.Unassigned.Count);
        Assert.All(file.Unassigned, u => Assert.StartsWith("Not placed", u.Reason));
        Assert.Contains(file.Notes, n => n.Contains("30 value(s) could not be placed"));
        Assert.Contains(file.Notes, n => n.Contains("\"Unused\" got no values"));
        var theme = Assert.Single(file.Themes);
        Assert.Equal(new[] { "F31.1" }, theme.Findings);
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
                                new ExtractedValue { Value = "Other", Quote = "Retrieval brings in only the documents the agent needs." },
                                new ExtractedValue { Value = "Not addressed", Quote = "Retrieval brings in only the documents the agent needs." },
                            },
                        },
                        new ExtractedAttribute { Attribute = "Tool or framework" },
                    },
                },
            },
        }));
        await RunHeader.WriteAsync(folder, store.LoadHeader(runId)! with { Stage = MultivocalExtractor.StageExtracted });

        var model = new FakeChatService().RespondsWith(prompt =>
            prompt.Contains("Name one to") ? """{"themes":[{"name":"Summaries keep the context short","description":"Sources keep running summaries."}]}"""
            : prompt.Contains("VALUES TO PLACE") ? """{"placements":[{"group":"V1","themes":["Summaries keep the context short"],"reason":""}]}"""
            : """{"tensions":[]}""");
        var screener = new MultivocalScreener(store, planner, searcher, () => model);
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, () => model);
        var mapper = new MultivocalMapper(store, planner, searcher, assessor, () => model);
        var extractor = new MultivocalExtractor(store, planner, searcher, assessor, mapper, pages, () => model);
        var synthesiser = new MultivocalSynthesiser(store, planner, searcher, assessor, mapper, extractor, pages, () => model, "fake-model");

        await Assert.ThrowsAsync<InvalidOperationException>(() => synthesiser.SynthesiseAsync(runId, "wrong-key"));
        var file = await synthesiser.SynthesiseAsync(runId, key);

        Assert.Equal(2, file.Findings.Count); // the value quoted only from the live page and "Not addressed" are left out
        var finding = file.Findings.Single(f => f.Value != "Other");
        Assert.Equal("Summarisation", finding.Value);
        Assert.Equal("page text", finding.EvidenceBasis);
        Assert.Contains(file.Notes, n => n.Contains("1 extracted value(s) were left out"));
        Assert.Contains(file.Notes, n => n.Contains("1 extracted value(s) only say the source does not address"));
        var notCoded = Assert.Single(file.NotCoded); // "Other" is listed in code, not themed by the model
        Assert.Equal(("RQ1", "Practice", 1), (notCoded.Question, notCoded.Attribute, notCoded.Sources));
        Assert.Equal(2, model.Prompts.Count); // RQ1 only (themes, then placement; one source has no tensions): the other questions have no findings
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
