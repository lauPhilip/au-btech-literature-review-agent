using AuBtechReviewAgent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class SecurityUtilityTests
{
    [Theory]
    [InlineData("YOUR_MISTRAL_API_KEY_PLACEHOLDER", true)]
    [InlineData("YOUR_ELSEVIER_API_KEY_PLACEHOLDER", true)]
    [InlineData("  your_ieee_api_key_placeholder ", true)]
    [InlineData("ci-placeholder", false)]
    [InlineData("sk-realkey123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RecognisesTheShippedPlaceholders(string? value, bool expected)
    {
        Assert.Equal(expected, SecurityUtility.IsPlaceholderKey(value));
    }

    [Theory]
    [InlineData("YOUR_MISTRAL_API_KEY_PLACEHOLDER")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not a key!")]
    public void NoUsableServerKeyComesBackEmpty(string? configured)
    {
        Assert.Equal(string.Empty, SecurityUtility.ConfiguredServerKey(configured));
    }

    [Fact]
    public void ARealServerKeyIsKeptAndCleaned()
    {
        Assert.Equal("abc-123_x", SecurityUtility.ConfiguredServerKey("  abc-123_x\n"));
    }
}

/// <summary>Startup with the placeholder keys that appsettings.json ships (issue #607).</summary>
[Collection(ApiCollection.Name)]
public sealed class PlaceholderKeyStartupTests
{
    private sealed class Host(Dictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        }
    }

    [Fact]
    public void APlaceholderMistralKeyStopsTheStartWithTheMissingKeyMessage()
    {
        using var host = new Host(new() { ["MISTRAL_API_KEY"] = "YOUR_MISTRAL_API_KEY_PLACEHOLDER" });
        var error = Assert.ThrowsAny<Exception>(() => host.CreateClient());
        var message = (error is InvalidOperationException ? error : error.InnerException ?? error).Message;
        Assert.Contains("MISTRAL_API_KEY is missing", message);
    }

    [Fact]
    public async Task PlaceholderOptionalKeysDoNotStopTheStart()
    {
        using var host = new Host(new()
        {
            ["MISTRAL_API_KEY"] = "server-key-for-the-placeholder-test",
            ["ELSEVIER_API_KEY"] = "YOUR_ELSEVIER_API_KEY_PLACEHOLDER",
            ["IEEE_API_KEY"] = "YOUR_IEEE_API_KEY_PLACEHOLDER",
        });
        using var client = host.CreateClient();
        Assert.True((await client.GetAsync("/robots.txt")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task ALocalModelStartsWithoutAMistralKey()
    {
        using var host = new Host(new()
        {
            ["MISTRAL_API_KEY"] = "YOUR_MISTRAL_API_KEY_PLACEHOLDER",
            ["Llm:Provider"] = "OpenAICompatible",
        });
        using var client = host.CreateClient();
        Assert.True((await client.GetAsync("/robots.txt")).IsSuccessStatusCode);
    }
}
