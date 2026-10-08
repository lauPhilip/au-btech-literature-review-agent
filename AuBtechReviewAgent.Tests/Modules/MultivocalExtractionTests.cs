using System.Net;
using System.Text;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Extracting the sources that passed against the fixed map (MLR block F, G12), with a fake web and model: every value
/// needs a quote found in the kept page, closed attributes take listed values only, and code records the known facts
/// and the coverage.
/// </summary>
public class MultivocalExtractionTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrextract-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private const string PageWords = "We keep a running summary of the conversation. Retrieval brings in only the documents the agent needs. We built it with LangGraph.";

    private static MapVersion Map() => new()
    {
        Number = 2,
        By = "reviewer",
        Attributes =
        {
            new MapAttribute { Name = "Practice", Question = "RQ1", Values = { "Retrieval", "Summarisation", "Memory", "Other" }, Multiple = true },
            new MapAttribute { Name = "Tool or framework", Question = "RQ1.1", Open = true, Multiple = true },
            new MapAttribute { Name = "Evidence", Question = "RQ3", Values = { "Measured", "Experience" } },
        },
    };

    private static ModelExtraction Answer(params (string Attribute, string Value, string Quote)[] values) => new()
    {
        Purpose = new ExtractedValue { Value = "Shows how a team manages agent context.", Quote = "We keep a running summary of the conversation." },
        Attributes = values.GroupBy(v => v.Attribute)
            .Select(g => new ModelExtractedAttribute { Attribute = g.Key, Values = g.Select(v => new ExtractedValue { Value = v.Value, Quote = v.Quote }).ToList() })
            .ToList(),
    };

    [Fact]
    public void OnlyValuesWithAQuoteInThePageAreKeptAndCoverageFollowsThem()
    {
        var answer = Answer(
            ("Practice", "retrieval", "Retrieval brings in only the documents the agent needs."),
            ("Practice", "Summarisation", "We keep a running summary of the conversation."),
            ("Practice", "Memory", "The agent has a long-term vector memory."), // not in the page
            ("Tool or framework", "LangGraph", "We built it with LangGraph."));
        var result = new GreyExtraction();

        MultivocalExtractor.Verify(result, answer, Map(), MultivocalPlan.Example(), PageWords);

        var practice = result.Attributes.Single(a => a.Attribute == "Practice");
        Assert.Equal(new[] { "Retrieval", "Summarisation" }, practice.Values.Select(v => v.Value)); // "retrieval" written as the map writes it
        Assert.Equal("Memory", Assert.Single(practice.Unsupported).Value);
        Assert.True(result.Attributes.Single(a => a.Attribute == "Evidence").NotStated);
        Assert.Equal(new[] { "RQ1", "RQ1.1" }, result.Coverage); // RQ3's attribute is not stated
        Assert.Equal("Shows how a team manages agent context.", result.Purpose!.Value);

        var noPurpose = new GreyExtraction();
        answer.Purpose!.Quote = "A sentence the page does not have.";
        MultivocalExtractor.Verify(noPurpose, answer, Map(), MultivocalPlan.Example(), PageWords);
        Assert.Null(noPurpose.Purpose);
    }

    [Fact]
    public void TheAnswerMustFitTheMap()
    {
        var map = Map();
        Assert.Null(ModelExtraction.Problem(Answer(("Practice", "retrieval", "q"), ("Practice", "Other", "q"), ("Tool or framework", "Anything", "q")), map));
        Assert.Contains("is not an attribute of the map", ModelExtraction.Problem(Answer(("Source type", "Grey", "q")), map));
        Assert.Contains("is not one of the values of \"Practice\"", ModelExtraction.Problem(Answer(("Practice", "Prompting", "q")), map));
        Assert.Contains("takes exactly one value", ModelExtraction.Problem(Answer(("Evidence", "Measured", "q"), ("Evidence", "Experience", "q")), map));
    }

    [Fact]
    public void TheCountsGiveEachValueAndNotStated()
    {
        var file = new MultivocalExtractionFile
        {
            Sources =
            {
                new GreyExtraction { Attributes = { new() { Attribute = "Practice", Values = { new() { Value = "Retrieval" }, new() { Value = "Memory" } } } } },
                new GreyExtraction { Attributes = { new() { Attribute = "Practice", Values = { new() { Value = "Retrieval" }, new() { Value = "retrieval" } } } } },
                new GreyExtraction { Attributes = { new() { Attribute = "Practice" } } },
                new GreyExtraction { Error = "The model call failed." },
            },
        };

        Assert.Equal(new[] { ("Retrieval", 2), ("Memory", 1), ("not stated", 1) }, file.Counts("Practice"));
    }

    private sealed class FakeWeb : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request.RequestUri!.AbsolutePath == "/post"
                ? (200, $"<html><body><main><p>{PageWords}</p></main></body></html>")
                : (404, "")));

        // The HttpClient that called the handler owns and disposes the response.
        private static HttpResponseMessage Respond((int Status, string Body) page) =>
            new((HttpStatusCode)page.Status) { Content = new StringContent(page.Body, Encoding.UTF8, "text/html") };
    }

    private sealed class Source : IGreySource
    {
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(new List<GreyRecord>
        {
            new("x:1", "Managing agent context", "A post.", "https://blog.example.org/post", "Ana", "blog.example.org", "blogs", new DateTime(2025, 5, 1), new Dictionary<string, long>()),
        });
    }

    [Fact]
    public async Task EverySourceThatPassedIsExtractedAgainstTheFixedMap()
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new Source());
        await searcher.SearchAsync(runId, key);
        var pages = new MultivocalPages(store, searcher, new PageFetcher(new FakeWeb(), _ => Task.CompletedTask));
        await pages.KeepAsync(runId, key, "https://blog.example.org/post");

        string folder = store.FolderOf(runId);
        var quality = new MultivocalQualityFile { RunId = runId, Threshold = 10, Sources = { new GreyQuality { Address = "blog.example.org/post", Title = "Managing agent context", Url = "https://blog.example.org/post", Points = 12, Outcome = "Passed" } } };
        File.WriteAllText(Path.Join(folder, MultivocalQualityAssessor.QualityFile), JsonSerializer.Serialize(quality));
        var mapFile = new MultivocalMapFile { RunId = runId, Versions = { Map() }, FixedVersion = 2 };
        mapFile.Versions[0].Number = 2;
        File.WriteAllText(Path.Join(folder, MultivocalMapper.MapFile), JsonSerializer.Serialize(mapFile));
        await RunHeader.WriteAsync(folder, store.LoadHeader(runId)! with { Stage = MultivocalMapper.StageMapped });

        var model = new FakeChatService().Returns(JsonSerializer.Serialize(Answer(
            ("Practice", "Retrieval", "Retrieval brings in only the documents the agent needs."),
            ("Tool or framework", "LangGraph", "We built it with LangGraph.")), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var screener = new MultivocalScreener(store, planner, searcher, () => model);
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, () => model);
        var mapper = new MultivocalMapper(store, planner, searcher, assessor, () => model);
        var extractor = new MultivocalExtractor(store, planner, searcher, assessor, mapper, pages, () => model, model: "fake-model", parallelism: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => extractor.ExtractAsync(runId, "wrong-key"));
        var file = await extractor.ExtractAsync(runId, key);

        var source = Assert.Single(file.Sources);
        Assert.Null(source.Error);
        Assert.Equal(2, file.MapVersion);
        Assert.Contains("Retrieval | Summarisation | Memory | Other", model.Prompts[0]);
        Assert.Equal("Retrieval", source.Attributes.Single(a => a.Attribute == "Practice").Values.Single().Value);
        Assert.Equal("2025-05-01", source.Known.Single(k => k.Name == "Date").Value);
        Assert.Equal("12 of 20", source.Known.Single(k => k.Name == "Quality points").Value);
        Assert.Equal(new[] { "RQ1", "RQ1.1" }, source.Coverage);
        Assert.Equal(MultivocalExtractor.StageExtracted, store.LoadHeader(runId)!.Stage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => extractor.ExtractAsync(runId, key)); // once

        var zip = RunArchive.Build(runId, folder, Array.Empty<(string, byte[])>(), ReviewModules.Find("multivocal")!.ArchiveFiles, null, null);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        Assert.Contains(archive.Entries, e => e.FullName == MultivocalExtractor.ExtractionFile);
    }
}
