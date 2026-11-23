using System.Net;
using System.Net.Http.Headers;
using AuBtechReviewAgent;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The recording service waits as long as the model server asks before it tries again (#384).</summary>
public class LlmRetryTests
{
    private static HttpOperationException Error(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var ex = new HttpOperationException(status, null, $"status {(int)status}", null);
        if (retryAfter is { } wait) ex.Data[LlmRetryPolicy.RetryAfterKey] = wait;
        return ex;
    }

    /// <summary>A policy that records its waits instead of sleeping.</summary>
    private static (LlmRetryPolicy Policy, List<TimeSpan> Waits) Recorded()
    {
        var waits = new List<TimeSpan>();
        var policy = new LlmRetryPolicy
        {
            Backoff = attempt => TimeSpan.FromSeconds(attempt),
            Delay = (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
        };
        return (policy, waits);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ARetryAfterIsWaitedOutAndEveryAttemptIsRecorded(HttpStatusCode status)
    {
        var fake = new FakeChatService()
            .Throws(Error(status, TimeSpan.FromSeconds(7)))
            .Throws(Error(status, TimeSpan.FromSeconds(12)))
            .Returns("{\"ok\":true}");
        var (policy, waits) = Recorded();
        var recorder = new RecordingChatCompletionService(fake, "m", retry: policy);

        var result = await recorder.GetChatMessageContentAsync("p");

        Assert.Equal("{\"ok\":true}", result.Content);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(12) }, waits);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal(3, call.Attempts);
        Assert.Equal("ok", call.Outcome);
    }

    [Fact]
    public async Task ARateLimitGetsSixAttemptsAndOtherErrorsFour()
    {
        var limited = new FakeChatService();
        for (int i = 0; i < 6; i++) limited.Throws(Error(HttpStatusCode.TooManyRequests));
        var (policy, waits) = Recorded();
        var recorder = new RecordingChatCompletionService(limited, "m", retry: policy);
        await Assert.ThrowsAsync<HttpOperationException>(() => recorder.GetChatMessageContentAsync("p"));
        Assert.Equal(6, Assert.Single(recorder.Calls).Attempts);
        Assert.Equal(5, waits.Count);

        var busy = new FakeChatService();
        for (int i = 0; i < 4; i++) busy.Throws(Error(HttpStatusCode.BadGateway));
        var (policy2, waits2) = Recorded();
        var recorder2 = new RecordingChatCompletionService(busy, "m", retry: policy2);
        await Assert.ThrowsAsync<HttpOperationException>(() => recorder2.GetChatMessageContentAsync("p"));
        Assert.Equal(4, Assert.Single(recorder2.Calls).Attempts);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, waits2.Select(w => w.TotalSeconds));
    }

    [Fact]
    public void WithoutRetryAfterTheBackoffIsUsedAndEveryWaitIsCapped()
    {
        var (policy, _) = Recorded();
        Assert.Equal(TimeSpan.FromSeconds(2), policy.NextWait(Error(HttpStatusCode.TooManyRequests), 2));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.NextWait(Error(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(10)), 1));
        // Retry-After is only followed on 429 and 503; a 502 keeps the backoff.
        Assert.Equal(TimeSpan.FromSeconds(1), policy.NextWait(Error(HttpStatusCode.BadGateway, TimeSpan.FromSeconds(30)), 1));
        Assert.Null(policy.NextWait(Error(HttpStatusCode.Unauthorized), 1));
    }

    [Fact]
    public void RetryAfterIsReadAsSecondsOrAsAnHttpDate()
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(5), LlmRetryPolicy.Parse(RetryConditionHeaderValue.Parse("5"), now));
        Assert.Equal(TimeSpan.FromSeconds(30), LlmRetryPolicy.Parse(RetryConditionHeaderValue.Parse("Mon, 23 Nov 2026 12:00:30 GMT"), now));
        Assert.Equal(TimeSpan.Zero, LlmRetryPolicy.Parse(RetryConditionHeaderValue.Parse("Mon, 23 Nov 2026 11:59:00 GMT"), now));
        Assert.Null(LlmRetryPolicy.Parse(null, now));
    }

    [Fact]
    public async Task TheModelClientPassesTheServersRetryAfterOn()
    {
        var handler = new StubHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"message\":\"slow down\"}") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
            return response;
        });
        var client = new OpenAICompatibleChatService("http://model.test/v1", "m", http: new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<HttpOperationException>(() => client.GetChatMessageContentsAsync(new ChatHistory("p")));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(9), LlmRetryPolicy.RetryAfterOf(ex));
    }

    private sealed class StubHandler(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer());
    }
}
