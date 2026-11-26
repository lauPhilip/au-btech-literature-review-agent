using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The parts of a systematic review's main.tex that make it complete against PRISMA 2020 and PRISMA-S: the search
/// strategy per source, the competing-interests statement, the statement on what the tool does not assess, and the
/// PRISMA 2020 checklist whose section references must all resolve. All of it is written in code.
/// </summary>
public class PrismaCompletenessTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "prisma-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static string SourcesFolder([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(here)!, "..", "..", "AuBtechReviewAgent", "Sources"));

    [Fact]
    public async Task EveryChecklistReferenceInAWholeRunResolvesToALabel()
    {
        string tex = (await ReviewPaperTests.SystematicMainTexAsync(_root)).Replace(LatexBreaks.Break, "");

        var labels = Regex.Matches(tex, @"\\label\{([^}]+)\}").Select(m => m.Groups[1].Value).ToList();
        var refs = Regex.Matches(tex, @"\\ref\{([^}]+)\}").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
        Assert.NotEmpty(refs);
        Assert.All(refs, r => Assert.Contains(r, labels));

        Assert.Contains("Table A.4: Search Strategy per Source", tex);
        Assert.Contains("Table A.5: PRISMA 2020 Checklist", tex);
        Assert.Contains(@"\subsection{Competing Interests}", tex);
        Assert.Contains(@"\subsection{Effect Measures and Reporting Bias}\label{sec:effect-measures-and-reporting-bias}" + "\n" + PaperLatex.Escape(PrismaChecklist.NotAssessedText(false)), tex);
        Assert.Contains(@"\textbf{Text read}", tex);
        // One checklist row per PRISMA 2020 item and sub-item (27 items, 42 rows).
        int start = tex.IndexOf("PRISMA 2020 Checklist", StringComparison.Ordinal);
        string checklist = tex[start..tex.IndexOf(@"\bottomrule", start, StringComparison.Ordinal)];
        Assert.Equal(42, Regex.Matches(checklist, @"^\d+[a-f]? & ", RegexOptions.Multiline).Count);
        // Each place is named, with its number.
        Assert.Contains("7 & Search strategy & Search Execution (Sec.~\\ref{sec:search-execution}); Table A.4 (Search Strategy per Source) & Reported", checklist);
        Assert.Contains("26 & Competing interests & Competing Interests (Sec.~\\ref{sec:competing-interests}) & Partly: a placeholder for the authors to complete", checklist);
        Assert.Contains("16a & Results of the search & Characteristics of the Included Studies (Sec.~\\ref{sec:characteristics-of-the-included-studies}); PRISMA flow diagram (Fig.~\\ref{fig:prisma-flow})", checklist);
        // The golden run has no research questions, so certainty is not rated and the checklist says why.
        Assert.Contains("15 & Certainty assessment & Effect Measures and Reporting Bias (Sec.~\\ref{sec:effect-measures-and-reporting-bias}) & Not done: the review has no research questions to rate", checklist);
    }

    [Fact]
    public void TheSearchStrategyTableListsEverySearchWithItsLimits()
    {
        var state = new ReviewState
        {
            MaxResultsPerSource = 5,
            YearFrom = 2020,
            YearTo = 2026,
            SearchPerspectives = { "agent loops", "agent safety" },
            SearchStringYields = { new("agent loops", 2, 4, 4), new("agent safety", 0, 0, 0) },
            SearchLogs =
            {
                new PlatformSearchLog { SourceName = "arXiv API", QueryUsed = "agent loops", Timestamp = "2026-11-19 19:04:19 UTC", Status = "Completed Successfully", PapersFound = 3, RawResponseFile = "SourceResponses/01-arxiv-api-q1.xml" },
                new PlatformSearchLog { SourceName = "Crossref", QueryUsed = "agent loops", Timestamp = "2026-11-19 19:04:20 UTC", Status = "Completed Successfully", PapersFound = 1, FromCache = true },
                new PlatformSearchLog { SourceName = "IEEE Xplore API", QueryUsed = "agent loops", Timestamp = "2026-11-19 19:04:21 UTC", Status = "Failed", ErrorMessage = "403" },
            },
        };

        string tex = SearchStrategyTable.Latex(state, "A.4", PaperLatex.Escape).Replace(LatexBreaks.Break, "");

        Assert.Contains("Table A.4: Search Strategy per Source", tex);
        Assert.Contains(@"arXiv API & agent loops & search\_query=all: & 2026-11-19 19:04:19 UTC & 3 & SourceResponses/01-arxiv-api-q1.xml", tex);
        Assert.Contains("Crossref & agent loops & query.bibliographic= & 2026-11-19 19:04:20 UTC (cached answer) & 1 & --", tex);
        Assert.Contains("& failed &", tex);
        string note = SearchStrategyTable.Note(state);
        Assert.Contains("asking for at most 3 records (the limit of 5 per source shared across 2 search strings)", note);
        Assert.Contains("The publication-year limit (2020–2026) was applied in code to the records returned, not in the request.", note);
        Assert.Contains("Not sent to any source, because the limit per source was reached first: \"agent safety\".", note);
        Assert.Contains("A cached answer was reused", note);
        Assert.Equal("", SearchStrategyTable.Latex(new ReviewState(), "A.4", PaperLatex.Escape));
    }

    [Theory]
    [InlineData("ArxivSource.cs", "arXiv API")]
    [InlineData("ElsevierSource.cs", "ScienceDirect API")]
    [InlineData("IeeeXploreSource.cs", "IEEE Xplore API")]
    [InlineData("OpenAlexSource.cs", "OpenAlex")]
    [InlineData("SemanticScholarSource.cs", "Semantic Scholar")]
    [InlineData("CrossrefSource.cs", "Crossref")]
    public void TheSentAsColumnMatchesHowEachAdapterBuildsItsRequest(string file, string sourceName)
    {
        string code = File.ReadAllText(Path.Join(SourcesFolder(), file));

        Assert.Contains($"SourceName => \"{sourceName}\"", code);
        Assert.Contains(SearchStrategyTable.SentAs[sourceName].Replace("...)", ""), code);
    }

    [Fact]
    public void AnItemWithoutItsSectionIsMissingAndWhatTheToolDoesNotDoIsSaid()
    {
        var empty = PrismaChecklist.Rows(new PrismaChecklistContext());
        var full = PrismaChecklist.Rows(new PrismaChecklistContext
        {
            Headings = new[] { "Rationale", "Selection Automation", "Competing Interests", PrismaChecklist.NotAssessedHeading, "Certainty of the Evidence", "Conclusion" },
            CertaintyTable = "4.2",
            HumanScreeningReview = true,
            FundingDeclared = true,
            TitleSaysSystematicReview = true,
        });

        Assert.Equal(42, empty.Count);
        Assert.Equal("Missing", empty.Single(r => r.Item == "3").Status);
        Assert.Equal("Reported", full.Single(r => r.Item == "3").Status);
        Assert.Equal("Reported", full.Single(r => r.Item == "1").Status);
        Assert.Contains("human check", full.Single(r => r.Item == "8").Status);
        Assert.Equal("Partly: a placeholder for the authors to complete", full.Single(r => r.Item == "26").Status);
        Assert.StartsWith("Reported: a rule in code inspired by GRADE-CERQual", full.Single(r => r.Item == "15").Status);
        Assert.Equal(@"Certainty of the Evidence (Sec.~\ref{sec:certainty-of-the-evidence})", full.Single(r => r.Item == "15").Where);
        Assert.Equal(@"Table 4.2 (Certainty of the Evidence for Each Answer); Conclusion (Sec.~\ref{sec:conclusion})", full.Single(r => r.Item == "22").Where);
        Assert.Equal("Not done: the review has no research questions to rate", empty.Single(r => r.Item == "22").Status);
        Assert.Equal("Not done", full.Single(r => r.Item == "14").Status);
        Assert.Equal(@"Effect Measures and Reporting Bias (Sec.~\ref{sec:effect-measures-and-reporting-bias})", full.Single(r => r.Item == "14").Where);
        Assert.Equal("", empty.Single(r => r.Item == "14").Where);
    }

    [Fact]
    public void TheFlowDiagramHasTheRetrievalAndEligibilityBoxesWithReasons()
    {
        var reasons = PrismaFlowCounts.NotRetrievedReasons(new[]
        {
            "arXiv", "none (no open-access copy known)", "none (download failed: 403 Forbidden)", "none (download failed: timeout)", "Unpaywall",
        });
        Assert.Equal(new[] { ("Download failed", 2), ("No open-access copy known", 1) }, reasons);

        var c = new PrismaFlowCounts { Identified = 9, Screened = 9, ExcludedAtScreening = 4, Included = 5, FullTextRetrieved = 2, NotRetrievedByReason = reasons };
        string tikz = PrismaFlowDiagram.ToTikz(c);

        Assert.Contains("Reports sought for retrieval (n = 5)", tikz);
        Assert.Contains("Reports not retrieved (n = 3):\\\\ Download failed (n = 2)\\\\ No open-access copy known (n = 1)\\\\ Kept and read from the abstract only", tikz);
        Assert.Contains("Reports assessed for eligibility (n = 5)\\\\ in full text (n = 2); on the abstract (n = 3)", tikz);
        Assert.Contains("Reports excluded (n = 0):\\\\ eligibility was decided at screening", tikz);
        Assert.Contains(@"\draw[arrow] (assessed) -- (included);", tikz);

        // Reasons that do not add up to the count (an older run) are left out rather than shown wrong.
        c.NotRetrievedByReason = new() { ("Download failed", 1) };
        Assert.Empty(c.NotRetrievedLines());
        Assert.Contains("Reports not retrieved (n = 3):\\\\ Kept and read from the abstract only}", PrismaFlowDiagram.ToTikz(c));
    }

    [Fact]
    public void ALabelIsGivenOncePerHeading()
    {
        var used = new HashSet<string>();

        Assert.Equal("sec:support-funding", LatexLabels.For("Support & Funding"));
        Assert.Equal(@"\label{sec:discussion}", LatexLabels.Tag("Discussion", used));
        Assert.Equal("", LatexLabels.Tag("Discussion", used));
        Assert.Equal("", LatexLabels.Tag("Discussion", null));
        Assert.DoesNotContain(@"\label", PaperLatex.Build(new ReviewPaper { Title = "T", Methods = { new("Eligibility", "E.") } }));
    }
}
