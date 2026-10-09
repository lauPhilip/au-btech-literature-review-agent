using System.Text.Json;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Writing and checking the paper of a multivocal run (MLR G-8), with a fake model, on the small run of the paper
/// tests: the shared writing steps in order, the questions answered in code in their place, the summary for
/// practitioners, the front matter written last, and the citation check against the kept page texts, in which a
/// source whose page was not kept is unverifiable.
/// </summary>
public class MultivocalWritingTests
{
    private const string KeptA = "We keep a running summary of the conversation.\n\nOur agents are built with LangGraph, which keeps state between steps.";
    private const string KeptB = "Retrieval brings in only the documents the agent needs. We also summarise old turns.";

    private static string? Kept(PageSnapshot page) => page.TextFile switch { "a.txt" => KeptA, "b.txt" => KeptB, _ => null };

    internal static string Respond(string prompt)
    {
        if (prompt.Contains("grounded synthesis outline")) return """{"themes":[{"theme":"Short contexts","claims":[{"text":"Sources keep running summaries","refs":[1,2]}]}]}""";
        if (prompt.Contains("You are writing one subsection of the Results"))
            return """{"heading":"Keeping the context short","text":"Sources keep a running summary of the conversation [1, 2]. Retrieval brings in only the documents the agent needs [2]."}""";
        if (prompt.Contains("You are writing the Discussion section"))
            return """{"text":"Summaries and retrieval keep contexts short [1, 2]. A forum post names LangChain memory [3]."}""";
        if (prompt.Contains("strict but constructive peer reviewer")) return """{"comments":[]}""";
        if (prompt.Contains("You are writing the summary for practitioners"))
            return """{"lead":"For developers who build LLM agents.","items":["Keep a running summary of the conversation [1, 2].","Retrieve only the documents the agent needs [2].","Treat framework advice from forums with care [3]."]}""";
        if (prompt.Contains("You are writing the title, abstract, rationale and objectives"))
            return """{"title":"Context engineering for LLM agents: a grey literature review","abstract":"We screened 4 grey sources twice and extracted 3; their findings form 2 themes. Summaries and retrieval keep agent contexts short.","rationale":"Agents keep a running summary of the conversation [1].","objectives":"To map the practices developers use to decide what goes into an agent's context window."}""";
        if (prompt.Contains("rigorous academic copyeditor"))
            return Regex.Match(prompt, @"ORIGINAL DRAFT:\s*(.*?)\s*TASK:", RegexOptions.Singleline).Groups[1].Value.Trim();
        if (prompt.Contains("You are checking citations"))
        {
            string e1 = Regex.Match(prompt, @"^E1: (.*)$", RegexOptions.Multiline).Groups[1].Value;
            int n = Regex.Matches(prompt, @"^S\d+: ", RegexOptions.Multiline).Count;
            return JsonSerializer.Serialize(new { results = Enumerable.Range(1, n).Select(i => new { sentence = i, verdict = "supported", excerpt = "E1", quote = e1[..Math.Min(40, e1.Length)], reason = "ok" }) });
        }
        return "{}";
    }

