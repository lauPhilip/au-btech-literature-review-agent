using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Keeps every test off the network. Each request an <see cref="HttpClient"/> sends through the real handler is
/// announced on the "HttpHandlerDiagnosticListener" before it leaves; this listener throws for any address that is not
/// on this machine, so the request never goes out and the code under test sees a failed request instead. A fake
/// handler, as the tests use for the sources, never reaches it. Loopback stays open for the tests that start a server.
/// A test run was once killed while a proxy refused calls to arxiv.org: a test had left the full-text fetch real.
/// </summary>
public static class NoNetwork
{
    public const string Message = "The tests make no network calls; use a fake handler or a test hook for ";

    [ModuleInitializer]
    internal static void Start() => DiagnosticListener.AllListeners.Subscribe(new Listeners());

    private sealed class Listeners : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "HttpHandlerDiagnosticListener") listener.Subscribe(new Requests());
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class Requests : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> e)
        {
            if (e.Key is not ("System.Net.Http.HttpRequestOut.Start" or "System.Net.Http.Request")) return;
            var request = e.Value?.GetType().GetProperty("Request")?.GetValue(e.Value) as HttpRequestMessage;
            if (request?.RequestUri is { IsAbsoluteUri: true, IsLoopback: false } address)
                throw new InvalidOperationException(Message + address.GetLeftPart(UriPartial.Path));
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

/// <summary>The guard that keeps the tests off the network (<see cref="NoNetwork"/>).</summary>
public class NoNetworkTests
{
    [Fact]
    public async Task ARequestToAnotherMachineIsStoppedBeforeItLeaves()
    {
        using var client = new HttpClient();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://arxiv.org/pdf/1.pdf"));
        Assert.StartsWith(NoNetwork.Message, error.Message);
        Assert.EndsWith("https://arxiv.org/pdf/1.pdf", error.Message);
    }

    [Fact]
    public async Task AFakeHandlerIsNotStopped()
    {
        using var client = new HttpClient(new Answers());
        var answer = await client.GetStringAsync("https://api.crossref.org/works/10.1000/x");
        Assert.Equal("fake", answer);
    }

    private sealed class Answers : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("fake") });
    }
}
