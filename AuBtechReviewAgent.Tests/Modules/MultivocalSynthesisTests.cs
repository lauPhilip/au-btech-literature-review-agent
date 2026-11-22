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

    /// <summary>The fake model's support check: every value supported, except "Retrieval" quoted from a sentence about summaries.</summary>
    private static string SupportAnswer(string prompt)
    {
        var verdicts = System.Text.RegularExpressions.Regex.Matches(prompt, @"^(F\d+\.\d+) \| [^:]+: (.*?) \| quote: ""(.*)""$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[2].Value == "Retrieval" && m.Groups[3].Value.Contains("summary")
                ? $$"""{"finding":"{{m.Groups[1].Value}}","verdict":"not_supported","reason":"The quote is about summaries."}"""
                : $$"""{"finding":"{{m.Groups[1].Value}}","verdict":"supported","reason":""}""");
        return $$"""{"verdicts":[{{string.Join(",", verdicts)}}]}""";
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
                                new ExtractedValue { Value = "Retrieval", Quote = "We keep a running summary of the conversation." }, // on the page, but about something else
                            },
                        },
                        new ExtractedAttribute { Attribute = "Tool or framework" },
                    },
                },
            },
        }));
        await RunHeader.WriteAsync(folder, store.LoadHeader(runId)! with { Stage = MultivocalExtractor.StageExtracted });

        var model = new FakeChatService().RespondsWith(prompt =>
            prompt.Contains("does each quote") ? SupportAnswer(prompt)
            : prompt.Contains("Name one to") ? """{"themes":[{"name":"Summaries keep the context short","description":"Sources keep running summaries."}]}"""
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
        Assert.Equal(4, model.Prompts.Count); // the support check of the one source, then RQ1 only (themes, placement and the second placement; one source has no tensions)
        var unsupported = Assert.Single(file.Unsupported); // the quote is on the page but is not about retrieval
        Assert.Equal(("Retrieval", "We keep a running summary of the conversation.", "The quote is about summaries."), (unsupported.Value, unsupported.Quote, unsupported.Reason));
        Assert.Contains(file.Notes, n => n.Contains("1 extracted value(s) were left out because their quote does not support the value"));
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
