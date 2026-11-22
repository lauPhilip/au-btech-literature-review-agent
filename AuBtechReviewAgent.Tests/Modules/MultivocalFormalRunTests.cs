using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A whole multivocal review with both pools, through the runner with the fake model (<see cref="FormalKit"/>): each step
/// of block I is checked here on a complete run, and the run stops, with its reason, at the first step not built yet.
/// </summary>
public class MultivocalFormalRunTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrformalrun-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    [Fact]
    public async Task TheSearchCoversBothPoolsAndKeepsItsRawAnswersInTheArchive()
    {
        var run = await FormalKit.RunAsync(_root);

        var ledger = run.Searcher.LoadLedger(run.RunId)!;
        Assert.Equal(new[] { "context engineering AND agents" }, run.Academic.Queries);
        Assert.Equal(2, ledger.FormalPool.Count());
        Assert.Equal(2, ledger.GreyPool.Count());
        Assert.Contains(ledger.Skipped, s => s.Source == "scopus");

        var zip = RunArchive.Build(run.RunId, run.Store.FolderOf(run.RunId), Array.Empty<(string, byte[])>(), ReviewModules.Find("multivocal")!.ArchiveFiles, null, null);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        Assert.Contains(archive.Entries, e => e.FullName == $"{RunArchive.RawResponsesFolder}/formal-openalex-1.json");
        var reread = JsonSerializer.Deserialize<MultivocalLedger>(File.ReadAllText(Path.Join(run.Store.FolderOf(run.RunId), MultivocalSearcher.LedgerFile)))!;
        Assert.Equal("10.1234/agents.1", reread.FormalPool.First().Paper!.Doi);
    }

    /// <summary>The second screening excludes the academic paper, so the two screenings disagree on it.</summary>
    private static string? SecondExcludesThePaper(string prompt) =>
        prompt.Contains("Evaluate the following academic source") && prompt.Contains("SECOND, INDEPENDENT")
            ? """{"decision":"Excluded","reasoning":"Not about practice.","briefSummary":"A paper.","confidence":"high","exclusionReason":"off-topic"}"""
            : null;

    [Fact]
    public async Task BothPoolsAreScreenedTwiceWithTheirAgreementApartAndTogether()
    {
        var run = await FormalKit.RunAsync(_root, SecondExcludesThePaper);

        var file = run.Screener.Load(run.RunId)!;
        Assert.Equal(2, file.Sources.Count(s => s.Formal));
        var paper = file.Sources.First(s => s.Formal);
        Assert.Equal(2, file.Sources.Count(s => !s.Formal));
        Assert.Equal("Included", paper.Decision); // a disagreement keeps the source and flags it, as for grey sources
        Assert.True(paper.Uncertain);
        Assert.Equal(1.0, file.KappaGrey);
        Assert.Equal(0.0, file.KappaFormal);
        Assert.NotEqual(file.KappaGrey, file.Kappa);
        Assert.Equal(MultivocalScreener.FormalPromptVersion, file.FormalPromptVersion);
        Assert.Equal(4, run.Model.Prompts.Count(p => p.StartsWith("Evaluate the following academic source", StringComparison.Ordinal)));
        Assert.Equal(4, run.Model.Prompts.Count(p => p.StartsWith("Evaluate the following grey literature source", StringComparison.Ordinal)));

        // The reviewer's look at the flagged decisions covers the formal pool too, in a run stopped between screening and scoring.
        await RunHeader.WriteAsync(run.Store.FolderOf(run.RunId), run.Store.LoadHeader(run.RunId)! with { Stage = MultivocalScreener.StageScreened });
        var reviewed = await run.Screener.ReviewAsync(run.RunId, run.Key, paper.Address, include: false, "Not about practice.");
        Assert.Equal(("Excluded", "Included"), (reviewed.Decision, reviewed.ModelDecision));
        Assert.True(run.Screener.Load(run.RunId)!.Sources.First(s => s.Formal).Reviewed);
    }

    /// <summary>Every field the formal pool adds to the module's files; a grey literature review writes none of them.</summary>
    public static readonly string[] FormalFields =
    {
        "Formal", "Paper", "FormalRemoved", "KappaGrey", "KappaFormal", "FormalPromptVersion",
        "Mmat", "TextBasis", "FullTextSource", "FormalAppraisalVersion",
    };

    [Fact]
    public async Task AGreyLiteratureReviewsFilesHaveNoneOfTheFormalPoolsFields()
    {
        var kit = await ModuleKit.RunAsync("multivocal");
        var files = Directory.GetFiles(kit.Folder, "multivocal-*.json");
        Assert.True(files.Length >= 7, $"only {files.Length} files");
        foreach (string path in files)
        {
            var names = new List<string>();
            void Walk(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object)
                    foreach (var p in e.EnumerateObject()) { names.Add(p.Name); Walk(p.Value); }
                else if (e.ValueKind == JsonValueKind.Array)
                    foreach (var item in e.EnumerateArray()) Walk(item);
            }
            Walk(JsonDocument.Parse(File.ReadAllText(path)).RootElement);
            Assert.Empty(names.Intersect(FormalFields));
        }
    }

    [Fact]
    public async Task TheRunStopsWithItsReasonAtTheFirstStepNotBuilt()
    {
        var run = await FormalKit.RunAsync(_root);

        Assert.Equal("Proposing the map stopped: " + MultivocalMapper.FormalNotBuiltMessage, run.Runner.StatusOf(run.RunId)!.Error);
        Assert.Equal(MultivocalQualityAssessor.StageAssessed, run.Store.LoadHeader(run.RunId)!.Stage);
    }

    [Fact]
    public async Task AFormalSourceIsKeptOnItsOpenFullTextOrItsAbstractAndTheQuoteCheckUsesThatText()
    {
        var run = await FormalKit.RunAsync(_root);

        var ledger = run.Searcher.LoadLedger(run.RunId)!;
        string full = MultivocalSearcher.AddressKey(ledger.FormalPool.First().Record.Url);
        string onAbstract = MultivocalSearcher.AddressKey(ledger.FormalPool.Last().Record.Url);
        var pages = run.Pages.Load(run.RunId).Pages;
        var fullPage = pages.Single(p => MultivocalSearcher.AddressKey(p.Url) == full);
        var abstractPage = pages.Single(p => MultivocalSearcher.AddressKey(p.Url) == onAbstract);
        Assert.Equal((MultivocalPages.FullTextBasis, "open-access link from the source", "https://example.org/w1.pdf"), (fullPage.TextBasis, fullPage.FullTextSource, fullPage.FinalUrl));
        Assert.Equal((MultivocalPages.AbstractBasis, "none (no open-access copy known)"), (abstractPage.TextBasis, abstractPage.FullTextSource));
        Assert.Equal(FormalKit.SecondAbstract, run.Pages.ReadText(run.RunId, abstractPage));
        Assert.Equal("", abstractPage.WaybackUrl); // no web page was read, so there is no capture to point to

        Assert.Equal("found", run.Pages.CheckQuote(run.RunId, full, FormalKit.FullTextOnly));
        Assert.Equal("not found", run.Pages.CheckQuote(run.RunId, onAbstract, FormalKit.FullTextOnly));
        Assert.Equal("found", run.Pages.CheckQuote(run.RunId, onAbstract, FormalKit.SecondAbstract));
    }

    [Fact]
    public async Task FormalSourcesAreAppraisedWithMmatAndTheThresholdAppliesToGreySourcesOnly()
    {
        var run = await FormalKit.RunAsync(_root);

        var quality = run.Quality.Load(run.RunId)!;
        Assert.Equal(MultivocalQualityAssessor.FormalAppraisalVersion, quality.FormalAppraisalVersion);
        var formal = quality.Sources.Where(q => q.Formal).ToList();
        Assert.Equal(2, formal.Count);
        Assert.All(formal, q => Assert.Equal(("Passed", 0.0), (q.Outcome, q.Points))); // kept whatever the appraisal; no Table 7 points
        Assert.All(formal, q => Assert.Empty(q.Answers));

        var withFullText = formal[0].Mmat!;
        Assert.Equal(("full text", "qualitative"), (withFullText.EvidenceBasis, withFullText.AppraisalCategory));
        Assert.Equal(new[] { "yes", "yes", "cant_tell", "cant_tell", "cant_tell" }, withFullText.Appraisal.Select(a => a.Answer));
        Assert.All(withFullText.Appraisal.Where(a => a.Answer == "yes"), a => Assert.True(a.QuoteVerified));
        Assert.Equal(MultivocalPages.FullTextBasis, formal[0].TextBasis);

        var onAbstract = formal[1].Mmat!;
        Assert.Equal(("abstract only", "not_empirical"), (onAbstract.EvidenceBasis, onAbstract.AppraisalCategory));
        Assert.True(onAbstract.KeyFindings.Single().QuoteVerified);
        Assert.Equal(MultivocalPages.AbstractBasis, formal[1].TextBasis);

        Assert.DoesNotContain(run.Model.Prompts, p => p.Contains("You assess the quality of one grey") && p.Contains("agent frameworks"));
        Assert.Equal(2, run.Model.Prompts.Count(p => p.Contains("Mixed Methods Appraisal Tool")));
    }
}
