using System.Net;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// /health and the database status line. SourceStatus is shared by the whole process, so every test uses its own
/// made-up host name and looks only at that host; nothing here resets the tracker or changes its clock.
/// </summary>
public class OperationsTests
{
    private static string NewHost() => $"db-{Guid.NewGuid():N}.example";

    private static SourceStatus.HostState StateOf(string host) => SourceStatus.Snapshot().Single(h => h.Host == host);

    [Fact]
    public void AFastAnswerIsNotAProblem()
    {
        string host = NewHost();
        SourceStatus.Record(host, HttpStatusCode.OK, TimeSpan.FromSeconds(1));

        var state = StateOf(host);
        Assert.Equal(SourceStatus.Problem.None, state.LastProblem);
        Assert.NotNull(state.LastOkUtc);
        Assert.Equal(0, state.RecentProblems);
        Assert.DoesNotContain(host, SourceStatus.Summary() ?? "");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 1, false, false, 0, SourceStatus.Problem.RateLimited, "is rate-limiting requests")]
    [InlineData(HttpStatusCode.OK, 2, false, false, 2, SourceStatus.Problem.RateLimited, "is rate-limiting requests")]
    [InlineData(HttpStatusCode.BadGateway, 1, false, false, 0, SourceStatus.Problem.ServerError, "had server errors")]
    [InlineData(null, 30, true, false, 0, SourceStatus.Problem.Timeout, "did not answer in time")]
    [InlineData(null, 1, false, true, 0, SourceStatus.Problem.Unreachable, "could not be reached")]
    [InlineData(HttpStatusCode.OK, 18, false, false, 0, SourceStatus.Problem.Slow, "is slow (answered in 18 s)")]
    public void ProblemsAreNamedInPlainWords(HttpStatusCode? status, int seconds, bool timedOut, bool unreachable, int retries,
        SourceStatus.Problem expected, string words)
    {
        string host = NewHost();
        SourceStatus.Record(host, status, TimeSpan.FromSeconds(seconds), timedOut, unreachable, retries);

        Assert.Equal(expected, StateOf(host).LastProblem);
        Assert.Contains($"{host} {words}", SourceStatus.Summary());
    }

    [Fact]
    public void KnownHostsGetTheirEverydayNames()
    {
        Assert.Equal("arXiv", SourceStatus.DisplayName("export.arxiv.org"));
        Assert.Equal("Semantic Scholar", SourceStatus.DisplayName("API.SemanticScholar.org"));
        Assert.Equal("other.example", SourceStatus.DisplayName("other.example"));
    }

    [Fact]
    public void ASourceWithARecentProblemMakesHealthDegraded()
    {
        string bad = NewHost(), good = NewHost();
        SourceStatus.Record(bad, HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2));
        SourceStatus.Record(good, HttpStatusCode.OK, TimeSpan.FromMilliseconds(300));

        var checks = SiteHealth.Sources().ToList();
        var badCheck = checks.Single(c => c.Name == $"source:{bad}");
        var goodCheck = checks.Single(c => c.Name == $"source:{good}");
        Assert.Equal("degraded", badCheck.Status);
        Assert.Contains("RateLimited", badCheck.Detail);
        Assert.Contains("no successful answer yet", badCheck.Detail);
        Assert.Equal("ok", goodCheck.Status);
        Assert.Contains("last answered just now", goodCheck.Detail);
    }

    [Theory]
    [InlineData(2_000L, "ok", "MB free")]
    [InlineData(300L, "degraded", "running low")]
    [InlineData(50L, "unhealthy", "Almost no disk space")]
    public void DiskSpaceDecidesTheStorageStatus(long freeMb, string expected, string words)
    {
        string root = Path.Combine(Path.GetTempPath(), $"health-{Guid.NewGuid():N}");
        try
        {
            var check = SiteHealth.Disk(root, _ => freeMb * 1024 * 1024);
            Assert.Equal(expected, check.Status);
            Assert.Contains(words, check.Detail);
            Assert.Empty(Directory.GetFiles(root)); // the probe file is cleaned up
            Assert.DoesNotContain(root, check.Detail); // no server paths in a public endpoint
        }
        finally { TestFolders.TryDelete(root); }
    }

    [Fact]
    public void UnknownFreeSpaceIsStillHealthyWhenTheFolderIsWritable()
    {
        string root = Path.Combine(Path.GetTempPath(), $"health-{Guid.NewGuid():N}");
        try
        {
            var check = SiteHealth.Disk(root, _ => null);
            Assert.Equal("ok", check.Status);
            Assert.Contains("free space unknown", check.Detail);
        }
        finally { TestFolders.TryDelete(root); }
    }

    [Fact]
    public void AMissingModelKeyIsUnhealthyUnlessALocalModelIsUsed()
    {
        Assert.Equal("unhealthy", SiteHealth.Model(new LlmOptions(), keyConfigured: false).Status);
        Assert.Equal("ok", SiteHealth.Model(new LlmOptions(), keyConfigured: true).Status);
        Assert.Equal("ok", SiteHealth.Model(new LlmOptions { Provider = "OpenAICompatible", BaseUrl = "http://localhost:11434/v1" }, keyConfigured: false).Status);
    }

    [Fact]
    public void AProblemReportCarriesTheRunIdAndVersionButNothingTyped()
    {
        var runId = Guid.Parse("3f2b6c1e-8a44-4d0b-9c1a-5e7f0a1b2c3d");
        string url = SiteSeo.ProblemReportUrl(runId, "1.4.0", "Screening");
        string decoded = Uri.UnescapeDataString(url);

        Assert.StartsWith(SiteSeo.RepositoryUrl + "/issues/new?", url);
        Assert.Contains(runId.ToString(), decoded);
        Assert.Contains("1.4.0", decoded);
        Assert.Contains("Run failed during Screening (3f2b6c1e)", decoded);
        Assert.DoesNotContain(" ", url); // fully escaped
    }
}
