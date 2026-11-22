using System.Net;
using System.Text;
using AuBtechReviewAgent;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The OpenAI-compatible model client: request bodies, reasoning answers, reasoning effort, server errors and the
/// routing of checks to the fast model.
/// </summary>
public class ModelClientTests
{
    [Fact]
    public void OpenAICompatibleRequestsCarryTemperatureAndJsonMode()
    {
        var history = new ChatHistory();
        history.AddUserMessage("Hello");
        var settings = new MistralAIPromptExecutionSettings { Temperature = 0.0 };
        settings.ExtensionData = new Dictionary<string, object> { ["response_format"] = new { type = "json_object" } };

        string body = OpenAICompatibleChatService.BuildRequestBody("llama3.1", history, settings);
        Assert.Contains("\"model\":\"llama3.1\"", body);
        Assert.Contains("\"temperature\":0", body);
        Assert.Contains("\"response_format\":{\"type\":\"json_object\"}", body);
        Assert.Contains("\"role\":\"user\"", body);

        var (text, model, usage) = OpenAICompatibleChatService.ParseResponse("""{"model":"llama3.1:8b","choices":[{"message":{"role":"assistant","content":"{\"ok\":true}"}}],"usage":{"total_tokens":12}}""");
        Assert.Equal("{\"ok\":true}", text);
        Assert.Equal("llama3.1:8b", model);
        Assert.Contains("12", usage);
    }

