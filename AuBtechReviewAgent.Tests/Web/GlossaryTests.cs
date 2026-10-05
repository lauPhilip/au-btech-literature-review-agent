using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The plain-language explanations shown by &lt;Term&gt; and on /glossary.</summary>
public class GlossaryTests
{
    [Fact]
    public void KeysAreUniqueAndEveryEntryHasAnExplanation()
    {
        Assert.Equal(Glossary.All.Count, Glossary.All.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(Glossary.All, e => Assert.False(string.IsNullOrWhiteSpace(e.Explanation)));
        Assert.NotNull(Glossary.Find("KAPPA"));
        Assert.Null(Glossary.Find("no-such-term"));
    }

    [Fact]
    public void EveryTermUsedInThePagesExists()
    {
        // A typo in <Term Key="..."> would silently show plain text, so check every literal key in the components.
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root)!;
        if (root == null) return; // not run from the repository (packaged tests)
        var keys = Directory.EnumerateFiles(Path.Join(root, "AuBtechReviewAgent", "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "<Term Key=\"([a-z-]+)\"").Select(m => m.Groups[1].Value))
            .Distinct().ToList();

        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.NotNull(Glossary.Find(k)));
    }
}
