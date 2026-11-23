using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The grey literature review's page at /mlr (Modules/Multivocal/Pages/MultivocalStart.razor and the parts it is
/// built from), read as source: the multivocal pages are not reachable in CI by a real run. These pin down what the
/// page offers, so splitting it into parts keeps every step, tab, field and call.
/// </summary>
public class MultivocalStartPageTests
{
    // Every file of the page: MultivocalStart itself and its parts, but not the paper and report pages.
    private static string? StartSource()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return null; // not run from the repository (packaged tests)
        string folder = Path.Join(root, "AuBtechReviewAgent", "Modules", "Multivocal", "Pages");
        var files = Directory.GetFiles(folder, "*.razor").Concat(Directory.GetFiles(folder, "*.cs"))
            .Where(f => !Path.GetFileName(f).StartsWith("MultivocalPaperPage", StringComparison.Ordinal)
                     && !Path.GetFileName(f).StartsWith("MultivocalReportPage", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);
        return string.Join("\n", files.Select(File.ReadAllText));
    }

    [Fact]
    public void ThePageAnswersAtBothAddresses()
    {
        string? source = StartSource();
        if (source == null) return;
        Assert.Contains("@page \"/mlr\"", source);
        Assert.Contains("@page \"/mlr/{RunId:guid}\"", source);
        Assert.Contains("[SupplyParameterFromQuery(Name = \"card\")]", source);
        Assert.Contains("Nav.NotFound()", source);
        Assert.Contains("ReviewModules.ElsewhereFor(", source);
    }

    [Fact]
    public void TheFormHasTheFiveStepsOfTheGuidelines()
    {
        string? source = StartSource();
        if (source == null) return;
        foreach (string step in new[]
        {
            "new(\"Need\", \"What is the review for, and who reads it?\", \"G2\", \"Need and audience\")",
            "new(\"Grey\", \"Is grey literature needed?\", \"G3\", \"Grey literature\")",
            "new(\"Questions\", \"What should the review answer?\", \"G4–G5\", \"Questions\")",
            "new(\"Search\", \"What to search, where, how much, and what to include?\", \"G6–G9, G11\", \"Search\")",
            "new(\"Protocol\", \"Check the plan. Starting the review writes it down, then runs every step on its own.\", \"G1\", \"Protocol\")",
        })
            Assert.Contains(step, source);
        foreach (string id in new[] { "p-topic", "p-goal", "p-existing", "p-include", "p-exclude", "p-top", "p-threshold", "step-title", "rq-", "rqt-" })
            Assert.Contains($"id=\"{id}", source.Replace("id=\"@($\"", "id=\""));
        foreach (string label in new[] { "Try an example", "Add a sub-question", "Add a research question", "Add a search string", "Propose search strings", "Start the review", "Back", "Next: " })
            Assert.Contains(label, source);
        Assert.Contains("Every answer is \"no\": by the guidelines, a systematic review may be enough. You can still go on.", source);
        Assert.Contains("Before the protocol can be written:", source);
        Assert.Contains("Everything is filled in. Check the protocol, then write it.", source);
    }

    [Fact]
    public void ThePanelHasEveryTab()
    {
        string? source = StartSource();
        if (source == null) return;
        Assert.Contains("\"protocol\" => \"Protocol\", \"progress\" => \"Progress\", \"sources\" => \"Sources found\", \"map\" => \"Systematic map\", \"themes\" => \"Themes\", _ => \"What this review will do\"", source);
        foreach (string tab in new[] { "progress", "plan", "protocol", "sources", "map", "themes" })
            Assert.Contains($"ChooseTab(\"{tab}\")", source);
        foreach (string heading in new[] { "Your review is ready", "Grey sources found", "Systematic map", "How many sources have each value", "Themes", "Values left out by the support check", "Findings in no theme", "What this review will do", "What you will get" })
            Assert.Contains(heading, source);
        Assert.Contains("<MultivocalProgressPanel ", source);
        Assert.Contains("aria-label=\"Protocol\"", source);
    }

    [Fact]
    public void TheSourcesTabKeepsEveryActionOnASource()
    {
        string? source = StartSource();
        if (source == null) return;
        foreach (string option in new[] { "passed", "below", "unassessed", "included", "look", "excluded", "duplicates", "all" })
            Assert.Contains($"<option value=\"{option}\">", source);
        foreach (string label in new[] { "Keep page text", "Fetching…", "Try again", "Assessing…", "Keep it", "Include", "Keep it out", "Exclude", "Note (optional)", "Wayback Machine", "look at this", "confirmed by you", "changed by you" })
            Assert.Contains(label, source);
        Assert.Contains("Opinion piece: its claim and the critical questions (G13)", source);
        Assert.Contains("Left out, as the quote was not found in the page: ", source);
        Assert.Contains("Screener.ReviewAsync(", source);
        Assert.Contains("Quality.RetryAsync(", source);
        Assert.Contains("Pages.KeepAsync(", source);
    }

    [Fact]
    public void ThePageCallsTheBrowserOnlyThroughTheSiteScript()
    {
        string? source = StartSource();
        if (source == null) return;
        foreach (string call in new[] { "traceableReview.focus", "traceableReview.getEditKey", "traceableReview.saveEditKey", "traceableReview.notifyDone", "traceableReview.requestNotify", "traceableReview.notifyPermission" })
            Assert.Contains($"\"{call}\"", source);
        Assert.Contains("Runner.Changed += OnRunChanged", source);
        Assert.Contains("Runner.Changed -= OnRunChanged", source);
        Assert.Contains("Runner.Start(", source);
        Assert.Contains("Planner.CreateAsync(plan, ", source);
        Assert.Contains("ReviewEngine.PreviewSearchStringsAsync(", source);
    }

    [Fact]
    public void EveryFileOfThePageUsesTheThemeColours()
    {
        string? source = StartSource();
        if (source == null) return;
        Assert.DoesNotContain("text-gray-", source);
        Assert.DoesNotContain("border-gray-", source);
        Assert.DoesNotContain("ui-h2", source);
        Assert.DoesNotContain("rounded-lg", source);
    }

    [Fact]
    public void FlaggedTextMustBeConfirmedBeforeTheReviewStarts()
    {
        string? source = StartSource();
        if (source == null) return;
        Assert.Contains("MultivocalPlanGuard.Lines(plan)", source);
        Assert.Contains("Some of your text reads like instructions to the language model:", source);
        Assert.Contains("@onchange=\"ConfirmFlags\"", source);
        Assert.Contains("disabled=\"@(Problems.Count > 0 || !FlagsConfirmed || saving)\"", source);
        Assert.Contains("Planner.CreateAsync(plan, FlagsConfirmed ? FlagLines : null)", source);
    }
}
