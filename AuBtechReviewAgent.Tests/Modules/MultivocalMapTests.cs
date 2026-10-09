using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The systematic map of a multivocal run (MLR block F, G12), with a fake model: proposed from the research questions
/// and the sources that passed, edited by the reviewer as new versions, then fixed.
/// </summary>
public class MultivocalMapTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrmap-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private sealed class Source : IGreySource
    {
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(new List<GreyRecord>
        {
            new("x:1", "Memory for LLM agents", "How agents keep notes between steps.", "https://a.example.org/1", "Ana", "a.example.org", "blogs", null, new Dictionary<string, long>()),
            new("x:2", "Weak post on agents", "Not much.", "https://b.example.org/2", "Bo", "b.example.org", "blogs", null, new Dictionary<string, long>()),
        });
    }

    private const string GoodMap = """
        {"attributes":[
          {"name":"Practice","question":"RQ1","description":"The context engineering practice described.","values":["Retrieval","Summarisation","Memory","Other"],"multiple":true,"open":false},
          {"name":"Tools","question":"RQ1.1","description":"Tools and frameworks named.","values":[],"multiple":true,"open":true}
        ]}
        """;

    /// <summary>A run at "Assessed": planned, searched, and with a quality file in which the first source passed.</summary>
    private async Task<(RunStore Store, Guid RunId, string Key, MultivocalMapper Mapper, FakeChatService Model)> AssessedRun(FakeChatService model, bool anyPassed = true)
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);
        var searcher = new MultivocalSearcher(store, planner, _ => new Source());
        await searcher.SearchAsync(runId, key);

        var quality = new MultivocalQualityFile
        {
            RunId = runId,
            Threshold = 10,
            Sources =
            {
                new GreyQuality { Address = "a.example.org/1", Title = "Memory for LLM agents", Url = "https://a.example.org/1", Points = 14, Outcome = anyPassed ? "Passed" : "Below threshold" },
                new GreyQuality { Address = "b.example.org/2", Title = "Weak post on agents", Url = "https://b.example.org/2", Points = 4, Outcome = "Below threshold" },
            },
        };
        string folder = store.FolderOf(runId);
        File.WriteAllText(Path.Join(folder, MultivocalQualityAssessor.QualityFile), JsonSerializer.Serialize(quality));
        await RunHeader.WriteAsync(folder, store.LoadHeader(runId)! with { Stage = MultivocalQualityAssessor.StageAssessed });

        var screener = new MultivocalScreener(store, planner, searcher, () => model);
        var pages = new MultivocalPages(store, searcher, new PageFetcher());
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, () => model);
        return (store, runId, key, new MultivocalMapper(store, planner, searcher, assessor, () => model, "fake-model"), model);
    }

    [Fact]
    public async Task TheModelProposesTheFirstVersionFromTheQuestionsAndThePassedSources()
    {
        var (store, runId, key, mapper, model) = await AssessedRun(new FakeChatService().Returns(GoodMap));

        var file = await mapper.ProposeAsync(runId, key);

        var version = Assert.Single(file.Versions);
        Assert.Equal("model", version.By);
        Assert.Equal(new[] { "Practice", "Tools" }, version.Attributes.Select(a => a.Name));
        Assert.Empty(version.Attributes[1].Values); // open text
        Assert.Equal(1, file.SourcesSeen);
        string prompt = Assert.Single(model.Prompts);
        Assert.Contains("RQ1.1", prompt);
        Assert.Contains("Memory for LLM agents", prompt);
        Assert.DoesNotContain("Weak post on agents", prompt); // below the quality threshold
        Assert.Equal(MultivocalMapper.StageMapping, store.LoadHeader(runId)!.Stage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mapper.ProposeAsync(runId, key)); // once
    }

    [Fact]
    public async Task AMapForQuestionsThePlanDoesNotHaveIsRejected()
    {
        string wrong = GoodMap.Replace("\"RQ1.1\"", "\"RQ9\"");
        var (_, runId, key, mapper, _) = await AssessedRun(new FakeChatService().Returns(wrong).Returns(wrong));

        var ex = await Assert.ThrowsAsync<LlmOutputException>(() => mapper.ProposeAsync(runId, key));
        Assert.Contains("must belong to one of the research questions", ex.Message);
    }

    [Fact]
    public async Task TheReviewersEditsAreNewVersionsAndTheFixedMapCannotChange()
    {
        var (store, runId, key, mapper, _) = await AssessedRun(new FakeChatService().Returns(GoodMap));
        var first = await mapper.ProposeAsync(runId, key);

        var edited = MultivocalMapper.Clean(first.Latest.Attributes);
        edited[0].Values = new List<string> { "Retrieval", " retrieval ", "", "Compression", "Other" };
        edited.Add(new MapAttribute { Name = "Evidence", Question = "rq2", Description = "How the practice was judged.", Values = new() { "Measured", "Experience" } });
        var saved = await mapper.SaveAsync(runId, key, edited);

        Assert.Equal(2, saved.Versions.Count);
        Assert.Equal("reviewer", saved.Latest.By);
        Assert.Equal(new[] { "Retrieval", "Compression", "Other" }, saved.Latest.Attributes[0].Values); // cleaned
        Assert.Equal("RQ2", saved.Latest.Attributes[2].Question);
        Assert.Equal(new[] { "Retrieval", "Summarisation", "Memory", "Other" }, saved.Versions[0].Attributes[0].Values); // version 1 kept

        var oneValue = MultivocalMapper.Clean(saved.Latest.Attributes);
        oneValue[2].Values = new List<string> { "Measured" };
        var bad = await Assert.ThrowsAsync<ArgumentException>(() => mapper.SaveAsync(runId, key, oneValue));
        Assert.Contains("at least two values", bad.Message);

        var fixedMap = await mapper.FixAsync(runId, key);
        Assert.Equal(2, fixedMap.FixedVersion);
        Assert.Equal(MultivocalMapper.StageMapped, store.LoadHeader(runId)!.Stage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mapper.SaveAsync(runId, key, edited));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mapper.FixAsync(runId, key));

        var zip = RunArchive.Build(runId, store.FolderOf(runId), Array.Empty<(string, byte[])>(), ReviewModules.Find("multivocal")!.ArchiveFiles, null, null);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        Assert.Contains(archive.Entries, e => e.FullName == MultivocalMapper.MapFile);
    }

    [Fact]
    public async Task WithoutAPassedSourceOrTheKeyThereIsNoMap()
    {
        var (_, runId, key, mapper, _) = await AssessedRun(new FakeChatService().Returns(GoodMap), anyPassed: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mapper.ProposeAsync(runId, key));

        var (_, run2, _, mapper2, _) = await AssessedRun(new FakeChatService().Returns(GoodMap));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mapper2.ProposeAsync(run2, "wrong-key"));
    }

    [Fact]
    public void NotStatedValuesAndCamelCaseNamesAreCleanedAway()
    {
        var cleaned = MultivocalMapper.Clean(new[]
        {
            new MapAttribute { Name = "ToolOrFramework", Question = "rq1.1", Open = true, Values = new() { "LangChain" } },
            new MapAttribute { Name = "Retrieval use", Question = "RQ3", Values = new() { "Yes", "No", "Not specified", "Unknown", "N/A." } },
        });

        Assert.Equal("Tool or framework", cleaned[0].Name);
        Assert.Equal("RQ1.1", cleaned[0].Question);
        Assert.Empty(cleaned[0].Values); // open text keeps no values
        Assert.Equal(new[] { "Yes", "No" }, cleaned[1].Values);
        Assert.Equal("Context window", MultivocalMapper.PlainName("Context window"));
        Assert.Equal("RAG", MultivocalMapper.PlainName("RAG"));
    }

    [Fact]
    public void TheModelsMapStaysWithTheQuestionsAndLeavesOutWhatIsKnown()
    {
        var plan = MultivocalPlan.Example();
        MapAttribute A(string name, string question, params string[] values) => new() { Name = name, Question = question, Values = values.ToList() };

        // As in the first live run: four attributes for RQ1, with "Memory" both a practice and an attribute of its own.
        var crowded = MultivocalMapper.Clean(new[]
        {
            A("Practice", "RQ1", "Retrieval", "Memory management", "Other"), A("Memory type", "RQ1", "Short-term", "Long-term"),
            A("Domain", "RQ1", "Coding", "Research"), A("Security focus", "RQ1", "Yes", "No"),
        });
        Assert.Contains("RQ1 has 4 attributes", MultivocalMapper.ModelProblem(crowded, plan));
        Assert.Null(MultivocalMapper.Problem(crowded, plan)); // the reviewer may still keep four

        var shared = MultivocalMapper.Clean(new[] { A("Practice", "RQ1", "Retrieval", "Memory", "Other"), A("Stage", "RQ1.2", "Design", "Memory", "Other") });
        Assert.Contains("\"Memory\" is a value of both", MultivocalMapper.ModelProblem(shared, plan));

        var known = MultivocalMapper.Clean(new[] { A("Practice", "RQ1", "Retrieval", "Other"), A("SourceType", "RQ2", "Grey", "Academic") });
        Assert.Null(MultivocalMapper.ModelProblem(known, plan)); // dropped in code, not sent back to the model
        Assert.True(MultivocalMapper.IsKnownFact(known[1]));
        Assert.False(MultivocalMapper.IsKnownFact(known[0]));

        var fine = MultivocalMapper.Clean(new[] { A("Practice", "RQ1", "Retrieval", "Other"), A("Code quality practice", "RQ1", "Linting", "Review") });
        Assert.Null(MultivocalMapper.ModelProblem(fine, plan)); // "quality" inside a longer name is fine
    }

    [Fact]
    public void TheKnownFactsComeFromTheRecordAndTheScore()
    {
        var record = new GreyRecord("x:1", "Memory for LLM agents", "s", "https://a.example.org/1", "Ana", "a.example.org", "qa", new DateTime(2025, 3, 1), new Dictionary<string, long>());
        var facts = MultivocalMapper.KnownFactsFor(record, new GreyQuality { Points = 13.5, Outcome = "Passed" });

        Assert.Equal(MultivocalMapper.KnownFacts.Select(f => f.Name), facts.Select(f => f.Name));
        Assert.Equal("Grey", facts.Single(f => f.Name == "Literature").Value);
        Assert.Equal("2025-03-01", facts.Single(f => f.Name == "Date").Value);
        Assert.Equal("13.5 of 20", facts.Single(f => f.Name == "Quality points").Value);
        Assert.Equal("not given", MultivocalMapper.KnownFactsFor(record with { Published = null }, null).Single(f => f.Name == "Date").Value);
    }

    private const string MapWithSourceType = """
        {"attributes":[
          {"name":"Practice","question":"RQ1","description":"The practice described.","values":["Retrieval","Summarisation","Other"],"multiple":true,"open":false},
          {"name":"SourceType","question":"RQ2","description":"Grey or academic.","values":["Grey","Academic"],"multiple":false,"open":false}
        ]}
        """;

    [Fact]
    public async Task AnAttributeForAKnownFactIsLeftOutOfTheProposalWithANote()
    {
        // As in a live run: RQ2 asks how often a practice is mentioned in grey and in academic sources.
        var (_, runId, key, mapper, model) = await AssessedRun(new FakeChatService().Returns(MapWithSourceType));

        var file = await mapper.ProposeAsync(runId, key);

        Assert.Single(model.Prompts); // accepted at once, not sent back
        Assert.Equal("Practice", Assert.Single(file.Latest.Attributes).Name);
        Assert.Contains(file.Notes, n => n.Contains("\"Source type\" (RQ2) was left out"));
        Assert.Contains("RQ2 has no attribute in the model's map, so the extraction records nothing for it.", file.Notes); // said, not hidden
    }
}
