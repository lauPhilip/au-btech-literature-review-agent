using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The run page (Components/Pages/Home.razor with its components and code files), read as source, since the tests do not render components.
/// Issue #152: its summary of settings lists the exclusion criterion with the others.
/// </summary>
public class RunPageTests
{
    private static string? ReadPage() => PageSource.Read("Home");

    [Fact]
    public void TheSummaryOfSettingsListsTheExclusionCriterionAfterTheInclusionCriterion()
    {
        string? page = ReadPage();
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

    [Fact]
    public void TheRunPageIsAShellWithTheRunViewAndKeyDialogAsComponents()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return; // not run from the repository (packaged tests)
        string pages = Path.Join(root, "AuBtechReviewAgent", "Components", "Pages");

        string shell = File.ReadAllText(Path.Join(pages, "Home.razor"));
        Assert.Contains("<HomeRunView Page=\"this\" />", shell);
        Assert.Contains("<HomeKeyDialog Page=\"this\" />", shell);

        // The children call the page's handlers with the page as receiver, so the page renders again after a click.
        string dialog = File.ReadAllText(Path.Join(pages, "HomeKeyDialog.razor"));
        Assert.Contains("id=\"key-dialog\"", dialog);
        Assert.DoesNotContain("@bind=", dialog);
        Assert.Contains("EventCallback.Factory.Create(Page, Page.SaveSessionKeys)", dialog);
        string run = File.ReadAllText(Path.Join(pages, "HomeRunView.razor"));
        Assert.Contains("EventCallback.Factory.Create(Page, Page.OnScreeningReviewSubmitted)", run);

        // The whole page, read back as one text, still holds what the old single file held.
        string page = ReadPage()!;
        Assert.Contains("StartReviewProcess", page);
        Assert.Contains("SaveDraftAsync", page);
        Assert.Contains("AttachToRun", page);
    }

    [Fact]
    public void OnlyTheBrowserWithTheEditKeyGetsCancelReviewAndItAsksFirst()
    {
        string? page = ReadPage();
        if (page == null) return; // not run from the repository (packaged tests)

        // Shown beside a run that is alive here, to the browser with the edit key; the engine checks the key again.
        Assert.Contains("@if (CanCancel)", page);
        Assert.Contains("CanCancel => !Composing && canEdit && currentSessionId != Guid.Empty && ReviewEngine.IsRunActive(currentSessionId)", page);
        Assert.Contains("ReviewEngine.CancelRun(runId, editKey, cancelReason)", page);

        // An inline confirm, no browser dialog; every handler re-renders the page.
        Assert.Contains("<CancelRunCard Asking=\"ConfirmingCancel\"", page);
        Assert.DoesNotContain("confirm(", page);
        string run = File.ReadAllText(Path.Join(Pages(), "HomeRunView.razor"));
        foreach (string handler in new[] { "AskToCancel", "KeepRunning", "CancelReview", "StartNewDraft" })
            Assert.Contains($"EventCallback.Factory.Create(Page, Page.{handler})", run);
        Assert.Contains("EventCallback.Factory.Create<string>(Page, Page.SetCancelReason)", run);

        // A cancelled run shows the reason from its ledger and a way to start again, not "Report this problem".
        int cancelled = page.IndexOf("@if (cancelled)", StringComparison.Ordinal);
        Assert.True(cancelled >= 0);
        Assert.Contains("Start a new review", page[cancelled..page.IndexOf("Report this problem", StringComparison.Ordinal)]);
    }

    private static string Pages()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return Path.Join(root!, "AuBtechReviewAgent", "Components", "Pages");
    }
}
