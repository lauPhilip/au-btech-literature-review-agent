using System.Net;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>A review started through the API joins the same line as the form, and a full line answers 503 with Retry-After.</summary>
[Collection(ApiCollection.Name)]
public class ReviewApiQueueTests
{
    [Fact]
    public async Task A_full_line_refuses_the_start_with_503_and_Retry_After_and_nothing_starts()
    {
        using var host = new ApiTestHost();
        var line = host.Engine.Coordinator;
        for (int i = 0; i < line.MaxConcurrent + 100; i++) Assert.True(line.TryAdmit(Guid.NewGuid(), $"198.51.100.{i % 250}").Admitted);

        var response = await ReviewApiTests.PostAsync(host.CreateClient(), ReviewApiTests.GoodBody());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var problem = await ReviewApiTests.ProblemOf(response);
        Assert.Equal("The tool is busy", problem.GetProperty("title").GetString());
        Assert.StartsWith("The tool is busy: 100 reviews are waiting already.", problem.GetProperty("detail").GetString());
        Assert.Equal(100, line.Queued);
        Assert.Empty(host.KeysSeenByModel);
    }

    [Fact]
    public async Task One_address_may_keep_only_its_share_of_reviews_in_the_line()
    {
        using var host = new ApiTestHost();
        var options = new RunsOptions();
        // The test server may give no address at all, or a loopback one; each such address has its share taken first.
        foreach (var address in new[] { null, IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            string me = RunQuotaService.NormalizeClientAddress(address);
            for (int i = 0; i < options.MaxRunningPerClient + options.MaxQueuedPerClient; i++) host.Engine.Coordinator.TryAdmit(Guid.NewGuid(), me);
        }

        var response = await ReviewApiTests.PostAsync(host.CreateClient(), ReviewApiTests.GoodBody());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var problem = await ReviewApiTests.ProblemOf(response);
        Assert.Equal("Too many reviews waiting", problem.GetProperty("title").GetString());
        Assert.Empty(host.KeysSeenByModel);
    }
}