    private class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"model":"local","choices":[{"message":{"content":"hi"}}]}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task OpenAICompatibleClientPostsToChatCompletions()
    {
        var handler = new StubHandler();
        var chat = new OpenAICompatibleChatService("http://localhost:11434/v1/", "llama3.1", "", new HttpClient(handler));
        var reply = await chat.GetChatMessageContentAsync("Say hi");

        Assert.Equal("hi", reply.Content);
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.Last!.RequestUri!.ToString());
        Assert.Null(handler.Last.Headers.Authorization); // Ollama needs no key
        Assert.Contains("Say hi", handler.Body);
    }

    [Fact]
    public void ReasoningModelAnswersAreReadFromTheTextChunksOnly()
    {
        // Mistral Large 4 answers with a list of chunks; the thinking chunk holds a JSON object of its own,
        // which must never be read as the answer.
        string json = """
            {"model":"mistral-large-4-0","choices":[{"message":{"role":"assistant","content":[
              {"type":"thinking","thinking":[{"type":"text","text":"Draft: {\"decision\":\"Excluded\"}"}]},
              {"type":"text","text":"{\"decision\":\"Included\","},
              {"type":"text","text":"\"reasoning\":\"On topic.\"}"}
            ]}}]}
            """;
        var (text, model, _) = OpenAICompatibleChatService.ParseResponse(json);

        Assert.Equal("{\"decision\":\"Included\",\"reasoning\":\"On topic.\"}", text);
        Assert.DoesNotContain("Draft", text);
        Assert.Equal("mistral-large-4-0", model);
        var answer = System.Text.Json.JsonSerializer.Deserialize<ScreeningAnswer>(LlmJson.ExtractObject(text),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Null(ScreeningAnswer.Validate(answer));
        Assert.True(answer.IsIncluded);
    }

    [Fact]
    public void ReasoningEffortIsSentOnlyWhenSet()
    {
        var history = new ChatHistory();
        history.AddUserMessage("Hello");

        Assert.Contains("\"reasoning_effort\":\"low\"", OpenAICompatibleChatService.BuildRequestBody("mistral-large-latest", history, null, "low"));
        Assert.DoesNotContain("reasoning_effort", OpenAICompatibleChatService.BuildRequestBody("mistral-large-latest", history, null, ""));
        Assert.DoesNotContain("reasoning_effort", OpenAICompatibleChatService.BuildRequestBody("llama3.1", history, null));
    }

    [Fact]
    public void GreedyDecisionsSendTopPOne()
    {
        // Mistral answers 400 "top_p must be 1 when using greedy sampling" to temperature 0 without top_p,
        // and every decision runs at temperature 0. The Semantic Kernel connector always sent it.
        var history = new ChatHistory();
        history.AddUserMessage("Screen this record");
        string body = OpenAICompatibleChatService.BuildRequestBody("mistral-large-latest", history, LlmJson.JsonMode(0.0), "low");

        Assert.Contains("\"temperature\":0", body);
        Assert.Contains("\"top_p\":1", body);
        Assert.DoesNotContain("top_p", OpenAICompatibleChatService.BuildRequestBody("llama3.1", history, null));
    }

    [Fact]
    public void MistralIsCalledWithLowReasoningEffortByDefault()
    {
        Assert.Equal("low", new LlmOptions().ReasoningEffort);
        Assert.IsType<OpenAICompatibleChatService>(LlmFactory.Create(new LlmOptions(), "test-key"));
    }

    private class RejectsEffortHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            if (body.Contains("reasoning_effort"))
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"message":"Extra inputs are not permitted: reasoning_effort"}""", Encoding.UTF8, "application/json"),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"model":"mistral-large-2411","choices":[{"message":{"content":"ok"}}]}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task AModelThatRejectsReasoningEffortIsAskedAgainWithoutIt()
    {
        var handler = new RejectsEffortHandler();
        var chat = new OpenAICompatibleChatService(LlmFactory.MistralBaseUrl, "mistral-large-2411", "key", new HttpClient(handler), "low");

        Assert.Equal("ok", (await chat.GetChatMessageContentAsync("first")).Content);
        Assert.Equal("ok", (await chat.GetChatMessageContentAsync("second")).Content);

        // One rejected call, then the parameter is left out for the rest of the run.
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Contains("reasoning_effort", handler.Bodies[0]);
        Assert.DoesNotContain("reasoning_effort", handler.Bodies[1]);
        Assert.DoesNotContain("reasoning_effort", handler.Bodies[2]);
    }

    [Fact]
    public async Task TheCallLogShowsTheReasoningEffortThatWasSent()
    {
        var sent = new RecordingChatCompletionService(
            new OpenAICompatibleChatService(LlmFactory.MistralBaseUrl, "mistral-large-latest", "key", new HttpClient(new StubHandler()), "low"), "mistral-large-latest");
        await sent.GetChatMessageContentAsync("hello");
        Assert.Equal("low", sent.Calls.Single().ReasoningEffort);

        // A model that rejects the setting is asked again without it, and the log says so.
        var dropped = new RecordingChatCompletionService(
            new OpenAICompatibleChatService(LlmFactory.MistralBaseUrl, "mistral-large-2411", "key", new HttpClient(new RejectsEffortHandler()), "low"), "mistral-large-2411");
        await dropped.GetChatMessageContentAsync("hello");
        Assert.Null(dropped.Calls.Single().ReasoningEffort);
    }

    [Fact]
    public void OtherErrorsAreNotRetriedWithoutReasoningEffort()
    {
        Assert.False(OpenAICompatibleChatService.RejectsReasoningEffort(HttpStatusCode.Unauthorized, "reasoning_effort"));
        Assert.False(OpenAICompatibleChatService.RejectsReasoningEffort(HttpStatusCode.BadRequest, "invalid model"));
        Assert.True(OpenAICompatibleChatService.RejectsReasoningEffort(HttpStatusCode.UnprocessableEntity, "reasoning_effort: unexpected value"));
    }

    [Fact]
    public void ErrorsCarryTheServersOwnReason()
    {
        // Seen in a live run: without the reason, the ledger said only "The model server answered 400."
        Assert.Equal(": top_p must be 1 when using greedy sampling",
            OpenAICompatibleChatService.ErrorDetail("""{"object":"error","message":"top_p must be 1 when using greedy sampling.","type":"invalid_request_error"}"""));
        Assert.Equal(": Invalid model", OpenAICompatibleChatService.ErrorDetail("""{"error":{"message":"Invalid model"}}"""));
        Assert.Equal(": Bad Gateway", OpenAICompatibleChatService.ErrorDetail("Bad Gateway"));
        Assert.Equal(" with an empty answer", OpenAICompatibleChatService.ErrorDetail(""));
        Assert.EndsWith("...", OpenAICompatibleChatService.ErrorDetail(new string('x', 500)));
    }

    [Fact]
    public async Task ChecksGoToTheFastModelAndEverythingElseToTheMainModel()
    {
        var options = new LlmOptions { Model = "mistral-large-latest", FastModel = "mistral-small-latest" };
        var router = new StageRoutedChatService(new FakeChatService().RespondsWith(_ => "main"), new FakeChatService().RespondsWith(_ => "fast"), options);

        foreach (var stage in new[] { "peer-review-filter", "screening", "screening-second", "citation-check", "citation-recheck" })
            using (LlmStage.Begin(stage))
            {
                Assert.Equal("fast", (await router.GetChatMessageContentAsync("x")).Content);
                Assert.Equal("mistral-small-latest", router.ModelFor(stage));
            }
        foreach (var stage in new[] { "search-perspectives", "extraction", "thematic-coding", "theme-sections", "citation-repair", "automated-peer-review" })
            using (LlmStage.Begin(stage))
            {
                Assert.Equal("main", (await router.GetChatMessageContentAsync("x")).Content);
                Assert.Equal("mistral-large-latest", router.ModelFor(stage));
            }
    }

    [Fact]
    public void WithoutAFastModelOneModelDoesEverything()
    {
        var options = new LlmOptions();
        Assert.False(options.HasFastModel);
        Assert.Equal("mistral-large-latest", options.ModelFor("screening"));
        Assert.IsType<OpenAICompatibleChatService>(LlmFactory.CreateForReview(options, "key"));
        Assert.False(new LlmOptions { FastModel = "mistral-large-latest" }.HasFastModel); // the same model is not a second one

        var split = new LlmOptions { FastModel = "mistral-small-latest" };
        Assert.IsType<StageRoutedChatService>(LlmFactory.CreateForReview(split, "key"));
        Assert.Equal("mistral-large-latest (Mistral API)", split.DisplayName);
        Assert.Equal("mistral-small-latest (Mistral API)", split.DisplayNameFor("screening"));
        Assert.Equal("none", split.ForFastModel().ReasoningEffort);
        Assert.Equal("mistral-small-latest", split.ForFastModel().Model);
    }

    [Fact]
    public async Task TheCallLogNamesTheModelEachStageWentTo()
    {
        var options = new LlmOptions { Model = "mistral-large-latest", FastModel = "mistral-small-latest" };
        var router = new StageRoutedChatService(new FakeChatService().RespondsWith(_ => "main"), new FakeChatService().RespondsWith(_ => "fast"), options);
        var recorder = new RecordingChatCompletionService(router, "mistral-large-latest");

        using (LlmStage.Begin("screening")) await recorder.GetChatMessageContentAsync("x");
        using (LlmStage.Begin("extraction")) await recorder.GetChatMessageContentAsync("y");

        Assert.Equal(new[] { "mistral-small-latest", "mistral-large-latest" }, recorder.Calls.OrderBy(c => c.Sequence).Select(c => c.ModelRequested));
        var settings = recorder.Summarize("test");
        Assert.Equal("mistral-large-latest", settings.Model);
        Assert.Equal("mistral-small-latest", settings.FastModel);

        // One model: no fast model on file.
        var single = new RecordingChatCompletionService(new FakeChatService().RespondsWith(_ => "ok"), "mistral-large-latest");
        using (LlmStage.Begin("screening")) await single.GetChatMessageContentAsync("x");
        Assert.Null(single.Summarize("test").FastModel);
    }
}
