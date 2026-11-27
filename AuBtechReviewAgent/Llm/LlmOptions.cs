using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>
/// Which language model the review uses, bound from the "Llm" section of appsettings.json.
/// Provider "Mistral" (default) uses the Mistral API. Provider "OpenAICompatible" talks to any server that
/// implements the OpenAI chat completions API: a local Ollama (BaseUrl http://localhost:11434/v1), LM Studio,
/// vLLM, llama.cpp server, or a hosted service. With a local model the whole review can run without sending
/// anything to a commercial provider.
/// </summary>
public class LlmOptions
{
    public string Provider { get; set; } = "Mistral";
    public string Model { get; set; } = "mistral-large-latest";

    /// <summary>OpenAICompatible only: base URL ending before /chat/completions, e.g. http://localhost:11434/v1.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>OpenAICompatible only: API key if the server needs one (Ollama does not). Keep it in user-secrets.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Mistral only: the base URL of its API. The EU regional endpoint by default, so prompts, papers and answers are
    /// processed in the EU (Mistral's regional inference; the global api.mistral.ai gives no region). A model that the
    /// EU endpoint does not serve can be reached by setting Llm:MistralBaseUrl to <see cref="LlmFactory.MistralGlobalUrl"/>.
    /// </summary>
    public string MistralBaseUrl { get; set; } = LlmFactory.MistralBaseUrl;

    /// <summary>
    /// The earlier name of <see cref="QualityParallelism"/>, still read when that is not set.
    /// </summary>
    public int ScreeningParallelism { get; set; } = 8;

    private int? _qualityParallelism;

    /// <summary>
    /// How many items one run sends to the main model at the same time (extraction, coding, writing, and every stage
    /// when there is no <see cref="FastModel"/>). Reasoning models take 30 to 110 seconds a call, so the calls are bound
    /// by waiting, not by the server's throughput. Not set: <see cref="ScreeningParallelism"/>.
    /// </summary>
    public int QualityParallelism
    {
        get => _qualityParallelism ?? ScreeningParallelism;
        set => _qualityParallelism = value;
    }

    /// <summary>How many items one run sends to <see cref="FastModel"/> at the same time (the <see cref="FastStages"/>).</summary>
    public int FastParallelism { get; set; } = 16;

    /// <summary>The width of a stage in one run: <see cref="FastParallelism"/> when it goes to the fast model, else <see cref="QualityParallelism"/>.</summary>
    public int ParallelismFor(string stage) => Math.Max(1, HasFastModel && FastStages.Contains(stage) ? FastParallelism : QualityParallelism);

    /// <summary>
    /// Mistral only: the reasoning_effort sent with every call ("low", "medium" or "high"). Reasoning models such as
    /// Mistral Large 4 always reason and use "high" when the parameter is left out, which made a review several
    /// times slower than with the earlier non-reasoning model. Empty: the parameter is not sent. A model that does
    /// not accept it is asked again without it.
    /// </summary>
    public string ReasoningEffort { get; set; } = "low";

    /// <summary>
    /// Optional second model for the narrow, high-volume checks of a systematic review (<see cref="FastStages"/>):
    /// the peer-review filter, both screenings and the citation checks. Those calls answer a short structured
    /// question, so a smaller model without reasoning is several times faster there; everything that writes or
    /// reasons (search strings, extraction, coding, writing, repair, peer review) stays on <see cref="Model"/>.
    /// Empty: <see cref="Model"/> does everything.
    /// </summary>
    public string FastModel { get; set; } = "";

    /// <summary>The reasoning_effort sent to <see cref="FastModel"/> ("none" switches reasoning off on Mistral Small 4).</summary>
    public string FastReasoningEffort { get; set; } = "none";

    /// <summary>The stages (LlmStage names) that go to <see cref="FastModel"/> when one is set.</summary>
    public static readonly IReadOnlySet<string> FastStages = new HashSet<string>(StringComparer.Ordinal)
    {
        "peer-review-filter", "screening", "screening-second", "citation-check", "citation-recheck",
    };

    public bool HasFastModel => !string.IsNullOrWhiteSpace(FastModel) && !FastModel.Trim().Equals(Model.Trim(), StringComparison.Ordinal);

    public string ModelFor(string stage) => HasFastModel && FastStages.Contains(stage) ? FastModel.Trim() : Model;

    /// <summary>A readable name of the model a stage uses, for the methods text.</summary>
    public string DisplayNameFor(string stage) => NameOf(ModelFor(stage));

    public bool IsOpenAICompatible => Provider.Equals("OpenAICompatible", StringComparison.OrdinalIgnoreCase);

    /// <summary>A readable name for the methods section, e.g. "mistral-large-latest (Mistral API)".</summary>
    public string DisplayName => NameOf(Model);

    /// <summary>The same settings with another model and effort, for the client of <see cref="FastModel"/>.</summary>
    public LlmOptions ForFastModel() => new()
    {
        Provider = Provider, Model = FastModel.Trim(), BaseUrl = BaseUrl, ApiKey = ApiKey, MistralBaseUrl = MistralBaseUrl,
        ScreeningParallelism = FastParallelism, FastParallelism = FastParallelism, ReasoningEffort = FastReasoningEffort,
    };

    private string NameOf(string model) => IsOpenAICompatible
        ? $"{model} (OpenAI-compatible endpoint{(string.IsNullOrWhiteSpace(BaseUrl) ? "" : " " + SafeHost(BaseUrl))})"
        : $"{model} (Mistral API)";

    private static string SafeHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
}
