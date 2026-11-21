using System.Text.RegularExpressions;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Redesign step 12: every page and the shared ui-* classes use the theme-aware colours (ink, muted, heading, line,
/// soft, page, primary and the five verdicts), so the dark theme can be offered on every page. A fixed grey or the
/// fixed brand navy as text would stay dark on a dark page. Read as source.
/// </summary>
public class ThemeColoursTests
{
    private static readonly Regex Fixed = new(@"(?<![\w:/-])(?:[a-z0-9\[\]=-]+:)*(?:(?:text|bg|border|divide|decoration|ring)-gray-\d{2,3}|text-brand\b|border-brand\b|bg-white\b)", RegexOptions.Compiled);

    private static string? Root()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root;
    }

    [Fact]
    public void NoComponentUsesAFixedGreyOrTheFixedNavyForText()
    {
        string? root = Root();
        if (root == null) return; // not run from the repository (packaged tests)

        var offenders = new[] { "Components", "Modules" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Join(root, "AuBtechReviewAgent", folder), "*.razor", SearchOption.AllDirectories))
            .SelectMany(file => Fixed.Matches(File.ReadAllText(file))
                .Select(m => m.Value)
                .Where(v => !v.StartsWith("print:", StringComparison.Ordinal))
                .Select(v => $"{Path.GetFileName(file)}: {v}"))
            .ToList();

        Assert.True(offenders.Count == 0, "Fixed colours left: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheSharedClassesUseTheThemeColours()
    {
        string? root = Root();
        if (root == null) return;

        string css = File.ReadAllText(Path.Join(root, "AuBtechReviewAgent", "Styles", "tailwind.input.css"));
        foreach (string name in new[] { "ui-card", "ui-btn-primary", "ui-btn-secondary", "ui-btn-ghost", "ui-input", "ui-label", "ui-h1", "ui-h2", "ui-h3", "ui-lead", "ui-muted", "ui-eyebrow", "ui-chip-ok", "ui-chip-partial", "ui-chip-bad" })
        {
            var line = css.Split('\n').Single(l => l.TrimStart().StartsWith($".{name} {{", StringComparison.Ordinal));
            Assert.DoesNotMatch(@"gray-\d|brand|bg-white|text-white|(green|amber|red)-\d", line);
        }
        Assert.Contains("background: rgb(var(--page))", css);
    }
}
