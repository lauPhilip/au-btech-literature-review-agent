using System.Net;
using System.Runtime.CompilerServices;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The retraction check, read against Crossref answers recorded from api.crossref.org (Fixtures/): the notices that
/// update a DOI (filter=updates:{DOI}) for Wakefield et al. (1998) and Lombardi et al. (2009), and the record of a JMIR
/// preprint that names its published version. No test touches the network.
/// </summary>
public class RetractionCheckTests : IDisposable
{
    private const string Wakefield = "10.1016/s0140-6736(97)11096-0";
    private const string Mikovits = "10.1126/science.1179052";
    private readonly string _run = Path.Join(Path.GetTempPath(), "retraction-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _asked = new();

    public void Dispose() => TestFolders.TryDelete(_run);

    internal static string Fixture(string name, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Join(Path.GetDirectoryName(here)!, "Fixtures", name));

    internal static string Work(string doi, string title) =>
        $$$"""{"status":"ok","message":{"DOI":"{{{doi}}}","type":"journal-article","title":["{{{title}}}"],"author":[{"given":"A","family":"Author"}]}}""";

    private Func<string, Task<string>> Answers(Dictionary<string, string> byUrl) => url =>
    {
        _asked.Add(url);
        foreach (var (key, body) in byUrl) if (url.Contains(key, StringComparison.Ordinal)) return Task.FromResult(body);
        throw new HttpRequestException("not found", null, HttpStatusCode.NotFound);
    };

    private static ScreeningLog Log(string id, string title, string? doi) => new(id, title, "Included", "fits", "REF", "", Doi: doi);

    [Fact]
    public void TheRecordedWakefieldAnswerHoldsACorrectionAndARetraction()
    {
        var notices = RetractionCheck.NoticesFor(Fixture("crossref-updates-wakefield-1998.json"), "10.1016/S0140-6736(97)11096-0");

        Assert.Equal(new[] { "correction", "retraction" }, notices.Select(n => n.Type));
        Assert.All(notices, n => Assert.Equal("retraction-watch", n.Source));
        Assert.Equal("10.1016/s0140-6736(10)60175-4", notices[1].NoticeDoi);
        Assert.Equal("2010-02-06", notices[1].Date);
        Assert.Equal(RetractionStatus.Retracted, RetractionCheck.StatusOf(notices));
        Assert.Equal(RetractionStatus.Corrected, RetractionCheck.StatusOf(notices.Take(1).ToList()));
    }

    [Fact]
    public void AnExpressionOfConcernRanksBelowARetractionAndAboveACorrection()
    {
        var notices = RetractionCheck.NoticesFor(Fixture("crossref-updates-mikovits-2009.json"), Mikovits);

        Assert.Equal(new[] { "retraction", "expression_of_concern" }, notices.Select(n => n.Type));
        Assert.Equal(RetractionStatus.Concern, RetractionCheck.StatusOf(notices.Skip(1).ToList()));
        Assert.Equal(RetractionStatus.Retracted, RetractionCheck.StatusOf(notices));
        // A notice of another DOI in the same answer is not this study's.
        Assert.Empty(RetractionCheck.NoticesFor(Fixture("crossref-updates-mikovits-2009.json"), "10.1126/science.0000000"));
    }

    [Fact]
    public void APreprintNamesItsPublishedVersion()
    {
        Assert.Equal("10.2196/76934", RetractionCheck.PublishedVersionOf(Fixture("crossref-work-jmir-preprint.json")));
        Assert.Null(RetractionCheck.PublishedVersionOf(Work("10.1/x", "A journal article")));
    }

    [Fact]
    public async Task EachDoiIsAskedForItsRecordAndItsNoticesAndTheRecordIsSharedWithTheEnrichment()
    {
        var get = Answers(new()
        {
            ["works?filter=updates:10.1016%2Fs0140-6736%2897%2911096-0"] = Fixture("crossref-updates-wakefield-1998.json"),
            ["works/10.1016/s0140-6736"] = Work(Wakefield, "Ileal-lymphoid-nodular hyperplasia, non-specific colitis, and pervasive developmental disorder in children"),
        });
        var study = Log("S2_1", "Ileal-lymphoid-nodular hyperplasia, non-specific colitis, and pervasive developmental disorder in children", "https://doi.org/" + Wakefield);

        var file = await RetractionCheck.CheckAsync(new[] { study, Log("ARXIV_1", "A preprint", "10.48550/arXiv.2401.00001"), Log("X", "No DOI", null) },
            _run, get, url => url + (url.Contains('?') ? "&" : "?") + "mailto=team%40example.org");

        Assert.Equal(3, file.IncludedAtCheck);
        var entry = Assert.Single(file.Studies); // the arXiv DOI and the study without a DOI are not at Crossref
        Assert.Equal(RetractionStatus.Retracted, entry.Status);
        Assert.Equal(2, entry.Lookups.Count);
        Assert.All(entry.Lookups, l => Assert.DoesNotContain("mailto", l.Url));
        Assert.All(entry.Lookups, l => Assert.True(File.Exists(Path.Join(_run, l.RawFile!))));
        // The enrichment finds the record under the same name and does not ask again.
        var (_, audit) = await new ReferenceEnricher(get).EnrichAsync(new[] { study }, _run);
        Assert.Equal("saved answer reused", audit.References[0].Lookups[0].Status);
        Assert.Single(_asked, u => u.Contains("api.crossref.org/works/"));
    }

    [Fact]
    public async Task ADoiWhoseRecordNamesAnotherWorkExcludesNothing()
    {
        var get = Answers(new()
        {
            ["works?filter=updates:"] = Fixture("crossref-updates-wakefield-1998.json"),
            ["works/10.1016/"] = Work(Wakefield, "A different paper altogether"),
        });

        var file = await RetractionCheck.CheckAsync(new[] { Log("S2_1", "Agent loops for safe orchestration", Wakefield) }, _run, get, u => u);

        var entry = Assert.Single(file.Studies);
        Assert.Equal(RetractionStatus.NotChecked, entry.Status);
        Assert.Contains("names another title", entry.NotCheckedReason);
        Assert.Empty(entry.Notices);
        Assert.DoesNotContain(_asked, u => u.Contains("filter=updates"));
    }

    [Fact]
    public async Task AFailedLookupLeavesTheStudyUncheckedAndSaysWhy()
    {
        Func<string, Task<string>> get = url => url.Contains("filter=updates")
            ? throw new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable)
            : Task.FromResult(Work("10.1234/x", "Agent loops"));

        var file = await RetractionCheck.CheckAsync(new[] { Log("S", "Agent loops", "10.1234/x") }, _run, get, u => u);

        Assert.Equal(RetractionStatus.NotChecked, file.Studies[0].Status);
        Assert.StartsWith("the notices could not be fetched (failed:", file.Studies[0].NotCheckedReason);
    }
}
