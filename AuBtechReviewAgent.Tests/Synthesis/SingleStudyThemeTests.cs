using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #447: a theme is a pattern several studies share. A theme on one study is sent back unless the codebook says
/// why that study alone answers a part of the objective, and the reason is kept in the codebook.
/// </summary>
public class SingleStudyThemeTests
{
    private static List<ThematicCode> Codes() => new()
    {
        new ThematicCode { Id = "C1", Reference = 1, Label = "fault recovery" },
        new ThematicCode { Id = "C2", Reference = 2, Label = "fault recovery" },
        new ThematicCode { Id = "C3", Reference = 3, Label = "certification" },
    };

    [Fact]
    public async Task AThemeOnOneStudyIsSentBackUntilItHasAReason()
    {
        var chat = new FakeChatService()
            .Returns("""{"themes":[{"name":"Recovery","description":"d","codes":["C1","C2"]},{"name":"Certification","description":"d","codes":["C3"]}],"unassigned":[]}""")
            .Returns("""{"themes":[{"name":"Recovery","description":"d","codes":["C1","C2"],"singleStudyReason":"ignored"},{"name":"Certification","description":"d","codes":["C3"],"singleStudyReason":"Only this study certifies agent loops."}],"unassigned":[]}""");

        var (themes, _) = await ThematicSynthesis.BuildCodebookAsync(chat, "objective", Codes(), 8);

        Assert.Contains("A theme needs codes from at least two studies;", chat.Prompts[0]);
        Assert.Contains("the theme \"Certification\" rests on one study ([3]). Merge its codes into a theme with another study, or give \"singleStudyReason\"", chat.Prompts[1]);
        Assert.Null(themes[0].SingleStudyReason); // a theme of two studies keeps no reason
        Assert.Equal("Only this study certifies agent loops.", themes[1].SingleStudyReason);
        string json = System.Text.Json.JsonSerializer.Serialize(new ThematicCodebook { Themes = themes });
        Assert.Contains("\"SingleStudyReason\":\"Only this study certifies agent loops.\"", json);
    }

    [Fact]
    public void WithOneCodedStudyNoReasonIsAskedFor()
    {
        var one = new List<ThematicCode> { new() { Id = "C1", Reference = 1, Label = "x" } };
        Assert.Null(ThematicSynthesis.SingleStudyThemeProblem(new[] { ("Recovery", (IEnumerable<string>)new[] { "C1" }, (string?)null) }, one, 1));
        Assert.NotNull(ThematicSynthesis.SingleStudyThemeProblem(new[] { ("Recovery", (IEnumerable<string>)new[] { "C1" }, (string?)" ") }, Codes(), 3));
    }
}
