using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A formal source is extracted from selected passages of its full text, not from up to 40,000 characters of it
/// (<see cref="FormalExtractionText"/>): its first passages and those most relevant to the map and the questions. Every
/// quote is checked against what was sent, which is copied from the kept text, and what was sent is on file.
/// </summary>
public class FormalExtractionTextTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "formal-extraction-text-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private const string Filler = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. ";

    /// <summary>The relevant passage: the sentence only the full text has, with words of the kit's map and topic.</summary>
    private const string Relevant = FormalKit.FullTextOnly + " Summarisation is the practice of context engineering these agents rely on.";

    /// <summary>A long full text: the kit's opening sentence, 60 passages of filler, and the relevant passage in the middle.</summary>
    private static List<DocumentChunk> LongText(string id) => Enumerable.Range(1, 62).Select(i => new DocumentChunk(id, i,
        i == 1 ? "We keep a running summary of the conversation." : i == 40 ? Relevant : $"Passage {i}. " + string.Concat(Enumerable.Repeat(Filler, 5)))).ToList();

    private static MapVersion Map() => new()
    {
        Number = 1,
        Attributes = { new MapAttribute { Name = "Practice", Question = "RQ1", Description = "The practice described.", Values = { "Retrieval", "Summarisation", "Other" }, Multiple = true } },
    };

    [Fact]
    public void AnAbstractOrAShortFullTextIsSentWhole()
    {
        var plan = MultivocalPlan.Example();
        Assert.Equal(new FormalTextForModel("An abstract.", null), FormalExtractionText.Select("An abstract.", MultivocalPages.AbstractBasis, plan, Map()));
        string shortText = "First passage.\n\nSecond passage.";
        Assert.Equal(new FormalTextForModel(shortText, null), FormalExtractionText.Select(shortText, MultivocalPages.FullTextBasis, plan, Map()));
    }

    [Fact]
    public void ALongFullTextIsSentAsItsFirstAndMostRelevantPassagesCopiedFromIt()
    {
        string kept = string.Join("\n\n", LongText("p").Select(c => c.Text));
        var sent = FormalExtractionText.Select(kept, MultivocalPages.FullTextBasis, MultivocalPlan.Example(), Map());

        Assert.True(kept.Length > 30_000);
        Assert.True(sent.Text.Length < 12_000, $"{sent.Text.Length} characters sent");
        Assert.StartsWith("We keep a running summary of the conversation.\n\nPassage 2. ", sent.Text);
        Assert.Contains(Relevant, sent.Text);
        Assert.Contains(FormalExtractionText.Break, sent.Text);
        // Every passage sent is in the kept text, so a quote found in what was sent is found in the kept text too.
        Assert.All(sent.Text.Split(FormalExtractionText.Break).SelectMany(p => p.Split("\n\n")), p => Assert.Contains(p, kept));
        Assert.Matches(@"^14 of 62 passages of the full text \([\d,]+ of [\d,]+ characters\)$", sent.Passages);
    }

    [Fact]
    public async Task TheExtractionSendsPassagesChecksQuotesAgainstThemAndKeepsWhatWasSentOnFile()
    {
        var run = await FormalKit.RunAsync(_root, fullText: (paper, _) => Task.FromResult(paper.Id == "OPENALEX_W1"
            ? new FullTextResult(LongText(paper.Id), "open-access link from the source", "https://example.org/w1.pdf")
            : new FullTextResult(Array.Empty<DocumentChunk>(), "none (no open-access copy known)", null)));

        string prompt = run.Model.Prompts.Single(p => p.StartsWith("You extract data from one academic source", StringComparison.Ordinal) && p.Contains(FormalKit.FullTextOnly));
        Assert.True(prompt.Length < 16_000, $"{prompt.Length} characters in the extraction prompt");
        Assert.DoesNotContain("Passage 30.", prompt);
        Assert.Contains("selected passages of it", prompt);

        var extraction = JsonSerializer.Deserialize<MultivocalExtractionFile>(File.ReadAllText(Path.Join(run.Store.FolderOf(run.RunId), MultivocalExtractor.ExtractionFile)))!;
        var formal = extraction.Sources.Where(s => s.Formal).ToList();
        Assert.Equal(new[] { MultivocalPages.FullTextBasis, MultivocalPages.AbstractBasis }, formal.Select(s => s.TextBasis));
        Assert.StartsWith("14 of 62 passages of the full text", formal[0].Passages);
        Assert.Null(formal[1].Passages);
        Assert.Equal("Evaluates running summaries.", formal[0].Purpose?.Value);
        Assert.Equal("Summarisation", formal[0].Attributes.Single().Values.Single().Value);
        Assert.All(formal, s => Assert.All(s.Attributes.SelectMany(a => a.Values), v => Assert.Equal("found", run.Pages.CheckQuote(run.RunId, s.Address, v.Quote))));
        Assert.Contains("For 1 long full text, the model was given the first passages", MultivocalFormalText.ExtractionText(run.Reporter.LoadInput(run.RunId)!));
        Assert.Equal("formal-extraction-v3", MultivocalExtractor.FormalPromptVersion);
    }
}
