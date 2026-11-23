using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The lean source blocks of the shared writing steps (<see cref="WritingProfile.LeanEvidence"/>): off by default, so
/// the systematic review's theme prompts are byte for byte what they were, and on for the multivocal review, whose
/// theme prompts then get only the findings placed in the theme, each still with its verified quote.
/// </summary>
public class LeanEvidenceTests
{
    // A source in two themes, with findings that put it in each; its codes are its findings' values, as in the multivocal review.
    internal static ReviewEvidence Evidence(int sources = 2, int findingsPerSource = 4, int chunks = 3, int chunkLength = 200)
    {
        var list = new List<EvidenceSource>();
        for (int r = 1; r <= sources; r++)
        {
            var findings = Enumerable.Range(1, findingsPerSource)
                .Select(i => new EvidenceFinding($"F{r}.{i}", $"Practice: value {r}-{i} about {(i % 2 == 0 ? "retrieval" : "summaries")}", $"Quote {r}-{i} found in the page."))
                .ToList();
            var text = Enumerable.Range(1, chunks).Select(c => new DocumentChunk($"s{r}", c, $"Passage {c} of source {r} on summaries and retrieval. " + new string('x', chunkLength))).ToList();
            list.Add(new EvidenceSource
            {
                Reference = r, Title = $"Source {r}", ReferenceLine = $"Author {r} (2025). Source {r}.", Profile = "Blog post; 3rd-tier outlet.",
                Findings = findings, Codes = findings.Select(f => f.Value).ToList(),
                Text = new ReferencedPaper(r, $"s{r}", $"Source {r}", "", text),
            });
        }
        EvidenceTheme Theme(string id, string name, int parity) => new()
        {
            Id = id, Name = name, Description = $"Sources on {name.ToLowerInvariant()}.",
            Sources = list.Select(s => s.Reference).ToList(),
            Codes = list.ToDictionary(s => s.Reference, s => (IReadOnlyList<string>)s.Findings.Where((_, i) => (i + 1) % 2 == parity).Select(f => f.Value).ToList()),
            FindingIds = list.ToDictionary(s => s.Reference, s => (IReadOnlyList<string>)s.Findings.Where((_, i) => (i + 1) % 2 == parity).Select(f => f.Id).ToList()),
        };
        return new ReviewEvidence { SourceLabel = "SOURCE", Sources = list, Themes = new[] { Theme("T1", "Summaries", 1), Theme("T2", "Retrieval", 0) } };
    }

    [Fact]
    public void TheSystematicProfileIsNotLeanAndADefaultBlockListsEveryFindingAsBefore()
    {
        Assert.False(WritingProfile.Systematic.LeanEvidence);
        var evidence = Evidence();
        var theme = evidence.Themes[0];

        string block = evidence.SourceBlock(1, theme, 2);
        Assert.Equal(block, evidence.SourceBlock(1, theme, 2, lean: false));
        // Every finding of the source, the codes line, and two excerpts: the block as it was before the lean option.
        Assert.All(evidence.Source(1)!.Findings, f => Assert.Contains($"Finding {f.Id}: {f.Value} (verbatim: \"{f.Quote}\")", block));
        Assert.Contains("Codes in this theme: ", block);
        Assert.Equal(2, block.Split("[1, page ").Length - 1);
    }

    [Fact]
    public void ALeanBlockListsOnlyTheThemesFindingsEachWithItsQuote()
    {
        var evidence = Evidence();
        string summaries = evidence.SourceBlock(1, evidence.Themes[0], 1, lean: true);
        string retrieval = evidence.SourceBlock(1, evidence.Themes[1], 1, lean: true);

        Assert.Contains("Finding F1.1: Practice: value 1-1 about summaries (verbatim: \"Quote 1-1 found in the page.\")", summaries);
        Assert.Contains("Finding F1.3:", summaries);
        Assert.DoesNotContain("Finding F1.2:", summaries);
        Assert.DoesNotContain("Finding F1.4:", summaries);
        Assert.Contains("Finding F1.2:", retrieval);
        Assert.DoesNotContain("Finding F1.1:", retrieval);
        // The codes repeat the findings' values, so a lean block leaves them out; the source's identity stays.
        Assert.DoesNotContain("Codes", summaries);
        Assert.StartsWith("SOURCE [1] Source 1", summaries);
        Assert.Contains("Blog post; 3rd-tier outlet.", summaries);
    }

