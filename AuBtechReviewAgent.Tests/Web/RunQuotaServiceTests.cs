using System.Net;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class RunQuotaServiceTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "quota-tests-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    private RunQuotaService Create(QuotaOptions? options = null, bool isDevelopment = false) =>
        new(options ?? new QuotaOptions { FreeRunsPerDay = 3, GlobalFreeRunsPerDay = 50 }, isDevelopment, _dir, () => _now);

    public void Dispose()
    {
        TestFolders.TryDelete(_dir);
    }

    [Fact]
    public void FreeTierAllowsThreeRunsPerDayThenBlocks()
    {
        var quota = Create();

        for (int i = 0; i < 3; i++) Assert.True(quota.TryReserve("1.2.3.4", QuotaTier.Free, out _, out _));
        Assert.False(quota.TryReserve("1.2.3.4", QuotaTier.Free, out var status, out var lease));
        Assert.Null(lease);
        Assert.False(status.Allowed);
        Assert.Equal(0, status.Remaining);
    }

    [Fact]
    public void QuotaIsPerAddress()
    {
        var quota = Create();
        for (int i = 0; i < 3; i++) quota.TryReserve("1.2.3.4", QuotaTier.Free, out _, out _);

        Assert.True(quota.TryReserve("5.6.7.8", QuotaTier.Free, out _, out _));
    }

    [Fact]
    public void QuotaResetsAtMidnightUtc()
    {
        var quota = Create();
        for (int i = 0; i < 3; i++) quota.TryReserve("1.2.3.4", QuotaTier.Free, out _, out _);

        _now = _now.Date.AddDays(1).AddMinutes(1);

        Assert.True(quota.GetStatus("1.2.3.4", QuotaTier.Free).Allowed);
    }

    [Fact]
    public void CountsSurviveARestart()
    {
        var first = Create();
        first.TryReserve("1.2.3.4", QuotaTier.Free, out _, out _);
        first.TryReserve("1.2.3.4", QuotaTier.Free, out _, out _);

        var second = Create();

        Assert.Equal(1, second.GetStatus("1.2.3.4", QuotaTier.Free).Remaining);
    }

    [Fact]
    public void StorageFileDoesNotContainTheAddress()
    {
        var quota = Create();
        quota.TryReserve("203.0.113.77", QuotaTier.Free, out _, out _);

        string file = File.ReadAllText(Path.Join(_dir, "App_Data", "run-quota.json"));

        Assert.DoesNotContain("203.0.113.77", file);
    }

    [Fact]
    public void RefundGivesTheRunBack()
    {
        var quota = Create();
        quota.TryReserve("1.2.3.4", QuotaTier.Free, out _, out var lease);

        quota.Refund(lease!);

        Assert.Equal(3, quota.GetStatus("1.2.3.4", QuotaTier.Free).Remaining);
    }

    [Fact]
    public void GlobalCeilingBlocksNewAddresses()
    {
        var quota = Create(new QuotaOptions { FreeRunsPerDay = 3, GlobalFreeRunsPerDay = 2 });
        quota.TryReserve("1.1.1.1", QuotaTier.Free, out _, out _);
        quota.TryReserve("2.2.2.2", QuotaTier.Free, out _, out _);

        Assert.False(quota.TryReserve("3.3.3.3", QuotaTier.Free, out var status, out _));
        Assert.Contains("shared free runs are used up", status.Message);
    }

    [Fact]
    public void ABlockedFreeTierSaysWhenRunsComeBackAndOffersOwnKey()
    {
        var quota = Create(); // the clock is at 10:00 UTC
        for (int i = 0; i < 3; i++) quota.TryReserve("1.2.3.4", QuotaTier.Free, out _, out _);

        var status = quota.GetStatus("1.2.3.4", QuotaTier.Free);

        Assert.Equal(new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc), status.ResetsUtc);
        Assert.Contains("in 14 h (midnight UTC)", status.Message);
        Assert.Contains("your own Mistral key", status.Message);
    }

    [Fact]
    public void ABlockedOwnKeySaysWhenTheOldestRunLeavesTheHour()
    {
        var quota = Create(new QuotaOptions { FreeRunsPerDay = 3, GlobalFreeRunsPerDay = 50, OwnKeyRunsPerHour = 2 });
        quota.TryReserve("1.2.3.4", QuotaTier.OwnKey, out _, out _);   // 10:00
        _now = _now.AddMinutes(20);
        quota.TryReserve("1.2.3.4", QuotaTier.OwnKey, out _, out _);   // 10:20

        var status = quota.GetStatus("1.2.3.4", QuotaTier.OwnKey);

        Assert.False(status.Allowed);
        Assert.Equal(new DateTime(2026, 9, 25, 11, 0, 0, DateTimeKind.Utc), status.ResetsUtc);
        Assert.Contains("in 40 min", status.Message);
    }

    [Theory]
    [InlineData(0, "in less than a minute")]
    [InlineData(0.2, "in 1 min")]
    [InlineData(59, "in 59 min")]
    [InlineData(60, "in 1 h")]
    [InlineData(312, "in 5 h 12 min")]
    public void ResetTimesAreRoundedUpAndReadable(double minutes, string expected)
    {
        Assert.Equal(expected, RunQuotaService.ResetText(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void OwnKeyRunsDoNotUseTheFreeQuota()
    {
        var quota = Create();
        for (int i = 0; i < 5; i++) Assert.True(quota.TryReserve("1.2.3.4", QuotaTier.OwnKey, out _, out _));

        Assert.Equal(3, quota.GetStatus("1.2.3.4", QuotaTier.Free).Remaining);
    }

    [Fact]
    public void AdminTokenGivesDeveloperTier()
    {
        var quota = Create(new QuotaOptions { AdminToken = "s3cret" });

        Assert.Equal(QuotaTier.Developer, quota.ResolveTier(false, "s3cret"));
        Assert.Equal(QuotaTier.Free, quota.ResolveTier(false, "wrong"));
        Assert.Equal(QuotaTier.OwnKey, quota.ResolveTier(true, null));
    }

    [Fact]
    public void EmptyAdminTokenNeverMatches()
    {
        var quota = Create(new QuotaOptions { AdminToken = "" });

        Assert.Equal(QuotaTier.Free, quota.ResolveTier(false, ""));
    }

    [Fact]
    public void DevelopmentEnvironmentIsUnlimitedByDefault()
    {
        var quota = Create(isDevelopment: true);

        Assert.Equal(QuotaTier.Developer, quota.ResolveTier(false, null));
    }

    [Fact]
    public void FreeTierCapsMaxResults()
    {
        var quota = Create(new QuotaOptions { FreeTierMaxResults = 5 });

        Assert.Equal(5, quota.MaxResultsFor(QuotaTier.Free, 50));
        Assert.Equal(50, quota.MaxResultsFor(QuotaTier.OwnKey, 50));
        Assert.Equal(1, quota.MaxResultsFor(QuotaTier.Free, 0));
    }

    [Theory]
    [InlineData("::ffff:192.0.2.1", "192.0.2.1")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    public void NormalizesAddresses(string input, string expected)
    {
        Assert.Equal(expected, RunQuotaService.NormalizeClientAddress(IPAddress.Parse(input)));
    }
}
