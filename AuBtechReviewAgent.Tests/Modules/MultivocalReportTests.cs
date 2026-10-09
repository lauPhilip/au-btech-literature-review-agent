using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The report of a multivocal run (MLR block G, G14), written in code from the run's files: the methods from the
/// counts on file, the guidelines table, the themes with what they rest on and quotes from distinct sources, and the
/// source repository numbered as the findings are.
/// </summary>
public class MultivocalReportTests
{
    private static GreyRecord Record(string url, string title, string kind) =>
        new("id:" + url, title, "", url, "", "", kind, null, new Dictionary<string, long>());

    private static MultivocalReportInput Input()
    {
        var plan = MultivocalPlan.Example();
        var a = "https://blog.example.org/a";
        var b = "https://papers.example.org/b";
        var c = "javascript:alert(1)"; // a bad address from a source's metadata is never linked
        string A(string url) => MultivocalSearcher.AddressKey(url);
        var findings = new List<SynthesisFinding>
        {
            new() { Id = "F1.1", Question = "RQ1", Attribute = "Practice", Value = "Summarisation", Quote = "We keep a running summary.", Address = A(a), Title = "Post A", Kind = "blogs", Tier = 3, QualityPoints = 11 },
            new() { Id = "F1.2", Question = "RQ1", Attribute = "Practice", Value = "Retrieval", Quote = "We fetch only what is needed.", Address = A(a), Title = "Post A", Kind = "blogs", Tier = 3, QualityPoints = 11 },
            new() { Id = "F2.1", Question = "RQ1", Attribute = "Practice", Value = "Summarisation", Quote = "Summaries cut tokens by half.", Address = A(b), Title = "Report B", Kind = "white-papers", Tier = 1, QualityPoints = 16 },
            new() { Id = "F3.1", Question = "RQ1", Attribute = "Practice", Value = "Other", Quote = "Neural fields.", Address = A(c), Title = "Repo C", Kind = "code", Tier = 3, QualityPoints = 9 },
        };
        return new MultivocalReportInput(
            new PlannedRun(Guid.NewGuid(), plan, new string('a', 64), new DateTime(2026, 10, 1)),
            new MultivocalLedger
            {
                SearchedUtc = new DateTime(2026, 10, 2),
                StoppingApplied = "the top 100 hits of each search string",
                Searches =
                {
                    new GreySearchLog { Source = "github", SearchString = "context engineering", Returned = 30, New = 30 },
                    new GreySearchLog { Source = "zenodo", SearchString = "context engineering", Error = "HTTP 400" },
                },
                Sources = { new() { Record = Record(a, "Post A", "blogs") }, new() { Record = Record(b, "Report B", "white-papers") }, new() { Record = Record(c, "Repo C", "code") } },
                Skipped = { new SkippedSearch("backlinks", "Not built yet.") },
            },
            new MultivocalScreeningFile
            {
                Kappa = 0.85,
                InclusionCriteria = plan.InclusionCriteria,
                Sources =
                {
                    new GreyScreening { Address = A(a), Decision = "Included" },
                    new GreyScreening { Address = A(b), Decision = "Included", Reviewed = true, ModelDecision = "Excluded" },
                    new GreyScreening { Address = A(c), Decision = "Included" },
                    new GreyScreening { Address = "x", Decision = "Excluded", ExclusionReason = "Marketing page" },
                },
            },
            new MultivocalPagesFile { Pages = { new PageSnapshot { Url = a, AccessedUtc = new DateTime(2026, 10, 3), WaybackUrl = "https://web.archive.org/web/20261003000000/" + a } } },
            new MultivocalQualityFile
            {
                Threshold = 10,
                Sources =
                {
                    new GreyQuality { Address = A(a), Points = 11, Outcome = "Passed" },
                    new GreyQuality { Address = A(b), Points = 16, Outcome = "Passed" },
                    new GreyQuality { Address = A(c), Points = 9, Outcome = "Below threshold" },
                },
            },
            new MultivocalMapFile
            {
                SourcesSeen = 2,
                FixedVersion = 1,
                Versions = { new MapVersion { Number = 1, By = "model", Attributes = { new MapAttribute { Name = "Practice", Question = "RQ1", Values = { "Summarisation", "Retrieval", "Other" } } } } },
            },
            new MultivocalExtractionFile
            {
                Sources =
                {
                    new GreyExtraction { Address = A(a), Title = "Post A", Url = a },
                    new GreyExtraction { Address = A(b), Title = "Report B | 2026", Url = b },
                    new GreyExtraction { Address = A(c), Title = "Repo C", Url = c },
                    new GreyExtraction { Address = "failed", Title = "Failed", Url = "https://failed.example.org", Error = "no page" },
                },
            },
            new MultivocalSynthesisFile
            {
                SynthesisedUtc = new DateTime(2026, 10, 4),
                Model = "fake-model",
                Findings = findings,
                Themes =
                {
                    MultivocalSynthesiser.Count(new SynthesisTheme
                    {
                        Question = "RQ1", Name = "Keeping the context short", Description = "Sources keep the context small",
                        Findings = { "F1.1", "F1.2", "F2.1" },
                        Tensions = { new SynthesisTension { FindingA = "F1.1", FindingB = "F2.1", Description = "Summarisation against Retrieval. They disagree." } },
                    }, findings),
                },
                NotCoded = { new NotCodedValue { Question = "RQ1", Attribute = "Practice", Findings = { "F3.1" }, Sources = 1 } },
                Unanswered = { "RQ1.1", "RQ1.2", "RQ2", "RQ3" },
            });
    }