    [Fact]
    public void ALeanBlockKeepsCodesThatAreNotFindingsAndEveryFindingWithoutATheme()
    {
        var evidence = Evidence();
        var source = evidence.Source(1)!;
        var withOwnCodes = new ReviewEvidence { Sources = new[] { new EvidenceSource { Reference = 1, Findings = source.Findings, Codes = new[] { "a code of its own" }, Text = source.Text } } };

        Assert.Contains("Codes: a code of its own", withOwnCodes.SourceBlock(1, null, 0, lean: true));
        string outline = evidence.SourceBlock(1, null, 1, lean: true);
        Assert.All(source.Findings, f => Assert.Contains($"Finding {f.Id}:", outline));
        Assert.Equal(1, outline.Split("[1, page ").Length - 1);
    }

    [Fact]
    public async Task TheThemePromptOfTheDefaultProfileIsUnchangedAndTheLeanOneHasOnlyTheThemesFindings()
    {
        var evidence = Evidence();
        async Task<string> Prompt(WritingProfile profile)
        {
            var model = new FakeChatService().RespondsWith(_ => """{"heading":"h","text":"Sources keep summaries [1, 2]."}""");
            await ReviewWriter.WriteThemeAsync(model, profile, "goal", evidence, evidence.Themes[0], 1, 2);
            return model.Prompts.Single();
        }

        string systematic = await Prompt(WritingProfile.Systematic);
        // The default prompt carries the default blocks, every finding of each source included.
        Assert.Contains(evidence.SourceBlock(1, evidence.Themes[0], 2) + "\n" + evidence.SourceBlock(2, evidence.Themes[0], 2), systematic);
        Assert.Contains("Finding F1.2:", systematic);

        string lean = await Prompt(WritingProfile.Systematic with { LeanEvidence = true });
        Assert.Contains("Finding F1.1:", lean);
        Assert.Contains("Finding F2.3:", lean);
        Assert.DoesNotContain("Finding F1.2:", lean);
        Assert.DoesNotContain("Finding F2.4:", lean);
        Assert.True(lean.Length < systematic.Length);
    }

    [Fact]
    public void ALeanThemeBlockCarriesOneShortExcerptOfWholeSentencesMostAboutTheTheme()
    {
        // Chunks of about 1,200 characters, as the multivocal review cuts its kept pages; the second has the theme's words late in it.
        string Sentence(int i) => $"Sentence {i} is ordinary text about agents and their tools in a long page.";
        string other = string.Join(" ", Enumerable.Range(1, 16).Select(Sentence));
        string relevant = string.Join(" ", Enumerable.Range(1, 10).Select(Sentence)) + " Running summaries keep the context short. Summaries replace old turns. " + string.Join(" ", Enumerable.Range(11, 4).Select(Sentence));
        var text = new ReferencedPaper(1, "s1", "Source 1", "", new[] { new DocumentChunk("s1", 1, other), new DocumentChunk("s1", 2, relevant) });
        var source = new EvidenceSource { Reference = 1, Title = "Source 1", Findings = new[] { new EvidenceFinding("F1.1", "Summaries", "Quote.") }, Text = text };
        var theme = new EvidenceTheme { Id = "T1", Name = "Summaries", Description = "Running summaries.", Sources = new[] { 1 }, FindingIds = new Dictionary<int, IReadOnlyList<string>> { [1] = new[] { "F1.1" } } };
        var evidence = new ReviewEvidence { Sources = new[] { source }, Themes = new[] { theme } };

        string lean = evidence.SourceBlock(1, theme, 2, lean: true);
        var excerpts = lean.Split('\n').Where(l => l.StartsWith("[1, page ", StringComparison.Ordinal)).ToList();
        string excerpt = Assert.Single(excerpts)["[1, page 2]: ".Length..];
        Assert.StartsWith("[1, page 2]: ", excerpts[0]);
        Assert.Contains("Running summaries keep the context short.", excerpt);
        Assert.InRange(excerpt.Length, 1, ReviewEvidence.LeanExcerptCharacters);
        Assert.EndsWith(".", excerpt);
        Assert.Contains(excerpt, relevant);

        // The default block still carries both chunks whole.
        string full = evidence.SourceBlock(1, theme, 2);
        Assert.Contains($"[1, page 1]: {other}", full);
        Assert.Contains($"[1, page 2]: {relevant}", full);
    }

    [Fact]
    public void ASentenceLongerThanTheExcerptIsCutAtASpaceAndEveryPieceIsCopiedFromTheChunk()
    {
        string words = string.Join(" ", Enumerable.Range(1, 300).Select(i => $"word{i}")) + ".";
        var pieces = ReviewEvidence.Excerpts(new DocumentChunk("s", 3, words), 700).ToList();
        Assert.True(pieces.Count > 1);
        Assert.All(pieces, p => Assert.InRange(p.Text.Length, 1, 700));
        Assert.All(pieces, p => Assert.Contains(p.Text, words));
        Assert.All(pieces, p => Assert.Equal(3, p.PageNumber));
        Assert.Equal(words, string.Join(" ", pieces.Select(p => p.Text)));
    }
}
