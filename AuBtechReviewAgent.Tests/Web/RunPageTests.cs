using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The run page (Components/Pages/Home.razor), read as source, since the tests do not render components.
/// Issue #152: its summary of settings lists the exclusion criterion with the others.
/// </summary>
public class RunPageTests
{
    private static string? PageSource()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? null : File.ReadAllText(Path.Join(root, "AuBtechReviewAgent", "Components", "Pages", "Home.razor"));
    }

    [Fact]
    public void TheSummaryOfSettingsListsTheExclusionCriterionAfterTheInclusionCriterion()
    {
        string? page = PageSource();
        if (page == null) return; // not run from the repository (packaged tests)
        int At(string text) { int i = page.IndexOf(text, StringComparison.Ordinal); Assert.True(i >= 0, $"the page has \"{text}\""); return i; }

        int include = At("@SettingRow(\"Include\"");
        int exclude = At("@SettingRow(\"Exclude\", ExclusionShown())");
        int years = At("@SettingRow(\"Years\"");
        Assert.True(include < exclude && exclude < years, "Exclude comes between Include and Years");

        // A finished or running run shows its stored criterion; a draft shows what was typed.
        Assert.Contains("runInfo.ExclusionCriteria", page);
        Assert.Contains(": exclusionCriteria)", page);
    }
}
