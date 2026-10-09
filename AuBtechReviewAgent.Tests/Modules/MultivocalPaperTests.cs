using System.Globalization;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The multivocal review's own parts of the paper (MLR G-7): its evidence in the shared shape, the method by the
/// guidelines' phases, the data figures, the answers to frequency and existence questions in code, the note under each
/// theme, and the summary for practitioners as a checked writing step. Built from a small run of three extracted
/// sources (and one that failed) for the example plan, whose RQ1.1 asks about existence and RQ2 about frequency.
/// </summary>
public class MultivocalPaperTests
{
    private const string KeptA = "We keep a running summary of the conversation.\n\nOur agents are built with LangGraph, which keeps state between steps.";
    private const string KeptB = "Retrieval brings in only the documents the agent needs. We also summarise old turns.";

    internal static MultivocalReportInput Input()
    {
        var runId = Guid.NewGuid();
        var plan = MultivocalPlan.Example("grey");
        GreyRecord Record(string url, string title, string producer, string kind, int? year) =>
            new("x:" + url, title, "A summary of " + title + ".", url, producer, new Uri(url).Host, kind, year is int y ? new DateTime(y, 3, 1) : null, new Dictionary<string, long>());
        var ledger = new MultivocalLedger
        {
            RunId = runId,
            SearchedUtc = new DateTime(2026, 10, 6),
            StoppingApplied = "Effort bounded: the top 100 hits of each search string.",
            Searches = { new GreySearchLog { Source = "stackexchange", SearchString = "context engineering", Requested = 100, Returned = 5, New = 5 } },
            Sources =
            {
                new FoundGreySource { Record = Record("https://blog.example.org/a", "Keeping agent context short", "Ana", "blogs", 2025) },
                new FoundGreySource { Record = Record("https://docs.acme.dev/b", "Retrieval for agents", "Acme", "documentation", 2026) },
                new FoundGreySource { Record = Record("https://forum.example.org/c", "Which framework for agent memory?", "Bo", "qa", null) },
                new FoundGreySource { Record = Record("https://news.example.org/d", "Agents everywhere", "Cy", "news", 2024) },
                new FoundGreySource { Record = Record("https://blog.example.org/a-copy", "Keeping agent context short", "Ana", "blogs", 2025) },
            },
        };
        var screening = new MultivocalScreeningFile
        {
            RunId = runId,
            Kappa = 0.8,
            InclusionCriteria = plan.InclusionCriteria,
            ExclusionCriteria = plan.ExclusionCriteria,
            Sources =
            {
                new GreyScreening { Address = "blog.example.org/a", Decision = "Included" },
                new GreyScreening { Address = "docs.acme.dev/b", Decision = "Included" },
                new GreyScreening { Address = "forum.example.org/c", Decision = "Included" },
                new GreyScreening { Address = "news.example.org/d", Decision = "Excluded", ExclusionReason = ExclusionReasons.OffTopic },
                new GreyScreening { Address = "blog.example.org/a-copy", Decision = "Duplicate" },
            },
        };
        var pages = new MultivocalPagesFile
        {
            RunId = runId,
            Pages =
            {
                new PageSnapshot { Url = "https://blog.example.org/a", AccessedUtc = new DateTime(2026, 10, 6), TextFile = "a.txt" },
                new PageSnapshot { Url = "https://docs.acme.dev/b", AccessedUtc = new DateTime(2026, 10, 6), TextFile = "b.txt" },
                new PageSnapshot { Url = "https://forum.example.org/c", AccessedUtc = new DateTime(2026, 10, 6), NotKept = "robots.txt does not allow it" },
            },
        };
        var quality = new MultivocalQualityFile
        {
            RunId = runId,
            Threshold = 10,
            Sources =
            {
                new GreyQuality { Address = "blog.example.org/a", Points = 11, Outcome = "Passed" },
                new GreyQuality { Address = "docs.acme.dev/b", Points = 15.5, Outcome = "Passed" },
                new GreyQuality { Address = "forum.example.org/c", Points = 12, Outcome = "Passed" },
            },
        };
        var mapVersion = new MapVersion
        {
            Number = 1,
            Attributes =
            {
                new MapAttribute { Name = "Practice", Question = "RQ1", Values = { "Retrieval", "Summarisation", "Other" }, Multiple = true },
                new MapAttribute { Name = "Tool", Question = "RQ1.1", Open = true, Multiple = true },
                new MapAttribute { Name = "Practice mentioned", Question = "RQ2", Values = { "Retrieval", "Summarisation" }, Multiple = true },
            },
        };
        var extraction = new MultivocalExtractionFile
        {
            RunId = runId,
            MapVersion = 1,
            Sources =
            {
                new GreyExtraction { Address = "blog.example.org/a", Title = "Keeping agent context short", Url = "https://blog.example.org/a" },
                new GreyExtraction { Address = "news.example.org/x", Title = "Failed", Url = "https://news.example.org/x", Error = "The model call failed." },
                new GreyExtraction { Address = "docs.acme.dev/b", Title = "Retrieval for agents", Url = "https://docs.acme.dev/b" },
                new GreyExtraction { Address = "forum.example.org/c", Title = "Which framework for agent memory?", Url = "https://forum.example.org/c" },
            },
        };
        SynthesisFinding F(string id, string question, string attribute, string value, string quote, string address, string kind, int tier, double points) =>
            new() { Id = id, Question = question, Attribute = attribute, Value = value, Quote = quote, Address = address, Kind = kind, Tier = tier, QualityPoints = points };
        var synthesis = new MultivocalSynthesisFile
        {
            RunId = runId,
            Findings =
            {
                F("F1.1", "RQ1", "Practice", "Summarisation", "We keep a running summary of the conversation.", "blog.example.org/a", "blogs", 3, 11),
                F("F1.2", "RQ1.1", "Tool", "LangGraph", "Our agents are built with LangGraph", "blog.example.org/a", "blogs", 3, 11),
                F("F1.3", "RQ2", "Practice mentioned", "Summarisation", "We keep a running summary of the conversation.", "blog.example.org/a", "blogs", 3, 11),
                F("F2.1", "RQ1", "Practice", "Retrieval", "Retrieval brings in only the documents the agent needs.", "docs.acme.dev/b", "documentation", 2, 15.5),
                F("F2.2", "RQ1", "Practice", "Summarisation", "We also summarise old turns.", "docs.acme.dev/b", "documentation", 2, 15.5),
                F("F2.3", "RQ2", "Practice mentioned", "Retrieval", "Retrieval brings in only the documents the agent needs.", "docs.acme.dev/b", "documentation", 2, 15.5),
                F("F2.4", "RQ2", "Practice mentioned", "Summarisation", "We also summarise old turns.", "docs.acme.dev/b", "documentation", 2, 15.5),
                F("F3.1", "RQ1.1", "Tool", "LangChain", "LangChain memory", "forum.example.org/c", "qa", 2, 12),
            },
            Themes =
            {
                new SynthesisTheme
                {
                    Question = "RQ1", Name = "Short contexts", Description = "Sources keep the context small.", Findings = { "F1.1", "F2.1", "F2.2" },
                    Tensions = { new SynthesisTension { FindingA = "F1.1", FindingB = "F2.1", Description = "One source summarises, the other retrieves." } },
                    Sources = 2, GreySources = 2, SecondTier = 1, ThirdTier = 1, MeanQuality = 13.3, EvidenceLabel = "grey literature only",
                },
                new SynthesisTheme { Question = "RQ1.1", Name = "Agent frameworks", Description = "Frameworks named.", Findings = { "F1.2", "F3.1" }, Sources = 2 },
            },
        };
        var planned = new PlannedRun(runId, plan, "abc123", new DateTime(2026, 10, 6));
        return new MultivocalReportInput(planned, ledger, screening, pages, quality, new MultivocalMapFile { RunId = runId, Versions = { mapVersion }, FixedVersion = 1 }, extraction, synthesis);
    }

