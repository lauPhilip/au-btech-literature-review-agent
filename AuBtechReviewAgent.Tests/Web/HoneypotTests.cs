using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The hidden field on the forms that start a review (issue #433): out of sight, out of the screen reader and out of
/// the tab order, with no script or inline style, and a start with it filled in is refused before anything is counted.
/// </summary>
public class HoneypotTests
{
    private static string? Read(params string[] path)
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? null : File.ReadAllText(Path.Join(new[] { root, "AuBtechReviewAgent" }.Concat(path).ToArray()));
    }

    [Fact]
    public void The_field_is_hidden_from_people_screen_readers_and_the_keyboard_without_a_script()
    {
        string? field = Read("Components", "Layout", "Honeypot.razor");
        if (field == null) return; // not run from the repository (packaged tests)
        Assert.Contains("<div class=\"sr-only\" aria-hidden=\"true\">", field);
        Assert.Contains("tabindex=\"-1\"", field);
        Assert.Contains("autocomplete=\"off\"", field);
        Assert.Contains("<label for=\"@Id\">", field);
        Assert.DoesNotContain("<script", field);
        Assert.DoesNotContain("style=", field);
    }

    [Fact]
    public void Both_start_forms_carry_the_field_and_refuse_a_start_that_filled_it_before_counting_it()
    {
        string? composer = Read("Components", "Dashboard", "ReviewComposer.razor");
        if (composer == null) return;
        Assert.Contains("<Honeypot Id=\"f-website\" @bind-Value=\"State.Honeypot\" />", composer);
        Assert.Contains("<Honeypot Id=\"mlr-website\" @bind-Value=\"honeypot\" />", Read("Modules", "Multivocal", "Pages", "MultivocalStart.razor"));

        string run = Read("Components", "Pages", "Home.Run.cs")!;
        int refused = run.IndexOf("if (!string.IsNullOrEmpty(State.Honeypot))", StringComparison.Ordinal);
        Assert.True(refused > 0);
        Assert.True(refused < run.IndexOf("Actions.TryTake(AuBtechReviewAgent.ActionLimiter.StartReview", StringComparison.Ordinal));
        Assert.True(refused < run.IndexOf("Coordinator.TryAdmit", StringComparison.Ordinal));
        Assert.True(refused < run.IndexOf("Quota.TryReserve", StringComparison.Ordinal));

        string plan = Read("Modules", "Multivocal", "Pages", "MultivocalStart.Planning.cs")!;
        int grey = plan.IndexOf("if (!string.IsNullOrEmpty(honeypot))", StringComparison.Ordinal);
        Assert.True(grey > 0 && grey < plan.IndexOf("Planner.CreateAsync", StringComparison.Ordinal));
    }
}
