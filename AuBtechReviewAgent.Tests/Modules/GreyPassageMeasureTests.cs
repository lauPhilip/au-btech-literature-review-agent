using AuBtechReviewAgent;
using Xunit;
using Xunit.Abstractions;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A grey literature review of 30 long pages, through the runner (<see cref="LongGreyRun"/>): the quality check and the
/// extraction get selected passages, every quote is still found in the kept page, what was sent is on file and the
/// method says so, and the prompts are far smaller. The table of prompt characters by stage is written to the test
/// output, and to the file PROMPT_CHARS_OUT names when it is set.
/// </summary>
public class GreyPassageMeasureTests
{
    private readonly ITestOutputHelper _out;
    public GreyPassageMeasureTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task LongPagesAreSentAsPassagesWithEveryQuoteFoundAndWhatWasSentOnFile()
    {
        var kit = LongGreyRun.ByStage(ModuleKit.Text((await ModuleKit.RunAsync("multivocal")).Archive[MultivocalRunLog.CallsFile]));
        string root = Path.Join(Path.GetTempPath(), "longgrey-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (byStage, input, prompts, pages, runId) = await LongGreyRun.RunAsync(root);
            string table = "stage | kit | 30 long pages\n" + string.Join("\n", kit.Keys.Union(byStage.Keys).OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => $"{k} | {kit.GetValueOrDefault(k)} | {byStage.GetValueOrDefault(k)}")) + $"\ntotal | {kit.Values.Sum()} | {byStage.Values.Sum()}";
            _out.WriteLine(table);
            if (Environment.GetEnvironmentVariable("PROMPT_CHARS_OUT") is { Length: > 0 } path) File.WriteAllText(path, table);

            // The first page (10,000 characters) is sent whole to the quality check; every other page is sent as passages.
            var grey = input.Quality.Sources.Where(q => !q.Formal).ToList();
            Assert.Equal(LongGreyRun.Pages, grey.Count);
            Assert.Null(grey.Single(q => q.Url.EndsWith("/p1", StringComparison.Ordinal)).Passages);
            Assert.All(grey.Where(q => !q.Url.EndsWith("/p1", StringComparison.Ordinal)), q => Assert.Matches(@"^\d+ of \d+ passages of the page \([\d,]+ of [\d,]+ characters\)$", q.Passages));
            Assert.All(input.Extraction.Sources, s => Assert.StartsWith("14 of ", s.Passages));
            Assert.All(grey.SelectMany(q => q.Answers).Where(a => a.Quote != null && a.Basis == "model"), a => Assert.Equal("found", pages.CheckQuote(runId, grey[0].Address, a.Quote!)));
            Assert.All(input.Extraction.Sources, s => Assert.All(s.Attributes.SelectMany(a => a.Values), v => Assert.Equal("found", pages.CheckQuote(runId, s.Address, v.Quote))));

            string quality = prompts.First(p => p.StartsWith("You assess the quality of one grey", StringComparison.Ordinal) && p.Contains("/p30", StringComparison.Ordinal));
            Assert.Contains("selected passages of a long page: its start, its end", quality);
            Assert.Contains(PassageSelection.Break, quality);
            Assert.True(quality.Length < 18_000, $"{quality.Length} characters in a quality prompt");
            string extraction = prompts.First(p => p.StartsWith("You extract data from one grey", StringComparison.Ordinal));
            Assert.Contains("selected passages of a long page: its start and the passages most relevant to the map", extraction);
            Assert.True(extraction.Length < 12_000, $"{extraction.Length} characters in an extraction prompt");

            string method = string.Join(" ", MultivocalPaperParts.Methods(input).Select(m => m.Text));
            Assert.Contains("For 29 long pages, the model was given the start and the end of the page and the passages that share the most words with each checklist item", method);
            Assert.Contains("For 30 long pages, the model was given the first passages and the passages that share the most words with the map and the questions", method);
            Assert.Equal("grey-quality-v4", MultivocalQualityAssessor.PromptVersion);
            Assert.Equal("grey-extraction-v3", MultivocalExtractor.PromptVersion);

            // Before #391 the quality check and the extraction sent 1,119,627 and 1,047,357 characters on this run.
            Assert.True(byStage["quality"] < 600_000, $"quality {byStage["quality"]}");
            Assert.True(byStage["extraction"] < 400_000, $"extraction {byStage["extraction"]}");
        }
        finally { TestFolders.TryDelete(root); }
    }

    [Fact]
    public async Task TheKitsShortPageIsSentWholeAndNothingIsSaidAboutPassages()
    {
        var run = await ModuleKit.RunAsync("multivocal");
        string quality = run.Prompts.First(p => p.StartsWith("You assess the quality of one grey", StringComparison.Ordinal));
        Assert.Contains("PAGE TEXT (the only evidence you may use):", quality);
        Assert.DoesNotContain(PassageSelection.Break, quality);
        Assert.DoesNotContain("long page", ModuleKit.Text(run.Archive[MultivocalPaper.MainTexFile]));
    }
}
