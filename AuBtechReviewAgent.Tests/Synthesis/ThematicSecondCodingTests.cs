using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Which coded studies are left without a theme, and the second, independent coding with its agreement. Both were
/// only reached through a whole run; these pin them down on their own.
/// </summary>
public class ThematicSecondCodingTests
{
    private static readonly List<Theme> Themes = new()
    {
        new Theme { Id = "T1", Name = "Recovery", Description = "How loops recover.", Studies = { 1, 2 } },
        new Theme { Id = "T2", Name = "Audit", Description = "How actions are audited.", Studies = { 2 } },
    };

    private static readonly List<ThematicCode> Codes = new()
    {
        new ThematicCode { Id = "C1", Reference = 1, Label = "fault recovery" },
        new ThematicCode { Id = "C2", Reference = 2, Label = "auditing" },
        new ThematicCode { Id = "C3", Reference = 2, Label = "logging" },
    };

    [Fact]
    public void AStudyIsWithoutAThemeOnlyWhenNoneOfItsCodesIsPlacedOrSetAside()
    {
        var codes = new List<ThematicCode>
        {
            new() { Id = "C1", Reference = 1 }, new() { Id = "C2", Reference = 2 }, new() { Id = "C3", Reference = 3 }, new() { Id = "C4", Reference = 3 },
        };
        var themeCodes = new[] { new[] { " c1 " } };
        Assert.Equal(new[] { 3 }, ThematicSynthesis.StudiesWithoutTheme(codes, themeCodes, new[] { "C2" }));
        Assert.Empty(ThematicSynthesis.StudiesWithoutTheme(codes, themeCodes, new[] { "C2", "c4" }));
    }

    [Fact]
    public async Task TheSecondCoderSeesOnlyThemesAndCodesAndItsAgreementIsKappaOverEveryCell()
    {
        var model = new FakeChatService().RespondsWith(_ => """{"studies":[{"ref":1,"themes":["t1"]},{"ref":2,"themes":[" T2 "]}]}""");

        var second = await ThematicSynthesis.SecondCodingAsync(model, Themes, Codes);

        string prompt = Assert.Single(model.Prompts).Replace("\r\n", "\n");
        Assert.StartsWith("You are the second, independent coder in the thematic synthesis of a systematic literature review.", prompt);
        Assert.Contains("THEMES:\nT1: Recovery. How loops recover.\nT2: Audit. How actions are audited.\n", prompt);
        Assert.Contains("Study 1: fault recovery\nStudy 2: auditing; logging", prompt);
        Assert.Null(second.Error);
        Assert.Equal(4, second.CellsCompared);
        Assert.Equal(3, second.Agreements);
        Assert.Equal(0.5, second.Kappa);
        Assert.Equal(new[] { "Study 2, T1 (Recovery): first coding only" }, second.Disagreements);
    }

    [Fact]
    public async Task AFailedSecondCodingIsReportedAndHasNoKappa()
    {
        var model = new FakeChatService().RespondsWith(_ => """{"studies":[{"ref":1,"themes":["T9"]}]}""");

        var second = await ThematicSynthesis.SecondCodingAsync(model, Themes, Codes);

        Assert.StartsWith("The second coding failed: ", second.Error);
        Assert.Null(second.Kappa);
        Assert.Equal(0, second.CellsCompared);
    }
}