    private static string Text(MultivocalReport report) => report.ToMarkdown();

    [Fact]
    public void TheMethodsAreWrittenFromTheCountsOnFile()
    {
        string md = Text(MultivocalReporter.Write(Input()));

        Assert.StartsWith("# Context engineering for LLM agents: a grey literature review", md); // grey only for now (decision 6)
        Assert.Contains("1 search ran across 1 source", md);
        Assert.Contains("questions and answers on Q&A sites; and code repositories", md); // the failed Zenodo search does not count as run
        Assert.Contains("| Reports and documents on Zenodo | context engineering | failed: HTTP 400 | 0 |", md);
        Assert.Contains("not run (not built yet).", md);
        Assert.Contains("κ = 0.85", md);
        Assert.Contains("The reviewer looked at 1 decision and changed 1", md);
        Assert.Contains("| Marketing page | 1 |", md);
        Assert.Contains("2 sources passed, 1 scored below the threshold and 0 could not be assessed.", md);
        Assert.Contains("| G8 | When to stop | The top 100 hits of each search string. |", md);
        Assert.Contains("version 1 was fixed", md);
        Assert.Contains("3 sources were extracted against it and 1 could not be", md);
        Assert.DoesNotContain("formal literature has not been searched", md); // a grey literature review does not apologise for the formal pool
    }

    [Fact]
    public void EachThemeSaysWhatItRestsOnAndQuotesDistinctSourcesBestFirst()
    {
        var input = Input();
        string md = Text(MultivocalReporter.Write(input));

        Assert.Contains("**Keeping the context short.** Sources keep the context small. This theme rests on 2 sources [S1, S2]: 1 from 1st-tier, 0 from 2nd-tier and 1 from 3rd-tier outlets", md);
        // One quote per source, the 1st-tier white paper first; F1.2 is the second finding of S1, so it is not shown.
        int paper = md.IndexOf("> \"Summaries cut tokens by half.\" (Practice: Summarisation) [S2]", StringComparison.Ordinal);
        int blog = md.IndexOf("> \"We keep a running summary.\" (Practice: Summarisation) [S1]", StringComparison.Ordinal);
        Assert.True(paper > 0 && blog > paper);
        Assert.DoesNotContain("We fetch only what is needed.", md);
        Assert.Contains("- Summarisation against Retrieval. They disagree. [S1, S2]", md);
        Assert.Contains("Not coded by the map: 1 finding on Practice from 1 source [S3]", md);
        Assert.Contains("No extracted source answers this question", md);
    }

    [Fact]
    public void TheGuidelinesTableHasAllFourteenAndTheRepositoryIsNumberedAsTheFindings()
    {
        var input = Input();
        var report = MultivocalReporter.Write(input);
        var guidelines = report.Sections.Single(s => s.Heading.StartsWith("8.")).Blocks.Single(b => b.Kind == "table");
        Assert.Equal(Enumerable.Range(1, 14).Select(i => $"G{i}"), guidelines.Rows.Select(r => r[0]));

        var repository = report.Sections.Single(s => s.Heading == "Source repository").Blocks.Single(b => b.Kind == "table");
        Assert.Equal(new[] { "S1", "S2", "S3" }, repository.Rows.Select(r => r[0])); // the failed extraction has no number
        Assert.Equal(new[] { "S1", "Post A", "Blog posts and practitioner articles", "3rd", "11 of 20", "https://blog.example.org/a", "2026-10-03", "https://web.archive.org/web/20261003000000/https://blog.example.org/a" }, repository.Rows[0]);
        Assert.Equal("", repository.Rows[2][5]); // only http(s) addresses are given
        Assert.Contains("| S2 | Report B \\| 2026 |", report.ToMarkdown()); // a "|" in a title does not break the table
    }

    [Fact]
    public void ARunThatIsNotSynthesisedHasNoReport()
    {
        string root = Path.Join(Path.GetTempPath(), "mlrreport-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RunStore(root);
            var planner = new MultivocalPlanner(store);
            var (runId, _) = planner.CreateAsync(MultivocalPlan.Example()).GetAwaiter().GetResult();
            var searcher = new MultivocalSearcher(store, planner, _ => throw new InvalidOperationException());
            var pages = new MultivocalPages(store, searcher, new PageFetcher(new HttpClientHandler(), _ => Task.CompletedTask));
            var model = new FakeChatService();
            var screener = new MultivocalScreener(store, planner, searcher, () => model);
            var quality = new MultivocalQualityAssessor(store, planner, searcher, screener, pages, () => model);
            var mapper = new MultivocalMapper(store, planner, searcher, quality, () => model);
            var extractor = new MultivocalExtractor(store, planner, searcher, quality, mapper, pages, () => model);
            var synthesiser = new MultivocalSynthesiser(store, planner, searcher, quality, mapper, extractor, pages, () => model);
            var reporter = new MultivocalReporter(store, planner, searcher, screener, pages, quality, mapper, extractor, synthesiser);

            Assert.Null(reporter.Build(runId));
            Assert.Contains(MultivocalReporter.ReportFile, ReviewModules.Find("multivocal")!.ArchiveFiles);
        }
        finally
        {
            TestFolders.TryDelete(root);
        }
    }
}