    private static string? Kept(PageSnapshot page) => page.TextFile switch { "a.txt" => KeptA, "b.txt" => KeptB, _ => null };

    [Fact]
    public void TheEvidenceIsTheSharedShapeNumberedAsTheSourceRepository()
    {
        var input = Input();

        var (evidence, questionOf) = MultivocalEvidence.Build(input, Kept);

        Assert.Equal(new[] { 1, 2, 3 }, evidence.Sources.Select(s => s.Reference)); // the failed extraction has no number
        var blog = evidence.Source(1)!;
        Assert.Equal(GreyReferences.Line(GreyReferences.Build(input.Ledger, input.Pages, input.Extraction)[0]), blog.ReferenceLine);
        Assert.Equal("Blog posts and practitioner articles; 3rd-tier outlet; quality 11 of 20 points; published 2025.", blog.Profile);
        Assert.Contains(blog.Findings, f => f.Id == "F1.1" && f.Value == "Practice: Summarisation");
        var chunk = Assert.Single(blog.Text!.Chunks); // both paragraphs fit one chunk
        Assert.Contains("LangGraph", chunk.Text);
        Assert.Empty(evidence.Source(3)!.Text!.Chunks); // its page was not kept: no text to quote from
        Assert.Equal("grey literature review", evidence.ReviewName);
        Assert.Equal("SOURCE", evidence.SourceLabel);

        // Only RQ1's theme is written by the model; RQ1.1 asks about existence and is answered in code.
        var theme = Assert.Single(evidence.Themes);
        Assert.Equal(("T1", "Short contexts", "RQ1"), (theme.Id, theme.Name, questionOf["T1"]));
        Assert.Equal(new[] { 1, 2 }, theme.Sources);
        Assert.Equal(new[] { "Practice: Retrieval", "Practice: Summarisation" }, theme.Codes[2]);

        Assert.Contains("The results rest on 3 grey sources, none of them peer reviewed: 1 blog posts and practitioner articles, 1 product and project documentation, 1 questions and answers on Q&A sites.", evidence.BaseFacts);
        Assert.Contains("0 from 1st-tier, 2 from 2nd-tier and 1 from 3rd-tier outlets", evidence.BaseFacts);
        Assert.Contains("Published between 2025 and 2026; 1 without a date.", evidence.BaseFacts);
        Assert.Contains("SOURCE [2] Retrieval for agents", evidence.SourceBlock(2, theme, excerpts: 1));
    }

