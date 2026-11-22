using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The API tests start the whole app, and the app sets the shared AppLog.Factory, so they run on their own and
/// not next to other tests (whose log lines would land in the capture, and who would log into a disposed host).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiCollection
{
    public const string Name = "api";
}

/// <summary>
/// The real app in memory, with the engine swapped for one that uses the scripted fake model and fake sources in
/// a temporary folder. Every key the model is created with is kept, and every log line is captured.
/// </summary>
internal sealed class ApiTestHost : WebApplicationFactory<Program>
{
    public const string ServerKey = "server-key-that-the-api-must-never-use";

    private readonly bool? _enabled;
    private readonly string _environment;

    public string Root { get; } = Path.Join(Path.GetTempPath(), "api-" + Guid.NewGuid().ToString("N"));
    public ConcurrentQueue<string> KeysSeenByModel { get; } = new();
    public LogCapture Logs { get; } = new();
    public PrismaReviewEngine Engine { get; }

    public ApiTestHost(bool? enabled = true, string environment = "Development")
    {
        _enabled = enabled;
        _environment = environment;
        Engine = new PrismaReviewEngine(ServerKey, "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = Root,
            ChatFactory = key =>
            {
                KeysSeenByModel.Enqueue(key);
                return new FakeChatService().RespondsWith(PipelineTests.Respond);
            },
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("MISTRAL_API_KEY", ServerKey);
        if (_enabled != null) builder.UseSetting("Api:Enabled", _enabled.Value ? "true" : "false");
        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(Logs);
            logging.AddFilter<LogCapture>(null, LogLevel.Debug);
        });
        builder.ConfigureTestServices(services => services.AddSingleton(Engine));
    }

    /// <summary>Waits until the run has finished and left the coordinator, so every file it writes is on disk.</summary>
    public async Task WaitForRunAsync(Guid runId, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Engine.IsRunActive(runId) && DateTime.UtcNow < until) await Task.Delay(200);
    }

    protected override void Dispose(bool disposing)
    {
        AppLog.Factory = NullLoggerFactory.Instance;
        base.Dispose(disposing);
        TestFolders.TryDelete(Root);
    }
}

/// <summary>Keeps every log line with its values, its exception and its scopes, so a test can search them.</summary>
internal sealed class LogCapture : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? _scopes;
    public ConcurrentQueue<string> Lines { get; } = new();
    public string All => string.Join("\n", Lines);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose() { }

    private sealed class Logger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes?.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = new StringBuilder($"{logLevel} {category}: {formatter(state, exception)} {exception}");
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                foreach (var value in values) line.Append(' ').Append(value.Key).Append('=').Append(value.Value);
            owner._scopes?.ForEachScope((scope, text) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    foreach (var pair in pairs) text.Append(" scope:").Append(pair.Key).Append('=').Append(pair.Value);
                else text.Append(" scope=").Append(scope);
            }, line);
            owner.Lines.Enqueue(line.ToString());
        }
    }
}
