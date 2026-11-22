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

    /// <summary>A sentence that is only in the first paper's open full text, not in its abstract.</summary>
    public const string FullTextOnly = "In our evaluation with twelve teams, summaries halved the prompt size.";

    /// <summary>The abstract of the second paper, which has no open full text.</summary>
    public const string SecondAbstract = "Agents page old turns out to a vector store when the window fills.";

    /// <summary>
    /// The fake database: a paper with a DOI and an open full text, a duplicate of it by title, one without any
    /// address, and a paper with only its abstract.
    /// </summary>
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
                new("OPENALEX_W4", "Memory hierarchies in agent frameworks", SecondAbstract, "2024-11-05", new() { "Ola Nordmann" }, "Proceedings of AgentConf", null, "https://example.org/papers/w4"),
            });
        }
    }

    /// <summary>The fake open-access search: the first paper has a full text, every other paper has none.</summary>
    public static Task<FullTextResult> FullText(AcademicPaper paper, string folder) => Task.FromResult(paper.Id == "OPENALEX_W1"
        ? new FullTextResult(new[] { new DocumentChunk(paper.Id, 1, "We keep a running summary of the conversation."), new DocumentChunk(paper.Id, 2, FullTextOnly) },
            "open-access link from the source", "https://example.org/w1.pdf")
        : new FullTextResult(Array.Empty<DocumentChunk>(), "none (no open-access copy known)", null));

    /// <summary>The fake model's extraction and MMAT appraisal of a formal source, every quote from the text it was given.</summary>
    public static string? Appraisal(string prompt) =>
        !prompt.Contains("You are extracting data from one study") ? null
        : prompt.Contains(FullTextOnly)
            ? $$"""{"studyType":"qualitative evaluation","method":{"value":"evaluation with teams","quote":"{{FullTextOnly}}"},"sample":{"value":"twelve teams","quote":"{{FullTextOnly}}"},"keyFindings":[{"value":"Summaries halved the prompt size.","quote":"{{FullTextOnly}}"}],"limitations":{"value":"not reported","quote":""},"appraisalCategory":"qualitative","appraisal":[{"id":"1.1","answer":"yes","quote":"We keep a running summary of the conversation."},{"id":"1.2","answer":"yes","quote":"{{FullTextOnly}}"},{"id":"1.3","answer":"cant_tell","quote":""},{"id":"1.4","answer":"cant_tell","quote":""},{"id":"1.5","answer":"cant_tell","quote":""}]}"""
            : $$"""{"studyType":"position paper","method":{"value":"not reported","quote":""},"sample":{"value":"not reported","quote":""},"keyFindings":[{"value":"Old turns go to a vector store.","quote":"{{SecondAbstract}}"}],"limitations":{"value":"not reported","quote":""},"appraisalCategory":"not_empirical","appraisal":[]}""";

    public sealed record Run(RunStore Store, Guid RunId, string Key, MultivocalRunner Runner, MultivocalSearcher Searcher, MultivocalScreener Screener,
        MultivocalWriter Writer, MultivocalReporter Reporter, FakeChatService Model, Database Academic, MultivocalPages Pages, MultivocalQualityAssessor Quality);

    /// <summary>Plans a multivocal review with one grey search and one academic database, and runs it as far as it goes.</summary>
    public static async Task<Run> RunAsync(string root, Func<string, string?>? respond = null)
    {
        var store = new RunStore(root);
        var model = new FakeChatService().RespondsWith(p => respond?.Invoke(p) ?? Appraisal(p) ?? ModuleKit.RespondMultivocal(p));
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
        var pages = new MultivocalPages(store, searcher, new PageFetcher(new ModuleKit.KitWeb(), _ => Task.CompletedTask), FullText);
        var screener = new MultivocalScreener(store, planner, searcher, Chat, parallelism: 1);
        var assessor = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, Chat, parallelism: 1);
        var mapper = new MultivocalMapper(store, planner, searcher, assessor, Chat);
        var extractor = new MultivocalExtractor(store, planner, searcher, assessor, mapper, pages, Chat, parallelism: 1);
        var synthesiser = new MultivocalSynthesiser(store, planner, searcher, assessor, mapper, extractor, pages, Chat);
        var reporter = new MultivocalReporter(store, planner, searcher, screener, pages, assessor, mapper, extractor, synthesiser);
        var writer = new MultivocalWriter(store, reporter, pages, Chat, "fake-model", parallelism: 1);
        var runner = new MultivocalRunner(store, searcher, screener, assessor, mapper, extractor, synthesiser, pages, writer, () => model, "fake-model");
        await runner.RunAsync(runId, key);
        return new Run(store, runId, key, runner, searcher, screener, writer, reporter, model, academic, pages, assessor);
    }
}
