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
        // The discussion call answers RQ1 first, the only question with a written theme (issue #461).
        if (prompt.Contains("You are writing the Discussion section"))
            return """{"answers":["Practitioners keep a running summary and retrieve only the documents the agent needs [1, 2]."],"text":"Summaries and retrieval keep contexts short [1, 2]. A forum post names LangChain memory [3]."}""";
        if (prompt.Contains("You are writing the sections that follow the discussion of"))
            return """{"limitations":"The evidence rests on blog posts and documentation that are not peer reviewed.","implications":"Practitioners can keep a running summary of the conversation [1].","futureResearch":"Comparative studies of summaries and retrieval in agents are missing from the theme on short contexts.","conclusion":"Short contexts are the shared practice, and the evidence for them is thin."}""";
        if (prompt.Contains("strict but constructive peer reviewer")) return """{"comments":[]}""";
        if (prompt.Contains("You are writing the summary for practitioners"))
            return """{"lead":"For developers who build LLM agents.","items":["Keep a running summary of the conversation [1, 2].","Retrieve only the documents the agent needs [2].","Treat framework advice from forums with care [3]."]}""";
        if (prompt.Contains("You are writing the title, abstract, rationale and objectives"))
            return """{"title":"Context engineering for LLM agents: a grey literature review","abstract":"We screened 4 grey sources twice and extracted 3; their findings form 2 themes. Summaries and retrieval keep agent contexts short.","rationale":"One developer guide suggests that agents keep a running summary of the conversation [1].","objectives":"To map the practices developers use to decide what goes into an agent's context window."}""";
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
        // The heading is the theme's name from the codebook, not the model's suggestion (issue #165).
        Assert.Equal(("synthesis-1", "Short contexts"), (theme.Field, theme.Heading));
        Assert.StartsWith("What this theme rests on: 2 sources [1, 2]", theme.Note);
        Assert.Contains("answered by counting, in code", paper.Results[3].Text);
        Assert.Contains("no theme that could be written up", paper.Results[2].Text);

        Assert.Contains("Theme: Short contexts", outline);
        Assert.Equal(3, paper.SummaryItems.Count);
        Assert.Equal("For developers who build LLM agents.", paper.SummaryLead);
        Assert.Equal("Context engineering for LLM agents: a grey literature review", paper.Title);
        Assert.Contains("extracted 3", paper.Abstract);
        Assert.Equal(new[] { "Title", "Abstract", "Rationale", "Objectives" }, paper.Style.Select(s => s.FieldName));
        Assert.False(paper.Style.Last().Applied); // the objectives are written in code from the questions

        // Every question is answered in one place (issue #461): RQ1 by the model with the discussion, the others in code.
        Assert.Equal(new[] { ("RQ1", "written"), ("RQ1.1", "code"), ("RQ1.2", "code"), ("RQ2", "code"), ("RQ3", "code") }, paper.Answers.Select(a => (a.Question, a.Kind)));
        Assert.StartsWith("Practitioners keep a running summary", paper.Answers[0].Text);
        Assert.Contains("Counted in code", paper.Answers[1].Text);
        Assert.Equal(MultivocalWriter.NoThemeText, paper.Answers[2].Text);
        Assert.StartsWith("The review set out to answer five research questions: (RQ1) Which context", paper.Objectives);
        Assert.Contains("(RQ1.1) Which tools", paper.Objectives);
        Assert.Equal("Short contexts are the shared practice, and the evidence for them is thin.", paper.Conclusion);
        Assert.StartsWith("Practitioners can keep", paper.Implications);
        Assert.NotEmpty(paper.Limitations);
        Assert.NotEmpty(paper.FutureResearch);
        Assert.Empty(paper.Notes);

        // The prompts name the review and its sources, and the abstract's numbers come from code.
        Assert.Contains(model.Prompts, p => p.Contains("You are writing one subsection of the Results") && p.Contains("a grey literature review following the Garousi et al. (2019) guidelines") && p.Contains("SOURCE [1]"));
        Assert.Contains(model.Prompts, p => p.Contains("You are writing the title") && p.Contains("Included at screening, before the quality check: 3; excluded at screening: 1.") && p.Contains("kept for the synthesis: 3"));
        Assert.Contains(model.Prompts, p => p.Contains("You are writing the summary for practitioners") && p.Contains("WRITTEN FOR: " + MultivocalReporter.Audience(input.Planned.Plan.Audience)));
        // The prompts number the questions as the plan does, the model answers only RQ1, and the front matter gets the answers and the conclusion.
        string discussion = Assert.Single(model.Prompts, p => p.Contains("You are writing the Discussion section"));
        Assert.Contains("RQ1.1: Which tools and frameworks support these practices?", discussion);
        Assert.Contains("one for each of RQ1 in that order", discussion);
        string closing = Assert.Single(model.Prompts, p => p.Contains("You are writing the sections that follow the discussion of"));
        Assert.Contains("threats to validity", closing);
        Assert.Contains("RQ2: How often", closing);
        Assert.Contains(model.Prompts, p => p.Contains("You are writing the title") && p.Contains("ANSWERS TO THE RESEARCH QUESTIONS") && p.Contains("CONCLUSION (written and checked"));
        Assert.Contains("6 of 6 writing steps · the title, abstract and introduction", lines);
    }

    [Fact]
    public async Task AThemeWhoseSectionCannotBeWrittenStaysInItsPlaceSaidInCode()
    {
        // Seen in a live run: one theme's answer failed twice and the theme vanished from the paper unseen.
        var input = MultivocalPaperTests.Input();
        var model = new FakeChatService().RespondsWith(p => p.Contains("You are writing one subsection of the Results") ? "not an answer" : Respond(p));

        var (paper, _) = await MultivocalWriter.WritePaperAsync(model, input, Kept, "fake-model", parallelism: 1);

        var unwritten = paper.Results[0];
        Assert.Equal(("RQ1", MultivocalWriter.UnwrittenKind), (unwritten.Question, unwritten.Kind));
        Assert.StartsWith("RQ1: ", unwritten.Heading);
        Assert.StartsWith(MultivocalWriter.UnwrittenText, unwritten.Text);
        Assert.Contains("What this theme rests on: 2 sources [1, 2]", unwritten.Text);
        Assert.DoesNotContain(paper.Results, r => r.Kind == "theme");
        Assert.Contains(paper.Notes, n => n.Contains("could not be written"));
        Assert.Contains("could not be written", MultivocalPaper.ResultsText(paper));
        Assert.Contains(model.Prompts, p => p.Contains("You are writing the title") && p.Contains("Themes written up as result sections: 0; 1 more theme could not be written up"));

        // Nothing in it is sent to the citation check: it was written in code.
        var audit = await MultivocalWriter.CheckPaperAsync(model, input, paper, Kept, parallelism: 1);
        Assert.DoesNotContain(audit.SupportChecks, c => c.Field == unwritten.Field);
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
        // The answer the model wrote and the parts after the discussion are checked too (issue #461).
        Assert.Contains(checks, c => c.Field == "rq-answer-RQ1");
        Assert.Contains(checks, c => c.Field == MultivocalWriter.ImplicationsField);
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
        Assert.Equal("The review set out to answer five research questions: (RQ1) Which context engineering practices do practitioners and researchers describe?; (RQ1.1) Which tools and frameworks support these practices?; (RQ1.2) How are the practices usually applied when an agent is built?; (RQ2) How often is each practice mentioned in grey and in academic sources?; (RQ3) Is the use of retrieval for context related to fewer reported agent failures?", built.Objectives);
        Assert.Equal(new[] { "RQ1: Short contexts", "RQ1.1: Which tools and frameworks support these practices?" }, built.Themes.Take(2).Select(t => t.Heading));
        Assert.Contains(@"\begin{tabular}", built.Themes[1].Latex); // RQ1.1 answered in code, as a table
        Assert.Contains("What this theme rests on", built.Themes[0].Latex);
        // The discussion opens on the answers, then its parts, the review's limitations in code, and the citation check (issue #461).
        Assert.Equal("", built.Discussion);
        Assert.Equal(new[] { "Answers to the research questions", "Interpretation", "Limitations of the evidence", "Limitations of the review", "Implications for practitioners", "Implications for researchers", "Automated citation check" },
            built.DiscussionNotes.Select(n => n.Heading));
        Assert.Contains(@"\textbf{RQ1.1. Which tools", built.DiscussionNotes[0].Latex);
        Assert.Contains("could not be verified", built.DiscussionNotes[^1].Text);
        Assert.Equal("Conclusion", built.Conclusion!.Heading);
        Assert.Equal("Summary for practitioners", built.Summary!.Heading);
        Assert.Equal(new[] { "Use of artificial intelligence", "Registration and protocol", "Availability" }, built.Declarations.Select(d => d.Heading));
        Assert.Contains("This run used version ", built.Declarations.Last().Text);
        Assert.DoesNotContain("+", built.Declarations.Last().Text);
        Assert.Equal(3, built.References.Count);

        string tex = PaperLatex.Build(built);
        Assert.Contains(@"\section{Summary for practitioners}", tex);
        Assert.True(tex.IndexOf(@"\section{Conclusion}") > tex.IndexOf(@"\section{Summary for practitioners}"));
        Assert.True(tex.IndexOf(@"\subsection{Answers to the research questions}") < tex.IndexOf(@"\subsection{Interpretation}"));
        Assert.Contains("the answers to the research questions and the discussion", built.Declarations[0].Text);
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
