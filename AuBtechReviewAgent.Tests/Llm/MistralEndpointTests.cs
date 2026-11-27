using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The tool calls Mistral's global endpoint at list price unless the server chooses the EU endpoint, which costs 1.1
/// times as much (Lau, 27 November 2026). Only an https address is ever used instead of the default.
/// </summary>
public class MistralEndpointTests
{
    [Fact]
    public void ARunCallsTheGlobalEndpointByDefault()
    {
        Assert.Equal("https://api.mistral.ai/v1", LlmFactory.MistralEndpoint(new LlmOptions()));
        Assert.Equal("https://api.mistral.ai/v1", LlmFactory.MistralEndpoint(new LlmOptions { FastModel = "mistral-small-latest" }.ForFastModel()));
    }

    [Fact]
    public void TheServerCanChooseTheEuEndpointAndTheFastModelFollows()
    {
        var options = new LlmOptions { MistralBaseUrl = LlmFactory.MistralEuUrl + "/", FastModel = "mistral-small-latest" };

        Assert.Equal("https://api.eu.mistral.ai/v1", LlmFactory.MistralEndpoint(options));
        Assert.Equal("https://api.eu.mistral.ai/v1", LlmFactory.MistralEndpoint(options.ForFastModel()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://api.eu.mistral.ai/v1")]
    [InlineData("api.eu.mistral.ai/v1")]
    [InlineData("file:///etc/passwd")]
    public void AnAddressThatIsNotHttpsFallsBackToTheDefault(string setting)
    {
        Assert.Equal(LlmFactory.MistralBaseUrl, LlmFactory.MistralEndpoint(new LlmOptions { MistralBaseUrl = setting }));
    }
}
