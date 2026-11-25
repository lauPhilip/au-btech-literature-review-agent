using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The coding and codebook answers are reused from the cache (the follow-up of #445): a rerun with the same prompt to the
/// same model asks nothing, another model or prompt asks again, a reused answer passes the step's checks again before it
/// is used, and the methods text counts what was reused.
/// </summary>
public class SynthesisCacheTests : IDisposable
{
    private const string Abstract = "Governance layers audit agent actions in production.";
    private const string CodingAnswer = """{"studies":[{"ref":1,"codes":[{"label":"fault recovery","findings":["F1.1"]}]},{"ref":2,"codes":[{"label":"auditing agent actions","findings":[],"quote":"Governance layers audit agent actions in production."}]}]}""";
    private const string CodebookAnswer = """{"themes":[{"name":"Safe operation","description":"d","codes":["C1","C2"]}],"unassigned":[]}""";

    private readonly string _root = Path.Join(Path.GetTempPath(), "synthesis-cache-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFolders.TryDelete(_root);

    private SynthesisCache Cache(string model = "model-a") => new(new ReviewCache(new CacheOptions { Folder = _root }, _root), model);

    private static ReferencedPaper[] Papers() => new[]
    {
        new ReferencedPaper(1, "P1", "Paper 1", "Agent loops recover from faults.", Array.Empty<DocumentChunk>()),
        new ReferencedPaper(2, "P2", "Paper 2", Abstract, Array.Empty<DocumentChunk>()),
    };

    private static StudyExtraction[] Extractions() => new[]
    {
        new StudyExtraction { ReferenceNumber = 1, StudyType = "case study", KeyFindings = { new EvidencedValue { Value = "Loops recover", Quote = "Agent loops recover from faults.", QuoteVerified = true } } },
        new StudyExtraction { ReferenceNumber = 2, StudyType = "case study" },
    };

    private static List<ThematicCode> Codes() => new()
    {
        new ThematicCode { Id = "C1", Reference = 1, Label = "fault recovery" },
        new ThematicCode { Id = "C2", Reference = 2, Label = "auditing agent actions" },
    };

    private Task<(List<ThematicCode> Codes, List<UncodedStudy> Uncoded)> Code(FakeChatService chat, SynthesisCache? cache, SynthesisReuse? reuse = null, string objective = "objective") =>
        ThematicSynthesis.CodeStudiesAsync(chat, objective, Papers(), Extractions(), 6, 1, cache: cache, reuse: reuse);

    /// <summary>Rewrites every stored answer of a kind, as a tampered or stale cache file would read.</summary>
    private void Tamper(string kind, string from, string to)
    {
        foreach (string file in Directory.GetFiles(Path.Join(_root, kind), "*.json", SearchOption.AllDirectories))
        {
            string json = File.ReadAllText(file);
            Assert.Contains(from, json);
            File.WriteAllText(file, json.Replace(from, to));
        }
    }

    [Fact]
    public async Task ARerunReusesTheCodingWithNoModelCallAndMarksIt()
    {
        var chat = new FakeChatService().RespondsWith(_ => CodingAnswer);
        var (first, _) = await Code(chat, Cache());
        var reuse = new SynthesisReuse();
        var (second, uncoded) = await Code(chat, Cache(), reuse);

        Assert.Single(chat.Prompts);
        Assert.All(first, c => Assert.Null(c.FromCache));
        Assert.All(second, c => Assert.True(c.FromCache));
        Assert.Equal(first.Select(c => (c.Id, c.Label, c.Anchor)), second.Select(c => (c.Id, c.Label, c.Anchor)));
        Assert.Equal("verified quote", second[1].Anchor);
        Assert.Empty(uncoded);
        Assert.Equal((1, 1), (reuse.CodingAnswers, reuse.CodingAnswersReused));
    }

    [Fact]
    public async Task AnotherModelOrPromptCodesAgain()
    {
        var chat = new FakeChatService().RespondsWith(_ => CodingAnswer);
        await Code(chat, Cache());
        await Code(chat, Cache("model-b"));
        await Code(chat, Cache(), objective: "another objective");
        await Code(chat, null);
        Assert.Equal(4, chat.Prompts.Count);
    }

    [Fact]
    public async Task AReusedCodingIsCheckedAgain()
    {
        var chat = new FakeChatService().RespondsWith(_ => CodingAnswer);
        await Code(chat, Cache());

        // A quote no longer in the abstract leaves its code unanchored, as for a fresh answer.
        Tamper(ThematicSynthesis.CodingCacheKind, "Governance layers audit", "Governance boards audit");
        var (codes, _) = await Code(chat, Cache());
        Assert.Single(chat.Prompts);
        Assert.Equal("unanchored", codes[1].Anchor);

        // A finding id the study does not have fails the check, so the model is asked again.
        Tamper(ThematicSynthesis.CodingCacheKind, "F1.1", "F2.1");
        var reuse = new SynthesisReuse();
        (codes, _) = await Code(chat, Cache(), reuse);
        Assert.Equal(2, chat.Prompts.Count);
        Assert.Equal(0, reuse.CodingAnswersReused);
        Assert.All(codes, c => Assert.Null(c.FromCache));
    }

    [Fact]
    public async Task ARerunReusesTheCodebookUnlessItNoLongerFitsTheCodes()
    {
        var chat = new FakeChatService().RespondsWith(_ => CodebookAnswer);
        var (first, _) = await ThematicSynthesis.BuildCodebookAsync(chat, "objective", Codes(), 8, cache: Cache());
        var reuse = new SynthesisReuse();
        var (second, _) = await ThematicSynthesis.BuildCodebookAsync(chat, "objective", Codes(), 8, cache: Cache(), reuse: reuse);
        Assert.Single(chat.Prompts);
        Assert.True(reuse.CodebookReused);
        Assert.Equal(first.Select(t => (t.Name, string.Join(",", t.Studies))), second.Select(t => (t.Name, string.Join(",", t.Studies))));

        await ThematicSynthesis.BuildCodebookAsync(chat, "objective", Codes(), 8, cache: Cache("model-b"));
        Assert.Equal(2, chat.Prompts.Count);

        // A stored codebook that names a code the run does not have is not used.
        Tamper(ThematicSynthesis.CodebookCacheKind, "\"C2\"", "\"C9\"");
        reuse = new SynthesisReuse();
        await ThematicSynthesis.BuildCodebookAsync(chat, "objective", Codes(), 8, cache: Cache(), reuse: reuse);
        Assert.Equal(3, chat.Prompts.Count);
        Assert.False(reuse.CodebookReused);
    }

    [Fact]
    public void TheMethodsCountWhatWasReused()
    {
        Assert.Equal("", ThematicSynthesis.ReuseSentence(null));
        Assert.Equal("", ThematicSynthesis.ReuseSentence(new SynthesisReuse { CodingAnswers = 3 }));
        Assert.StartsWith("3 of 3 coding answers and the codebook were reused from an earlier identical answer",
            ThematicSynthesis.ReuseSentence(new SynthesisReuse { CodingAnswers = 3, CodingAnswersReused = 3, CodebookReused = true }));
        Assert.StartsWith("1 of 2 coding answers was reused", ThematicSynthesis.ReuseSentence(new SynthesisReuse { CodingAnswers = 2, CodingAnswersReused = 1 }));
        Assert.StartsWith("The codebook was reused", ThematicSynthesis.ReuseSentence(new SynthesisReuse { CodingAnswers = 2, CodebookReused = true }));

        var book = new ThematicCodebook { Codes = Codes(), Themes = { new Theme { Id = "T1", Name = "Safe operation", Studies = { 1, 2 } } } };
        Assert.DoesNotContain("reused", ThematicSynthesis.MethodsText(book, "m", true));
        book.Reuse = new SynthesisReuse { CodingAnswers = 1, CodingAnswersReused = 1, CodebookReused = true };
        Assert.Contains("1 of 1 coding answer and the codebook were reused", ThematicSynthesis.MethodsText(book, "m", true));
    }
}
