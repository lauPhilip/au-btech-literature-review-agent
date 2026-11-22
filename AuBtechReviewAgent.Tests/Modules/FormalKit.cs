using AuBtechReviewAgent;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A whole multivocal review with both pools, run through the runner as the server runs it, with the module kit's fake
/// web, grey source and model, and a fake academic database (block I of docs/roadmap/mlr-todo.md). Each step of the
/// formal pool is checked on this run, not only by unit tests.
/// </summary>
public static class FormalKit
{
    /// <summary>The text of the formal paper's abstract; the quotes of the fake model are taken from it.</summary>
    public const string Abstract = "We keep a running summary of the conversation. Agents that summarise their history stay within the context window.";

    /// <summary>The fake database: one paper with a DOI and an abstract, one duplicate of it by title, one without any address.</summary>
    public sealed class Database : IAcademicSource
    {
        public string SourceName => "Fake database";
        public List<string> Queries { get; } = new();
        public IReadOnlyList<string> LastRawResponses => new[] { "{\"results\":3}" };

        public Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5)
        {
            lock (Queries) Queries.Add(query);
            return Task.FromResult(new List<AcademicPaper>
            {
                new("OPENALEX_W1", "Context summaries for LLM agents", Abstract, "2025-03-01", new() { "Jane Doe", "Kim Lee" }, "Journal of Agents", "10.1234/agents.1"),
                new("OPENALEX_W2", "Context Summaries for LLM Agents.", Abstract, "2025", new() { "Jane Doe" }, "Journal of Agents"),
                new("OPENALEX_W3", "An index page", "Front matter.", "2024", new(), ""),
            });
        }
    }

    public sealed record Run(RunStore Store, Guid RunId, string Key, MultivocalRunner Runner, MultivocalSearcher Searcher, MultivocalScreener Screener,
        MultivocalWriter Writer, MultivocalReporter Reporter, FakeChatService Model, Database Academic);

    /// <summary>Plans a multivocal review with one grey search and one academic database, and runs it as far as it goes.</summary>
    public static async Task<Run> RunAsync(string root, Func<string, string?>? respond = null)
    {
        var store = new RunStore(root);
        var model = new FakeChatService().RespondsWith(p => respond?.Invoke(p) ?? ModuleKit.RespondMultivocal(p));
        IChatCompletionService Chat() => LlmRecording.Current ?? (IChatCompletionService)model;
        var planner = new MultivocalPlanner(store);
        var plan = MultivocalPlan.Example();
        plan.GreySearches = new List<string> { "stackexchange" };
        plan.GreySearchStrings = new List<string> { "context engineering" };
        plan.FormalDatabases = new List<string> { "openalex", "scopus" };
        plan.FormalSearchStrings = new List<string> { "context engineering AND agents" };
        var (runId, key) = await planner.CreateAsync(plan);

        var academic = new Database();
        var searcher = new MultivocalSearcher(store, planner, _ => new ModuleKit.KitGreySource(), k => k == "openalex" ? academic : null);
        var pages = new MultivocalPages(store, searcher, new PageFetcher(new ModuleKit.KitWeb(), _ => Task.CompletedTask));
        var screener = new MultivocalScreener(store, planner, searcher, Chat, parallelism: 1);
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, Chat, parallelism: 1);
        var mapper = new MultivocalMapper(store, planner, searcher, assessor, Chat);
        var extractor = new MultivocalExtractor(store, planner, searcher, assessor, mapper, pages, Chat, parallelism: 1);
        var synthesiser = new MultivocalSynthesiser(store, planner, searcher, assessor, mapper, extractor, pages, Chat);
        var reporter = new MultivocalReporter(store, planner, searcher, screener, pages, assessor, mapper, extractor, synthesiser);
        var writer = new MultivocalWriter(store, reporter, pages, Chat, "fake-model", parallelism: 1);
        var runner = new MultivocalRunner(store, searcher, screener, assessor, mapper, extractor, synthesiser, pages, writer, () => model, "fake-model");
        await runner.RunAsync(runId, key);
        return new Run(store, runId, key, runner, searcher, screener, writer, reporter, model, academic);
    }
}
