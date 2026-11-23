using System.Text;
using System.Text.Json;
using AuBtechReviewAgent;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A whole grey literature review of 30 long pages, run through the runner with the fake model of the module kit, to
/// measure what each stage sends to the model (the PromptChars of llm-calls.json, summed by stage).
/// </summary>
public static class LongGreyRun
{
    public const int Pages = 30;

    /// <summary>The length of page <paramref name="number"/>: from 10,000 to about 68,000 characters, so some pages are longer than the old 40,000 cut.</summary>
    public static int LengthOf(int number) => 10_000 + (number - 1) * 2_000;

    private sealed class Web : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            bool page = path.StartsWith("/p", StringComparison.Ordinal) && int.TryParse(path[2..], out int n);
            var response = page
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(LongGreyPages.Html(int.Parse(path[2..]), LengthOf(int.Parse(path[2..]))), Encoding.UTF8, "text/html") }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("") };
            return Task.FromResult(response);
        }
    }

    private sealed class Source : IGreySource
    {
        public string Key => "stackexchange";
        public string Name => "Fake";
        public IReadOnlyList<string> LastRawResponses => new[] { "{}" };
        public Task<List<GreyRecord>> SearchAsync(string query, int maxResults) => Task.FromResult(Enumerable.Range(1, Pages)
            .Select(i => new GreyRecord($"x:{i}", $"Managing the context of an agent, part {i}", "A post on agent context.", $"https://blog.example.org/p{i}",
                "Ana", "blog.example.org", "blogs", new DateTime(2025, 5, 1), new Dictionary<string, long>()))
            .ToList());
    }

    /// <summary>Runs the review and returns the prompt characters sent, by stage, from its llm-calls.json, with the run's files and prompts.</summary>
    public static async Task<(SortedDictionary<string, long> ByStage, MultivocalReportInput Input, IReadOnlyList<string> Prompts, MultivocalPages Pages, Guid RunId)> RunAsync(string root)
    {
        var store = new RunStore(root);
        var model = new FakeChatService().RespondsWith(ModuleKit.RespondMultivocal);
        Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService Chat() => LlmRecording.Current ?? (Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService)model;
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example("grey");
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        var (runId, key) = await planner.CreateAsync(plan);

        var searcher = new MultivocalSearcher(store, planner, _ => new Source());
        var pages = new MultivocalPages(store, searcher, new PageFetcher(new Web(), _ => Task.CompletedTask));
        var screener = new MultivocalScreener(store, planner, searcher, Chat, parallelism: 1);
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, Chat, parallelism: 1);
        var mapper = new MultivocalMapper(store, planner, searcher, assessor, Chat);
        var extractor = new MultivocalExtractor(store, planner, searcher, assessor, mapper, pages, Chat, parallelism: 1);
        var synthesiser = new MultivocalSynthesiser(store, planner, searcher, assessor, mapper, extractor, pages, Chat);
        var reporter = new MultivocalReporter(store, planner, searcher, screener, pages, assessor, mapper, extractor, synthesiser);
        var writer = new MultivocalWriter(store, reporter, pages, Chat, "fake-model", parallelism: 1);
        var runner = new MultivocalRunner(store, searcher, screener, assessor, mapper, extractor, synthesiser, pages, writer, () => model, "fake-model");
        await runner.RunAsync(runId, key);
        if (runner.StatusOf(runId)?.Error is string error) throw new InvalidOperationException("The long grey run stopped: " + error);
        List<string> prompts;
        lock (model.Prompts) prompts = model.Prompts.ToList();
        return (ByStage(File.ReadAllText(Path.Join(store.FolderOf(runId), MultivocalRunLog.CallsFile))), reporter.LoadInput(runId)!, prompts, pages, runId);
    }

    /// <summary>The prompt characters of a call log, summed by stage.</summary>
    public static SortedDictionary<string, long> ByStage(string callsJson)
    {
        var calls = JsonDocument.Parse(callsJson).RootElement.GetProperty("Calls").EnumerateArray();
        var sums = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var c in calls)
        {
            string stage = c.GetProperty("Stage").GetString()!;
            sums[stage] = sums.GetValueOrDefault(stage) + c.GetProperty("PromptChars").GetInt64();
        }
        return sums;
    }
}
