using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The landing page's citation example (CitationLoop) at rest in each of its states, in both themes. The CI
/// accessibility check sees the example only stopped on its first verdict (it asks for less motion), so the other
/// states are checked here from the theme colours in Styles/tailwind.input.css: every text reaches 4.5:1 on what is
/// behind it once a fade has finished. Read as source.
/// </summary>
public class CitationLoopContrastTests
{
    private static readonly Regex Token = new(@"--(?<name>[a-z-]+):\s*(?<r>\d+)\s+(?<g>\d+)\s+(?<b>\d+);", RegexOptions.Compiled);

    // Text colour, the colour behind it, and where in the example the pair is seen.
    private static readonly (string Text, string Behind, string Where)[] Pairs =
    {
        ("muted", "page", "the card's title line"),
        ("text", "page", "the sentence"),
        ("navy", "page", "a citation number at rest"),
        ("navy", "active", "a citation number while it is checked"),
        ("muted", "soft", "\"Checking citation N against the paper…\""),
        ("supported", "supported-fill", "citation 5 marked Supported"),
        ("partly", "partly-fill", "citation 4 marked Partly supported"),
        ("not-supported", "not-supported-fill", "citation 10 marked Not supported"),
        ("supported", "page", "the Supported chip"),
        ("partly", "page", "the Partly supported chip"),
        ("not-supported", "page", "the Not supported chip"),
        ("text", "supported-fill", "the quote on the Supported card"),
        ("text", "partly-fill", "the quote on the Partly supported card"),
        ("text", "not-supported-fill", "the reason on the Not supported card"),
        ("muted", "supported-fill", "the source and note on the Supported card"),
        ("muted", "partly-fill", "the source and note on the Partly supported card"),
        ("muted", "not-supported-fill", "the source and note on the Not supported card"),
    };

    private static string? Root()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root;
    }

    [Theory]
    [InlineData(":root {")]
    [InlineData("[data-theme=\"dark\"] {")]
    public void EveryStateOfTheExampleMeetsContrastAtItsFinalColours(string theme)
    {
        string? root = Root();
        if (root == null) return; // not run from the repository (packaged tests)

        string css = File.ReadAllText(Path.Join(root, "AuBtechReviewAgent", "Styles", "tailwind.input.css"));
        int start = css.IndexOf(theme, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No {theme} block in tailwind.input.css");
        string block = css[start..css.IndexOf('}', start)];
        var colours = Token.Matches(block).ToDictionary(
            m => m.Groups["name"].Value,
            m => new[] { m.Groups["r"].Value, m.Groups["g"].Value, m.Groups["b"].Value }.Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray());

        var failing = Pairs
            .Select(p => (p.Where, Ratio: Contrast(colours[p.Text], colours[p.Behind])))
            .Where(p => p.Ratio < 4.5)
            .Select(p => $"{p.Where} ({p.Ratio:0.00}:1)")
            .ToList();

        Assert.True(failing.Count == 0, $"Below 4.5:1 in {theme.TrimEnd(' ', '{')}: " + string.Join(", ", failing));
    }

    [Fact]
    public void TheVerdictStatesUseTheColoursCheckedHere()
    {
        string? root = Root();
        if (root == null) return;

        string css = File.ReadAllText(Path.Join(root, "AuBtechReviewAgent", "Styles", "tailwind.input.css"));
        Assert.Contains(".tai-loop-ok { --loop-ink: var(--supported); --loop-fill: var(--supported-fill);", css);
        Assert.Contains(".tai-loop-partly { --loop-ink: var(--partly); --loop-fill: var(--partly-fill);", css);
        Assert.Contains(".tai-loop-not { --loop-ink: var(--not-supported); --loop-fill: var(--not-supported-fill);", css);
        Assert.Contains("0%, 8% { background-color: rgb(var(--active)); color: rgb(var(--navy));", css);
        Assert.Contains("9.5%, 31.5% { background-color: rgb(var(--loop-fill)); color: rgb(var(--loop-ink));", css);
        Assert.Contains("33.3%, 100% { background-color: transparent; color: rgb(var(--navy));", css);
    }

    // WCAG 2.x contrast ratio of two sRGB colours.
    private static double Contrast(int[] a, int[] b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(int[] rgb)
    {
        double[] c = rgb.Select(v => v / 255.0).Select(v => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4)).ToArray();
        return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
    }
}
