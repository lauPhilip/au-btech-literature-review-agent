using AuBtechReviewAgent.Components.Layout;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The browser tab's title follows a run (#682): the head is rendered once, so the page sends each new title itself.</summary>
public class TabTitleSyncTests
{
    private sealed class Recorder : IJSRuntime
    {
        public List<(string Id, object?[] Args)> Calls { get; } = new();
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
    }

    [Fact]
    public void TheTitleIsTheOneTheHeadWrites()
    {
        Assert.Equal("42% · Your review · TraceableAI", TabTitleSync.Full("42% · Your review"));
        Assert.Equal("✓ Review ready · TraceableAI", TabTitleSync.Full("✓ Review ready"));
        Assert.Equal("TraceableAI · Literature reviews", TabTitleSync.Full("TraceableAI · Literature reviews"));
    }

    [Fact]
    public async Task ATitleIsSentOnlyWhenItChanges()
    {
        var js = new Recorder();
        var sync = new TabTitleSync(js, NullLogger.Instance);
        await sync.UpdateAsync("2% · Your review");
        await sync.UpdateAsync("2% · Your review");
        await sync.UpdateAsync("57% · Your review");
        await sync.UpdateAsync("✓ Review ready");
        Assert.Equal(new[] { "2% · Your review · TraceableAI", "57% · Your review · TraceableAI", "✓ Review ready · TraceableAI" },
            js.Calls.Select(c => (string)c.Args[0]!));
        Assert.All(js.Calls, c => Assert.Equal("traceableReview.setTitle", c.Id));
    }
}
