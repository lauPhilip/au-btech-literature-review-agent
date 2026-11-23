using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The multivocal review writes from lean evidence (<see cref="WritingProfile.LeanEvidence"/>): a theme prompt gets
/// only the findings placed in that theme, and the outline gets each source's findings with one excerpt, so the
/// prompts stay small without changing what the writer may cite or how citations are checked.
/// </summary>
public class MultivocalLeanWritingTests
{
    private const string KeptA = "We keep a running summary of the conversation.\n\nOur agents are built with LangGraph, which keeps state between steps.";
    private const string KeptB = "Retrieval brings in only the documents the agent needs. We also summarise old turns.";

    private static string? Kept(PageSnapshot page) => page.TextFile switch { "a.txt" => KeptA, "b.txt" => KeptB, _ => null };

    [Fact]
    public void TheMultivocalProfileIsLeanAndEachThemeKnowsItsFindings()
    {
        var input = MultivocalPaperTests.Input();
        Assert.True(MultivocalWriter.Profile(input.Planned.Plan).LeanEvidence);

        var (evidence, _) = MultivocalEvidence.Build(input, Kept);
        var theme = evidence.Themes.Single(t => t.Name == "Short contexts");
        Assert.Equal(new[] { "F1.1" }, theme.FindingIds[1]);
        Assert.Equal(new[] { "F2.1", "F2.2" }, theme.FindingIds[2]);
    }

    [Fact]
    public async Task AThemePromptHasOnlyThatThemesFindingsAndTheOutlineOneExcerptPerSource()
    {
        var input = MultivocalPaperTests.Input();
        var model = new FakeChatService().RespondsWith(MultivocalWritingTests.Respond);

        await MultivocalWriter.WritePaperAsync(model, input, Kept, "fake-model", parallelism: 1);

        string theme = model.Prompts.Single(p => p.Contains("You are writing one subsection of the Results"));
        Assert.Contains("Finding F1.1: Practice: Summarisation (verbatim: \"We keep a running summary of the conversation.\")", theme);
        Assert.Contains("Finding F2.1:", theme);
        Assert.Contains("Finding F2.2:", theme);
        // The tool named by source 1 and the counted mentions of source 2 belong to other questions, not to this theme.
        Assert.DoesNotContain("F1.2", theme);
        Assert.DoesNotContain("F1.3", theme);
        Assert.DoesNotContain("F2.3", theme);
        Assert.DoesNotContain("F2.4", theme);

        string outline = model.Prompts.Single(p => p.Contains("grounded synthesis outline"));
        foreach (var id in new[] { "F1.1", "F1.2", "F1.3", "F2.1", "F2.2", "F2.3", "F2.4" }) Assert.Contains($"Finding {id}:", outline);
        Assert.Equal(1, outline.Split("[1, page ").Length - 1);
        Assert.Equal(1, outline.Split("[2, page ").Length - 1);
        Assert.Equal(1, MultivocalWriter.OutlineExcerpts);
    }

    [Fact]
    public void AtTheSizeOfARealRunTheLeanOutlineAndThemeEvidenceAreFarSmaller()
    {
        // 30 sources of 8 findings each, with kept texts of 20 chunks of about 1,200 characters, in two themes.
        var evidence = LeanEvidenceTests.Evidence(sources: 30, findingsPerSource: 8, chunks: 20, chunkLength: MultivocalEvidence.ChunkCharacters - 60);
        string before = string.Join("\n", evidence.Sources.Select(s => evidence.SourceBlock(s.Reference, null, 2)));
        string after = string.Join("\n", evidence.Sources.Select(s => evidence.SourceBlock(s.Reference, null, MultivocalWriter.OutlineExcerpts, lean: true)));
        Assert.True(after.Length < before.Length * 0.6, $"outline evidence {after.Length} of {before.Length} characters");
        // At most one excerpt and the verified findings per source.
        Assert.True(after.Length <= evidence.Sources.Count * (MultivocalEvidence.ChunkCharacters + 8 * 120 + 200), $"outline evidence {after.Length} characters");

        var theme = evidence.Themes[0];
        int Theme(bool lean) => string.Join("\n", theme.Sources.Select(r => evidence.SourceBlock(r, theme, 1, lean))).Length;
        Assert.True(Theme(true) < Theme(false));
    }
}
