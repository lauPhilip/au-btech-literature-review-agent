using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The multivocal review's own figures in its metrics line (issue #415), from the module kit's finished runs: each pool
/// apart, κ with what it is computed over, Table 7 points of the grey sources only and the MMAT appraisals of the
/// academic ones, and the kind of review.
/// </summary>
public class MultivocalMetricsTests
{
    private static async Task<RunMetrics> MetricsOf(string kit)
    {
        var run = await ModuleKit.RunAsync(kit);
        return JsonSerializer.Deserialize<RunMetrics>(run.Archive[MultivocalRunLog.MetricsFile])!;
    }

    [Fact]
    public async Task AGreyOnlyRunHasItsGreyPoolAndNoFormalOne()
    {
        var m = await MetricsOf("multivocal");

        Assert.Equal("multivocal", m.ReviewType);
        Assert.True(m.IsMultivocal);
        Assert.Null(m.FormalPool);
        var grey = Assert.IsType<PoolMetrics>(m.GreyPool);
        Assert.True(grey.Identified > 0);
        Assert.Equal(m.Screened, grey.Screened);
        Assert.True(grey.Table7Scored > 0);
        Assert.Equal(m.MeanQualityPoints, grey.MeanTable7Points);
        Assert.Null(grey.MmatAppraised);
        Assert.Equal(new[] { "grey" }, m.Pools.Select(p => p.Key));
    }

    [Fact]
    public async Task ATwoPoolRunCountsEachPoolApartAsTheRunViewDoes()
    {
        string root = Path.Join(Path.GetTempPath(), "mlr-metrics-" + Guid.NewGuid().ToString("N"));
        try { await TwoPools(root); }
        finally { TestFolders.TryDelete(root); }
    }

    private static async Task TwoPools(string root)
    {
        var run = await FormalKit.RunAsync(root);
        var input = run.Reporter.LoadInput(run.RunId)!;
        var m = MultivocalRunLog.BuildMetrics(input, run.Writer.Load(run.RunId)!, run.Writer.LoadAudit(run.RunId), new List<LlmCallRecord>());

        foreach (var (formal, pool) in new[] { (false, m.GreyPool!), (true, m.FormalPool!) })
        {
            var counts = MultivocalPools.For(formal, input.Ledger, input.Screening, input.Quality, input.Extraction);
            Assert.Equal((counts.Identified, counts.Included, counts.Excluded, counts.Kept, counts.Extracted, counts.AbstractOnly),
                (pool.Identified, pool.Included, pool.Excluded, pool.Kept, pool.Extracted, pool.AbstractOnly));
            Assert.Equal(counts.Kappa, pool.Kappa);
            Assert.Equal(counts.Second!.ScreenedTwice, pool.ScreenedTwice);
            Assert.Equal(counts.Second.Audit, pool.AuditSample);
            Assert.Equal((counts.Second.TargetedTotal, counts.Second.TargetedAgreed), (pool.TargetedSecond, pool.TargetedAgreed));
        }

        // Academic sources are appraised with MMAT, not on Table 7, so their (zero) points are not in the grey mean.
        Assert.True(m.FormalPool!.MmatAppraised > 0);
        Assert.Null(m.FormalPool.Table7Scored);
        var greyScored = input.Quality.Sources.Where(q => !q.Formal && q.Outcome is "Passed" or "Below threshold").ToList();
        Assert.Equal(greyScored.Count, m.GreyPool!.Table7Scored);
        Assert.Equal(m.GreyPool.MeanTable7Points, m.MeanQualityPoints);
        Assert.Equal(new[] { "grey", "formal" }, m.Pools.Select(p => p.Key));
    }

    [Fact]
    public void KappaSaysWhatItIsComputedOver()
    {
        Assert.Equal("audit sample", MultivocalRunLog.KappaBasis(new MultivocalScreeningFile { AuditSeed = "5a3e0001" }));
        Assert.Equal("all sources", MultivocalRunLog.KappaBasis(new MultivocalScreeningFile())); // screened before 23 Nov 2026, every source twice
    }

    [Fact]
    public void AnOlderMultivocalLineStillLoadsWithoutPools()
    {
        var m = JsonSerializer.Deserialize<RunMetrics>("""{"RunId":"r","Settings":{"Module":"multivocal"},"ScreeningKappa":0.7}""")!;
        Assert.Equal("multivocal", m.Type);
        Assert.Null(m.GreyPool);
        Assert.Null(m.ScreeningKappaBasis);
        Assert.Empty(m.Pools);
    }
}
