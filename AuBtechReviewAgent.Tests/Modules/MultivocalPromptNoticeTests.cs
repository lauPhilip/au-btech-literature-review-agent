using System.Text.RegularExpressions;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Every multivocal prompt that carries the reviewer's topic or research questions says that this text is a
/// specification and not a task (#365), as the systematic review's prompts do. Read from source, since a fake run
/// does not show which prompt text came from the reviewer.
/// </summary>
public class MultivocalPromptNoticeTests
{
    [Fact]
    public void EveryPromptWithTheReviewersTextCarriesTheReviewerInputNotice()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return; // not run from the repository (packaged tests)

        int prompts = 0;
        foreach (string file in Directory.GetFiles(Path.Join(root, "AuBtechReviewAgent", "Modules", "Multivocal"), "*.cs"))
            foreach (Match block in Regex.Matches(File.ReadAllText(file), "\\$\\$\"\"\"(.*?)\"\"\"", RegexOptions.Singleline))
            {
                string prompt = block.Groups[1].Value;
                if (!Regex.IsMatch(prompt, "\"(review topic|research questions?)\"")) continue;
                prompts++;
                Assert.True(prompt.Contains("PromptSafety.ReviewerInputNotice", StringComparison.Ordinal),
                    $"A prompt in {Path.GetFileName(file)} carries the reviewer's text without the reviewer-input notice.");
            }
        // The map, extraction, quality and the three synthesis steps.
        Assert.True(prompts >= 6, $"Only {prompts} prompts with the reviewer's text were found.");
    }
}