    [Fact]
    public async Task ThePaperIsWrittenThroughTheSharedStepsWithTheCodeAnswersInPlace()
    {
        var input = MultivocalPaperTests.Input();
        var model = new FakeChatService().RespondsWith(Respond);
        var lines = new List<string>();

        var (paper, outline) = await MultivocalWriter.WritePaperAsync(model, input, Kept, "fake-model", parallelism: 1, new SyncProgress(lines.Add));

        // Every question in its place: RQ1 written by the model, RQ1.1 and RQ2 counted in code, RQ1.2 and RQ3 said in code.
        Assert.Equal(new[] { ("RQ1", "theme"), ("RQ1.1", "code"), ("RQ1.2", "code"), ("RQ2", "code"), ("RQ3", "code") },
            paper.Results.Select(r => (r.Question, r.Kind)));
        var theme = paper.Results[0];
        Assert.Equal(("synthesis-1", "Keeping the context short"), (theme.Field, theme.Heading));
        Assert.StartsWith("What this theme rests on: 2 sources [1, 2]", theme.Note);
        Assert.Contains("answered by counting, in code", paper.Results[3].Text);
        Assert.Contains("no theme that could be written up", paper.Results[2].Text);

        Assert.Contains("Theme: Short contexts", outline);
        Assert.Equal(3, paper.SummaryItems.Count);
        Assert.Equal("For developers who build LLM agents.", paper.SummaryLead);
        Assert.Equal("Context engineering for LLM agents: a grey literature review", paper.Title);
        Assert.Contains("extracted 3", paper.Abstract);
        Assert.Equal(new[] { "Title", "Abstract", "Rationale", "Objectives" }, paper.Style.Select(s => s.FieldName));
        Assert.Empty(paper.Notes);

        // The prompts name the review and its sources, and the abstract's numbers come from code.
        Assert.Contains(model.Prompts, p => p.Contains("You are writing one subsection of the Results") && p.Contains("a grey literature review following the Garousi et al. (2019) guidelines") && p.Contains("SOURCE [1]"));
        Assert.Contains(model.Prompts, p => p.Contains("You are writing the title") && p.Contains("Included at screening, before the quality check: 3; excluded at screening: 1.") && p.Contains("kept for the synthesis: 3"));
        Assert.Contains(model.Prompts, p => p.Contains("You are writing the summary for practitioners") && p.Contains("WRITTEN FOR: " + MultivocalReporter.Audience(input.Planned.Plan.Audience)));
        Assert.Contains("6 of 6 writing steps · the title, abstract and introduction", lines);
    }

    [Fact]
    public async Task EveryCitationIsCheckedAgainstTheKeptPageAndASourceWithoutOneIsUnverifiable()
    {
        var input = MultivocalPaperTests.Input();
        var model = new FakeChatService().RespondsWith(Respond);
        var (paper, _) = await MultivocalWriter.WritePaperAsync(model, input, Kept, "fake-model", parallelism: 1);

        var audit = await MultivocalWriter.CheckPaperAsync(model, input, paper, Kept, parallelism: 1);

        var checks = audit.SupportChecks;
        Assert.Contains(checks, c => c.Field == MultivocalWriter.RationaleField);
        Assert.Contains(checks, c => c.Field == "synthesis-1");
        Assert.Contains(checks, c => c.Field == MultivocalWriter.SummaryField);
        Assert.DoesNotContain(checks, c => c.Field.StartsWith("answer-")); // counted in code, not written
        // Source 3's page was not kept: its citations are unverifiable, never judged on its search summary or old quotes.
        Assert.All(checks.Where(c => c.Reference == 3), c => Assert.Equal(CitationSupportChecker.Unverifiable, c.Verdict));
        Assert.Equal(2, checks.Count(c => c.Reference == 3)); // the discussion and the third summary item
        Assert.All(checks.Where(c => c.Reference != 3), c => Assert.Equal("supported", c.Verdict));
        Assert.Equal(audit.SupportSummary, paper.Checks);
        Assert.NotNull(paper.CheckedUtc);
        Assert.Equal(3, audit.SourcesCited);
        Assert.Empty(audit.NotCited);
        Assert.Equal(3, paper.SummaryItems.Count); // the items survive the check one per line
    }