    [Fact]
    public void FrequencyAndExistenceQuestionsAreAnsweredInCodeFromTheCheckedFindings()
    {
        var input = Input();
        var plan = input.Planned.Plan;
        Assert.True(MultivocalPaperParts.AnsweredInCode("frequency"));
        Assert.True(MultivocalPaperParts.AnsweredInCode("existence"));
        Assert.False(MultivocalPaperParts.AnsweredInCode("description"));

        var rq2 = plan.Numbered().Single(q => q.Number == "RQ2");
        var answer = MultivocalPaperParts.CodeAnswer(input, rq2.Number, rq2.Question);
        var table = Assert.Single(MultivocalPaperParts.CodeAnswerTables(input, "RQ2"));
        Assert.Equal(new[] { "Value", "Sources", "Share", "Cited" }, table.Headers);
        Assert.Equal(new[] { "Summarisation", "2", "67%", "[1, 2]" }, table.Rows[0]);
        Assert.Equal(new[] { "Retrieval", "1", "33%", "[2]" }, table.Rows[1]);
        Assert.Equal(new[] { "not stated", "1", "33%", "" }, table.Rows[2]); // the forum post states no practice
        Assert.StartsWith("RQ2: How often", answer.Heading);
        Assert.Contains("answered by counting, in code", answer.Text);
        Assert.Contains("A count says how many sources state a value, not how common it is in practice.", answer.Text);
        Assert.Contains(@"\begin{tabular}", answer.Latex);

        var tools = Assert.Single(MultivocalPaperParts.CodeAnswerTables(input, "RQ1.1")); // existence: the tools the sources name
        Assert.Equal(new[] { new[] { "LangChain", "1", "33%", "[3]" }, new[] { "LangGraph", "1", "33%", "[1]" }, new[] { "not stated", "1", "33%", "" } },
            tools.Rows.Select(r => r.ToArray()));
    }

    [Fact]
    public void TheNoteUnderAThemeSaysWhatItRestsOnAndWhereSourcesDisagree()
    {
        var input = Input();

        string note = MultivocalPaperParts.ThemeNote(input, input.Synthesis.Themes[0]);

        Assert.Equal("What this theme rests on: 2 sources [1, 2], 0 from 1st-tier, 1 from 2nd-tier and 1 from 3rd-tier outlets, with a mean quality of 13.3 of 20 points (grey literature only). " +
                     "Tension between sources: One source summarises, the other retrieves [1, 2].", note);
    }

