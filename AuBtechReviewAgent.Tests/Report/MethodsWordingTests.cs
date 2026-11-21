using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Wording faults in the methods text and the appendix found in live runs: issues #120, #122 and #168.</summary>
public class MethodsWordingTests
{
    [Theory]
    [InlineData("mistral-small-latest (Mistral API)", "temperature 0", "(mistral-small-latest, Mistral API, temperature 0)")]
    [InlineData("llama3.1 (OpenAI-compatible endpoint localhost)", null, "(llama3.1, OpenAI-compatible endpoint localhost)")]
    [InlineData("mistral-large-latest", "temperature 0", "(mistral-large-latest, temperature 0)")]
    [InlineData("  gpt-x  ", null, "(gpt-x)")]
    public void AModelNameIsNeverBracketedTwice(string model, string? details, string expected) =>
        Assert.Equal(expected, MethodsSectionWriter.ModelInBrackets(model, details));

    [Fact]
    public void TheMethodsTextHasNoDoubledBrackets()
    {
        var stats = new ReviewStats { Screened = 10, Included = 4, Excluded = 6, DualScreened = 10, ScreeningKappa = 0.8 };
        string selection = MethodsSectionWriter.SelectionProcess(false, stats, modelName: "mistral-large-latest (Mistral API)");
        string synthesis = ThematicSynthesis.MethodsText(null, "mistral-large-latest (Mistral API)", true, "mistral-small-latest (Mistral API)");

        foreach (string text in new[] { selection, synthesis })
        {
            Assert.DoesNotContain("))", text);
            Assert.DoesNotContain("API),", text);
        }
    }

    [Theory]
    [InlineData(3, 4, 0.0, true)]     // the run in #122: 75% agreement, kappa 0.00
    [InlineData(9, 10, 0.1, true)]
    [InlineData(5, 10, 0.0, false)]   // low agreement: kappa says nothing surprising
    [InlineData(9, 10, 0.6, false)]   // kappa is fine
    public void ALowKappaNextToHighAgreementIsExplained(int agreed, int total, double kappa, bool explained)
    {
        string note = MethodsSectionWriter.KappaNote(agreed, total, kappa);
        Assert.Equal(explained, note.Length > 0);
        if (explained) Assert.Contains("by chance", note);
    }

    [Fact]
    public void TheSelectionTextExplainsTheKappaOfTheRunInTheIssue()
    {
        var stats = new ReviewStats { Screened = 4, Included = 1, Excluded = 3, DualScreened = 4, ScreeningDisagreements = 1, ScreeningKappa = 0.0 };
        string text = MethodsSectionWriter.SelectionProcess(false, stats, modelName: "m");

        Assert.Contains("agreed on 3 of 4 records (Cohen's kappa = 0.00)", text);
        Assert.Contains("Kappa is low although most decisions agree", text);
        Assert.Equal("", MethodsSectionWriter.KappaNote(3, 4, null));
    }
}
