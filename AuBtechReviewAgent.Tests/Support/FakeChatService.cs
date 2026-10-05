using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent.Tests;

/// <summary>A scripted stand-in for the Mistral chat service: each call returns (or throws) the next scripted item.</summary>
public class FakeChatService : IChatCompletionService
{
    private readonly Queue<Func<ChatHistory, string>> _script = new();
    public List<string> Prompts { get; } = new();

    public FakeChatService Returns(string response) { _script.Enqueue(_ => response); return this; }
    public FakeChatService Returns(Func<ChatHistory, string> responder) { _script.Enqueue(responder); return this; }
    public FakeChatService Throws(Exception ex) { _script.Enqueue(_ => throw ex); return this; }

    private Func<string, string>? _responder;
    /// <summary>Answer every call by looking at the prompt (used when the call order does not matter).</summary>
    public FakeChatService RespondsWith(Func<string, string> responder) { _responder = responder; return this; }

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        lock (Prompts) Prompts.Add(string.Join("\n", chatHistory.Select(m => m.Content)));
        string text;
        if (_script.Count > 0) text = _script.Dequeue()(chatHistory);
        else if (_responder != null) text = _responder(Prompts[^1]);
        else throw new InvalidOperationException("No scripted response left.");
        IReadOnlyList<ChatMessageContent> result = new[] { new ChatMessageContent(AuthorRole.Assistant, text) { ModelId = "fake-model" } };
        return Task.FromResult(result);
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