    [Fact]
    public void TheMethodAndDataPartsAreWrittenInCode()
    {
        var input = Input();
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var methods = MultivocalPaperParts.Methods(input);
            Assert.Equal(new[] { "Planning (G1–G5)", "Search (G6–G8)", "Selection (G9, G10)", "Quality (G11)", "Systematic map and extraction (G12)", "Synthesis (G13)" },
                methods.Select(m => m.Heading));
            Assert.Contains(@"$\kappa$ = 0.80", methods[2].Latex); // Cohen's κ, set so pdflatex can print it

            var flow = MultivocalPaperParts.Flow(input);
            Assert.Equal((5, 1, 4, 3, 1, 3, 3, 1), (flow.Found, flow.Duplicates, flow.Screened, flow.Included, flow.Excluded, flow.Passed, flow.Extracted, flow.NotExtracted));
            Assert.Equal(new[] { ("documentation", 1), ("qa", 1), ("blogs", 1) }, MultivocalPaperParts.Kinds(input).Select(k => (k.Kind, k.Sources)));
            Assert.Equal(1, MultivocalPaperParts.QualityBands(input.Quality).Single(b => b.From == 10).Sources); // 11 points
            Assert.Equal(1, MultivocalPaperParts.QualityBands(input.Quality).Single(b => b.From == 14).Sources); // 15.5 points

            string data = MultivocalPaperParts.DataLatex(input);
            Assert.Contains("Found: 5 sources", data);
            Assert.Contains("threshold 10", data);
            Assert.Equal(CountOf(data, @"\begin{"), CountOf(data, @"\end{"));
            Assert.Contains("Practice mentioned & RQ2 & Summarisation & 2", data);
            Assert.Contains("The searches found 5 grey sources", MultivocalPaperParts.DataText(input));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    [Fact]
    public async Task TheSummaryForPractitionersCitesEveryItemAndUsesOnlyTheResultsNumbers()
    {
        var sections = new[] { new SynthesisSection { Field = "synthesis-1", Heading = "Short contexts", Text = "Two sources keep running summaries [1, 2], and one retrieves only what is needed [2]." } };
        var profile = new WritingProfile { Review = "a grey literature review", Sources = "sources" };
        var model = new FakeChatService()
            .Returns("""{"lead":"For developers building agents.","items":["Keep a running summary [1].","Retrieve only what is needed.","Measure context size [2]."]}""") // an item without a citation
            .Returns("""{"lead":"For developers building agents.","items":["Keep a running summary [1].","Retrieve only what is needed [9].","Measure context size [2]."]}""") // [9] is not a source
            .Returns("""{"lead":"For developers building agents [1].","items":["Keep a running summary; 40 teams do [1].","Retrieve only what is needed [2].","Check the summary against the source [1, 2]."]}"""); // a cited lead, an invented number

        var rejected = await ReviewWriter.WritePractitionerSummaryAsync(model, profile, "context engineering", "Map the practices", "practitioners",
            sections, "Summaries and retrieval complement each other [1, 2].", "[1] Ana\n[2] Acme", referenceCount: 2);

        Assert.Null(rejected); // three answers, none acceptable: no summary rather than an unchecked one
        Assert.Contains("item 2 has no citation", model.Prompts[1]);
        Assert.Contains("citation numbers 9 are not in the reference list", model.Prompts[2]);
        Assert.Contains("WRITTEN FOR: practitioners", model.Prompts[0]);

        var good = new FakeChatService().Returns("""{"lead":"For developers who build LLM agents.","items":["Keep a running summary of long conversations [1, 2].","Retrieve only the documents the agent needs [2].","Note that the summary practice rests on a blog post and documentation [1, 2]."]}""");
        var summary = await ReviewWriter.WritePractitionerSummaryAsync(good, profile, "context engineering", "Map the practices", "practitioners",
            sections, "Summaries and retrieval complement each other [1, 2].", "[1] Ana\n[2] Acme", referenceCount: 2);
        Assert.Equal(3, summary!.Items.Count);
        Assert.Equal("Keep a running summary of long conversations [1, 2].\nRetrieve only the documents the agent needs [2].\nNote that the summary practice rests on a blog post and documentation [1, 2].", summary.Text);
    }

    [Fact]
    public void ThePaperHasASummarySectionOnlyWhenTheModuleGivesOne()
    {
        var withSummary = PaperLatex.Build(new ReviewPaper { Title = "T", Summary = new PaperSection("Summary for practitioners", "", @"\begin{itemize}\item Keep it short [1].\end{itemize}") });
        Assert.Contains(@"\section{Summary for practitioners}", withSummary);
        Assert.True(withSummary.IndexOf(@"\section{Discussion}", StringComparison.Ordinal) < withSummary.IndexOf(@"\section{Summary for practitioners}", StringComparison.Ordinal));
        Assert.DoesNotContain("Summary for practitioners", PaperLatex.Build(new ReviewPaper { Title = "T" }));
    }
}
