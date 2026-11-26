using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>One PDF case of the attack set (#534): its script for <see cref="TinyPdf"/> and what the defences must do.</summary>
public sealed record PdfAttackCase(string Id, string Trick, int Hidden, List<string> Flags, List<string> NotToModel, string Script)
{
    public override string ToString() => Id;
}

/// <summary>
/// The attack set's PDFs (AttackSet/pdfs.txt): text a PDF hides from its reader is taken out of the paper's chunks,
/// flagged when it reads like instructions, never wrapped for the model, and on file after a whole systematic review.
/// </summary>
public class InjectionAttackSetPdfTests : PipelineFeatureRun
{
    private const string Visible = "We keep a running summary of the conversation in every agent loop.";

    private static readonly Lazy<IReadOnlyList<PdfAttackCase>> Loaded = new(Load);

    public static IReadOnlyList<PdfAttackCase> Cases => Loaded.Value;

    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases) data.Add(c.Id);
        return data;
    }

    private static string File([CallerFilePath] string here = "") => Path.Join(Path.GetDirectoryName(here)!, "AttackSet", "pdfs.txt");

    private static IReadOnlyList<PdfAttackCase> Load()
    {
        string all = System.IO.File.ReadAllText(File()).Replace("\r\n", "\n");
        var heads = Regex.Matches(all, @"^=== (\S+) ===$", RegexOptions.Multiline);
        var cases = new List<PdfAttackCase>();
        for (int i = 0; i < heads.Count; i++)
        {
            int start = heads[i].Index + heads[i].Length, end = i + 1 < heads.Count ? heads[i + 1].Index : all.Length;
            var lines = all[start..end].Split('\n');
            string Say(string key) => string.Join("\n", lines.Where(l => l.StartsWith("#! " + key + " ", StringComparison.Ordinal)).Select(l => l[(key.Length + 4)..]));
            cases.Add(new PdfAttackCase(heads[i].Groups[1].Value, Say("trick"), int.Parse(Say("hidden")),
                Say("flags").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
                Say("not-to-model").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
                string.Join("\n", lines.Where(l => !l.StartsWith('#')))));
        }
        return cases;
    }

    private static PdfAttackCase Case(string id) => Cases.Single(c => c.Id == id);

    /// <summary>The paper's full text as the systematic review reads it: the PDF on disk, read and chunked.</summary>
    private static FullTextResult FullText(PdfAttackCase c, string folder)
    {
        string path = Path.Join(folder, c.Id + ".pdf");
        System.IO.File.WriteAllBytes(path, TinyPdf.Write(c.Script));
        var (chunks, parts, hidden) = DocumentRAGUtility.ReadPdf(path, c.Id);
        return new FullTextResult(chunks, "open-access link from the source", "https://example.org/" + c.Id + ".pdf") { HiddenParts = parts, Hidden = hidden };
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void EachPdfLosesItsHiddenTextAndRaisesItsFlags(string id)
    {
        var c = Case(id);
        var result = FullText(c, Directory.CreateDirectory(_root).FullName);
        string kept = string.Join("\n", result.Chunks.Select(ch => ch.Text));

        Assert.Equal(c.Hidden, result.HiddenParts);
        Assert.Equal(c.Flags, HiddenText.Flags(kept, result.Hidden)?.Select(f => f.Pattern).ToList() ?? new());
        Assert.Contains(Visible, kept);
        string wrapped = PromptSafety.Wrap(kept, "attack set");
        Assert.All(c.NotToModel, s => Assert.DoesNotContain(s, wrapped));
        if (c.Hidden == 0) Assert.Equal("", result.Hidden);
    }

    [Fact]
    public async Task AWholeReviewKeepsThePdfsHiddenTextFromTheModelAndOnFile()
    {
        static string IdOf(PdfAttackCase c) => "PDF_" + c.Id;
        var papers = Cases.Select(c => new AcademicPaper(IdOf(c), "Agent context: " + c.Id, Visible, "Published: 2025", new() { "Ana Attack" }, "Journal of Tests")).ToArray();
        var chat = new FakeChatService().RespondsWith(PipelineTests.Respond);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions())
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => chat,
            SourceFactory = _ => new PipelineTests.FakeSource(papers),
            Cache = ReviewCache.Disabled,
            FullTextFetcher = (paper, folder) => Task.FromResult(FullText(Cases.Single(c => IdOf(c) == paper.Id), folder)),
        };
        var runId = Guid.NewGuid();
        await engine.RunReviewAsync(runId, Request() with { MaxResultsPerSource = 25 });
        var state = engine.LoadState(runId)!;
        List<string> prompts;
        lock (chat.Prompts) prompts = chat.Prompts.ToList();

        Assert.Equal(PrismaReviewEngine.StageComplete, state.Stats.ProcessingStage);
        foreach (var c in Cases)
        {
            var entry = state.FullTextFlags?.SingleOrDefault(f => f.PaperId == IdOf(c));
            if (c.Hidden == 0) { Assert.Null(entry); continue; }
            Assert.NotNull(entry);
            Assert.Equal(c.Flags, entry!.Flags.Select(f => f.Pattern));
            Assert.Equal(HiddenTextRemoved.Of(0, c.Hidden), entry.Removed);
        }
        Assert.NotEmpty(prompts);
        foreach (string hidden in Cases.SelectMany(c => c.NotToModel))
            Assert.DoesNotContain(prompts, p => p.Contains(hidden, StringComparison.Ordinal));
    }
}
