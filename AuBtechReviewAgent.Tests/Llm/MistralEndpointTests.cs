using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Mistral's DPA promises EU processing only on the regional endpoint, so the tool calls api.eu.mistral.ai unless the
/// server is told otherwise, and only an https address is ever used instead.
/// </summary>
public class MistralEndpointTests
{
    [Fact]
    public void ARunCallsTheEuEndpointByDefault()
    {
        Assert.Equal("https://api.eu.mistral.ai/v1", LlmFactory.MistralEndpoint(new LlmOptions()));
        Assert.Equal("https://api.eu.mistral.ai/v1", LlmFactory.MistralEndpoint(new LlmOptions { FastModel = "mistral-small-latest" }.ForFastModel()));
    }

    [Fact]
    public void TheServerCanChooseTheGlobalEndpointAndTheFastModelFollows()
    {
        var options = new LlmOptions { MistralBaseUrl = LlmFactory.MistralGlobalUrl + "/", FastModel = "mistral-small-latest" };

        Assert.Equal("https://api.mistral.ai/v1", LlmFactory.MistralEndpoint(options));
        Assert.Equal("https://api.mistral.ai/v1", LlmFactory.MistralEndpoint(options.ForFastModel()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://api.mistral.ai/v1")]
    [InlineData("api.mistral.ai/v1")]
    [InlineData("file:///etc/passwd")]
    public void AnAddressThatIsNotHttpsFallsBackToTheEuEndpoint(string setting)
    {
        Assert.Equal(LlmFactory.MistralBaseUrl, LlmFactory.MistralEndpoint(new LlmOptions { MistralBaseUrl = setting }));
    }
}