    [Fact]
    public async Task TheSameCheckWithTheSameEvidenceComesFromTheCache()
    {
        string folder = Path.Join(Path.GetTempPath(), "mlrcache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new ReviewCache(new CacheOptions { Folder = folder }, folder);
            var input = MultivocalPaperTests.Input();
            var model = new FakeChatService().RespondsWith(Respond);
            var (paper, _) = await MultivocalWriter.WritePaperAsync(model, input, Kept, "fake-model", parallelism: 1);
            var again = JsonSerializer.Deserialize<MultivocalPaperFile>(JsonSerializer.Serialize(paper))!;

            var first = await MultivocalWriter.CheckPaperAsync(model, input, paper, Kept, parallelism: 1, cache: new CheckCache(cache, "fake-model"));
            int asked = model.Prompts.Count(p => p.Contains("You are checking citations"));
            var second = await MultivocalWriter.CheckPaperAsync(model, input, again, Kept, parallelism: 1, cache: new CheckCache(cache, "fake-model"));

            Assert.True(asked > 0);
            Assert.Equal(asked, model.Prompts.Count(p => p.Contains("You are checking citations"))); // nothing asked again
            Assert.Equal(first.SupportChecks.Select(c => (c.Field, c.Reference, c.Verdict)), second.SupportChecks.Select(c => (c.Field, c.Reference, c.Verdict)));

            // Another model is another key: its answers are its own.
            await MultivocalWriter.CheckPaperAsync(model, input, JsonSerializer.Deserialize<MultivocalPaperFile>(JsonSerializer.Serialize(paper))!, Kept, parallelism: 1, cache: new CheckCache(cache, "other-model"));
            Assert.True(model.Prompts.Count(p => p.Contains("You are checking citations")) > asked);

            // The support check of the synthesis is kept the same way, by source.
            var supportModel = new FakeChatService().RespondsWith(p => """{"verdicts":[{"finding":"F1.1","verdict":"supported","reason":""}]}""");
            var findings = new[] { input.Synthesis.Findings[0] };
            await MultivocalSynthesiser.CheckSupportAsync(supportModel, findings, new HashSet<string>(), cache, "fake-model");
            var (supported, _, uncheckedCount) = await MultivocalSynthesiser.CheckSupportAsync(supportModel, findings, new HashSet<string>(), cache, "fake-model");
            Assert.Single(supportModel.Prompts);
            Assert.Equal((1, 0), (supported.Count, uncheckedCount));
        }
        finally
        {
            TestFolders.TryDelete(folder);
        }
    }

    [Fact]
    public async Task TheCheckedPaperIsBuiltInTheSharedShapeAndWrittenAsLatex()
    {
        var input = MultivocalPaperTests.Input();
        var model = new FakeChatService().RespondsWith(Respond);
        var (paper, _) = await MultivocalWriter.WritePaperAsync(model, input, Kept, "fake-model", parallelism: 1);
        var audit = await MultivocalWriter.CheckPaperAsync(model, input, paper, Kept, parallelism: 1);

        var built = MultivocalPaper.Build(input, paper, audit);

        Assert.Equal("AI-GENERATED GREY LITERATURE REVIEW", built.KindLabel);
        Assert.Equal(paper.Title, built.Title);
        Assert.EndsWith("The research questions were RQ1: Which context engineering practices do practitioners and researchers describe?; RQ1.1: Which tools and frameworks support these practices?; RQ1.2: How are the practices usually applied when an agent is built?; RQ2: How often is each practice mentioned in grey and in academic sources?; RQ3: Is the use of retrieval for context related to fewer reported agent failures?", built.Objectives);
        Assert.Equal(new[] { "RQ1: Keeping the context short", "RQ1.1: Which tools and frameworks support these practices?" }, built.Themes.Take(2).Select(t => t.Heading));
        Assert.Contains(@"\begin{tabular}", built.Themes[1].Latex); // RQ1.1 answered in code, as a table
        Assert.Contains("What this theme rests on", built.Themes[0].Latex);
        Assert.Equal("Automated citation check", Assert.Single(built.DiscussionNotes).Heading);
        Assert.Contains("could not be verified", built.DiscussionNotes[0].Text);
        Assert.Equal("Summary for practitioners", built.Summary!.Heading);
        Assert.Equal(new[] { "Use of artificial intelligence", "Registration and protocol", "Availability" }, built.Declarations.Select(d => d.Heading));
        Assert.Equal(3, built.References.Count);

        string tex = PaperLatex.Build(built);
        Assert.Contains(@"\section{Summary for practitioners}", tex);
        Assert.Contains(@"\item Keep a running summary of the conversation [1, 2].", tex);
        Assert.Contains(@"\bibitem{ref3}", tex);
        Assert.Equal(CountOf(tex, @"\begin{"), CountOf(tex, @"\end{"));

        // The summary is shown one item per paragraph; its sentences are the ones the check numbered.
        Assert.Equal(CitationSupportChecker.SplitSentences(paper.SummaryItems.Aggregate((a, b) => a + "\n" + b)),
            CitationSupportChecker.SplitSentences(string.Join("\n\n", paper.SummaryItems)));
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
